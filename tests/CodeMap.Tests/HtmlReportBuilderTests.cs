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
    public void Shows_a_stale_schema_banner_for_an_older_map()
    {
        // A v(Version-1) map is missing whole facets; the banner must explain the empty sections.
        var doc = new MapDocument { UserVersion = MapSchema.Version - 1 };
        var html = HtmlReportBuilder.Build(doc);
        Assert.Contains("class=\"banner\"", html);
        Assert.Contains($"schema v{MapSchema.Version - 1}", html);
        Assert.Contains("testatlas index", html); // tells the reader how to fix it
    }

    [Fact]
    public void Does_not_show_the_banner_for_a_current_map() // vacuity guard for the test above
    {
        var html = HtmlReportBuilder.Build(Doc()); // Doc() is UserVersion == MapSchema.Version
        Assert.DoesNotContain("class=\"banner\"", html);
    }

    // A doc with page objects and structural edges: LoginPage is driven by a step method and extends
    // BasePage; BasePage and DeadPage are page objects nothing drives (orphans).
    private static MapDocument DocWithCollaborators() => new()
    {
        UserVersion = MapSchema.Version,
        Classes = new[]
        {
            new ClassRow(1, 1, "LoginSteps", "N", null, Kinds.StepClass, "S.cs", 1, 9),
            new ClassRow(2, 1, "LoginPage", "N", "BasePage", Kinds.PageObject, "P.cs", 1, 9),
            new ClassRow(3, 1, "BasePage", "N", null, Kinds.PageObject, "B.cs", 1, 9),
            new ClassRow(4, 1, "DeadPage", "N", null, Kinds.PageObject, "D.cs", 1, 9),
        },
        Methods = new[] { new MethodRow(1, 1, 1, "WhenTheySignIn", "WhenTheySignIn()", "public", Kinds.StepDefinitionMethod, "S.cs", 3, 3) },
        Edges = new[]
        {
            new EdgeRow(RefKinds.Method, 1, RefKinds.Class, 2, EdgeKinds.UsesType, BindConfidence.Exact),
            new EdgeRow(RefKinds.Class, 2, RefKinds.Class, 3, EdgeKinds.Inherits, BindConfidence.Exact),
        },
    };

    [Fact]
    public void Collaborators_panel_ranks_drivers_and_flags_orphans()
    {
        var html = HtmlReportBuilder.Build(DocWithCollaborators());

        Assert.Contains("Collaborators", html);
        Assert.Contains("3 page objects", html);
        Assert.Contains("2 unused", html);            // BasePage + DeadPage are driven by nothing

        // The driven page object shows its driver count and its base (inherits).
        Assert.Contains("LoginPage", html);
        Assert.Contains("1 method", html);
        Assert.Contains("BasePage", html);            // rendered in LoginPage's "extends" cell

        // An orphan is explicitly tagged unused.
        Assert.Contains("tag-unused", html);
        Assert.Contains("DeadPage", html);
    }

    [Fact]
    public void No_collaborators_panel_when_there_are_no_page_objects_or_api_clients() // vacuity guard
    {
        var html = HtmlReportBuilder.Build(Doc()); // Doc() has only a step class
        Assert.DoesNotContain("Collaborators", html);
    }

    [Fact]
    public void Panels_are_collapsible_and_features_default_collapsed()
    {
        var html = HtmlReportBuilder.Build(Doc()); // has a coverage panel and a Login feature

        // Top-level panels are <details> so they can be folded away.
        Assert.Contains("<details class=\"panel\" open>", html);
        // Feature entries default COLLAPSED (no `open`) so a large report is scannable.
        Assert.Contains("<details class=\"feature\">", html);
        Assert.DoesNotContain("<details class=\"feature\" open>", html);
        // Bulk controls exist.
        Assert.Contains("expand all", html);
        Assert.Contains("collapse all", html);
        Assert.Contains("setAllFeatures", html);
    }

    [Fact]
    public void Feature_summary_shows_an_unbound_badge_when_a_step_is_unbound()
    {
        // Doc()'s "Login <b>" feature contains the unbound "pigs can fly" step.
        var html = HtmlReportBuilder.Build(Doc());
        Assert.Contains("badge unbound", html);
        Assert.Contains("1 unbound", html); // the per-feature count in the collapsed summary
    }

    [Fact]
    public void Diagnostics_table_is_wrapped_so_long_messages_never_overflow_the_page()
    {
        var longPath = "Msbuild failed when processing the file " + new string('C', 200) + ".csproj";
        var doc = new MapDocument
        {
            UserVersion = MapSchema.Version,
            Diagnostics = new[] { new DiagnosticRow("warning", "nuget_missing_package", longPath, "X.csproj") },
        };
        var html = HtmlReportBuilder.Build(doc);

        // The wide table sits in its own scroll container so the page body never scrolls sideways.
        Assert.Contains("<div class=\"table-scroll\"><table class=\"grid diag\">", html);
        Assert.Contains("</table></div></details>", html);
        Assert.Contains(longPath, html); // message rendered in full (it wraps via CSS, not truncation)
    }

    // A doc whose one step method calls both a URL route and an operation-level endpoint, with the
    // step bound to a scenario — so the panel can show each endpoint's blast radius.
    private static MapDocument DocWithEndpoints() => new()
    {
        UserVersion = MapSchema.Version,
        Classes = new[] { new ClassRow(1, 1, "OrderSteps", "N", null, Kinds.StepClass, "S.cs", 1, 9) },
        Methods = new[]
        {
            new MethodRow(1, 1, 1, "WhenOrdering", "WhenOrdering()", "public", Kinds.StepDefinitionMethod, "S.cs", 3, 5),
            new MethodRow(2, 1, 1, "WhenPaying", "WhenPaying()", "public", Kinds.StepDefinitionMethod, "S.cs", 6, 8),
        },
        StepDefinitions = new[]
        {
            new StepDefinitionRow(1, 1, 1, 1, "When", "ordering", ExpressionKinds.Regex, "", "S.cs", 3),
            new StepDefinitionRow(2, 2, 1, 1, "When", "paying", ExpressionKinds.Regex, "", "S.cs", 6), // MethodId=2
        },
        Features = new[] { new FeatureRow(1, 1, "Orders", "d", "", "Orders.feature") },
        Scenarios = new[] { new ScenarioRow(1, 1, 1, "Place order", "scenario", "", 0, "Orders.feature", 3) },
        // Two connecting steps into the /api/orders endpoint, so its drill-down lists a multi-via scenario.
        ScenarioSteps = new[]
        {
            new ScenarioStepRow(1, 1, 1, "When", "ordering", 0, false, false, "Orders.feature", 4),
            new ScenarioStepRow(2, 1, 1, "When", "paying", 1, false, false, "Orders.feature", 5),
        },
        Endpoints = new[]
        {
            new EndpointRow(1, "POST", "/api/orders"),
            new EndpointRow(2, "GET", "GetSupplierRequest"),
        },
        Edges = new[]
        {
            new EdgeRow(RefKinds.ScenarioStep, 1, RefKinds.StepDefinition, 1, EdgeKinds.BindsTo, BindConfidence.Exact),
            new EdgeRow(RefKinds.ScenarioStep, 2, RefKinds.StepDefinition, 2, EdgeKinds.BindsTo, BindConfidence.Exact),
            new EdgeRow(RefKinds.Method, 1, RefKinds.Endpoint, 1, EdgeKinds.CallsEndpoint, ""),
            new EdgeRow(RefKinds.Method, 2, RefKinds.Endpoint, 1, EdgeKinds.CallsEndpoint, ""), // 2nd call site into /api/orders
            new EdgeRow(RefKinds.Method, 1, RefKinds.Endpoint, 2, EdgeKinds.CallsEndpoint, ""),
        },
    };

    [Fact]
    public void Endpoints_panel_lists_routes_operations_and_their_blast_radius()
    {
        var html = HtmlReportBuilder.Build(DocWithEndpoints());

        Assert.Contains("API endpoints", html);
        Assert.Contains("1 route", html);
        Assert.Contains("1 operation", html);

        // Both shapes render with a verb badge and the endpoint identity.
        Assert.Contains("/api/orders", html);
        Assert.Contains("GetSupplierRequest", html);
        Assert.Contains("verb post", html);
        Assert.Contains("verb get", html);

        // The URL is a "route"; the typed request is an "operation".
        Assert.Contains(">route<", html);
        Assert.Contains(">operation<", html);

        // Blast radius: each endpoint reaches the one bound scenario (vacuity: not "0", not "none").
        Assert.Contains("1 scenario", html);
    }

    [Fact]
    public void Endpoint_row_expands_to_its_affected_scenarios()
    {
        var html = HtmlReportBuilder.Build(DocWithEndpoints());

        // The scenarios cell is now a toggle backed by a hidden detail row (the drill-down structure).
        Assert.Contains("toggleEp(this)", html);
        Assert.Contains("class=\"ep-det\"", html);
        Assert.Contains("function toggleEp", html); // the reveal script is wired in

        // Vacuity, scoped to the endpoints panel (the Features tree also names the scenario): the detail
        // lists the real feature → scenario → connecting step, grouped into a per-feature card, not just a count.
        var epPanel = Between(html, "<h2>API endpoints</h2>", "<h2>Features</h2>");
        Assert.Contains("ep-scn-name\">Place order<", epPanel); // the reached scenario
        Assert.Contains("ep-fg-name\">Orders<", epPanel);       // the feature-group card label
        Assert.Contains("scenario across", epPanel);            // the detail header ("1 scenario across 1 feature")

        // /api/orders is reached via two steps — the via list joins with real <b> markup, not the
        // double-encoded "&lt;/b&gt;" that a re-escaped separator would produce.
        Assert.Contains("via <b>ordering</b>, <b>paying</b>", epPanel);
        Assert.DoesNotContain("&lt;/b&gt;", epPanel);
        // GetSupplierRequest is reached via one step — single, un-joined.
        Assert.Contains("via <b>ordering</b></span>", epPanel);
    }

    [Fact]
    public void Scenario_in_the_features_panel_shows_the_endpoints_it_exercises()
    {
        var html = HtmlReportBuilder.Build(DocWithEndpoints());

        // The forward view: the "Place order" scenario's step calls both endpoints, so its Features-panel
        // row lists them. Scope to the Features panel (both routes also appear in the endpoints panel).
        var features = Between(html, "<h2>Features</h2>", "</main>");
        Assert.Contains("class=\"s-apis\"", features);
        Assert.Contains("exercises 2 endpoints", features);   // the forward count (vacuity: not 0/absent)
        Assert.Contains("/api/orders", features);             // the URL route it hits
        Assert.Contains("GetSupplierRequest", features);      // the operation it hits

        // Symmetry: an endpoint lists the scenario iff the scenario lists the endpoint. GetSupplierRequest's
        // detail row (endpoints panel) names "Place order", and here "Place order" names GetSupplierRequest.
        var endpoints = Between(html, "<h2>API endpoints</h2>", "<h2>Features</h2>");
        Assert.Contains("Place order", endpoints);
    }

    /// <summary>The substring between two markers — scopes an assertion to one panel of the report.</summary>
    private static string Between(string html, string start, string end)
    {
        var a = html.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, $"marker not found: {start}");
        var b = html.IndexOf(end, a, StringComparison.Ordinal);
        Assert.True(b > a, $"end marker not found after start: {end}");
        return html.Substring(a, b - a);
    }

    [Fact]
    public void No_endpoints_panel_when_the_map_has_none() // vacuity guard
    {
        var html = HtmlReportBuilder.Build(Doc()); // Doc() has no endpoints
        Assert.DoesNotContain("API endpoints", html);
    }

    [Fact]
    public void Handles_an_empty_map_without_throwing()
    {
        var html = HtmlReportBuilder.Build(new MapDocument { UserVersion = MapSchema.Version });
        Assert.Contains("<!doctype html>", html);
        Assert.Contains("0<span>%</span>", html); // no steps → 0% coverage, no divide-by-zero
    }
}
