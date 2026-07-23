using TestAtlas.Core.Indexing;
using TestAtlas.Core.Model;
using Xunit;

namespace TestAtlas.Tests;

/// <summary>Small pure-logic units: the classifier's never-throw contract and the glob filter.</summary>
public sealed class ClassifierTests
{
    [Fact]
    public void Classify_degrades_to_other_and_never_throws()
    {
        // Slice-1 stub + the G2 constraint: unrecognised input yields "other", never an exception.
        Assert.Equal(Kinds.Other, Classifier.ClassifyClass(null, null));
        Assert.Equal(Kinds.Other, Classifier.ClassifyMethod(null, null));
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
