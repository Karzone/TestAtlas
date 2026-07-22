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
    public void Page_object_shaped_class_without_Page_suffix_is_captured()
    {
        // Slice 1 classifies it as "other"; the point here is that a Page-less page object is
        // still extracted with its methods so slice-2 detection has something to reclassify.
        var navigator = _fx.Doc.Classes.Single(c => c.Name == "Navigator");
        Assert.Equal("Fixture.SpecFlow", navigator.Namespace);
        Assert.Equal(Kinds.Other, navigator.Kind);

        var methods = MethodsIn(navigator.ProjectId)
            .Where(m => m.ClassId == navigator.Id)
            .Select(m => m.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "EnterUsername", "NavigateToLogin" }, methods);
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
