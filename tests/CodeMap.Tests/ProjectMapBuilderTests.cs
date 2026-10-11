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
            new ClassRow(3, 2, "BSteps", "N", null, Kinds.StepClass, "B.cs", 1, 3), // hosts the step def in Lib.B
        },
        Methods = new[] { new MethodRow(1, 3, 2, "M", "M()", "public", Kinds.StepDefinitionMethod, "B.cs", 2, 2) },
        StepDefinitions = new[] { new StepDefinitionRow(1, 1, 3, 2, "Given", "x", ExpressionKinds.Regex, "", "B.cs", 2) },
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
    public void A_project_reference_with_no_semantic_edge_renders_as_a_dashed_build_reference()
    {
        var doc = new MapDocument
        {
            UserVersion = MapSchema.Version,
            Meta = new Dictionary<string, string> { [MapSchema.MetaSolutionPath] = "/x/My.sln" },
            Projects = new[]
            {
                new ProjectRow(1, "UI.Tests", "U.csproj", "net8.0", Kinds.BddTests),
                new ProjectRow(2, "WebDriver.Support", "W.csproj", "net8.0", Kinds.SharedLibrary),
            },
            // Only a ProjectReference — no binds_to/uses_type/inherits (an infra lib consumed via DI).
            Edges = new[] { new EdgeRow(RefKinds.Project, 1, RefKinds.Project, 2, EdgeKinds.References, "") },
        };
        var html = ProjectMapBuilder.Build(doc);

        // The infra lib is connected (not an isolated 0-degree node) via a distinctly-styled dashed edge.
        Assert.Contains("class=\"edge ref\" data-a=\"1\" data-b=\"2\"", html);
        Assert.Contains("<b>1</b><span>build-only reference</span>", html);   // called out in the header
        Assert.Contains("<b>0</b><span>code dependencies</span>", html);      // …not counted as a code dependency
        Assert.Contains("\"k\":{},\"ref\":true", html);                         // flagged in the blob for the panel
    }

    [Fact]
    public void Escapes_project_names()
    {
        var html = ProjectMapBuilder.Build(Doc());
        Assert.Contains("Lib.B &lt;b&gt;", html);
        Assert.DoesNotContain("Lib.B <b>", html);
    }

    [Fact]
    public void Has_theme_toggle_search_zoom_and_direction_highlight_rules()
    {
        var html = ProjectMapBuilder.Build(Doc());

        // Manual light/dark: a theme toggle + the data-theme overrides + an early theme-init script.
        Assert.Contains("toggleTheme()", html);
        Assert.Contains("[data-theme=\"dark\"]", html);
        Assert.Contains("[data-theme=\"light\"]", html);
        Assert.Contains("testatlas:theme", html);

        // Navigation chrome: search-and-jump, zoom buttons and fit-to-view, plus the drawn edge key.
        Assert.Contains("placeholder=\"Search projects\"", html);
        Assert.Contains("title=\"Fit to view (F)\"", html);
        Assert.Contains("title=\"Reset zoom to 100% (0)\"", html);
        Assert.Contains("Arrows point to what a project depends on", html);

        // Highlights separate the two directions: blue = what the focus uses, violet = what uses it.
        Assert.Contains(".eg.r-down .edge{stroke:var(--fill-primary)}", html);
        Assert.Contains(".eg.r-up .edge{stroke:var(--violet)}", html);
        Assert.Contains(".node.r-dim{opacity:.32}", html);
    }

    [Fact]
    public void Embeds_a_dependency_panel_with_the_adjacency_data()
    {
        var html = ProjectMapBuilder.Build(Doc());

        // Panel scaffolding + pin/unpin behaviour.
        Assert.Contains("id=\"panel\"", html);
        Assert.Contains("window.pin=function", html);
        Assert.Contains("window.unpin=function", html);
        Assert.Contains("Depends on", html);
        Assert.Contains("Used by", html);

        // The data blob carries node labels and the one cross-project link with its kind/count detail,
        // so the panel can list "Feature.A → Lib.B · binds steps 1×". Names are JSON-escaped (< → <).
        Assert.Contains("window.__MAP__=", html);
        Assert.Contains("\"a\":1,\"b\":2,\"w\":1,\"d\":\"1 binds_to\",\"k\":{\"binds_to\":1},\"ref\":false", html);
        Assert.Contains("Lib.B \\u003cb>", html);       // the < is <-escaped inside the <script> blob
        Assert.DoesNotContain("Lib.B <b>", html);       // never a raw tag that could break the script

        // Class-level drill-down: the link carries the target classes behind the dependency, so the
        // panel can expand "Feature.A → Lib.B" into "BSteps · 1 binds_to".
        Assert.Contains("\"cls\":[{\"c\":\"BSteps\",\"k\":\"binds_to\",\"w\":1}]", html);
        Assert.Contains("data-toggle=", html);
        Assert.Contains("(e.cls.length===1?' class ':' classes ')", html); // the visible "N class(es)" expander label

        // Regression: the pointer must be captured only once a drag MOVES (via down.id), NOT on
        // pointerdown — capturing on pointerdown retargets the click to the <svg> so node clicks never
        // reach pin() and the panel never opens (the real-click bug).
        Assert.Contains("setPointerCapture(down.id)", html);
        Assert.DoesNotContain("setPointerCapture(ev.pointerId)", html);
    }

    [Fact]
    public void Panel_data_surfaces_the_api_endpoints_each_project_calls()
    {
        var doc = new MapDocument
        {
            UserVersion = MapSchema.Version,
            Projects = new[] { new ProjectRow(1, "Api.Tests", "A.csproj", "net8.0", Kinds.BddTests) },
            Classes = new[] { new ClassRow(1, 1, "Steps", "N", null, Kinds.StepClass, "A.cs", 1, 3) },
            Methods = new[] { new MethodRow(1, 1, 1, "M", "M()", "public", Kinds.StepDefinitionMethod, "A.cs", 2, 2) },
            Endpoints = new[]
            {
                new EndpointRow(1, "POST", "/api/orders"),
                new EndpointRow(2, "GET", "GetSupplierRequest"),
            },
            Edges = new[]
            {
                new EdgeRow(RefKinds.Method, 1, RefKinds.Endpoint, 1, EdgeKinds.CallsEndpoint, ""),
                new EdgeRow(RefKinds.Method, 1, RefKinds.Endpoint, 2, EdgeKinds.CallsEndpoint, ""),
            },
        };
        var html = ProjectMapBuilder.Build(doc);

        // The node blob carries the project's endpoints (verb + route), ordered by route — a URL route
        // and an operation-level one. A calls_endpoint edge never creates a project link (vacuity).
        Assert.Contains("\"eps\":[", html);
        Assert.Contains("{\"v\":\"POST\",\"r\":\"/api/orders\"}", html);
        Assert.Contains("{\"v\":\"GET\",\"r\":\"GetSupplierRequest\"}", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "class=\"node "));
        Assert.DoesNotContain("class=\"edge\"", html);

        // The card says how many it calls; the panel renders an "API endpoints called" section with
        // verb badges + an operation tag.
        Assert.Contains("BDD tests · 2 endpoints", html);
        Assert.Contains("API endpoints called", html);
        Assert.Contains("verb v-", html);
    }

    [Fact]
    public void Drops_the_shared_name_prefix_from_labels()
    {
        var doc = new MapDocument
        {
            UserVersion = MapSchema.Version,
            Projects = new[]
            {
                new ProjectRow(1, "Shop.Tests.Api", "A.csproj", "net8.0", Kinds.BddTests),
                new ProjectRow(2, "Shop.Core", "C.csproj", "net8.0", Kinds.SharedLibrary),
            },
        };
        var html = ProjectMapBuilder.Build(doc);

        Assert.Contains("\"prefix\":\"Shop.\"", html);
        Assert.Contains("\"name\":\"Shop.Tests.Api\",\"short\":\"Tests.Api\"", html);
        Assert.Contains("<div class=\"nm\">Core</div>", html);

        // A segment only counts when every name keeps one after it: "Shop" + "Shop.Core" share nothing.
        var flat = ProjectMapBuilder.Build(new MapDocument
        {
            UserVersion = MapSchema.Version,
            Projects = new[]
            {
                new ProjectRow(1, "Shop", "S.csproj", "net8.0", Kinds.Other),
                new ProjectRow(2, "Shop.Core", "C.csproj", "net8.0", Kinds.SharedLibrary),
            },
        });
        Assert.Contains("\"prefix\":\"\"", flat);
        Assert.Contains("<div class=\"nm\">Shop.Core</div>", flat);
    }

    [Fact]
    public void Lays_projects_out_in_layers_ordered_to_cut_crossings()
    {
        // The SampleShop graph: three test suites → four API / page libraries → Core. Layering counts
        // build-only references too, so plain ProjectReference edges are enough to shape it.
        var names = new[]
        {
            (1, "SampleShop.Api.Cart", Kinds.SharedLibrary), (2, "SampleShop.Api.Catalog", Kinds.SharedLibrary),
            (3, "SampleShop.Api.Identity", Kinds.SharedLibrary), (4, "SampleShop.Core", Kinds.SharedLibrary),
            (5, "SampleShop.Tests.Api", Kinds.BddTests), (6, "SampleShop.Tests.E2E", Kinds.BddTests),
            (7, "SampleShop.Tests.Ui", Kinds.BddTests), (8, "SampleShop.Ui.Pages", Kinds.SharedLibrary),
        };
        var deps = new[] { (1, 4), (2, 4), (3, 4), (5, 1), (5, 2), (5, 3), (5, 4), (6, 1), (6, 2), (6, 3), (6, 4), (6, 8), (7, 4), (7, 8), (8, 4) };
        var doc = new MapDocument
        {
            UserVersion = MapSchema.Version,
            Projects = names.Select(x => new ProjectRow(x.Item1, x.Item2, "p.csproj", "net8.0", x.Item3)).ToArray(),
            Edges = deps.Select(d => new EdgeRow(RefKinds.Project, d.Item1, RefKinds.Project, d.Item2, EdgeKinds.References, "")).ToArray(),
        };
        var html = ProjectMapBuilder.Build(doc);

        string NodeKey(int id) => $"\"{id}\":{{\"name\":\"{names[id - 1].Item2}\",\"short\":\"{names[id - 1].Item2[11..]}\",\"kind\":\"{names[id - 1].Item3}\",";
        foreach (var (id, col, row, x, y) in new[]
        {
            (5, 0, 0, 0, 80), (6, 0, 1, 0, 160), (7, 0, 2, 0, 240),                       // entry points
            (1, 1, 0, 356, 40), (2, 1, 1, 356, 120), (3, 1, 2, 356, 200), (8, 1, 3, 356, 280), // shared code
            (4, 2, 0, 712, 160),                                                          // foundation
        })
        {
            var start = html.IndexOf(NodeKey(id), StringComparison.Ordinal);
            Assert.True(start >= 0, $"node {id} missing from the blob");
            Assert.Contains($"\"col\":{col},\"row\":{row},\"x\":{x},\"y\":{y}", html.Substring(start, 200));
        }
        Assert.Contains("\"lay\":{\"W\":232,\"H\":62,\"w\":944,\"h\":342}", html);
        Assert.Contains("<b>Entry points</b><span>3</span>", html);
        Assert.Contains("<b>Shared code</b><span>4</span>", html);
        Assert.Contains("<b>Foundation</b><span>1</span>", html);
    }

    [Fact]
    public void A_dependency_cycle_still_lays_out()
    {
        var doc = new MapDocument
        {
            UserVersion = MapSchema.Version,
            Projects = new[]
            {
                new ProjectRow(1, "A", "A.csproj", "net8.0", Kinds.Other),
                new ProjectRow(2, "B", "B.csproj", "net8.0", Kinds.Other),
            },
            Edges = new[]
            {
                new EdgeRow(RefKinds.Project, 1, RefKinds.Project, 2, EdgeKinds.References, ""),
                new EdgeRow(RefKinds.Project, 2, RefKinds.Project, 1, EdgeKinds.References, ""),
            },
        };
        var html = ProjectMapBuilder.Build(doc);
        Assert.Contains("\"name\":\"A\",\"short\":\"A\",\"kind\":\"other\",\"in\":1,\"out\":1,\"col\":0", html);
        Assert.Contains("\"name\":\"B\",\"short\":\"B\",\"kind\":\"other\",\"in\":1,\"out\":1,\"col\":1", html);
    }

    [Fact]
    public void Handles_a_map_with_no_projects()
    {
        var html = ProjectMapBuilder.Build(new MapDocument { UserVersion = MapSchema.Version });
        Assert.Contains("No projects", html);
        Assert.Contains("</html>", html);
    }
}
