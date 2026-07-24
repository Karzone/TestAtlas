using System.Text;
using Microsoft.Data.Sqlite;

namespace TestAtlas.Core.Storage;

/// <summary>A projected class row, as read back from a map file.</summary>
public sealed record ClassRow(int Id, int ProjectId, string Name, string Namespace, string? BaseType,
    string Kind, string FilePath, int LineStart, int LineEnd);

/// <summary>A projected method row.</summary>
public sealed record MethodRow(int Id, int ClassId, int ProjectId, string Name, string Signature,
    string Visibility, string Kind, string FilePath, int LineStart, int LineEnd);

/// <summary>A projected project row.</summary>
public sealed record ProjectRow(int Id, string Name, string Path, string? TargetFramework, string Kind);

/// <summary>A projected step-definition row.</summary>
public sealed record StepDefinitionRow(int Id, int MethodId, int ClassId, int ProjectId, string Keyword,
    string Expression, string ExpressionKind, string? Parameters, string FilePath, int LineStart);

/// <summary>A projected feature row.</summary>
public sealed record FeatureRow(int Id, int ProjectId, string Name, string? Description, string? Tags, string FilePath);

/// <summary>A projected scenario row.</summary>
public sealed record ScenarioRow(int Id, int FeatureId, int ProjectId, string Name, string Kind, string? Tags,
    int ExampleRowCount, string FilePath, int LineStart);

/// <summary>A projected scenario-step row.</summary>
public sealed record ScenarioStepRow(int Id, int ScenarioId, int ProjectId, string Keyword, string Text,
    int Ordinal, bool HasDocString, bool HasDataTable, string FilePath, int LineStart);

/// <summary>A projected endpoint row (spec §5.1, slice 4; Path/TargetApi added in slice 5).</summary>
public sealed record EndpointRow(int Id, string Verb, string Route, string? Path = null, string? TargetApi = null);

/// <summary>A projected edge row.</summary>
public sealed record EdgeRow(string FromKind, int FromId, string ToKind, int? ToId, string EdgeKind, string? Confidence);

/// <summary>A projected diagnostic row.</summary>
public sealed record DiagnosticRow(string Severity, string Code, string Message, string? Location);

