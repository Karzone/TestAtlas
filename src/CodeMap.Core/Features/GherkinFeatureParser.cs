using GherkinAst = Gherkin.Ast;

namespace TestAtlas.Core.Features;

/// <summary>A parsed feature, decoupled from the Gherkin package AST (spec §5.1).</summary>
public sealed record ParsedFeature(
    string Name,
    string Description,
    IReadOnlyList<string> Tags,
    IReadOnlyList<ParsedScenario> Scenarios);

/// <summary>A parsed scenario or scenario outline.</summary>
public sealed record ParsedScenario(
    string Name,
    string Kind,                 // "scenario" | "scenario_outline"
    IReadOnlyList<string> OwnTags,
    int ExampleRowCount,
    int Line,
    IReadOnlyList<ParsedStep> Steps);

/// <summary>An ordered step within a scenario.</summary>
public sealed record ParsedStep(
    string Keyword,              // Given | When | Then | And | But | * (trimmed)
    string Text,
    bool HasDocString,
    bool HasDataTable,
    int Line);

/// <summary>
/// Wraps the official Cucumber Gherkin parser. Any parse failure yields <c>null</c> (spec G4: the
/// Gherkin layer is optional and never breaks indexing) — the caller records a diagnostic.
/// </summary>
public static class GherkinFeatureParser
{
    public static ParsedFeature? Parse(string content)
    {
        try
        {
            var doc = new global::Gherkin.Parser().Parse(new StringReader(content ?? string.Empty));
            var feature = doc.Feature;
            if (feature is null) return null;

            var scenarios = new List<ParsedScenario>();
            CollectScenarios(feature.Children, scenarios);

            return new ParsedFeature(
                feature.Name ?? string.Empty,
                feature.Description ?? string.Empty,
                feature.Tags.Select(t => t.Name).ToList(),
                scenarios);
        }
        catch
        {
            return null;
        }
    }

    private static void CollectScenarios(IEnumerable<GherkinAst.IHasLocation> children, List<ParsedScenario> into)
    {
        foreach (var child in children)
        {
            switch (child)
            {
                case GherkinAst.Scenario sc:
                    into.Add(ToScenario(sc));
                    break;
                case GherkinAst.Rule rule:
                    CollectScenarios(rule.Children, into); // scenarios may be nested under a Rule
                    break;
                // Background steps are shared setup, not a scenario — skipped for now.
            }
        }
    }

    private static ParsedScenario ToScenario(GherkinAst.Scenario sc)
    {
        var examples = sc.Examples?.ToList() ?? new List<GherkinAst.Examples>();
        var isOutline = examples.Count > 0;
        var exampleRows = examples.Sum(e => e.TableBody?.Count() ?? 0);

        var steps = sc.Steps.Select(st => new ParsedStep(
            Keyword: (st.Keyword ?? string.Empty).Trim(),
            Text: st.Text ?? string.Empty,
            HasDocString: st.Argument is GherkinAst.DocString,
            HasDataTable: st.Argument is GherkinAst.DataTable,
            Line: st.Location.Line)).ToList();

        return new ParsedScenario(
            Name: sc.Name ?? string.Empty,
            Kind: isOutline ? "scenario_outline" : "scenario",
            OwnTags: sc.Tags.Select(t => t.Name).ToList(),
            ExampleRowCount: exampleRows,
            Line: sc.Location.Line,
            Steps: steps);
    }
}
