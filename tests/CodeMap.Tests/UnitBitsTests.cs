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