/// <summary>An in-memory view of a whole map file — used by <c>stats</c> and the tests.</summary>
public sealed class MapDocument
{
    public int UserVersion { get; init; }
    public IReadOnlyDictionary<string, string> Meta { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<ProjectRow> Projects { get; init; } = Array.Empty<ProjectRow>();
    public IReadOnlyList<ClassRow> Classes { get; init; } = Array.Empty<ClassRow>();
    public IReadOnlyList<MethodRow> Methods { get; init; } = Array.Empty<MethodRow>();
    public IReadOnlyList<StepDefinitionRow> StepDefinitions { get; init; } = Array.Empty<StepDefinitionRow>();
    public IReadOnlyList<FeatureRow> Features { get; init; } = Array.Empty<FeatureRow>();
    public IReadOnlyList<ScenarioRow> Scenarios { get; init; } = Array.Empty<ScenarioRow>();
    public IReadOnlyList<ScenarioStepRow> ScenarioSteps { get; init; } = Array.Empty<ScenarioStepRow>();
    public IReadOnlyList<EndpointRow> Endpoints { get; init; } = Array.Empty<EndpointRow>();
    public IReadOnlyList<EdgeRow> Edges { get; init; } = Array.Empty<EdgeRow>();
    public IReadOnlyList<DiagnosticRow> Diagnostics { get; init; } = Array.Empty<DiagnosticRow>();
}

/// <summary>Reads a CodeMap/TestAtlas SQLite map file back into memory.</summary>
public static class MapReader
{
    public static MapDocument Read(string dbPath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(dbPath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        using var conn = new SqliteConnection(connectionString);
        conn.Open();

        var doc = new MapDocument
        {
            UserVersion = ReadUserVersion(conn),
            Meta = ReadMeta(conn),
            Projects = ReadProjects(conn),
            Classes = ReadClasses(conn),
            Methods = ReadMethods(conn),
            StepDefinitions = ReadStepDefinitions(conn),
            Features = ReadFeatures(conn),
            Scenarios = ReadScenarios(conn),
            ScenarioSteps = ReadScenarioSteps(conn),
            Endpoints = ReadEndpoints(conn),
            Edges = ReadEdges(conn),
            Diagnostics = ReadDiagnostics(conn),
        };
        SqliteConnection.ClearAllPools();
        return doc;
    }

    /// <summary>True if the file is a readable SQLite db carrying the TestAtlas schema.</summary>
    public static bool TryValidate(string dbPath, out int userVersion, out string? error)
    {
        userVersion = 0;
        error = null;
        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(dbPath),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();
            using var conn = new SqliteConnection(cs);
            conn.Open();
            userVersion = ReadUserVersion(conn);
            // Presence of the core tables is our signal that this is our schema.
            foreach (var table in new[] { "meta", "projects", "classes", "methods", "step_definitions", "diagnostics" })
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n;";
                var p = cmd.CreateParameter(); p.ParameterName = "$n"; p.Value = table; cmd.Parameters.Add(p);
                if (cmd.ExecuteScalar() is null)
                {
                    error = $"Not a TestAtlas map file: missing table '{table}'.";
                    SqliteConnection.ClearAllPools();
                    return false;
                }
            }
            SqliteConnection.ClearAllPools();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Canonical logical dump used by the determinism test (spec §10): every logical row in id
    /// order, with the volatile <c>generated_utc</c> meta value excluded. Two runs on identical
    /// input must produce byte-identical dumps.
    /// </summary>
    public static string LogicalDump(string dbPath)
    {
        var doc = Read(dbPath);
        var sb = new StringBuilder();
        sb.Append("user_version=").Append(doc.UserVersion).Append('\n');

        foreach (var kvp in doc.Meta.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (kvp.Key == MapSchema.MetaGeneratedUtc) continue; // volatile: excluded by design
            sb.Append("meta:").Append(kvp.Key).Append('=').Append(kvp.Value).Append('\n');
        }

        foreach (var p in doc.Projects)
            sb.Append("project|").Append(p.Id).Append('|').Append(p.Name).Append('|')
              .Append(p.Path).Append('|').Append(p.TargetFramework).Append('|').Append(p.Kind).Append('\n');

        foreach (var c in doc.Classes)
            sb.Append("class|").Append(c.Id).Append('|').Append(c.ProjectId).Append('|').Append(c.Name)
              .Append('|').Append(c.Namespace).Append('|').Append(c.BaseType).Append('|').Append(c.Kind)
              .Append('|').Append(c.FilePath).Append('|').Append(c.LineStart).Append('|').Append(c.LineEnd).Append('\n');

        foreach (var m in doc.Methods)
            sb.Append("method|").Append(m.Id).Append('|').Append(m.ClassId).Append('|').Append(m.ProjectId)
              .Append('|').Append(m.Name).Append('|').Append(m.Signature).Append('|').Append(m.Visibility)
              .Append('|').Append(m.Kind).Append('|').Append(m.FilePath).Append('|').Append(m.LineStart)
              .Append('|').Append(m.LineEnd).Append('\n');

        foreach (var s in doc.StepDefinitions)
            sb.Append("stepdef|").Append(s.Id).Append('|').Append(s.MethodId).Append('|').Append(s.ClassId)
              .Append('|').Append(s.ProjectId).Append('|').Append(s.Keyword).Append('|').Append(s.Expression)
              .Append('|').Append(s.ExpressionKind).Append('|').Append(s.Parameters).Append('|')
              .Append(s.FilePath).Append('|').Append(s.LineStart).Append('\n');

        foreach (var f in doc.Features)
            sb.Append("feature|").Append(f.Id).Append('|').Append(f.ProjectId).Append('|').Append(f.Name)
              .Append('|').Append(f.Tags).Append('|').Append(f.FilePath).Append('\n');

        foreach (var s in doc.Scenarios)
            sb.Append("scenario|").Append(s.Id).Append('|').Append(s.FeatureId).Append('|').Append(s.Name)
              .Append('|').Append(s.Kind).Append('|').Append(s.Tags).Append('|').Append(s.ExampleRowCount)
              .Append('|').Append(s.LineStart).Append('\n');

        foreach (var s in doc.ScenarioSteps)
            sb.Append("step|").Append(s.Id).Append('|').Append(s.ScenarioId).Append('|').Append(s.Keyword)
              .Append('|').Append(s.Text).Append('|').Append(s.Ordinal).Append('|').Append(s.LineStart).Append('\n');

        foreach (var e in doc.Endpoints)
            sb.Append("endpoint|").Append(e.Id).Append('|').Append(e.Verb).Append('|').Append(e.Route).Append('\n');

        foreach (var e in doc.Edges)
            sb.Append("edge|").Append(e.FromKind).Append('|').Append(e.FromId).Append('|').Append(e.ToKind)
              .Append('|').Append(e.ToId).Append('|').Append(e.EdgeKind).Append('|').Append(e.Confidence).Append('\n');

        foreach (var d in doc.Diagnostics)
            sb.Append("diag|").Append(d.Severity).Append('|').Append(d.Code).Append('|')
              .Append(d.Message).Append('|').Append(d.Location).Append('\n');

        return sb.ToString();
    }

    private static int ReadUserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static Dictionary<string, string> ReadMeta(SqliteConnection conn)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM meta;";
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = r.IsDBNull(1) ? string.Empty : r.GetString(1);
        return map;
    }

