using TestAtlas.Core.Analysis;
using TestAtlas.Core.Model;
using TestAtlas.Core.Storage;
using Xunit;

namespace TestAtlas.Tests;

/// <summary>
/// Reverse-dependency ("blast radius") analysis over a hand-built map. The step class has two step
/// methods — SignIn uses the LoginPage page object, Other uses nothing — so changing LoginPage must
/// affect the sign-in scenario ONLY (precision), and changing the base page object must reach it
/// transitively through inheritance.
/// </summary>
public sealed class ImpactAnalyzerTests
{
    private static MapDocument Doc() => new()
    {
        UserVersion = MapSchema.Version,
        Projects = new[] { new ProjectRow(1, "P", "P.csproj", "net8.0", Kinds.BddTests) },
        Classes = new[]
        {
            new ClassRow(1, 1, "Steps", "N", null, Kinds.StepClass, "Steps.cs", 1, 9),
            new ClassRow(2, 1, "LoginPage", "N", "BasePage", Kinds.PageObject, "LoginPage.cs", 1, 9),
            new ClassRow(3, 1, "BasePage", "N", null, Kinds.PageObject, "BasePage.cs", 1, 9),
        },
        Methods = new[]
        {
            new MethodRow(1, 1, 1, "SignIn", "SignIn()", "public", Kinds.StepDefinitionMethod, "Steps.cs", 3, 4),
            new MethodRow(2, 1, 1, "Other", "Other()", "public", Kinds.StepDefinitionMethod, "Steps.cs", 6, 7),
        },
        StepDefinitions = new[]
        {
            new StepDefinitionRow(1, 1, 1, 1, "When", "user signs in", ExpressionKinds.Regex, "", "Steps.cs", 3),
            new StepDefinitionRow(2, 2, 1, 1, "When", "user does other", ExpressionKinds.Regex, "", "Steps.cs", 6),
        },
        Features = new[] { new FeatureRow(1, 1, "Login", null, null, "Login.feature") },
        Scenarios = new[]
        {
            new ScenarioRow(1, 1, 1, "Sign in scenario", "scenario", null, 0, "Login.feature", 3),
            new ScenarioRow(2, 1, 1, "Other scenario", "scenario", null, 0, "Login.feature", 8),
        },
        ScenarioSteps = new[]
        {
            new ScenarioStepRow(1, 1, 1, "When", "user signs in", 0, false, false, "Login.feature", 4),
            new ScenarioStepRow(2, 2, 1, "When", "user does other", 0, false, false, "Login.feature", 9),
        },
        Edges = new[]
        {
            new EdgeRow(RefKinds.ScenarioStep, 1, RefKinds.StepDefinition, 1, EdgeKinds.BindsTo, BindConfidence.Exact),
            new EdgeRow(RefKinds.ScenarioStep, 2, RefKinds.StepDefinition, 2, EdgeKinds.BindsTo, BindConfidence.Exact),
            new EdgeRow(RefKinds.Method, 1, RefKinds.Class, 2, EdgeKinds.UsesType, BindConfidence.Exact),   // SignIn → LoginPage
            new EdgeRow(RefKinds.Class, 2, RefKinds.Class, 3, EdgeKinds.Inherits, BindConfidence.Exact),    // LoginPage : BasePage
        },
    };

    [Fact]
    public void Changing_a_page_object_affects_only_scenarios_whose_step_methods_use_it()
    {
        var r = ImpactAnalyzer.Analyze(Doc(), new ImpactQuery(ImpactTargetKind.Class, "LoginPage"));

        Assert.True(r.Found);
        var s = Assert.Single(r.Scenarios);
        Assert.Equal("Sign in scenario", s.Scenario);
        Assert.Equal("Login", s.Feature);
        Assert.Contains("user signs in", s.Via);
        // Precision: the "Other scenario" (whose step method never touches LoginPage) is NOT affected.
        Assert.DoesNotContain(r.Scenarios, x => x.Scenario == "Other scenario");
    }

    [Fact]
    public void Changing_a_base_page_object_reaches_scenarios_transitively_through_inheritance()
    {
        // BasePage is the base of LoginPage; SignIn uses LoginPage → the sign-in scenario is affected.
        var r = ImpactAnalyzer.Analyze(Doc(), new ImpactQuery(ImpactTargetKind.Class, "BasePage"));
        Assert.True(r.Found);
        Assert.Contains(r.Scenarios, x => x.Scenario == "Sign in scenario");
        Assert.Contains("derived", r.TargetLabel);
    }

    [Fact]
    public void Changing_a_step_definition_affects_the_binding_scenarios_directly()
    {
        var r = ImpactAnalyzer.Analyze(Doc(), new ImpactQuery(ImpactTargetKind.Step, "signs in"));
        var s = Assert.Single(r.Scenarios);
        Assert.Equal("Sign in scenario", s.Scenario);
    }

    [Fact]
    public void Changing_a_method_affects_scenarios_binding_its_step_definitions()
    {
        var r = ImpactAnalyzer.Analyze(Doc(), new ImpactQuery(ImpactTargetKind.Method, "Other"));
        var s = Assert.Single(r.Scenarios);
        Assert.Equal("Other scenario", s.Scenario);
    }

    [Fact]
    public void An_unknown_target_reports_nothing_affected()
    {
        var r = ImpactAnalyzer.Analyze(Doc(), new ImpactQuery(ImpactTargetKind.Class, "Nope"));
        Assert.False(r.Found);
        Assert.Empty(r.Scenarios);
    }

