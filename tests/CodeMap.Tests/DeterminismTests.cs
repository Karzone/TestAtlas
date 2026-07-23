using TestAtlas.Core.Model;
using TestAtlas.Core.Storage;
using Xunit;

namespace TestAtlas.Tests;

/// <summary>Spec §10 / A6: identical input ⇒ identical logical content (ordering aside).</summary>
public sealed class DeterminismTests
{
    // ---- writer-level: the logical dump excludes only the volatile timestamp ------------------
    private static IndexResult ResultWith(string generatedUtc, string className) => new(
        new MapMeta("1.0.0", generatedUtc, "/x/My.sln", new string('a', 64)),
        new[] { new ProjectEntity(1, "P", "P.csproj", "net8.0", Kinds.Other) },
        new[] { new ClassEntity(1, 1, className, "N", null, Kinds.Other, "C.cs", 1, 3) },
        Array.Empty<MethodEntity>(),
        Array.Empty<StepDefinitionEntity>(),
        Array.Empty<FeatureEntity>(),
        Array.Empty<ScenarioEntity>(),
        Array.Empty<ScenarioStepEntity>(),
        Array.Empty<EdgeEntity>(),
        Array.Empty<DiagnosticEntity>(),
        IndexOutcome.Success);

    [Fact]
    public void Logical_dump_ignores_the_generated_timestamp()
    {
        using var temp = new TempDir();
        var a = temp.File("a.db");
        var b = temp.File("b.db");
        SqliteMapWriter.Write(ResultWith("2020-01-01T00:00:00Z", "Same"), a);
        SqliteMapWriter.Write(ResultWith("2026-07-22T09:30:00Z", "Same"), b);

        Assert.Equal(MapReader.LogicalDump(a), MapReader.LogicalDump(b));
    }

    [Fact]
    public void Logical_dump_reflects_real_content_changes() // vacuity guard for the test above
    {
        using var temp = new TempDir();
        var a = temp.File("a.db");
        var b = temp.File("b.db");
        SqliteMapWriter.Write(ResultWith("2020-01-01T00:00:00Z", "Alpha"), a);
        SqliteMapWriter.Write(ResultWith("2020-01-01T00:00:00Z", "Beta"), b);

        Assert.NotEqual(MapReader.LogicalDump(a), MapReader.LogicalDump(b));
    }

    // ---- end-to-end: two real index runs over the fixture are byte-identical -------------------
    [Fact]
    public void Two_index_runs_produce_identical_logical_dumps()
    {
        using var temp = new TempDir();
        var db1 = temp.File("run1.db");
        var db2 = temp.File("run2.db");

        var r1 = CliRunner.Index(FixturePaths.FixtureSolution, db1, "--quiet");
        var r2 = CliRunner.Index(FixturePaths.FixtureSolution, db2, "--quiet");
        Assert.Equal(0, r1.Code);
        Assert.Equal(0, r2.Code);

        var dump1 = MapReader.LogicalDump(db1);
        var dump2 = MapReader.LogicalDump(db2);
        Assert.Equal(dump1, dump2);
        Assert.Contains("class|", dump1);           // vacuity: real entity rows present
        Assert.Equal(64, MapReader.Read(db1).Meta[MapSchema.MetaInputHash].Length);
    }
}
