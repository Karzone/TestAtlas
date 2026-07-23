using TestAtlas.Core.Model;
using TestAtlas.Core.Reporting;
using TestAtlas.Core.Storage;
using Xunit;

namespace TestAtlas.Tests;

/// <summary>
/// Pure builder tests over a hand-built <see cref="MapDocument"/> (no Roslyn): the report reflects
/// real binding state, computes coverage, and — critically — HTML-escapes all map-derived text.
/// </summary>
public sealed class HtmlReportBuilderTests
{
    // A doc with one bound, one unbound, and one ambiguous step, plus a hostile string to escape.
    private static MapDocument Doc() => new()
    {
        UserVersion = MapSchema.Version,
        Meta = new Dictionary<string, string>
        {
            [MapSchema.MetaSolutionPath] = "/repo/My.sln",
            [MapSchema.MetaGeneratedUtc] = "2020-01-01T00:00:00Z",
            [MapSchema.MetaToolVersion] = "1.2.3",
        },
        Projects = new[] { new ProjectRow(1, "Proj", "Proj.csproj", "net8.0", Kinds.BddTests) },
        Classes = new[] { new ClassRow(1, 1, "Steps", "N", null, Kinds.StepClass, "Steps.cs", 1, 9) },
        StepDefinitions = new[]
        {
            new StepDefinitionRow(1, 1, 1, 1, "Given", "a user named (.*)", ExpressionKinds.Regex, "", "Steps.cs", 3),
            new StepDefinitionRow(2, 1, 1, 1, "Given", "the system is ready", ExpressionKinds.CucumberExpression, "", "Steps.cs", 4),
            new StepDefinitionRow(3, 1, 1, 1, "Given", "the system is (.*)", ExpressionKinds.Regex, "", "Steps.cs", 5),
        },
        Features = new[] { new FeatureRow(1, 1, "Login <b>", "desc", "@smoke", "Login.feature") },
        Scenarios = new[] { new ScenarioRow(1, 1, 1, "Sign in", "scenario", "@smoke", 0, "Login.feature", 3) },
        ScenarioSteps = new[]
        {
            new ScenarioStepRow(1, 1, 1, "Given", "a user named Alice", 0, false, false, "Login.feature", 4),
            new ScenarioStepRow(2, 1, 1, "And", "pigs can fly", 1, false, false, "Login.feature", 5),
            new ScenarioStepRow(3, 1, 1, "Given", "the system is ready", 2, false, false, "Login.feature", 6),
        },
        Edges = new[]
        {
            new EdgeRow(RefKinds.ScenarioStep, 1, RefKinds.StepDefinition, 1, EdgeKinds.BindsTo, BindConfidence.Exact),
            new EdgeRow(RefKinds.ScenarioStep, 2, RefKinds.StepDefinition, null, EdgeKinds.Unbound, ""),
            new EdgeRow(RefKinds.ScenarioStep, 3, RefKinds.StepDefinition, 2, EdgeKinds.BindsTo, BindConfidence.Ambiguous),
            new EdgeRow(RefKinds.ScenarioStep, 3, RefKinds.StepDefinition, 3, EdgeKinds.BindsTo, BindConfidence.Ambiguous),
        },
    };

    [Fact]
    public void Emits_a_self_contained_document()
    {
        var html = HtmlReportBuilder.Build(Doc());
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("</html>", html);
        Assert.DoesNotContain("<link", html);       // no external stylesheet
        Assert.DoesNotContain("src=\"http", html);   // no external script/img
    }

    [Fact]
    public void Renders_each_binding_state_with_its_evidence()
    {
        var html = HtmlReportBuilder.Build(Doc());

        // Bound → shows the resolved step-definition expression and its location.
        Assert.Contains("a user named (.*)", html);
        Assert.Contains("Steps.cs:3", html);

        // Unbound → the honest "no match" label.
        Assert.Contains("pigs can fly", html);
        Assert.Contains("no matching step definition", html);

        // Ambiguous → both candidates surfaced.
        Assert.Contains("2 candidates", html);
        Assert.Contains("the system is (.*)", html);
        Assert.Contains("the system is ready", html);
    }

    [Fact]
    public void Computes_binding_coverage() // 2 of 3 steps reached a definition = 67%
    {
        var html = HtmlReportBuilder.Build(Doc());
        Assert.Contains("67<span>%</span>", html);
    }

    [Fact]
    public void Escapes_map_derived_text_so_content_cannot_inject_markup()
    {
        var html = HtmlReportBuilder.Build(Doc());
        // The feature name literally contains "<b>"; it must appear escaped, never as a live tag.
        Assert.Contains("Login &lt;b&gt;", html);
        Assert.DoesNotContain("Login <b>", html);
    }

    [Fact]
    public void Handles_an_empty_map_without_throwing()
    {
        var html = HtmlReportBuilder.Build(new MapDocument { UserVersion = MapSchema.Version });
        Assert.Contains("<!doctype html>", html);
        Assert.Contains("0<span>%</span>", html); // no steps → 0% coverage, no divide-by-zero
    }
}
