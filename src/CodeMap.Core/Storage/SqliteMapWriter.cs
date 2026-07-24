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
            AtomicReplace(tempPath, fullOut);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best effort cleanup */ }
            }
        }
    }

    /// <summary>
    /// Rename the temp file over the target (same directory ⇒ same volume). If the target is
    /// read-only or briefly locked, clear the attribute and retry once; on a genuine lock (the map
    /// open in another program) surface a clear, actionable message instead of a raw OS error.
    /// </summary>
    private static void AtomicReplace(string tempPath, string target)
    {
        try
        {
            File.Move(tempPath, target, overwrite: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            try
            {
                if (File.Exists(target))
                    File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                File.Move(tempPath, target, overwrite: true);
            }
            catch (Exception ex2) when (ex2 is UnauthorizedAccessException or IOException)
            {
                throw new IOException(
                    $"Could not replace '{target}'. It may be open in another program (e.g. DB Browser) " +
                    "or read-only — close it, or pass a different --output path.", ex2);
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
                InsertStepDefinitions(conn, tx, result.StepDefinitions);
                InsertFeatures(conn, tx, result.Features);
                InsertScenarios(conn, tx, result.Scenarios);
                InsertScenarioSteps(conn, tx, result.ScenarioSteps);
                InsertEndpoints(conn, tx, result.Endpoints);
                InsertEdges(conn, tx, result.Edges);
                InsertDiagnostics(conn, tx, result.Diagnostics);
                PopulateSearch(conn, tx, result);

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

    private static void InsertStepDefinitions(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<StepDefinitionEntity> steps)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO step_definitions(id, method_id, class_id, project_id, keyword, expression, expression_kind, parameters, file_path, line_start) " +
            "VALUES ($id, $mid, $cid, $pid, $kw, $ex, $ek, $pm, $fp, $ls);";
        var id = Add(cmd, "$id");
        var mid = Add(cmd, "$mid");
        var cid = Add(cmd, "$cid");
        var pid = Add(cmd, "$pid");
        var kw = Add(cmd, "$kw");
        var ex = Add(cmd, "$ex");
        var ek = Add(cmd, "$ek");
        var pm = Add(cmd, "$pm");
        var fp = Add(cmd, "$fp");
        var ls = Add(cmd, "$ls");

        foreach (var s in steps)
        {
            id.Value = s.Id;
            mid.Value = s.MethodId;
            cid.Value = s.ClassId;
            pid.Value = s.ProjectId;
            kw.Value = s.Keyword;
            ex.Value = s.Expression;
            ek.Value = s.ExpressionKind;
            pm.Value = (object?)s.Parameters ?? DBNull.Value;
            fp.Value = s.FilePath;
            ls.Value = s.LineStart;
            cmd.ExecuteNonQuery();
        }
    }

    private static void InsertFeatures(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<FeatureEntity> features)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO features(id, project_id, name, description, tags, file_path) VALUES ($id,$pid,$n,$d,$t,$fp);";
        var id = Add(cmd, "$id"); var pid = Add(cmd, "$pid"); var n = Add(cmd, "$n");
        var d = Add(cmd, "$d"); var t = Add(cmd, "$t"); var fp = Add(cmd, "$fp");
        foreach (var f in features)
        {
            id.Value = f.Id; pid.Value = f.ProjectId; n.Value = f.Name;
            d.Value = (object?)f.Description ?? DBNull.Value; t.Value = (object?)f.Tags ?? DBNull.Value; fp.Value = f.FilePath;
            cmd.ExecuteNonQuery();
        }
    }

    private static void InsertScenarios(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<ScenarioEntity> scenarios)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO scenarios(id, feature_id, project_id, name, kind, tags, example_row_count, file_path, line_start) " +
            "VALUES ($id,$fid,$pid,$n,$k,$t,$erc,$fp,$ls);";
        var id = Add(cmd, "$id"); var fid = Add(cmd, "$fid"); var pid = Add(cmd, "$pid"); var n = Add(cmd, "$n");
        var k = Add(cmd, "$k"); var t = Add(cmd, "$t"); var erc = Add(cmd, "$erc"); var fp = Add(cmd, "$fp"); var ls = Add(cmd, "$ls");
        foreach (var s in scenarios)
        {
            id.Value = s.Id; fid.Value = s.FeatureId; pid.Value = s.ProjectId; n.Value = s.Name;
            k.Value = s.Kind; t.Value = (object?)s.Tags ?? DBNull.Value; erc.Value = s.ExampleRowCount;
            fp.Value = s.FilePath; ls.Value = s.LineStart;
            cmd.ExecuteNonQuery();
        }
    }

    private static void InsertScenarioSteps(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<ScenarioStepEntity> steps)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO scenario_steps(id, scenario_id, project_id, keyword, text, ordinal, has_docstring, has_table, file_path, line_start) " +
            "VALUES ($id,$sid,$pid,$kw,$tx,$o,$ds,$tb,$fp,$ls);";
        var id = Add(cmd, "$id"); var sid = Add(cmd, "$sid"); var pid = Add(cmd, "$pid"); var kw = Add(cmd, "$kw");
        var txt = Add(cmd, "$tx"); var o = Add(cmd, "$o"); var ds = Add(cmd, "$ds"); var tb = Add(cmd, "$tb");
        var fp = Add(cmd, "$fp"); var ls = Add(cmd, "$ls");
        foreach (var s in steps)
        {
            id.Value = s.Id; sid.Value = s.ScenarioId; pid.Value = s.ProjectId; kw.Value = s.Keyword;
            txt.Value = s.Text; o.Value = s.Ordinal; ds.Value = s.HasDocString ? 1 : 0; tb.Value = s.HasDataTable ? 1 : 0;
            fp.Value = s.FilePath; ls.Value = s.LineStart;
            cmd.ExecuteNonQuery();
        }
    }

    private static void InsertEndpoints(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<EndpointEntity> endpoints)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO endpoints(id, verb, route, path, target_api) VALUES ($id,$v,$r,$p,$t);";
        var id = Add(cmd, "$id"); var v = Add(cmd, "$v"); var r = Add(cmd, "$r");
        var p = Add(cmd, "$p"); var t = Add(cmd, "$t");
        foreach (var e in endpoints)
        {
            id.Value = e.Id; v.Value = e.Verb; r.Value = e.Route;
            p.Value = (object?)e.Path ?? DBNull.Value; t.Value = (object?)e.TargetApi ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
    }

    private static void InsertEdges(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<EdgeEntity> edges)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO edges(id, from_kind, from_id, to_kind, to_id, edge_kind, confidence) VALUES ($id,$fk,$fi,$tk,$ti,$ek,$cf);";
        var id = Add(cmd, "$id"); var fk = Add(cmd, "$fk"); var fi = Add(cmd, "$fi");
        var tk = Add(cmd, "$tk"); var ti = Add(cmd, "$ti"); var ek = Add(cmd, "$ek"); var cf = Add(cmd, "$cf");
        var n = 0;
        foreach (var e in edges)
        {
            id.Value = ++n; fk.Value = e.FromKind; fi.Value = e.FromId; tk.Value = e.ToKind;
            ti.Value = (object?)e.ToId ?? DBNull.Value; ek.Value = e.EdgeKind; cf.Value = (object?)e.Confidence ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Populate the FTS5 search tables (spec §5.3): <c>search_steps</c> over step-definition text +
    /// method/class names; <c>search_scenarios</c> over feature/scenario names + step text + tags.
    /// rowids mirror the step-definition / scenario ids so consumers can join back.
    /// </summary>
    private static void PopulateSearch(SqliteConnection conn, SqliteTransaction tx, IndexResult result)
    {
        var methodName = result.Methods.ToDictionary(m => m.Id, m => m.Name);
        var className = result.Classes.ToDictionary(c => c.Id, c => c.Name);
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO search_steps(rowid, expression, method_name, class_name) VALUES ($r,$e,$m,$c);";
            var r = Add(cmd, "$r"); var e = Add(cmd, "$e"); var m = Add(cmd, "$m"); var c = Add(cmd, "$c");
            foreach (var sd in result.StepDefinitions)
            {
                r.Value = sd.Id; e.Value = sd.Expression;
                m.Value = methodName.TryGetValue(sd.MethodId, out var mn) ? mn : string.Empty;
                c.Value = className.TryGetValue(sd.ClassId, out var cn) ? cn : string.Empty;
                cmd.ExecuteNonQuery();
            }
        }

        var featureName = result.Features.ToDictionary(f => f.Id, f => f.Name);
        var stepsByScenario = result.ScenarioSteps
            .GroupBy(s => s.ScenarioId)
            .ToDictionary(g => g.Key, g => string.Join(" ", g.OrderBy(x => x.Ordinal).Select(x => x.Text)));
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO search_scenarios(rowid, feature_name, scenario_name, step_text, tags) VALUES ($r,$f,$s,$t,$g);";
            var r = Add(cmd, "$r"); var f = Add(cmd, "$f"); var s = Add(cmd, "$s"); var t = Add(cmd, "$t"); var g = Add(cmd, "$g");
            foreach (var sc in result.Scenarios)
            {
                r.Value = sc.Id;
                f.Value = featureName.TryGetValue(sc.FeatureId, out var fn) ? fn : string.Empty;
                s.Value = sc.Name;
                t.Value = stepsByScenario.TryGetValue(sc.Id, out var st) ? st : string.Empty;
                g.Value = sc.Tags ?? string.Empty;
                cmd.ExecuteNonQuery();
            }
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
