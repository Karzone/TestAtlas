using Microsoft.Data.Sqlite;
using TestAtlas.Core.Model;

namespace TestAtlas.Core.Storage;

/// <summary>
/// Writes an <see cref="IndexResult"/> to a single SQLite map file (spec §9). The write is
/// atomic: the db is built in a sibling temp file, then renamed over the target, so a consumer
/// never observes a half-written map. Pooling is disabled so the file handle is released before
/// the rename.
/// </summary>
public static class SqliteMapWriter
{
    /// <summary>Build the map at <paramref name="outputPath"/>, overwriting atomically.</summary>
    public static void Write(IndexResult result, string outputPath)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("Output path required.", nameof(outputPath));

        var fullOut = Path.GetFullPath(outputPath);
        var dir = Path.GetDirectoryName(fullOut) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(dir);

        var tempPath = Path.Combine(dir, $".{Path.GetFileName(fullOut)}.{Guid.NewGuid():N}.tmp");

        try
        {
            WriteTo(result, tempPath);
            // Atomic replace: rename over the target (same directory ⇒ same volume).
            File.Move(tempPath, fullOut, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best effort cleanup */ }
            }
        }
    }

    private static void WriteTo(IndexResult result, string dbPath)
    {
        if (File.Exists(dbPath)) File.Delete(dbPath);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        using (var conn = new SqliteConnection(connectionString))
        {
            conn.Open();

            using (var tx = conn.BeginTransaction())
            {
                Exec(conn, tx, MapSchema.CreateSql);
                Exec(conn, tx, $"PRAGMA user_version = {MapSchema.Version};");

                InsertMeta(conn, tx, result.Meta);
                InsertProjects(conn, tx, result.Projects);
                InsertClasses(conn, tx, result.Classes);
                InsertMethods(conn, tx, result.Methods);
                InsertDiagnostics(conn, tx, result.Diagnostics);

                tx.Commit();
            }
        }

        // Ensure the SQLite file handle is fully released before the caller renames it.
        SqliteConnection.ClearAllPools();
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void InsertMeta(SqliteConnection conn, SqliteTransaction tx, MapMeta meta)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO meta(key, value) VALUES ($k, $v);";
        var k = cmd.CreateParameter(); k.ParameterName = "$k"; cmd.Parameters.Add(k);
        var v = cmd.CreateParameter(); v.ParameterName = "$v"; cmd.Parameters.Add(v);

        void Put(string key, string value)
        {
            k.Value = key;
            v.Value = value;
            cmd.ExecuteNonQuery();
        }

        Put(MapSchema.MetaToolVersion, meta.ToolVersion);
        Put(MapSchema.MetaGeneratedUtc, meta.GeneratedUtc);
        Put(MapSchema.MetaSolutionPath, meta.SolutionPath);
        Put(MapSchema.MetaInputHash, meta.InputHash);
    }

    private static void InsertProjects(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<ProjectEntity> projects)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO projects(id, name, path, target_framework, kind) VALUES ($id, $n, $p, $tfm, $k);";
        var id = Add(cmd, "$id");
        var n = Add(cmd, "$n");
        var p = Add(cmd, "$p");
        var tfm = Add(cmd, "$tfm");
        var k = Add(cmd, "$k");

        foreach (var proj in projects)
        {
            id.Value = proj.Id;
            n.Value = proj.Name;
            p.Value = proj.Path;
            tfm.Value = (object?)proj.TargetFramework ?? DBNull.Value;
            k.Value = proj.Kind;
            cmd.ExecuteNonQuery();
        }
    }

    private static void InsertClasses(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<ClassEntity> classes)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO classes(id, project_id, name, namespace, base_type, kind, file_path, line_start, line_end) " +
            "VALUES ($id, $pid, $n, $ns, $bt, $k, $fp, $ls, $le);";
        var id = Add(cmd, "$id");
        var pid = Add(cmd, "$pid");
        var n = Add(cmd, "$n");
        var ns = Add(cmd, "$ns");
        var bt = Add(cmd, "$bt");
        var k = Add(cmd, "$k");
        var fp = Add(cmd, "$fp");
        var ls = Add(cmd, "$ls");
        var le = Add(cmd, "$le");

        foreach (var c in classes)
        {
            id.Value = c.Id;
            pid.Value = c.ProjectId;
            n.Value = c.Name;
            ns.Value = c.Namespace;
            bt.Value = (object?)c.BaseType ?? DBNull.Value;
            k.Value = c.Kind;
            fp.Value = c.FilePath;
            ls.Value = c.LineStart;
            le.Value = c.LineEnd;
            cmd.ExecuteNonQuery();
        }
    }

    private static void InsertMethods(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<MethodEntity> methods)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO methods(id, class_id, project_id, name, signature, visibility, kind, file_path, line_start, line_end) " +
            "VALUES ($id, $cid, $pid, $n, $sig, $vis, $k, $fp, $ls, $le);";
        var id = Add(cmd, "$id");
        var cid = Add(cmd, "$cid");
        var pid = Add(cmd, "$pid");
        var n = Add(cmd, "$n");
        var sig = Add(cmd, "$sig");
        var vis = Add(cmd, "$vis");
        var k = Add(cmd, "$k");
        var fp = Add(cmd, "$fp");
        var ls = Add(cmd, "$ls");
        var le = Add(cmd, "$le");

        foreach (var m in methods)
        {
            id.Value = m.Id;
            cid.Value = m.ClassId;
            pid.Value = m.ProjectId;
            n.Value = m.Name;
            sig.Value = m.Signature;
            vis.Value = m.Visibility;
            k.Value = m.Kind;
            fp.Value = m.FilePath;
            ls.Value = m.LineStart;
            le.Value = m.LineEnd;
            cmd.ExecuteNonQuery();
        }
    }

    private static void InsertDiagnostics(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<DiagnosticEntity> diagnostics)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO diagnostics(severity, code, message, location) VALUES ($s, $c, $m, $l);";
        var s = Add(cmd, "$s");
        var c = Add(cmd, "$c");
        var m = Add(cmd, "$m");
        var l = Add(cmd, "$l");

        foreach (var d in diagnostics)
        {
            s.Value = d.Severity.ToString().ToLowerInvariant();
            c.Value = d.Code;
            m.Value = d.Message;
            l.Value = (object?)d.Location ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
    }

    private static SqliteParameter Add(SqliteCommand cmd, string name)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        cmd.Parameters.Add(p);
        return p;
    }
}
