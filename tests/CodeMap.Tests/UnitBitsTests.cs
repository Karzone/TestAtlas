using TestAtlas.Core.Indexing;
using TestAtlas.Core.Model;
using Xunit;

namespace TestAtlas.Tests;

/// <summary>Small pure-logic units: the classifier's never-throw contract and the glob filter.</summary>
public sealed class ClassifierTests
{
    private static ClassFacts CF(string name, string? baseType = null, bool binding = false,
        bool testClass = false, int methods = 0, int stepM = 0, int testM = 0, int hookM = 0,
        int instMembers = 0, int uiMembers = 0, int apiMembers = 0, bool refUi = false, bool refApi = false)
        => new(name, baseType, binding, testClass, methods, stepM, testM, hookM,
            instMembers, uiMembers, apiMembers, refUi, refApi);

    private static string Classify(ClassFacts f, Func<string?, string?>? baseKind = null)
        => Classifier.ClassifyClass(f, ClassifierOptions.Default, baseKind ?? (_ => null));

    [Theory]
    // step class — [Binding] or a step-attributed method
    [InlineData("S1", true, false, 0, 0, 0, 0, 0, 0, 0, false, false, Kinds.StepClass)]
    // page object — ≥50% of instance members reference a UI type
    [InlineData("Navigator", false, false, 0, 0, 0, 0, 2, 1, 0, false, false, Kinds.PageObject)]
    // api client — name suffix + references RestSharp/HttpClient
    [InlineData("AccountClient", false, false, 0, 0, 0, 0, 0, 0, 0, false, true, Kinds.ApiClient)]
    // api client — ≥50% of methods reference an API type
    [InlineData("Gateway", false, false, 2, 0, 0, 0, 0, 0, 1, false, false, Kinds.ApiClient)]
    // test class — [TestFixture]/[TestClass]
    [InlineData("T1", false, true, 0, 0, 0, 0, 0, 0, 0, false, false, Kinds.TestClass)]
    // test class — a test-attributed method
    [InlineData("T2", false, false, 0, 0, 1, 0, 0, 0, 0, false, false, Kinds.TestClass)]
    // hook class — a hook-attributed method, nothing else
    [InlineData("Hooks", false, false, 0, 0, 0, 1, 0, 0, 0, false, false, Kinds.HookClass)]
    // plain
    [InlineData("Plain", false, false, 3, 0, 0, 0, 3, 0, 0, false, false, Kinds.Other)]
    public void Classifies_classes(string name, bool binding, bool testClass, int methods, int stepM,
        int testM, int hookM, int instMembers, int uiMembers, int apiMembers, bool refUi, bool refApi, string expected)
        => Assert.Equal(expected, Classify(CF(name, binding: binding, testClass: testClass, methods: methods,
            stepM: stepM, testM: testM, hookM: hookM, instMembers: instMembers, uiMembers: uiMembers,
            apiMembers: apiMembers, refUi: refUi, refApi: refApi)));

    [Fact]
    public void Page_object_by_name_suffix_needs_a_ui_reference()
    {
        Assert.Equal(Kinds.PageObject, Classify(CF("LoginPage", refUi: true)));
        Assert.Equal(Kinds.Other, Classify(CF("LoginPage", refUi: false))); // suffix alone isn't enough
    }

    [Fact]
    public void Inherits_a_page_object()
        => Assert.Equal(Kinds.PageObject,
            Classify(CF("SpecialPanel", baseType: "BasePage"),
                     baseKind: n => n == "BasePage" ? Kinds.PageObject : null));

    [Fact]
    public void Step_class_wins_over_page_object_when_both_apply()
        => Assert.Equal(Kinds.StepClass, Classify(CF("UiSteps", binding: true, instMembers: 2, uiMembers: 2)));

    [Theory]
    [InlineData(true, false, false, Kinds.PageObject, Kinds.StepDefinitionMethod)]
    [InlineData(false, true, false, Kinds.PageObject, Kinds.HookMethod)]
    [InlineData(false, false, true, Kinds.ApiClient, Kinds.TestMethod)]
    [InlineData(false, false, false, Kinds.PageObject, Kinds.PageObjectMethod)]
    [InlineData(false, false, false, Kinds.ApiClient, Kinds.ApiMethod)]
    [InlineData(false, false, false, Kinds.Other, Kinds.Other)]
    public void Classifies_methods(bool step, bool hook, bool test, string classKind, string expected)
        => Assert.Equal(expected, Classifier.ClassifyMethod(new MethodFacts(step, hook, test), classKind));

    [Theory]
    [InlineData("a cart with {int} items", ExpressionKinds.Regex, ExpressionKinds.CucumberExpression)]  // {int} wins
    [InlineData("a user named (.*)", ExpressionKinds.CucumberExpression, ExpressionKinds.Regex)]          // metachars ⇒ regex
    [InlineData("the dashboard is shown", ExpressionKinds.Regex, ExpressionKinds.Regex)]                  // literal ⇒ default
    [InlineData("the dashboard is shown", ExpressionKinds.CucumberExpression, ExpressionKinds.CucumberExpression)]
    public void Detects_expression_kind(string expr, string frameworkDefault, string expected)
        => Assert.Equal(expected, Classifier.DetectExpressionKind(expr, frameworkDefault));

