using TestAtlas.Core.Model;
using TestAtlas.Core.Storage;

namespace TestAtlas.Core.Analysis;

/// <summary>What kind of entity the impact query targets.</summary>
public enum ImpactTargetKind { Class, Method, Step, Endpoint }

/// <summary>An impact query: "what scenarios are affected if I change &lt;value&gt;?"</summary>
public sealed record ImpactQuery(ImpactTargetKind Kind, string Value);

/// <summary>One scenario reachable from the changed entity, with the step text(s) that connect it.</summary>
public sealed record AffectedScenario(
    int ScenarioId, string Scenario, string Feature, string FeatureFile, IReadOnlyList<string> Via);

/// <summary>The blast radius of a change.</summary>
public sealed record ImpactResult(
    bool Found, string TargetLabel, int StepDefinitionCount, int FeatureCount,
    IReadOnlyList<AffectedScenario> Scenarios);

/// <summary>
/// Reverse-dependency ("blast radius") analysis over the map's edges: given a step definition, method,
/// or class (page object / API client / step class), find the test scenarios that would be affected by
/// changing it. Walks the forward chain backwards —
/// <c>Scenario ← ScenarioStep ←binds_to← StepDefinition ←(method uses_type)← changed class</c>,
/// following <c>inherits</c> and <c>uses_type</c> transitively (through composed page objects too).
/// Class granularity by design: a step is affected when its method reaches the changed class, which
/// needs no restored build (the syntax-only guarantee). Purely in-memory, no Roslyn / IO.
/// </summary>
public static class ImpactAnalyzer
{
    public static ImpactResult Analyze(MapDocument doc, ImpactQuery query)
    {
        var classById = doc.Classes.ToDictionary(c => c.Id);
        var classOfMethod = doc.Methods.ToDictionary(m => m.Id, m => m.ClassId);
        var stepDefsByMethod = doc.StepDefinitions.GroupBy(s => s.MethodId).ToDictionary(g => g.Key, g => g.Select(s => s.Id).ToList());

        // uses_type: method → class it drives; inherits: derived → base.
        var usesEdges = doc.Edges.Where(e => e.EdgeKind == EdgeKinds.UsesType && e.FromKind == RefKinds.Method && e.ToId is int)
            .Select(e => (Method: e.FromId, Class: e.ToId!.Value)).ToList();
        var inheritsEdges = doc.Edges.Where(e => e.EdgeKind == EdgeKinds.Inherits && e.FromKind == RefKinds.Class && e.ToId is int)
            .Select(e => (Derived: e.FromId, Base: e.ToId!.Value)).ToList();
        // binds_to: scenario step → step definition (reverse: step def → scenario steps binding it).
        var boundBy = new Dictionary<int, List<int>>();
        foreach (var e in doc.Edges)
            if (e.EdgeKind == EdgeKinds.BindsTo && e.FromKind == RefKinds.ScenarioStep && e.ToKind == RefKinds.StepDefinition && e.ToId is int sd)
                (boundBy.TryGetValue(sd, out var l) ? l : (boundBy[sd] = new())).Add(e.FromId);

        // calls_endpoint: method → endpoint (for --endpoint queries).
        var endpointCalls = doc.Edges.Where(e => e.EdgeKind == EdgeKinds.CallsEndpoint && e.FromKind == RefKinds.Method && e.ToId is int)
            .Select(e => (Method: e.FromId, Endpoint: e.ToId!.Value)).ToList();

        var (affectedStepDefs, label) = ResolveTargets(doc, query, classById, classOfMethod, stepDefsByMethod, usesEdges, inheritsEdges, endpointCalls);
        if (affectedStepDefs.Count == 0)
            return new ImpactResult(false, label, 0, 0, Array.Empty<AffectedScenario>());

        // Affected step definitions → the scenario steps that bind them → their scenarios.
        var scenarioStepById = doc.ScenarioSteps.ToDictionary(s => s.Id);
        var scenarioById = doc.Scenarios.ToDictionary(s => s.Id);
        var featureById = doc.Features.ToDictionary(f => f.Id);

        var viaByScenario = new Dictionary<int, SortedSet<string>>();
        foreach (var sd in affectedStepDefs)
            if (boundBy.TryGetValue(sd, out var steps))
                foreach (var stId in steps)
                    if (scenarioStepById.TryGetValue(stId, out var st))
                    {
                        if (!viaByScenario.TryGetValue(st.ScenarioId, out var set))
                            viaByScenario[st.ScenarioId] = set = new(StringComparer.Ordinal);
                        set.Add(st.Text);
                    }

        var scenarios = viaByScenario
            .Select(kv =>
            {
                var sc = scenarioById.TryGetValue(kv.Key, out var s) ? s : null;
                var ft = sc is not null && featureById.TryGetValue(sc.FeatureId, out var f) ? f : null;
                return new AffectedScenario(kv.Key, sc?.Name ?? "?", ft?.Name ?? "?", ft?.FilePath ?? "", kv.Value.ToList());
            })
            .OrderBy(a => a.Feature, StringComparer.Ordinal)
            .ThenBy(a => a.Scenario, StringComparer.Ordinal)
            .ThenBy(a => a.ScenarioId)
            .ToList();

        var featureCount = scenarios.Select(a => a.Feature).Distinct().Count();
        return new ImpactResult(true, label, affectedStepDefs.Count, featureCount, scenarios);
    }

