using System.Text.Json;
using TestAtlas.Core.Analysis;
using TestAtlas.Core.Storage;

namespace TestAtlas.Mcp;

/// <summary>
/// The Model Context Protocol server surface over a TestAtlas map (<c>atlas.db</c>). Read-only: it
/// answers an agent's queries — impact/blast-radius, endpoint reach, lexical search, summary stats —
/// against the same map the CLI/report read, via <see cref="MapReader"/> + <see cref="ImpactAnalyzer"/>.
///
/// Protocol handling is hand-rolled JSON-RPC 2.0 (no external MCP SDK, keeping the tool dependency-free
/// and offline). <see cref="HandleLine"/> is pure — one request line in, one response line out (or null
/// for a notification) — so the whole tool surface is unit-testable without touching stdio.
/// </summary>
public sealed class McpServer
{
    private const string ProtocolVersion = "2024-11-05";
    private const string ServerName = "testatlas";
    private const string ServerVersion = "2.0.0";
    private const int MaxRows = 200; // cap any list response so a huge map can't flood the agent

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _dbPath;
    private readonly MapDocument _doc;
    private readonly IReadOnlyList<ToolDef> _tools;

    public McpServer(string dbPath) : this(dbPath, MapReader.Read(dbPath)) { }

    /// <summary>Test seam: inject a preloaded map instead of reading from disk.</summary>
    public McpServer(string dbPath, MapDocument doc)
    {
        _dbPath = dbPath;
        _doc = doc;
        _tools = BuildTools();
    }

    private sealed record ToolDef(string Name, string Description, object InputSchema, Func<JsonElement, string> Handler);

