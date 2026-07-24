using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using TestAtlas.Core.Binding;
using TestAtlas.Core.Features;
using TestAtlas.Core.Model;
using DiagnosticSeverity = TestAtlas.Core.Model.DiagnosticSeverity;

namespace TestAtlas.Core.Indexing;

/// <summary>
/// Slice-1 indexer: loads a solution via MSBuildWorkspace and extracts Project/Class/Method
/// entities with file/line locations, capturing load diagnostics as first-class rows. Read-only,
/// offline, no AI (spec constraints). Classification is stubbed to <c>other</c> (see
/// <see cref="Classifier"/>); the Gherkin/binding layers arrive in later slices.
/// </summary>
public sealed class SolutionIndexer
{
    private static readonly string ToolVersion =
        typeof(SolutionIndexer).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private readonly Func<DateTime> _nowUtc;

    public SolutionIndexer(Func<DateTime>? nowUtc = null)
        => _nowUtc = nowUtc ?? (() => DateTime.UtcNow);

    public async Task<IndexResult> IndexAsync(IndexOptions options, CancellationToken ct = default)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        MsBuildGuard.EnsureRegistered();

        var fullPath = Path.GetFullPath(options.SolutionPath);
        var rootDir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        var diagnostics = new List<DiagnosticEntity>();
        var hasher = new InputHasher();

        // Raw load messages are captured here and only classified (severity + category) AFTER we
        // know which projects actually yielded content — see the reclassification pass below.
        var rawWorkspaceDiags = new List<(WorkspaceDiagnosticKind Kind, string Message)>();
        var rawLock = new object();

        void Verbose(string m) => options.VerboseLog?.Invoke(m);

