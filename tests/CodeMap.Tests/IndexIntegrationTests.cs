using TestAtlas.Core.Model;
using TestAtlas.Core.Storage;
using Xunit;

namespace TestAtlas.Tests;

/// <summary>
/// End-to-end assertions over the map produced from the fixture solution. Vacuity-proof: exact
/// entity counts, specific named classes with resolved file/line locations, method depth, and the
/// meta/schema stamp — never "it ran without throwing".
/// </summary>
public sealed class IndexIntegrationTests : IClassFixture<IndexedFixtureSolution>
{
    private readonly IndexedFixtureSolution _fx;
    public IndexIntegrationTests(IndexedFixtureSolution fx) => _fx = fx;

    [Fact]
    public void Clean_solution_indexes_successfully()
        => Assert.Equal(0, _fx.ExitCode); // spec §7: exit 0 = success

    [Fact]
    public void Yields_exact_project_class_method_counts()
    {
        Assert.Equal(2, _fx.Doc.Projects.Count);
        Assert.Equal(15, _fx.Doc.Classes.Count);
        Assert.Equal(10, _fx.Doc.Methods.Count);
        Assert.Empty(_fx.Doc.Diagnostics);
    }

    [Fact]
    public void Both_bdd_projects_are_present()   // spec A4: SpecFlow + Reqnroll in one run
    {
        var names = _fx.Doc.Projects.Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Fixture.Reqnroll", "Fixture.SpecFlow" }, names);
    }

    [Fact]
    public void Per_project_counts_match()
    {
        var specflow = ProjectByName("Fixture.SpecFlow");
        var reqnroll = ProjectByName("Fixture.Reqnroll");

        Assert.Equal(9, ClassesIn(specflow).Count);
        Assert.Equal(7, MethodsIn(specflow).Count);
        Assert.Equal(6, ClassesIn(reqnroll).Count);
        Assert.Equal(3, MethodsIn(reqnroll).Count);
    }

    [Fact]
    public void Named_step_class_has_correct_location_and_methods()
    {
        var loginSteps = _fx.Doc.Classes.Single(c => c.Name == "LoginSteps");

        Assert.Equal(Kinds.StepClass, loginSteps.Kind); // carries [Binding]
        Assert.Equal("Fixture.SpecFlow", loginSteps.Namespace);
        Assert.EndsWith("SpecFlow/LoginSteps.cs", loginSteps.FilePath.Replace('\\', '/'));

        // Location is resolved from the actual source, not guessed.
        var expectedLine = DeclarationLine("SpecFlow/LoginSteps.cs", "class LoginSteps");
        Assert.Equal(expectedLine, loginSteps.LineStart);
        Assert.True(loginSteps.LineEnd > loginSteps.LineStart);

        // Depth: exactly the five methods we declared (including the ambiguous pair).
        var methods = MethodsIn(loginSteps.ProjectId)
            .Where(m => m.ClassId == loginSteps.Id)
            .Select(m => m.Name)
            .OrderBy(n => n)
            .ToArray();
        Assert.Equal(
            new[] { "GivenAUserNamed", "SystemReadyExact", "SystemReadyPattern", "ThenTheDashboardIsShown", "WhenTheySignIn" },
            methods);
    }

    [Fact]
    public void Page_object_without_Page_suffix_is_detected_by_its_UI_types()
    {
        // Navigator has no "Page" suffix — it's classified as a page object purely because ≥50% of
        // its instance members reference Playwright types (IPage/ILocator). Its methods inherit
        // page_object_method.
        var navigator = _fx.Doc.Classes.Single(c => c.Name == "Navigator");
        Assert.Equal("Fixture.SpecFlow", navigator.Namespace);
        Assert.Equal(Kinds.PageObject, navigator.Kind);

        var methods = MethodsIn(navigator.ProjectId)
            .Where(m => m.ClassId == navigator.Id)
            .ToList();
        Assert.Equal(new[] { "EnterUsername", "NavigateToLogin" }, methods.Select(m => m.Name).OrderBy(n => n).ToArray());
        Assert.All(methods, m => Assert.Equal(Kinds.PageObjectMethod, m.Kind));
    }

