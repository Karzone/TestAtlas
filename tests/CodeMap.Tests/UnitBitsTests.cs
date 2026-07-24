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
    // step class — contains a step-attributed method ([Binding] alone is a hook class, see below)
    [InlineData("S1", false, false, 0, 1, 0, 0, 0, 0, 0, false, false, Kinds.StepClass)]
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
    public void Inheriting_an_api_client_wins_over_the_page_object_ratio()
    {
        // GlobalConfigNetworkApiService : BaseApiService, yet ≥50% of its members touch UI types. The
        // base type is the stronger signal, so it's an api_client — not the page_object the greedy
        // UI-ratio rule claimed first. (Fails on the pre-reorder classifier: it returned page_object.)
        var facts = CF("GlobalConfigNetworkApiService", baseType: "BaseApiService",
            instMembers: 4, uiMembers: 4, refUi: true);
        Assert.Equal(Kinds.ApiClient, Classify(facts, n => n == "BaseApiService" ? Kinds.ApiClient : null));
    }

    [Fact]
    public void A_static_class_is_never_a_page_object_or_api_client()
    {
        // A static RestSharp helper references API types and even holds a marker — which would trip the
        // api rules — but a `static class` is never instantiated, so it is not a collaborator. It falls
        // through to `other`. (Fails on the pre-guard classifier: it returned api_client.)
        var facts = new ClassFacts("RestSharpHttpRequestExecutionReportingDetailsCreator", BaseTypeName: null,
            HasBindingAttribute: false, HasTestClassAttribute: false, MethodCount: 3, StepMethodCount: 0,
            TestMethodCount: 0, HookMethodCount: 0, InstanceMemberCount: 0, UiReferencingMembers: 0,
            ApiReferencingMembers: 3, ReferencesUiType: false, ReferencesApiType: true,
            HoldsOrConstructsApiMarker: true, IsStatic: true);
        Assert.Equal(Kinds.Other, Classify(facts));
    }

    [Fact]
    public void Step_class_wins_over_page_object_when_both_apply()
        => Assert.Equal(Kinds.StepClass, Classify(CF("UiSteps", binding: true, stepM: 1, instMembers: 2, uiMembers: 2)));

    [Fact]
    public void A_binding_class_with_only_hooks_is_a_hook_class_not_a_step_class()
    {
        // [Binding] also marks hook-only classes ([BeforeScenario]/[AfterScenario]/…); without a real
        // step binding they are hook classes, so a shared library hosting a global hooks class isn't
        // promoted to bdd_tests. (Fails on the old rule, which made any [Binding] class a step class.)
        Assert.Equal(Kinds.HookClass, Classify(CF("GlobalHooks", binding: true, hookM: 2)));
        // …but a [Binding] class WITH a step binding is still a step class.
        Assert.Equal(Kinds.StepClass, Classify(CF("LoginSteps", binding: true, stepM: 3)));
    }

    [Fact]
    public void Holds_a_rest_client_marker_in_a_field_so_it_is_an_api_client()
    {
        // The real-world shape: an HTTP wrapper whose client lives in a field and is driven through a
        // variable — the marker type name never appears in a method body, so the method-ratio rule
        // sees 0 api-referencing methods. It must still classify api_client on the holds/constructs
        // signal, else `new Wrapper<Req>()` never registers as an operation-level endpoint.
        var facts = new ClassFacts("BaseRequest", BaseTypeName: null, HasBindingAttribute: false,
            HasTestClassAttribute: false, MethodCount: 3, StepMethodCount: 0, TestMethodCount: 0,
            HookMethodCount: 0, InstanceMemberCount: 3, UiReferencingMembers: 0, ApiReferencingMembers: 0,
            ReferencesUiType: false, ReferencesApiType: true, HoldsOrConstructsApiMarker: true);
        Assert.Equal(Kinds.ApiClient, Classify(facts));

        // Vacuity: merely *referencing* an api type in one method (without holding/constructing one and
        // without a majority) is NOT enough — the holds signal is what tips it.
        var notHolding = facts with { HoldsOrConstructsApiMarker = false };
        Assert.Equal(Kinds.Other, Classify(notHolding));
    }

    [Fact]
    public void Wraps_an_api_client_by_constructing_one()
    {
        // The service-layer propagation: a NAMED wrapper that constructs an already-classified
        // api_client (BaseApiService → `new BaseRequest<..>()`) is itself part of the API layer. It has
        // no direct RestSharp/HttpClient reference, so only the constructs-an-api-client rule can catch
        // it — and only once the resolver knows BaseRequest's kind (i.e. after the fixpoint). The rule is
        // name-gated: "BaseApiService" carries the …Service suffix, so it qualifies.
        var facts = new ClassFacts("BaseApiService", BaseTypeName: null, HasBindingAttribute: false,
            HasTestClassAttribute: false, MethodCount: 1, StepMethodCount: 0, TestMethodCount: 0,
            HookMethodCount: 0, InstanceMemberCount: 0, UiReferencingMembers: 0, ApiReferencingMembers: 0,
            ReferencesUiType: false, ReferencesApiType: false, ConstructedTypeNames: new[] { "BaseRequest" });

        Assert.Equal(Kinds.Other, Classifier.ClassifyClass(facts, ClassifierOptions.Default, _ => null));
        Assert.Equal(Kinds.ApiClient, Classifier.ClassifyClass(facts, ClassifierOptions.Default,
            n => n == "BaseRequest" ? Kinds.ApiClient : null));
    }

    [Fact]
    public void A_utility_that_constructs_an_api_client_but_is_not_named_like_one_stays_other()
    {
        // The precision guard on the constructs-an-api-client rule. A *Utilities/*Helper consumer that
        // internally news up a request wrapper is calling the API, not being it — so with no transport
        // marker, no inheritance and no API name-suffix it must classify `other`. Before the name gate
        // this returned api_client (the 40+ *Utilities false positives on the real 28-project solution),
        // so this assertion FAILS on the pre-gate classifier.
        var facts = new ClassFacts("NetworkUtilities", BaseTypeName: null, HasBindingAttribute: false,
            HasTestClassAttribute: false, MethodCount: 2, StepMethodCount: 0, TestMethodCount: 0,
            HookMethodCount: 0, InstanceMemberCount: 0, UiReferencingMembers: 0, ApiReferencingMembers: 0,
            ReferencesUiType: false, ReferencesApiType: false, ConstructedTypeNames: new[] { "BaseRequest" });

        // Even with the resolver reporting BaseRequest as an api_client, the utility is not promoted.
        Assert.Equal(Kinds.Other, Classifier.ClassifyClass(facts, ClassifierOptions.Default,
            n => n == "BaseRequest" ? Kinds.ApiClient : null));
    }

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

    private static ClassEntity Cls(int id, string kind) => new(id, 1, "C" + id, "N", null, kind, "C.cs", 1, 2);

    [Fact]
    public void Project_with_step_classes_is_bdd_even_when_test_classes_outnumber_them()
    {
        // The real-world mislabel: a step-definition project (bound to by other projects) that also
        // carries many generated [TestFixture] codebehind classes. Step classes win ⇒ bdd_tests.
        var classes = new[]
        {
            Cls(1, Kinds.StepClass),
            Cls(2, Kinds.TestClass), Cls(3, Kinds.TestClass), Cls(4, Kinds.TestClass),
        };
        Assert.Equal(Kinds.BddTests, Classifier.SummariseProject(classes));
    }

    [Fact]
    public void Project_with_only_test_classes_stays_unit_tests()
        => Assert.Equal(Kinds.UnitTests, Classifier.SummariseProject(new[] { Cls(1, Kinds.TestClass), Cls(2, Kinds.TestClass) }));

    [Fact]
    public void Project_with_neither_is_a_shared_library()
        => Assert.Equal(Kinds.SharedLibrary, Classifier.SummariseProject(new[] { Cls(1, Kinds.PageObject), Cls(2, Kinds.Helper) }));
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
