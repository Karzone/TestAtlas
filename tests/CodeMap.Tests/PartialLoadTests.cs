using TestAtlas.Core.Storage;
using Xunit;

namespace TestAtlas.Tests;

/// <summary>
/// Spec A5: a solution containing one project that fails to load still produces a map for the rest,
/// exits 1, and records the failure as a diagnostic. Driven through the real CLI so the exit code
/// is genuinely observed.
/// </summary>
public sealed class PartialLoadTests
{
    [Fact]
    public void Unloadable_project_still_writes_map_with_exit_1_and_diagnostic()
    {
        using var temp = new TempDir();
        var db = temp.File("broken.db");

        var (code, _, _) = CliRunner.Index(FixturePaths.BrokenSolution, db, "--quiet");

        // Exit code 1 — completed with warnings, not fatal.
        Assert.Equal(1, code);
        Assert.True(File.Exists(db));

        var doc = MapReader.Read(db);
        Assert.Contains(doc.Classes, c => c.Name == "Widget");             // good project survived
        Assert.Contains(doc.Diagnostics, d => d.Severity == "error");      // failure recorded
    }
}