    [Fact]
    public void Step_definitions_are_extracted_with_keyword_and_expression_kind()
    {
        // 5 in LoginSteps + 3 in CheckoutSteps.
        Assert.Equal(8, _fx.Doc.StepDefinitions.Count);
        Assert.All(_fx.Doc.Classes.Where(c => c.Name is "LoginSteps" or "CheckoutSteps"),
            c => Assert.Equal(Kinds.StepClass, c.Kind));

        // A SpecFlow regex binding.
        var named = _fx.Doc.StepDefinitions.Single(s => s.Expression == "a user named (.*)");
        Assert.Equal("Given", named.Keyword);
        Assert.Equal(ExpressionKinds.Regex, named.ExpressionKind);

        // A Reqnroll cucumber binding, with its parameter captured.
        var cart = _fx.Doc.StepDefinitions.Single(s => s.Expression == "a cart with {int} item(s)");
        Assert.Equal("Given", cart.Keyword);
        Assert.Equal(ExpressionKinds.CucumberExpression, cart.ExpressionKind);
        Assert.Contains("int", cart.Parameters);

        // The method those bindings hang off is a step_definition.
        var m = _fx.Doc.Methods.Single(x => x.Name == "GivenAUserNamed");
        Assert.Equal(Kinds.StepDefinitionMethod, m.Kind);
    }

