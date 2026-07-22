using System.Runtime.CompilerServices;

// MSBuildWorkspace + MSBuildLocator are process-global and not safe to drive from several tests at
// once, so the whole assembly runs serially. The unit tests (matcher/glob/classifier) are fast
// enough that this costs nothing meaningful.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace TestAtlas.Tests;

/// <summary>Locates the checked-in fixture solutions relative to this source file.</summary>
internal static class FixturePaths
{
    public static string FixturesDir([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "fixtures"));

    public static string FixtureSolution => Path.Combine(FixturesDir(), "FixtureSolution.sln");
    public static string BrokenSolution => Path.Combine(FixturesDir(), "BrokenSolution.sln");
}

/// <summary>A scratch directory that deletes itself on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "testatlas-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}
