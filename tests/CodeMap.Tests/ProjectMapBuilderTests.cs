using TestAtlas.Core.Model;
using TestAtlas.Core.Reporting;
using TestAtlas.Core.Storage;
using Xunit;

namespace TestAtlas.Tests;

/// <summary>
/// Pure builder tests over a hand-built <see cref="MapDocument"/>: the project graph is derived from
/// cross-project edges only, arrows point consumer→provider, and the page is self-contained + escaped.
/// </summary>
public sealed class ProjectMapBuilderTests
{
    // Feature.A (project 1) binds a step defined in Lib.B (project 2) — a cross-project dependency.
    // The inherits edge inside project 1 must NOT create a project link.
    private static MapDocument Doc() => new()
    {
        UserVersion = MapSchema.Version,
        Meta = new Dictionary<string, string> { [MapSchema.MetaSolutionPath] = "/x/My.sln" },
        Projects = new[]
        {
            new ProjectRow(1, "Feature.A", "A.csproj", "net8.0", Kinds.BddTests),
            new ProjectRow(2, "Lib.B <b>", "B.csproj", "net8.0", Kinds.SharedLibrary),
        },
        Classes = new[]
        {
            new ClassRow(1, 1, "C1", "N", null, Kinds.StepClass, "A.cs", 1, 3),
            new ClassRow(2, 1, "C2", "N", null, Kinds.Other, "A.cs", 1, 3),
        },
        Methods = new[] { new MethodRow(1, 1, 1, "M", "M()", "public", Kinds.Other, "A.cs", 2, 2) },
        StepDefinitions = new[] { new StepDefinitionRow(1, 1, 1, 2, "Given", "x", ExpressionKinds.Regex, "", "B.cs", 2) },
        ScenarioSteps = new[] { new ScenarioStepRow(1, 1, 1, "Given", "x", 0, false, false, "A.feature", 1) },
        Edges = new[]
        {
            new EdgeRow(RefKinds.ScenarioStep, 1, RefKinds.StepDefinition, 1, EdgeKinds.BindsTo, BindConfidence.Exact), // A→B
            new EdgeRow(RefKinds.Class, 1, RefKinds.Class, 2, EdgeKinds.Inherits, BindConfidence.Exact),               // in-project (ignored)
        },
    };

    [Fact]
    public void Emits_a_self_contained_svg_page()
    {
        var html = ProjectMapBuilder.Build(Doc());
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<svg", html);
        Assert.DoesNotContain("<link", html);
        Assert.DoesNotContain("src=\"http", html);
        Assert.Contains("Feature.A", html);
    }

    [Fact]
    public void Draws_one_directed_edge_from_consumer_to_provider()
    {
        var html = ProjectMapBuilder.Build(Doc());
        // Exactly one project dependency: Feature.A (1) → Lib.B (2). The in-project inherits edge is excluded.
        Assert.Contains("class=\"edge\" data-a=\"1\" data-b=\"2\"", html);
        Assert.DoesNotContain("data-a=\"2\" data-b=\"1\"", html);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "class=\"node ").Count);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "class=\"edge\""));
    }

    [Fact]
    public void Escapes_project_names()
    {
        var html = ProjectMapBuilder.Build(Doc());
        Assert.Contains("Lib.B &lt;b&gt;", html);
        Assert.DoesNotContain("Lib.B <b>", html);
    }

    [Fact]
    public void Has_theme_toggle_collapsible_header_and_a_working_hover_highlight_rule()
    {
        var html = ProjectMapBuilder.Build(Doc());

        // Manual light/dark: a theme toggle + the data-theme overrides + an early theme-init script.
        Assert.Contains("toggleTheme()", html);
        Assert.Contains("[data-theme=\"dark\"]", html);
        Assert.Contains("[data-theme=\"light\"]", html);
        Assert.Contains("testatlas:theme", html);

        // Full-view: a collapsible floating panel.
        Assert.Contains("toggleHeader()", html);
        Assert.Contains("id=\"collapseBtn\"", html);

        // Hover-highlight specificity fix: the .active rule is scoped under svg.has-focus so it beats
        // the dimming rule (the earlier bug was that dimming out-ranked the highlight).
        Assert.Contains("svg.has-focus .edge.active", html);
        Assert.Contains("svg.has-focus .node.active", html);
    }

    [Fact]
    public void Handles_a_map_with_no_projects()
    {
        var html = ProjectMapBuilder.Build(new MapDocument { UserVersion = MapSchema.Version });
        Assert.Contains("No projects", html);
        Assert.Contains("</html>", html);
    }
}