    private static (HashSet<int> StepDefs, string Label) ResolveTargets(
        MapDocument doc, ImpactQuery query,
        IReadOnlyDictionary<int, ClassRow> classById,
        IReadOnlyDictionary<int, int> classOfMethod,
        IReadOnlyDictionary<int, List<int>> stepDefsByMethod,
        List<(int Method, int Class)> usesEdges,
        List<(int Derived, int Base)> inheritsEdges,
        List<(int Method, int Endpoint)> endpointCalls)
    {
        var affected = new HashSet<int>();

        switch (query.Kind)
        {
            case ImpactTargetKind.Endpoint:
            {
                // Endpoints whose route contains the query → the methods that call them.
                var matched = doc.Endpoints
                    .Where(e => e.Route.Contains(query.Value, StringComparison.OrdinalIgnoreCase)).ToList();
                var matchedIds = matched.Select(e => e.Id).ToHashSet();
                var seedMethods = endpointCalls.Where(c => matchedIds.Contains(c.Endpoint)).Select(c => c.Method).ToHashSet();

                // Direct: the calling method IS a step definition.
                foreach (var m in seedMethods)
                    if (stepDefsByMethod.TryGetValue(m, out var sd)) affected.UnionWith(sd);

                // Indirect: the calling method lives in a client/helper class — step methods that use
                // that class (transitively, same reach rules as a class change) are affected too.
                var seedClasses = seedMethods
                    .Select(m => classOfMethod.TryGetValue(m, out var c) ? c : -1)
                    .Where(c => c >= 0).ToHashSet();
                var reach = ExpandReach(seedClasses, usesEdges, inheritsEdges, classOfMethod);
                var reachedMethods = usesEdges.Where(e => reach.Contains(e.Class)).Select(e => e.Method).ToHashSet();
                foreach (var s in doc.StepDefinitions)
                    if (reachedMethods.Contains(s.MethodId)) affected.Add(s.Id);

                var verbs = string.Join(", ", matched.Take(3).Select(e => $"{e.Verb} {e.Route}"));
                var suffix = matched.Count > 3 ? $" … +{matched.Count - 3} more" : "";
                return (affected, $"endpoint matching \"{query.Value}\" ({matched.Count} endpoint(s): {verbs}{suffix})");
            }
            case ImpactTargetKind.Step:
            {
                var matches = doc.StepDefinitions
                    .Where(s => s.Expression.Contains(query.Value, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var s in matches) affected.Add(s.Id);
                return (affected, $"step matching \"{query.Value}\" ({matches.Count} definition(s))");
            }
            case ImpactTargetKind.Method:
            {
                var methods = doc.Methods.Where(m => string.Equals(m.Name, query.Value, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var m in methods)
                    if (stepDefsByMethod.TryGetValue(m.Id, out var sd)) affected.UnionWith(sd);
                return (affected, $"method {query.Value} ({methods.Count} match(es))");
            }
            default: // Class
            {
                var seed = doc.Classes.Where(c => string.Equals(c.Name, query.Value, StringComparison.OrdinalIgnoreCase)).Select(c => c.Id).ToList();
                if (seed.Count == 0) return (affected, $"class {query.Value} (not found)");

                // inheritClosure: the class + everything that derives from it (their OWN step defs change).
                var inheritClosure = new HashSet<int>(seed);
                bool grew;
                do
                {
                    grew = false;
                    foreach (var (d, b) in inheritsEdges)
                        if (inheritClosure.Contains(b) && inheritClosure.Add(d)) grew = true;
                } while (grew);

                // reachSet: also classes whose methods use an affected class — so impact flows through
                // composed page objects (PageB ← PageA ← StepClass), transitively.
                var reach = ExpandReach(inheritClosure, usesEdges, inheritsEdges, classOfMethod);

                // A step def is affected if its class is directly changed (inheritClosure), or its method
                // uses a class in the reach set (precise: sibling step methods are NOT swept in).
                foreach (var s in doc.StepDefinitions)
                    if (inheritClosure.Contains(s.ClassId)) affected.Add(s.Id);
                var reachedMethods = usesEdges.Where(e => reach.Contains(e.Class)).Select(e => e.Method).ToHashSet();
                foreach (var s in doc.StepDefinitions)
                    if (reachedMethods.Contains(s.MethodId)) affected.Add(s.Id);

                var kind = classById.TryGetValue(seed[0], out var c0) ? c0.Kind : "?";
                var derivedNote = inheritClosure.Count > seed.Count ? $", {inheritClosure.Count - seed.Count} derived" : "";
                return (affected, $"class {query.Value} ({kind}{derivedNote})");
            }
        }
    }

    /// <summary>
    /// Transitive reach of a set of changed classes: classes deriving from them plus classes whose
    /// methods use any reached class — repeated to a fixpoint, so impact flows through composed
    /// page objects / client wrappers.
    /// </summary>
    private static HashSet<int> ExpandReach(
        HashSet<int> seed,
        List<(int Method, int Class)> usesEdges,
        List<(int Derived, int Base)> inheritsEdges,
        IReadOnlyDictionary<int, int> classOfMethod)
    {
        var reach = new HashSet<int>(seed);
        bool grew;
        do
        {
            grew = false;
            foreach (var (m, c) in usesEdges)
                if (reach.Contains(c) && classOfMethod.TryGetValue(m, out var owner) && reach.Add(owner)) grew = true;
            foreach (var (d, b) in inheritsEdges)
                if (reach.Contains(b) && reach.Add(d)) grew = true;
        } while (grew);
        return reach;
    }
}