    private static List<ProjectRow> ReadProjects(SqliteConnection conn)
    {
        var list = new List<ProjectRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, path, target_framework, kind FROM projects ORDER BY id;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ProjectRow(r.GetInt32(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4)));
        return list;
    }

    private static List<ClassRow> ReadClasses(SqliteConnection conn)
    {
        var list = new List<ClassRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, project_id, name, namespace, base_type, kind, file_path, line_start, line_end " +
            "FROM classes ORDER BY id;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ClassRow(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetString(6),
                r.GetInt32(7), r.GetInt32(8)));
        return list;
    }

    private static List<MethodRow> ReadMethods(SqliteConnection conn)
    {
        var list = new List<MethodRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, class_id, project_id, name, signature, visibility, kind, file_path, line_start, line_end " +
            "FROM methods ORDER BY id;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new MethodRow(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3),
                r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
                r.GetInt32(8), r.GetInt32(9)));
        return list;
    }

    private static bool TableExists(SqliteConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n;";
        var p = cmd.CreateParameter(); p.ParameterName = "$n"; p.Value = name; cmd.Parameters.Add(p);
        return cmd.ExecuteScalar() is not null;
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (string.Equals(r.GetString(1), column, StringComparison.Ordinal)) return true;
        return false;
    }

    private static List<StepDefinitionRow> ReadStepDefinitions(SqliteConnection conn)
    {
        var list = new List<StepDefinitionRow>();
        if (!TableExists(conn, "step_definitions")) return list; // older map (schema v1) — no crash
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, method_id, class_id, project_id, keyword, expression, expression_kind, parameters, file_path, line_start " +
            "FROM step_definitions ORDER BY id;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new StepDefinitionRow(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3),
                r.GetString(4), r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
                r.GetString(8), r.GetInt32(9)));
        return list;
    }

    private static List<FeatureRow> ReadFeatures(SqliteConnection conn)
    {
        var list = new List<FeatureRow>();
        if (!TableExists(conn, "features")) return list;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, project_id, name, description, tags, file_path FROM features ORDER BY id;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new FeatureRow(r.GetInt32(0), r.GetInt32(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5)));
        return list;
    }

    private static List<ScenarioRow> ReadScenarios(SqliteConnection conn)
    {
        var list = new List<ScenarioRow>();
        if (!TableExists(conn, "scenarios")) return list;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, feature_id, project_id, name, kind, tags, example_row_count, file_path, line_start FROM scenarios ORDER BY id;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ScenarioRow(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.GetInt32(6), r.GetString(7), r.GetInt32(8)));
        return list;
    }

    private static List<ScenarioStepRow> ReadScenarioSteps(SqliteConnection conn)
    {
        var list = new List<ScenarioStepRow>();
        if (!TableExists(conn, "scenario_steps")) return list;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, scenario_id, project_id, keyword, text, ordinal, has_docstring, has_table, file_path, line_start FROM scenario_steps ORDER BY id;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ScenarioStepRow(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3), r.GetString(4),
                r.GetInt32(5), r.GetInt32(6) != 0, r.GetInt32(7) != 0, r.GetString(8), r.GetInt32(9)));
        return list;
    }

    private static List<EdgeRow> ReadEdges(SqliteConnection conn)
    {
        var list = new List<EdgeRow>();
        if (!TableExists(conn, "edges")) return list;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT from_kind, from_id, to_kind, to_id, edge_kind, confidence FROM edges ORDER BY id;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new EdgeRow(r.GetString(0), r.GetInt32(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetInt32(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return list;
    }

    private static List<EndpointRow> ReadEndpoints(SqliteConnection conn)
    {
        var list = new List<EndpointRow>();
        if (!TableExists(conn, "endpoints")) return list; // tolerate pre-v4 maps
        var enriched = ColumnExists(conn, "endpoints", "path"); // v5+ carries path/target_api
        using var cmd = conn.CreateCommand();
        cmd.CommandText = enriched
            ? "SELECT id, verb, route, path, target_api FROM endpoints ORDER BY id;"
            : "SELECT id, verb, route FROM endpoints ORDER BY id;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new EndpointRow(r.GetInt32(0), r.GetString(1), r.GetString(2),
                enriched && !r.IsDBNull(3) ? r.GetString(3) : null,
                enriched && !r.IsDBNull(4) ? r.GetString(4) : null));
        return list;
    }

    /// <summary>FTS5 search over <c>search_steps</c> (spec §5.3 / A7). Returns matching step-def rowids.</summary>
    public static IReadOnlyList<long> SearchSteps(string dbPath, string query)
        => SearchFts(dbPath, "search_steps", query);

    /// <summary>FTS5 search over <c>search_scenarios</c> (spec §5.3). Returns matching scenario rowids.</summary>
    public static IReadOnlyList<long> SearchScenarios(string dbPath, string query)
        => SearchFts(dbPath, "search_scenarios", query);

    private static IReadOnlyList<long> SearchFts(string dbPath, string table, string query)
    {
        var match = ToFtsMatch(query);
        var cs = new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(dbPath), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var ids = new List<long>();
        // Empty after sanitisation (blank or punctuation-only) → no rows, never an error.
        if (match.Length > 0 && TableExists(conn, table))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT rowid FROM {table} WHERE {table} MATCH $q ORDER BY rowid;";
            var p = cmd.CreateParameter(); p.ParameterName = "$q"; p.Value = match; cmd.Parameters.Add(p);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }
        SqliteConnection.ClearAllPools();
        return ids;
    }

    /// <summary>
    /// Turn arbitrary user text into a safe FTS5 MATCH expression. FTS5 query syntax is picky — a bare
    /// hyphen, colon, quote, <c>*</c>, or a reserved token is a *syntax error*, not "no match" (an agent
    /// searching for <c>GDV2013-NT015</c> or an unbalanced quote would otherwise fault). Each whitespace
    /// token is stripped of quotes and wrapped in double quotes, which FTS5 treats as a literal phrase, so
    /// no character is ever interpreted as an operator; tokens with no letter/digit are dropped. Terms are
    /// AND-ed (all must appear) — the sensible default for narrowing search.
    /// </summary>
    internal static string ToFtsMatch(string? query)
    {
        var tokens = (query ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Replace("\"", " ").Trim())
            .Where(t => t.Any(char.IsLetterOrDigit))
            .Select(t => '"' + t + '"');
        return string.Join(' ', tokens);
    }

    private static List<DiagnosticRow> ReadDiagnostics(SqliteConnection conn)
    {
        var list = new List<DiagnosticRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT severity, code, message, location FROM diagnostics ORDER BY id;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new DiagnosticRow(r.GetString(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3)));
        return list;
    }
}