        using var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, e) =>
        {
            lock (rawLock)
                rawWorkspaceDiags.Add((e.Diagnostic.Kind, e.Diagnostic.Message));
        };

        IReadOnlyList<Project> csProjects;
        int expectedCsProjects;
        try
        {
            if (fullPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                Verbose($"Opening solution {fullPath}");
                var solution = await workspace.OpenSolutionAsync(fullPath, cancellationToken: ct);
                csProjects = solution.Projects
                    .Where(p => p.Language == LanguageNames.CSharp)
                    .ToList();
                expectedCsProjects = CountCsharpProjectsInSln(fullPath);
            }
            else
            {
                Verbose($"Opening project {fullPath}");
                var project = await workspace.OpenProjectAsync(fullPath, cancellationToken: ct);
                csProjects = project.Language == LanguageNames.CSharp
                    ? new[] { project }
                    : Array.Empty<Project>();
                expectedCsProjects = 1;
            }
        }
        catch (Exception ex)
        {
            // A total failure to open is fatal — but still return a (project-less) result so the
            // CLI can write an empty map + diagnostic rather than crashing.
            diagnostics.Add(new DiagnosticEntity(
                DiagnosticSeverity.Error, "open_failed", ex.Message, Location: null));
            var failMeta = new MapMeta(ToolVersion, FormatUtc(_nowUtc()), fullPath, hasher.Compute());
            return new IndexResult(
                failMeta,
                Array.Empty<ProjectEntity>(),
                Array.Empty<ClassEntity>(),
                Array.Empty<MethodEntity>(),
                Array.Empty<StepDefinitionEntity>(),
                Array.Empty<FeatureEntity>(),
                Array.Empty<ScenarioEntity>(),
                Array.Empty<ScenarioStepEntity>(),
                Array.Empty<EndpointEntity>(),
                Array.Empty<EdgeEntity>(),
                diagnostics,
                IndexOutcome.Fatal);
        }

        // Apply include/exclude project filters (globs over name + path).
        var selected = csProjects
            .Where(p => IsSelected(p, options))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.FilePath ?? string.Empty, StringComparer.Ordinal)
            .ToList();

        // ---- Extract (into ID-less holders so IDs can be assigned in canonical order) ----
        var projectHolders = new List<ProjectHolder>();
        foreach (var project in selected)
        {
            ct.ThrowIfCancellationRequested();
            Verbose($"Indexing project {project.Name}");
            var holder = new ProjectHolder
            {
                Name = project.Name,
                Path = ToRelative(rootDir, project.FilePath),
                FullPath = project.FilePath is { } fp ? SafeFullPath(fp) : string.Empty,
                TargetFramework = ReadTargetFramework(project),
            };

            if (project.FilePath is { } pf && File.Exists(pf))
                hasher.Add(ToRelative(rootDir, pf), SafeRead(pf));

            var projectDir = project.FilePath is { } pdir
                ? Path.GetDirectoryName(SafeFullPath(pdir)) ?? rootDir
                : rootDir;

            foreach (var document in project.Documents)
            {
                ct.ThrowIfCancellationRequested();
                if (document.FilePath is not { } docPath) continue;
                if (!docPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsGeneratedOutput(projectDir, docPath)) continue; // skip obj/ + bin/ (spec §6 finding)

                var relDoc = ToRelative(rootDir, docPath);
                if (File.Exists(docPath)) hasher.Add(relDoc, SafeRead(docPath));

                await ExtractDocumentAsync(document, relDoc, holder, ct);
            }

            // Discover + parse .feature files under the project (spec §4/§5.1). Optional layer:
            // a parse failure is a warning, never fatal (G4).
            foreach (var featurePath in EnumerateFeatureFiles(projectDir))
            {
                ct.ThrowIfCancellationRequested();
                var content = SafeRead(featurePath);
                var relFeat = ToRelative(rootDir, featurePath);
                hasher.Add(relFeat, content);

                var parsed = GherkinFeatureParser.Parse(content);
                if (parsed is null)
                {
                    diagnostics.Add(new DiagnosticEntity(DiagnosticSeverity.Warning, "feature_parse_error",
                        $"Could not parse feature file '{featurePath}'.", relFeat));
                    continue;
                }
                holder.Features.Add(new FeatureHolder { RelPath = relFeat, Parsed = parsed });
            }

            projectHolders.Add(holder);
        }

        // ---- Reclassify workspace load messages now that we know which projects yielded content ----
        var loadedWithContent = new HashSet<string>(
            projectHolders.Where(h => h.Classes.Count > 0).Select(h => h.FullPath),
            StringComparer.OrdinalIgnoreCase);

        List<(WorkspaceDiagnosticKind Kind, string Message)> rawSnapshot;
        lock (rawLock) rawSnapshot = rawWorkspaceDiags.ToList();

        foreach (var (kind, message) in rawSnapshot)
        {
            var path = WorkspaceDiagnosticClassifier.ExtractProjectPath(message);
            var loadedOk = path is not null && loadedWithContent.Contains(SafeFullPath(path));

            var (code, severity) = WorkspaceDiagnosticClassifier.Classify(message, loadedOk);
            if (kind == WorkspaceDiagnosticKind.Warning)
                severity = DiagnosticSeverity.Warning; // a workspace warning never escalates to error

            diagnostics.Add(new DiagnosticEntity(severity, code, message, path));
        }

        // ---- Classify (spec §6): class kinds with an inheritance fixpoint, then method kinds ----
        var allClasses = projectHolders.SelectMany(p => p.Classes).ToList();
        var classifierOptions = ClassifierOptions.Default;

        foreach (var ch in allClasses)
            ch.Kind = ch.Facts is { } f ? Classifier.ClassifyClass(f, classifierOptions, _ => null) : Kinds.Other;

        // Resolve inherits-a-page-object / -api-client to a fixpoint (kinds only ever upgrade).
        for (var pass = 0; pass < 10; pass++)
        {
            var kindByName = allClasses
                .Where(c => c.Kind is Kinds.PageObject or Kinds.ApiClient)
                .GroupBy(c => c.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Kind, StringComparer.Ordinal);

            string? BaseKind(string? baseName)
                => baseName is not null && kindByName.TryGetValue(baseName, out var k) ? k : null;

            var changed = false;
            foreach (var ch in allClasses.Where(c => c.Kind == Kinds.Other && c.Facts is not null))
            {
                var k = Classifier.ClassifyClass(ch.Facts!, classifierOptions, BaseKind);
                if (k != Kinds.Other) { ch.Kind = k; changed = true; }
            }
            if (!changed) break;
        }

        foreach (var ch in allClasses)
            foreach (var mh in ch.Methods)
                mh.Kind = mh.Facts is { } mf ? Classifier.ClassifyMethod(mf, ch.Kind) : Kinds.Other;

        // The HTTP-executing types, resolved after the fixpoint — the gate for operation-level
        // endpoints: `new Wrapper<Request>()` is an operation only when Wrapper is an api_client.
        var apiClientNames = new HashSet<string>(
            allClasses.Where(c => c.Kind == Kinds.ApiClient).Select(c => c.Name), StringComparer.Ordinal);

        // Request descriptors: request-type name → its statically-declared (verb, route, target api). Lets
        // an operation surface the real POST /api/… route + verb instead of only the type name + guess.
        var requestEndpoints = new Dictionary<string, (string Verb, string Route, string? TargetApi)>(StringComparer.Ordinal);
        foreach (var ch in allClasses)
            if (ch.RequestEndpoint is { } re)
                requestEndpoints[ch.Name] = re;

        // ---- Assign deterministic IDs in canonical order ----
        var projects = new List<ProjectEntity>();
        var classes = new List<ClassEntity>();
        var methods = new List<MethodEntity>();
        var stepDefs = new List<StepDefinitionEntity>();
        var features = new List<FeatureEntity>();
        var scenarios = new List<ScenarioEntity>();
        var scenarioSteps = new List<ScenarioStepEntity>();
        var projectId = 0;
        var classId = 0;
        var methodId = 0;
        var stepDefId = 0;
        var featureId = 0;
        var scenarioId = 0;
        var scenarioStepId = 0;

        // Structural-edge inputs, gathered as IDs are assigned (resolved after the loop, spec §5.2).
        var classInherits = new List<(int ClassId, string BaseName)>();
        var methodUses = new List<(int MethodId, int ClassId, HashSet<string> Names)>();
        var classHolds = new List<(int ClassId, HashSet<string> Names)>(); // holds edges: class → collaborator field/property types
        var methodEndpointCalls = new List<(int MethodId, string Verb, string Route)>();

        foreach (var ph in projectHolders) // already ordered by (Name, Path)
        {
            projectId++;
            var pid = projectId;

            var orderedClasses = ph.Classes
                .OrderBy(c => c.Namespace, StringComparer.Ordinal)
                .ThenBy(c => c.Name, StringComparer.Ordinal)
                .ThenBy(c => c.FilePath, StringComparer.Ordinal)
                .ThenBy(c => c.LineStart)
                .ToList();

            var projClasses = new List<ClassEntity>();

            foreach (var ch in orderedClasses)
            {
                classId++;
                var cid = classId;
                var ce = new ClassEntity(
                    cid, pid, ch.Name, ch.Namespace, ch.BaseType, ch.Kind,
                    ch.FilePath, ch.LineStart, ch.LineEnd);
                projClasses.Add(ce);

                if (ch.Facts?.BaseTypeName is { Length: > 0 } baseName)
                    classInherits.Add((cid, baseName));

                if (ch.HeldTypeNames.Count > 0)
                    classHolds.Add((cid, ch.HeldTypeNames));

                var orderedMethods = ch.Methods
                    .OrderBy(m => m.Name, StringComparer.Ordinal)
                    .ThenBy(m => m.Signature, StringComparer.Ordinal)
                    .ThenBy(m => m.LineStart)
                    .ToList();

                foreach (var mh in orderedMethods)
                {
                    methodId++;
                    var mid = methodId;
                    methods.Add(new MethodEntity(
                        mid, cid, pid, mh.Name, mh.Signature, mh.Visibility, mh.Kind,
                        mh.FilePath, mh.LineStart, mh.LineEnd));

                    if (mh.UsedTypeNames.Count > 0)
                        methodUses.Add((mid, cid, mh.UsedTypeNames));

                    foreach (var (verb, route) in mh.EndpointCalls)
                        methodEndpointCalls.Add((mid, verb, route));

                    // Operation-level endpoints: a request type handed to an HTTP-executing generic
                    // wrapper. The request-type name is the operation identity (no URL at the call
                    // site). The verb is the request descriptor's real Method when it declares one,
                    // else inferred from the leading verb word (spec §5.1).
                    foreach (var (wrapper, request) in mh.OperationCandidates)
                        if (apiClientNames.Contains(wrapper))
                        {
                            var verb = requestEndpoints.TryGetValue(request, out var re)
                                ? re.Verb : SyntaxScan.VerbFromOperationName(request);
                            methodEndpointCalls.Add((mid, verb, request));
                        }

                    foreach (var sb in mh.StepBindings
                        .OrderBy(s => s.Keyword, StringComparer.Ordinal)
                        .ThenBy(s => s.Expression, StringComparer.Ordinal))
                    {
                        stepDefId++;
                        stepDefs.Add(new StepDefinitionEntity(
                            stepDefId, mid, cid, pid, sb.Keyword, sb.Expression, sb.ExpressionKind,
                            sb.Parameters, mh.FilePath, mh.LineStart));
                    }
                }
            }

            projects.Add(new ProjectEntity(
                pid, ph.Name, ph.Path, ph.TargetFramework, Classifier.SummariseProject(projClasses)));
            classes.AddRange(projClasses);

            // Features / scenarios / scenario-steps (deterministic: by feature path, then file order).
            foreach (var fh in ph.Features.OrderBy(f => f.RelPath, StringComparer.Ordinal))
            {
                featureId++;
                var fid = featureId;
                features.Add(new FeatureEntity(
                    fid, pid, fh.Parsed.Name, fh.Parsed.Description, string.Join(" ", fh.Parsed.Tags), fh.RelPath));

                foreach (var sc in fh.Parsed.Scenarios)
                {
                    scenarioId++;
                    var sid = scenarioId;
                    // Tag inheritance materialised (spec Q4): feature tags + the scenario's own tags.
                    var scTags = string.Join(" ", fh.Parsed.Tags.Concat(sc.OwnTags));
                    scenarios.Add(new ScenarioEntity(
                        sid, fid, pid, sc.Name, sc.Kind, scTags, sc.ExampleRowCount, fh.RelPath, sc.Line));

                    var ordinal = 0;
                    foreach (var st in sc.Steps)
                    {
                        scenarioStepId++;
                        scenarioSteps.Add(new ScenarioStepEntity(
                            scenarioStepId, sid, pid, st.Keyword, st.Text, ordinal++,
                            st.HasDocString, st.HasDataTable, fh.RelPath, st.Line));
                    }
                }
            }
        }

        // ---- binds_to: resolve each scenario step against its project's step definitions (spec §5.2) ----
        var edges = BuildBindingEdges(stepDefs, scenarioSteps);

        // ---- inherits / uses_type: the structural graph linking classes + their collaborators ----
        edges.AddRange(BuildStructuralEdges(classes, classInherits, methodUses, classHolds));

        // ---- endpoints: dedupe (verb, route) solution-wide; call sites become calls_endpoint edges ----
        var endpoints = methodEndpointCalls
            .Select(c => (c.Verb, c.Route))
            .Distinct()
            .OrderBy(e => e.Route, StringComparer.Ordinal)
            .ThenBy(e => e.Verb, StringComparer.Ordinal)
            .Select((e, i) =>
            {
                // e.Route is the operation's request-type name; enrich it with the descriptor's real
                // route + API bucket when that type declared them (null for URL-route endpoints).
                var (path, targetApi) = requestEndpoints.TryGetValue(e.Route, out var re)
                    ? (re.Route, re.TargetApi) : (null, null);
                return new EndpointEntity(i + 1, e.Verb, e.Route, path, targetApi);
            })
            .ToList();
        var endpointId = endpoints.ToDictionary(e => (e.Verb, e.Route), e => e.Id);
        edges.AddRange(methodEndpointCalls
            .Select(c => (c.MethodId, EndpointId: endpointId[(c.Verb, c.Route)]))
            .Distinct()
            .OrderBy(x => x.MethodId).ThenBy(x => x.EndpointId)
            .Select(x => new EdgeEntity(RefKinds.Method, x.MethodId, RefKinds.Endpoint, x.EndpointId,
                EdgeKinds.CallsEndpoint, "")));

        // ---- Outcome (spec §7 exit-code contract) ----
        var loadedCount = selected.Count;
        var anyLoadError = diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
        if (loadedCount < expectedCsProjects && expectedCsProjects > 0)
        {
            diagnostics.Add(new DiagnosticEntity(
                DiagnosticSeverity.Error,
                "project_not_loaded",
                $"{expectedCsProjects - loadedCount} of {expectedCsProjects} C# project(s) failed to load.",
                Location: null));
            anyLoadError = true;
        }

        var outcome = csProjects.Count == 0
            ? IndexOutcome.Fatal
            : anyLoadError
                ? IndexOutcome.CompletedWithWarnings
                : IndexOutcome.Success;

        var meta = new MapMeta(ToolVersion, FormatUtc(_nowUtc()), fullPath, hasher.Compute());
        return new IndexResult(
            meta, projects, classes, methods, stepDefs, features, scenarios, scenarioSteps, endpoints, edges, diagnostics, outcome);
    }

    /// <summary>
    /// Resolve each scenario step against the solution's step definitions using the tested matcher,
    /// producing binds_to (exact/ambiguous) and unbound edges (spec §5.2). Matching is
    /// **solution-wide** — a feature's steps bind to definitions in ANY project, because large suites
    /// keep step definitions in shared library projects referenced by the feature projects; scoping
    /// per-project reported those (the majority) as falsely unbound. Bindings are precompiled once.
    /// And/But keywords inherit the running primary keyword within a scenario (kept for the effective
    /// keyword, though matching itself is keyword-agnostic — see StepMatcher).
    /// </summary>
    private static List<EdgeEntity> BuildBindingEdges(
        IReadOnlyList<StepDefinitionEntity> stepDefs, IReadOnlyList<ScenarioStepEntity> scenarioSteps)
    {
        var edges = new List<EdgeEntity>();
        if (scenarioSteps.Count == 0) return edges;

        // One solution-wide candidate set, in stepDef id order (deterministic).
        var compiled = stepDefs
            .Select(sd => StepMatcher.Compile(new StepBinding(
                ToBindingKeyword(sd.Keyword), sd.Expression, ToExpressionKind(sd.ExpressionKind), sd.Id.ToString())))
            .Where(c => c is not null).Select(c => c!).ToList();

        foreach (var scenarioGroup in scenarioSteps.GroupBy(s => s.ScenarioId))
        {
            StepKeyword? previousPrimary = null;
            foreach (var step in scenarioGroup.OrderBy(s => s.Ordinal))
            {
                var keyword = StepKeywords.Resolve(step.Keyword, previousPrimary);
                previousPrimary = keyword;

                var result = StepMatcher.Match(new ScenarioStepInput(keyword, step.Text), compiled);

                if (result.Confidence == MatchConfidence.Unbound)
                {
                    edges.Add(new EdgeEntity(RefKinds.ScenarioStep, step.Id, RefKinds.StepDefinition, null, EdgeKinds.Unbound, ""));
                    continue;
                }

                var confidence = result.Confidence == MatchConfidence.Ambiguous ? BindConfidence.Ambiguous : BindConfidence.Exact;
                foreach (var match in result.Matches.OrderBy(m => int.Parse(m.Binding.Reference)))
                    edges.Add(new EdgeEntity(
                        RefKinds.ScenarioStep, step.Id, RefKinds.StepDefinition, int.Parse(match.Binding.Reference),
                        EdgeKinds.BindsTo, confidence));
            }
        }

        return edges;
    }

    /// <summary>
    /// Resolve the syntactic structural signals into edges (spec §5.2): a class's base type to a
    /// solution class (<c>inherits</c>), and a method's used type-names to the page-object / API-client
    /// classes it drives (<c>uses_type</c>). Name resolution is by simple class name across the whole
    /// solution; a name matching more than one class is recorded as <c>ambiguous</c>. External types
    /// (base classes / collaborators outside the solution) simply produce no edge. Emitted in a
    /// canonical order so two runs over identical input are byte-identical.
    /// </summary>
    private static List<EdgeEntity> BuildStructuralEdges(
        IReadOnlyList<ClassEntity> classes,
        IReadOnlyList<(int ClassId, string BaseName)> classInherits,
        IReadOnlyList<(int MethodId, int ClassId, HashSet<string> Names)> methodUses,
        IReadOnlyList<(int ClassId, HashSet<string> Names)> classHolds)
    {
        var edges = new List<EdgeEntity>();

        var idsByName = classes
            .GroupBy(c => c.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).OrderBy(i => i).ToList(), StringComparer.Ordinal);

        // uses_type / holds targets are deliberately limited to the collaborators the map is about — page
        // objects and API clients — which keeps the edge set signal-rich and bounded on huge solutions.
        var collaboratorIdsByName = classes
            .Where(c => c.Kind is Kinds.PageObject or Kinds.ApiClient)
            .GroupBy(c => c.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).OrderBy(i => i).ToList(), StringComparer.Ordinal);

        foreach (var (classId, baseName) in classInherits)
        {
            if (!idsByName.TryGetValue(baseName, out var all)) continue;
            var targets = all.Where(t => t != classId).ToList(); // never a self-edge
            if (targets.Count == 0) continue;
            var conf = targets.Count == 1 ? BindConfidence.Exact : BindConfidence.Ambiguous;
            foreach (var t in targets)
                edges.Add(new EdgeEntity(RefKinds.Class, classId, RefKinds.Class, t, EdgeKinds.Inherits, conf));
        }

        foreach (var (methodId, classId, names) in methodUses)
        {
            // (targetClassId → ambiguous?) so a target reached through an ambiguous name is marked so.
            var targets = new SortedDictionary<int, bool>();
            foreach (var name in names)
            {
                if (!collaboratorIdsByName.TryGetValue(name, out var ids)) continue;
                var real = ids.Where(id => id != classId).ToList(); // ignore self-references
                var ambiguous = real.Count > 1;
                foreach (var id in real)
                    targets[id] = targets.TryGetValue(id, out var was) ? was || ambiguous : ambiguous;
            }
            foreach (var (targetId, ambiguous) in targets)
                edges.Add(new EdgeEntity(RefKinds.Method, methodId, RefKinds.Class, targetId, EdgeKinds.UsesType,
                    ambiguous ? BindConfidence.Ambiguous : BindConfidence.Exact));
        }

        // holds: a class declares a collaborator as a field/property/return/param type. Captures the
        // aggregator/DI shape (`WorkflowApiService Workflow { get; } = new(context);`) that a name-based
        // construction scan misses — target-typed `new()` has no type name, but the member's declared type
        // does. This is the "referenced / live" signal that separates held collaborators from the genuinely
        // orphaned (reached by nothing) in the report's unused list.
        foreach (var (classId, names) in classHolds)
        {
            var targets = new SortedSet<int>();
            foreach (var name in names)
                if (collaboratorIdsByName.TryGetValue(name, out var ids))
                    foreach (var id in ids.Where(id => id != classId)) // never a self-edge
                        targets.Add(id);
            foreach (var targetId in targets)
                edges.Add(new EdgeEntity(RefKinds.Class, classId, RefKinds.Class, targetId, EdgeKinds.Holds, BindConfidence.Exact));
        }

        return edges
            .OrderBy(e => e.EdgeKind, StringComparer.Ordinal)
            .ThenBy(e => e.FromId)
            .ThenBy(e => e.ToId ?? 0)
            .ToList();
    }

    private static BindingKeyword ToBindingKeyword(string keyword) => keyword switch
    {
        "Given" => BindingKeyword.Given,
        "When" => BindingKeyword.When,
        "Then" => BindingKeyword.Then,
        _ => BindingKeyword.StepDefinition,
    };

    private static ExpressionKind ToExpressionKind(string kind)
        => kind == ExpressionKinds.CucumberExpression ? ExpressionKind.CucumberExpression : ExpressionKind.Regex;

    private static IEnumerable<string> EnumerateFeatureFiles(string projectDir)
    {
        try
        {
            return Directory.EnumerateFiles(projectDir, "*.feature", SearchOption.AllDirectories)
                .Where(f => !IsGeneratedOutput(projectDir, f))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static async Task ExtractDocumentAsync(
        Document document, string relDoc, ProjectHolder holder, CancellationToken ct)
    {
        try
        {
            if (await document.GetSyntaxRootAsync(ct) is not { } root) return;
            var tree = root.SyntaxTree;
            var exprDefault = SyntaxScan.FrameworkExpressionDefault(root);

            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                // LineStart points at the declared name (the `class X` line), not any leading
                // attribute; LineEnd is the closing brace. Kind is assigned in a later pass.
                var ch = new ClassHolder
                {
                    Name = type.Identifier.ValueText,
                    Namespace = ResolveNamespace(type),
                    BaseType = type.BaseList?.Types.FirstOrDefault()?.Type.ToString(),
                    Facts = SyntaxScan.GatherClassFacts(type),
                    RequestEndpoint = SyntaxScan.RequestEndpointOf(type),
                    HeldTypeNames = SyntaxScan.HeldTypeNames(type),
                    FilePath = relDoc,
                    LineStart = tree.GetLineSpan(type.Identifier.Span).StartLinePosition.Line + 1,
                    LineEnd = tree.GetLineSpan(type.Span).EndLinePosition.Line + 1,
                };

                foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
                {
                    var mh = new MethodHolder
                    {
                        Name = method.Identifier.ValueText,
                        Signature = BuildSignature(method),
                        Visibility = ResolveVisibility(method.Modifiers, memberDefaultPrivate: true),
                        Facts = SyntaxScan.GatherMethodFacts(method),
                        FilePath = relDoc,
                        LineStart = tree.GetLineSpan(method.Identifier.Span).StartLinePosition.Line + 1,
                        LineEnd = tree.GetLineSpan(method.Span).EndLinePosition.Line + 1,
                        UsedTypeNames = SyntaxScan.UsedTypeNames(method, type),
                        EndpointCalls = SyntaxScan.EndpointCalls(method, type),
                        OperationCandidates = SyntaxScan.GenericOperationCandidates(method),
                    };

                    var parameters = string.Join(", ", method.ParameterList.Parameters
                        .Select(p => $"{p.Type} {p.Identifier.ValueText}".Trim()));
                    foreach (var (keyword, expression) in SyntaxScan.StepBindings(method))
                    {
                        mh.StepBindings.Add(new StepBindingHolder
                        {
                            Keyword = keyword,
                            Expression = expression,
                            ExpressionKind = Classifier.DetectExpressionKind(expression, exprDefault),
                            Parameters = parameters,
                        });
                    }

                    ch.Methods.Add(mh);
                }

                holder.Classes.Add(ch);
            }
        }
        catch
        {
            // A single malformed document must never break the run (spec G4/G2). Record and move on.
            holder.Classes.Add(new ClassHolder
            {
                Name = "<parse-error>",
                Namespace = string.Empty,
                Kind = Kinds.Other,
                FilePath = relDoc,
                LineStart = 0,
                LineEnd = 0,
            });
        }
    }

    /// <summary>True if the document lives under the project's <c>obj/</c> or <c>bin/</c> tree.</summary>
    private static bool IsGeneratedOutput(string projectDir, string docFullPath)
    {
        try
        {
            var rel = Path.GetRelativePath(projectDir, SafeFullPath(docFullPath)).Replace('\\', '/');
            return rel.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
                || rel.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                || rel.Contains("/bin/", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveNamespace(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is BaseNamespaceDeclarationSyntax ns)
                return ns.Name.ToString();
        }
        return string.Empty;
    }

    private static string BuildSignature(MethodDeclarationSyntax method)
    {
        var types = method.ParameterList.Parameters
            .Select(p => p.Type?.ToString() ?? "?");
        return $"{method.Identifier.ValueText}({string.Join(", ", types)})";
    }

    private static string ResolveVisibility(SyntaxTokenList modifiers, bool memberDefaultPrivate)
    {
        var isPublic = modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));
        var isProtected = modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword));
        var isInternal = modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));
        var isPrivate = modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword));

        if (isPublic) return "public";
        if (isProtected && isInternal) return "protected internal";
        if (isProtected) return "protected";
        if (isInternal) return "internal";
        if (isPrivate) return "private";
        return memberDefaultPrivate ? "private" : "internal";
    }

    private static string? ReadTargetFramework(Project project)
    {
        // ParseOptions/CompilationOptions don't carry the TFM cleanly; read it from the csproj text.
        if (project.FilePath is not { } path || !File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path);
            var single = Regex.Match(text, "<TargetFramework>([^<]+)</TargetFramework>");
            if (single.Success) return single.Groups[1].Value.Trim();
            var multi = Regex.Match(text, "<TargetFrameworks>([^<]+)</TargetFrameworks>");
            if (multi.Success) return multi.Groups[1].Value.Trim();
        }
        catch
        {
            // Non-fatal.
        }
        return null;
    }

    private static int CountCsharpProjectsInSln(string slnPath)
    {
        try
        {
            var count = 0;
            foreach (var line in File.ReadLines(slnPath))
            {
                // Project("{GUID}") = "Name", "relative\path.csproj", "{GUID}"
                var m = Regex.Match(line, "^Project\\(\"\\{[^}]+\\}\"\\)\\s*=\\s*\"[^\"]*\",\\s*\"([^\"]+)\"");
                if (m.Success && m.Groups[1].Value.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }

    private static string ToRelative(string rootDir, string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        try
        {
            return Path.GetRelativePath(rootDir, path).Replace('\\', '/');
        }
        catch
        {
            return path.Replace('\\', '/');
        }
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return string.Empty; }
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private static bool IsSelected(Project project, IndexOptions options)
    {
        var name = project.Name;
        var path = project.FilePath ?? string.Empty;

        if (options.Include.Count > 0 &&
            !options.Include.Any(g => Glob.IsMatch(g, name, path)))
            return false;

        if (options.Exclude.Any(g => Glob.IsMatch(g, name, path)))
            return false;

        return true;
    }

    private static string FormatUtc(DateTime utc)
        => utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

    // ---- ID-less extraction holders ----
    private sealed class ProjectHolder
    {
        public string Name = string.Empty;
        public string Path = string.Empty;
        public string FullPath = string.Empty;
        public string? TargetFramework;
        public List<ClassHolder> Classes { get; } = new();
        public List<FeatureHolder> Features { get; } = new();
    }

    private sealed class FeatureHolder
    {
        public string RelPath = string.Empty;
        public ParsedFeature Parsed = null!;
    }

    private sealed class ClassHolder
    {
        public string Name = string.Empty;
        public string Namespace = string.Empty;
        public string? BaseType;
        public string Kind = Kinds.Other;
        public ClassFacts? Facts;
        public (string Verb, string Route, string? TargetApi)? RequestEndpoint; // set when the type is a request descriptor
        public HashSet<string> HeldTypeNames = new(StringComparer.Ordinal); // collaborators held as field/property/return/param types
        public string FilePath = string.Empty;
        public int LineStart;
        public int LineEnd;
        public List<MethodHolder> Methods { get; } = new();
    }

    private sealed class MethodHolder
    {
        public string Name = string.Empty;
        public string Signature = string.Empty;
        public string Visibility = "private";
        public string Kind = Kinds.Other;
        public MethodFacts? Facts;
        public string FilePath = string.Empty;
        public int LineStart;
        public int LineEnd;
        public HashSet<string> UsedTypeNames = new(StringComparer.Ordinal);
        public List<(string Verb, string Route)> EndpointCalls = new();
        public List<(string Wrapper, string Request)> OperationCandidates = new();
        public List<StepBindingHolder> StepBindings { get; } = new();
    }

    private sealed class StepBindingHolder
    {
        public string Keyword = string.Empty;
        public string Expression = string.Empty;
        public string ExpressionKind = ExpressionKinds.Regex;
        public string Parameters = string.Empty;
    }
}
