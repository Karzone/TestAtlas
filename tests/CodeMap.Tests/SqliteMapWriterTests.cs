using TestAtlas.Core.Model;
using TestAtlas.Core.Storage;
using Xunit;

namespace TestAtlas.Tests;

/// <summary>Writer-level tests that need no Roslyn — a hand-built result, written and read back.</summary>
public sealed class SqliteMapWriterTests
{
    private static IndexResult Sample() => new(
        new MapMeta("9.9.9", "2020-01-01T00:00:00Z", "/x/My.sln", new string('a', 64)),
        new[] { new ProjectEntity(1, "P", "P.csproj", "net8.0", Kinds.Other) },
        new[] { new ClassEntity(1, 1, "C", "N", "Base", Kinds.Other, "C.cs", 1, 3) },
        new[] { new MethodEntity(1, 1, 1, "M", "M()", "public", Kinds.Other, "C.cs", 2, 2) },
        Array.Empty<DiagnosticEntity>(),
        IndexOutcome.Success);

    [Fact]
    public void Write_overwrites_atomically_and_leaves_no_temp_files()
    {
        using var temp = new TempDir();
        var target = temp.File("codemap.db");

        // A pre-existing (garbage) file at the target must be cleanly replaced.
        File.WriteAllText(target, "not a database — should be overwritten");

        SqliteMapWriter.Write(Sample(), target);

        Assert.True(MapReader.TryValidate(target, out var version, out _));
        Assert.Equal(MapSchema.Version, version);

        // No sibling temp files (".<name>.<guid>.tmp") left behind by the atomic write.
        var leftovers = Directory.GetFiles(temp.Path)
            .Select(Path.GetFileName)
            .Where(n => n!.EndsWith(".tmp"))
            .ToArray();
        Assert.Empty(leftovers);
    }

    [Fact]
    public void Round_trips_rows_and_schema_version()
    {
        using var temp = new TempDir();
        var target = temp.File("codemap.db");
        SqliteMapWriter.Write(Sample(), target);

        var doc = MapReader.Read(target);
        Assert.Equal(MapSchema.Version, doc.UserVersion);
        var c = Assert.Single(doc.Classes);
        Assert.Equal("C", c.Name);
        Assert.Equal("Base", c.BaseType);
        var m = Assert.Single(doc.Methods);
        Assert.Equal("M()", m.Signature);
        Assert.Equal("9.9.9", doc.Meta[MapSchema.MetaToolVersion]);
    }
}