    /// <summary>
    /// Handle one JSON-RPC request line; returns the response line, or null for a notification (no id).
    /// Never throws — a fault becomes a JSON-RPC error response so the transport loop stays alive.
    /// </summary>
    public string? HandleLine(string line)
    {
        JsonElement root;
        try { using var doc = JsonDocument.Parse(line); root = doc.RootElement.Clone(); }
        catch { return Serialize(new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32700, message = "Parse error" } }); }

        var hasId = root.TryGetProperty("id", out var idEl);
        object? id = hasId ? JsonElementToId(idEl) : null;
        var method = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

        // Notifications (no id) get no response — e.g. notifications/initialized.
        if (!hasId) return null;

        try
        {
            return method switch
            {
                "initialize" => Result(id, new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = ServerName, version = ServerVersion },
                }),
                "ping" => Result(id, new { }),
                "tools/list" => Result(id, new { tools = _tools.Select(t => new { name = t.Name, description = t.Description, inputSchema = t.InputSchema }) }),
                "tools/call" => HandleToolCall(id, root),
                _ => Error(id, -32601, $"Method not found: {method}"),
            };
        }
        catch (Exception ex)
        {
            return Error(id, -32603, $"Internal error: {ex.Message}");
        }
    }

    private string HandleToolCall(object? id, JsonElement root)
    {
        var @params = root.TryGetProperty("params", out var p) ? p : default;
        var name = @params.ValueKind == JsonValueKind.Object && @params.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() : null;
        var args = @params.ValueKind == JsonValueKind.Object && @params.TryGetProperty("arguments", out var a) ? a : default;

        var tool = _tools.FirstOrDefault(t => t.Name == name);
        if (tool is null) return Error(id, -32602, $"Unknown tool: {name}");

        var text = tool.Handler(args);
        // Per MCP, a tool result is content blocks; text carries the (JSON) payload the agent parses.
        return Result(id, new { content = new[] { new { type = "text", text } }, isError = false });
    }

    // ---- tools -------------------------------------------------------------------------------------

    private IReadOnlyList<ToolDef> BuildTools() => new List<ToolDef>
    {
        new("stats", "Summary of the test map: project/class/method counts, class-kind breakdown, endpoints, and edge tallies.",
            new { type = "object", properties = new { } },
            _ => Stats()),

        new("impact",
            "Blast radius of a change: the test scenarios affected by changing a class, method, step definition, or endpoint. " +
            "Returns the affected scenarios (feature + the connecting step text), plus step-definition and feature counts.",
            new
            {
                type = "object",
                properties = new
                {
                    target = new { type = "string", @enum = new[] { "class", "method", "step", "endpoint" }, description = "What kind of entity to trace." },
                    value = new { type = "string", description = "The name/route to match (class or method name, step expression substring, or endpoint route substring)." },
                },
                required = new[] { "target", "value" },
            },
            Impact),

        new("search_steps", "Full-text search over step definitions (expression text + method + class name). Returns matching step definitions.",
            new { type = "object", properties = new { query = new { type = "string", description = "Search terms." } }, required = new[] { "query" } },
            a => SearchSteps(Arg(a, "query"))),

        new("search_scenarios", "Full-text search over scenarios (feature name + scenario name + step text + tags). Returns matching scenarios.",
            new { type = "object", properties = new { query = new { type = "string", description = "Search terms." } }, required = new[] { "query" } },
            a => SearchScenarios(Arg(a, "query"))),

        new("list_endpoints", "The HTTP endpoints/operations the suite calls, each with verb, route (real path when known), and its scenario blast radius. Highest-reach first.",
            new { type = "object", properties = new { limit = new { type = "integer", description = "Max rows (default 50)." } } },
            ListEndpoints),
    };

    private string Stats()
    {
        var kinds = _doc.Classes.GroupBy(c => c.Kind).OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());
        var edges = _doc.Edges.GroupBy(e => e.EdgeKind).OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());
        return Serialize(new
        {
            solution = _doc.Meta.TryGetValue(MapSchema.MetaSolutionPath, out var s) ? s : null,
            schemaVersion = _doc.UserVersion,
            projects = _doc.Projects.Count,
            classes = _doc.Classes.Count,
            methods = _doc.Methods.Count,
            classKinds = kinds,
            stepDefinitions = _doc.StepDefinitions.Count,
            features = _doc.Features.Count,
            scenarios = _doc.Scenarios.Count,
            endpoints = _doc.Endpoints.Count,
            edges,
        });
    }

    private string Impact(JsonElement args)
    {
        var target = Arg(args, "target");
        var value = Arg(args, "value");
        var kind = target switch
        {
            "class" => ImpactTargetKind.Class,
            "method" => ImpactTargetKind.Method,
            "step" => ImpactTargetKind.Step,
            "endpoint" => ImpactTargetKind.Endpoint,
            _ => (ImpactTargetKind?)null,
        };
        if (kind is null || string.IsNullOrEmpty(value))
            return Serialize(new { error = "impact requires 'target' (class|method|step|endpoint) and 'value'." });

        var r = ImpactAnalyzer.Analyze(_doc, new ImpactQuery(kind.Value, value));
        return Serialize(new
        {
            found = r.Found,
            target = r.TargetLabel,
            stepDefinitions = r.StepDefinitionCount,
            features = r.FeatureCount,
            scenarios = r.Scenarios.Count,
            affected = r.Scenarios.Take(MaxRows).Select(sc => new { scenario = sc.Scenario, feature = sc.Feature, via = sc.Via }),
            truncated = r.Scenarios.Count > MaxRows ? r.Scenarios.Count - MaxRows : 0,
        });
    }

    private string SearchSteps(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Serialize(new { error = "search_steps requires 'query'." });
        var ids = MapReader.SearchSteps(_dbPath, query).ToHashSet();
        var hits = _doc.StepDefinitions.Where(s => ids.Contains(s.Id)).Take(MaxRows)
            .Select(s => new
            {
                expression = s.Expression,
                keyword = s.Keyword,
                @class = _doc.Classes.FirstOrDefault(c => c.Id == s.ClassId)?.Name,
                location = $"{s.FilePath}:{s.LineStart}",
            });
        return Serialize(new { count = ids.Count, hits });
    }

    private string SearchScenarios(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Serialize(new { error = "search_scenarios requires 'query'." });
        var ids = MapReader.SearchScenarios(_dbPath, query).ToHashSet();
        var featureById = _doc.Features.ToDictionary(f => f.Id);
        var hits = _doc.Scenarios.Where(s => ids.Contains(s.Id)).Take(MaxRows)
            .Select(s => new
            {
                scenario = s.Name,
                feature = featureById.TryGetValue(s.FeatureId, out var f) ? f.Name : null,
                tags = string.IsNullOrEmpty(s.Tags) ? null : s.Tags,
                location = $"{s.FilePath}:{s.LineStart}",
            });
        return Serialize(new { count = ids.Count, hits });
    }

    private string ListEndpoints(JsonElement args)
    {
        var limit = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number
            ? Math.Clamp(l.GetInt32(), 1, MaxRows) : 50;
        var reach = ImpactAnalyzer.EndpointReachAll(_doc);
        var rows = _doc.Endpoints
            .Select(e => (Ep: e, Scenarios: reach.TryGetValue(e.Id, out var r) ? r.ScenarioIds.Count : 0,
                          CallSites: reach.TryGetValue(e.Id, out var r2) ? r2.CallSiteCount : 0))
            .OrderByDescending(x => x.Scenarios).ThenByDescending(x => x.CallSites)
            .ThenBy(x => x.Ep.Route, StringComparer.Ordinal)
            .Take(limit)
            .Select(x => new
            {
                verb = x.Ep.Verb,
                route = x.Ep.Path ?? x.Ep.Route,
                requestType = x.Ep.Path is null ? null : x.Ep.Route,
                targetApi = x.Ep.TargetApi,
                callSites = x.CallSites,
                scenarios = x.Scenarios,
            });
        return Serialize(new { total = _doc.Endpoints.Count, endpoints = rows });
    }

    // ---- JSON-RPC plumbing -------------------------------------------------------------------------

    private static string Result(object? id, object result) => Serialize(new { jsonrpc = "2.0", id, result });
    private static string Error(object? id, int code, string message) => Serialize(new { jsonrpc = "2.0", id, error = new { code, message } });
    private static string Serialize(object o) => JsonSerializer.Serialize(o, Json);

    private static string? Arg(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>Preserve the request id's JSON type (number or string) for the response.</summary>
    private static object? JsonElementToId(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.Number => id.TryGetInt64(out var n) ? n : id.GetDouble(),
        JsonValueKind.String => id.GetString(),
        _ => null,
    };
}
