using TestAtlas.Core.Storage;

namespace TestAtlas.Tests;

/// <summary>
/// Indexes the (good) fixture solution exactly once via the real CLI and reads the resulting map,
/// shared across the assertions in <see cref="IndexIntegrationTests"/> / <see cref="CliTests"/>.
/// Loading a solution costs a couple of seconds, so we do it once per test class.
/// </summary>
public sealed class IndexedFixtureSolution : IDisposable
{
    private readonly TempDir _temp = new();

    public int ExitCode { get; }
    public MapDocument Doc { get; }
    public string DbPath { get; }

    public IndexedFixtureSolution()
    {
        DbPath = _temp.File("map.db");
        var (code, _, _) = CliRunner.Index(FixturePaths.FixtureSolution, DbPath, "--quiet");
        ExitCode = code;
        Doc = MapReader.Read(DbPath);
    }

    public void Dispose() => _temp.Dispose();
}
