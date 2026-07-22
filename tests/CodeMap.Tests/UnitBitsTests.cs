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