    [Fact]
    public void EndpointReachAll_computes_call_sites_and_scenarios_directly_and_through_a_client()
    {
        // Endpoint 10 (a URL route) is called straight from the step method SignIn; endpoint 11 (an
        // operation) is called inside LoginPage.Open, and SignIn uses LoginPage — so both reach the
        // sign-in scenario, one directly and one transitively through the page object.
        var doc = new MapDocument
        {
            UserVersion = MapSchema.Version,
            Classes = Doc().Classes,
            Methods = new[]
            {
                new MethodRow(1, 1, 1, "SignIn", "SignIn()", "public", Kinds.StepDefinitionMethod, "Steps.cs", 3, 4),
                new MethodRow(5, 2, 1, "Open", "Open()", "public", Kinds.PageObjectMethod, "LoginPage.cs", 3, 4),
            },
            StepDefinitions = new[] { new StepDefinitionRow(1, 1, 1, 1, "When", "user signs in", ExpressionKinds.Regex, "", "Steps.cs", 3) },
            Scenarios = new[] { new ScenarioRow(1, 1, 1, "Sign in scenario", "scenario", null, 0, "Login.feature", 3) },
            ScenarioSteps = new[] { new ScenarioStepRow(1, 1, 1, "When", "user signs in", 0, false, false, "Login.feature", 4) },
            Endpoints = new[]
            {
                new EndpointRow(10, "POST", "/api/orders"),
                new EndpointRow(11, "GET", "GetSupplierRequest"),
            },
            Edges = new[]
            {
                new EdgeRow(RefKinds.ScenarioStep, 1, RefKinds.StepDefinition, 1, EdgeKinds.BindsTo, BindConfidence.Exact),
                new EdgeRow(RefKinds.Method, 1, RefKinds.Class, 2, EdgeKinds.UsesType, BindConfidence.Exact), // SignIn → LoginPage
                new EdgeRow(RefKinds.Method, 1, RefKinds.Endpoint, 10, EdgeKinds.CallsEndpoint, ""),           // direct call site
                new EdgeRow(RefKinds.Method, 5, RefKinds.Endpoint, 11, EdgeKinds.CallsEndpoint, ""),           // inside LoginPage
            },
        };

        var reach = ImpactAnalyzer.EndpointReachAll(doc);

        Assert.Equal(1, reach[10].CallSiteCount);
        Assert.Equal(new[] { 1 }, reach[10].ScenarioIds); // direct

        Assert.Equal(1, reach[11].CallSiteCount);
        Assert.Equal(new[] { 1 }, reach[11].ScenarioIds); // transitive through the page object
    }

    [Fact]
    public void EndpointScenarioDetails_resolves_the_affected_scenarios_with_their_connecting_steps()
    {
        // Same graph as above, now with a Feature so the drill-down can resolve names: endpoint 11 is
        // called inside LoginPage.Open, reached transitively by the SignIn step method.
        var doc = new MapDocument
        {
            UserVersion = MapSchema.Version,
            Classes = Doc().Classes,
            Methods = new[]
            {
                new MethodRow(1, 1, 1, "SignIn", "SignIn()", "public", Kinds.StepDefinitionMethod, "Steps.cs", 3, 4),
                new MethodRow(5, 2, 1, "Open", "Open()", "public", Kinds.PageObjectMethod, "LoginPage.cs", 3, 4),
            },
            StepDefinitions = new[] { new StepDefinitionRow(1, 1, 1, 1, "When", "user signs in", ExpressionKinds.Regex, "", "Steps.cs", 3) },
            Features = new[] { new FeatureRow(1, 1, "Login", "d", "", "Login.feature") },
            Scenarios = new[] { new ScenarioRow(1, 1, 1, "Sign in scenario", "scenario", null, 0, "Login.feature", 3) },
            ScenarioSteps = new[] { new ScenarioStepRow(1, 1, 1, "When", "user signs in", 0, false, false, "Login.feature", 4) },
            Endpoints = new[]
            {
                new EndpointRow(10, "POST", "/api/orders"),
                new EndpointRow(11, "GET", "GetSupplierRequest"),
            },
            Edges = new[]
            {
                new EdgeRow(RefKinds.ScenarioStep, 1, RefKinds.StepDefinition, 1, EdgeKinds.BindsTo, BindConfidence.Exact),
                new EdgeRow(RefKinds.Method, 1, RefKinds.Class, 2, EdgeKinds.UsesType, BindConfidence.Exact), // SignIn → LoginPage
                new EdgeRow(RefKinds.Method, 1, RefKinds.Endpoint, 10, EdgeKinds.CallsEndpoint, ""),
                new EdgeRow(RefKinds.Method, 5, RefKinds.Endpoint, 11, EdgeKinds.CallsEndpoint, ""),
            },
        };

        // Only the requested endpoint is resolved (the report resolves just its shown rows).
        var details = ImpactAnalyzer.EndpointScenarioDetails(doc, new[] { 11 });
        Assert.True(details.ContainsKey(11));
        Assert.False(details.ContainsKey(10));

        // The detail is the sign-in scenario, with its feature and the step text that connects it — the
        // same drill-down `impact --endpoint` prints, not just a count.
        var scen = Assert.Single(details[11]);
        Assert.Equal("Sign in scenario", scen.Scenario);
        Assert.Equal("Login", scen.Feature);
        Assert.Equal(new[] { "user signs in" }, scen.Via);

        // An empty request set resolves nothing (guard).
        Assert.Empty(ImpactAnalyzer.EndpointScenarioDetails(doc, Array.Empty<int>()));
    }
}