    [Fact]
    public void Never_throws_on_odd_input()
    {
        Assert.Equal(Kinds.Other, Classifier.ClassifyMethod(new MethodFacts(false, false, false), "nonsense-kind"));
        Assert.Equal(Kinds.Other, Classifier.SummariseProject(Array.Empty<ClassEntity>()));
    }
}

public sealed class MsBuildGuardTests
{
    [Fact]
    public void Picks_the_newest_sdk()
    {
        var output = "6.0.428 [/usr/share/dotnet/sdk]\n8.0.129 [/usr/share/dotnet/sdk]\n7.0.100 [/usr/share/dotnet/sdk]\n";
        Assert.Equal(Path.Combine("/usr/share/dotnet/sdk", "8.0.129"), MsBuildGuard.ParseNewestSdkDir(output));
    }

    [Fact]
    public void Handles_windows_paths_and_preview_versions()
    {
        var output = "8.0.400 [C:\\Program Files\\dotnet\\sdk]\r\n9.0.100-preview.3 [C:\\Program Files\\dotnet\\sdk]\r\n";
        Assert.Equal(Path.Combine("C:\\Program Files\\dotnet\\sdk", "9.0.100-preview.3"),
            MsBuildGuard.ParseNewestSdkDir(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no brackets here")]
    public void Returns_null_when_no_sdk_line(string output)
        => Assert.Null(MsBuildGuard.ParseNewestSdkDir(output));
}

public sealed class WorkspaceDiagnosticClassifierTests
{
    [Fact]
    public void Missing_package_on_a_loaded_project_is_a_warning()
    {
        var (code, sev) = WorkspaceDiagnosticClassifier.Classify(
            "Msbuild failed ... Unable to find package IG.Party.Dto. No packages exist ...",
            projectLoadedWithContent: true);
        Assert.Equal("nuget_missing_package", code);
        Assert.Equal(DiagnosticSeverity.Warning, sev);
    }

    [Fact]
    public void Vulnerability_advisory_is_a_warning_when_the_project_loaded()
    {
        var (code, sev) = WorkspaceDiagnosticClassifier.Classify(
            "Package 'RestSharp' 106.6.9 has a known high severity vulnerability, https://…",
            projectLoadedWithContent: true);
        Assert.Equal("nuget_vulnerability", code);
        Assert.Equal(DiagnosticSeverity.Warning, sev);
    }

    [Fact]
    public void Orphan_reqnroll_codebehind_is_a_warning()
    {
        var (code, sev) = WorkspaceDiagnosticClassifier.Classify(
            "For code-behind file 'X.feature.cs', no feature file was found. Set project property …",
            projectLoadedWithContent: true);
        Assert.Equal("reqnroll_orphan_codebehind", code);
        Assert.Equal(DiagnosticSeverity.Warning, sev);
    }

    [Fact]
    public void An_unreadable_project_file_is_an_error_even_if_others_loaded()
    {
        var (code, sev) = WorkspaceDiagnosticClassifier.Classify(
            "The project file could not be loaded. The 'ItemGroup' start tag …",
            projectLoadedWithContent: true);
        Assert.Equal("project_load_failed", code);
        Assert.Equal(DiagnosticSeverity.Error, sev);
    }

    [Fact]
    public void A_diagnostic_on_a_project_that_did_not_load_is_an_error()
    {
        var (_, sev) = WorkspaceDiagnosticClassifier.Classify(
            "Unable to find package Foo.", projectLoadedWithContent: false);
        Assert.Equal(DiagnosticSeverity.Error, sev);
    }

    [Fact]
    public void Extracts_the_csproj_path_from_a_message()
        => Assert.Equal(
            @"C:\repo\API\OneFAT.API.Party\OneFAT.API.Party.csproj",
            WorkspaceDiagnosticClassifier.ExtractProjectPath(
                @"Msbuild failed when processing the file 'C:\repo\API\OneFAT.API.Party\OneFAT.API.Party.csproj' with message: nope"));

    [Fact]
    public void Returns_no_path_when_there_is_no_csproj_quote()
        => Assert.Null(WorkspaceDiagnosticClassifier.ExtractProjectPath("something without a project path"));
}

public sealed class GlobTests
{
    [Theory]
    [InlineData("LegacyTests", "LegacyTests", "/repo/tests/LegacyTests.csproj", true)]     // bare name
    [InlineData("**/LegacyTests.csproj", "LegacyTests", "/repo/tests/LegacyTests.csproj", true)] // path glob
    [InlineData("*Tests", "AcceptanceTests", "/repo/AcceptanceTests.csproj", true)]        // suffix
    [InlineData("legacytests", "LegacyTests", "/repo/LegacyTests.csproj", true)]           // case-insensitive
    [InlineData("Foo*", "BarTests", "/repo/BarTests.csproj", false)]                       // no match
    [InlineData("**/Other.csproj", "LegacyTests", "/repo/LegacyTests.csproj", false)]      // different path
    public void Matches_name_or_path(string pattern, string name, string path, bool expected)
        => Assert.Equal(expected, Glob.IsMatch(pattern, name, path));
}
