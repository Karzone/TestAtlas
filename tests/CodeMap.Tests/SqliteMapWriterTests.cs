using Microsoft.Data.Sqlite;
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
        new[] { new ClassEntity(1, 1, "C", "N", "Base", Kinds.StepClass, "C.cs", 1, 3) },
        new[] { new MethodEntity(1, 1, 1, "M", "M()", "public", Kinds.StepDefinitionMethod, "C.cs", 2, 2) },
        new[] { new StepDefinitionEntity(1, 1, 1, 1, "Given", "a thing", ExpressionKinds.Regex, "", "C.cs", 2) },
        new[] { new FeatureEntity(1, 1, "F", "desc", "@tag", "F.feature") },
        new[] { new ScenarioEntity(1, 1, 1, "S", "scenario", "@tag", 0, "F.feature", 3) },
        new[] { new ScenarioStepEntity(1, 1, 1, "Given", "a thing", 0, false, false, "F.feature", 4) },
        new[] { new EndpointEntity(1, "POST", "/api/orders") },
        new[]
        {
            new EdgeEntity(RefKinds.ScenarioStep, 1, RefKinds.StepDefinition, 1, EdgeKinds.BindsTo, BindConfidence.Exact),
            new EdgeEntity(RefKinds.Method, 1, RefKinds.Endpoint, 1, EdgeKinds.CallsEndpoint, ""),
        },
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
    public void Overwrites_a_read_only_target()
    {
        using var temp = new TempDir();
        var target = temp.File("codemap.db");
        SqliteMapWriter.Write(Sample(), target);
        File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);

        // Must not throw — the writer clears read-only and replaces (the "file is locked/read-only" fix).
        SqliteMapWriter.Write(Sample(), target);
        Assert.True(MapReader.TryValidate(target, out _, out _));
    }

    [Fact]
    public void Reads_an_older_schema_db_without_crashing()
    {
        using var temp = new TempDir();
        var target = temp.File("v1.db");
        SqliteMapWriter.Write(Sample(), target); // v2

        // Downgrade to a v1-shaped file: drop the new table + stamp the old version.
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = target, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP TABLE step_definitions; PRAGMA user_version = 1;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var doc = MapReader.Read(target);          // must not throw on the missing table
        Assert.Equal(1, doc.UserVersion);
        Assert.Empty(doc.StepDefinitions);
        Assert.Single(doc.Classes);                // the rest still reads
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
        var s = Assert.Single(doc.StepDefinitions);
        Assert.Equal("Given", s.Keyword);
        Assert.Equal("a thing", s.Expression);
        Assert.Equal(ExpressionKinds.Regex, s.ExpressionKind);
        Assert.Single(doc.Features);
        Assert.Single(doc.Scenarios);
        var st = Assert.Single(doc.ScenarioSteps);
        Assert.Equal("a thing", st.Text);
        var ep = Assert.Single(doc.Endpoints);
        Assert.Equal("POST", ep.Verb);
        Assert.Equal("/api/orders", ep.Route);
        Assert.Equal(2, doc.Edges.Count);
        var bind = doc.Edges.Single(x => x.EdgeKind == EdgeKinds.BindsTo);
        Assert.Equal(BindConfidence.Exact, bind.Confidence);
        var call = doc.Edges.Single(x => x.EdgeKind == EdgeKinds.CallsEndpoint);
        Assert.Equal(RefKinds.Endpoint, call.ToKind);
        Assert.Equal(ep.Id, call.ToId);
        Assert.Equal("9.9.9", doc.Meta[MapSchema.MetaToolVersion]);
    }
}