    [Fact]
    public void Gherkin_features_scenarios_and_steps_are_extracted()
    {
        // 2 .feature files (Login + Checkout), 3 scenarios, 8 steps in source order.
        Assert.Equal(2, _fx.Doc.Features.Count);
        Assert.Equal(3, _fx.Doc.Scenarios.Count);
        Assert.Equal(8, _fx.Doc.ScenarioSteps.Count);

        var featureNames = _fx.Doc.Features.Select(f => f.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Checkout", "Login" }, featureNames);

        // Scenario tags round-trip from the .feature file.
        var signIn = _fx.Doc.Scenarios.Single(s => s.Name == "Successful sign in");
        Assert.Equal("@smoke", signIn.Tags);
        var login = _fx.Doc.Features.Single(f => f.Name == "Login");
        Assert.Equal(login.Id, signIn.FeatureId);

        // The And keyword is preserved verbatim (resolution to Given/When/Then happens in the matcher).
        Assert.Contains(_fx.Doc.ScenarioSteps, s => s.Keyword == "And" && s.Text == "pigs can fly");
    }

    [Fact]
    public void Bound_step_links_to_its_step_definition()
    {
        var aliceStep = _fx.Doc.ScenarioSteps.Single(s => s.Text == "a user named Alice");
        var edge = _fx.Doc.Edges.Single(e => e.FromKind == RefKinds.ScenarioStep && e.FromId == aliceStep.Id);

        Assert.Equal(EdgeKinds.BindsTo, edge.EdgeKind);
        Assert.Equal(BindConfidence.Exact, edge.Confidence);
        Assert.Equal(RefKinds.StepDefinition, edge.ToKind);

        var target = _fx.Doc.StepDefinitions.Single(d => d.Id == edge.ToId);
        Assert.Equal("a user named (.*)", target.Expression); // the "And"/keyword-agnostic binding it resolves to
    }

    [Fact]
    public void Unmatched_step_is_recorded_as_unbound()  // the deliberately-unbound "And pigs can fly"
    {
        var pigs = _fx.Doc.ScenarioSteps.Single(s => s.Text == "pigs can fly");
        var edge = _fx.Doc.Edges.Single(e => e.FromKind == RefKinds.ScenarioStep && e.FromId == pigs.Id);

        Assert.Equal(EdgeKinds.Unbound, edge.EdgeKind);
        Assert.Null(edge.ToId);   // no step definition on the other end
    }

    [Fact]
    public void Ambiguous_step_records_both_candidates_as_ambiguous()
    {
        // "the system is ready" matches BOTH "the system is ready" and "the system is (.*)".
        var ready = _fx.Doc.ScenarioSteps.Single(s => s.Text == "the system is ready");
        var edges = _fx.Doc.Edges.Where(e => e.FromKind == RefKinds.ScenarioStep && e.FromId == ready.Id).ToList();

        Assert.Equal(2, edges.Count);
        Assert.All(edges, e => Assert.Equal(EdgeKinds.BindsTo, e.EdgeKind));
        Assert.All(edges, e => Assert.Equal(BindConfidence.Ambiguous, e.Confidence));

        var expressions = edges
            .Select(e => _fx.Doc.StepDefinitions.Single(d => d.Id == e.ToId).Expression)
            .OrderBy(x => x)
            .ToArray();
        Assert.Equal(new[] { "the system is (.*)", "the system is ready" }, expressions);
    }

    [Fact]
    public void Edge_tallies_match_the_fixture()
    {
        var bindsTo = _fx.Doc.Edges.Where(e => e.EdgeKind == EdgeKinds.BindsTo).ToList();
        var exact = bindsTo.Count(e => e.Confidence == BindConfidence.Exact);
        var ambiguous = bindsTo.Count(e => e.Confidence == BindConfidence.Ambiguous);
        var unbound = _fx.Doc.Edges.Count(e => e.EdgeKind == EdgeKinds.Unbound);

        Assert.Equal(6, exact);      // the six cleanly-resolved steps
        Assert.Equal(2, ambiguous);  // the two candidates for "the system is ready"
        Assert.Equal(1, unbound);    // "pigs can fly"
    }

    [Fact]
    public void Fts5_search_over_step_definitions_finds_the_matching_row()
    {
        // Vacuity-proof: the FTS index resolves 'dashboard' to exactly the "the dashboard is shown" step def.
        var hits = MapReader.SearchSteps(_fx.DbPath, "dashboard");
        var dashboard = _fx.Doc.StepDefinitions.Single(d => d.Expression == "the dashboard is shown");
        Assert.Equal(new[] { (long)dashboard.Id }, hits.ToArray());

        // And it discriminates: a token in no step definition returns nothing.
        Assert.Empty(MapReader.SearchSteps(_fx.DbPath, "zzzznope"));
    }

    [Fact]
    public void Fts5_search_over_scenarios_finds_the_matching_scenario()
    {
        // 'dashboard' appears only in the "Successful sign in" scenario's step text.
        var hits = MapReader.SearchScenarios(_fx.DbPath, "dashboard");
        var signIn = _fx.Doc.Scenarios.Single(s => s.Name == "Successful sign in");
        Assert.Equal(new[] { (long)signIn.Id }, hits.ToArray());

        // The feature name is indexed too: 'login' matches both Login scenarios, neither Checkout one.
        var loginHits = MapReader.SearchScenarios(_fx.DbPath, "login")
            .Select(id => _fx.Doc.Scenarios.Single(s => s.Id == (int)id).Name)
            .OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Readiness", "Successful sign in" }, loginHits);

        Assert.Empty(MapReader.SearchScenarios(_fx.DbPath, "zzzznope"));
    }

    [Fact]
    public void Method_signature_and_visibility_are_recorded()
    {
        var m = _fx.Doc.Methods.Single(x => x.Name == "GivenAUserNamed");
        Assert.Equal("GivenAUserNamed(string)", m.Signature);
        Assert.Equal("public", m.Visibility);
    }

    [Fact]
    public void Meta_and_schema_version_are_written()
    {
        Assert.Equal(MapSchema.Version, _fx.Doc.UserVersion);

        Assert.True(_fx.Doc.Meta.TryGetValue(MapSchema.MetaToolVersion, out var tool) && tool.Length > 0);
        Assert.True(_fx.Doc.Meta.TryGetValue(MapSchema.MetaGeneratedUtc, out var gen) && gen.EndsWith("Z"));
        Assert.True(_fx.Doc.Meta.TryGetValue(MapSchema.MetaSolutionPath, out var sln) && sln.EndsWith("FixtureSolution.sln"));
        Assert.True(_fx.Doc.Meta.TryGetValue(MapSchema.MetaInputHash, out var hash) && hash.Length == 64);
    }

    // ---- helpers -----------------------------------------------------------------------------
    private int ProjectByName(string name) => _fx.Doc.Projects.Single(p => p.Name == name).Id;
    private List<ClassRow> ClassesIn(int projectId) => _fx.Doc.Classes.Where(c => c.ProjectId == projectId).ToList();
    private List<MethodRow> MethodsIn(int projectId) => _fx.Doc.Methods.Where(m => m.ProjectId == projectId).ToList();

    private static int DeclarationLine(string relFixturePath, string needle)
    {
        var full = Path.Combine(FixturePaths.FixturesDir(), relFixturePath.Replace('/', Path.DirectorySeparatorChar));
        var lines = File.ReadAllLines(full);
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Contains(needle))
                return i + 1; // 1-based
        throw new Xunit.Sdk.XunitException($"'{needle}' not found in {relFixturePath}");
    }
}
