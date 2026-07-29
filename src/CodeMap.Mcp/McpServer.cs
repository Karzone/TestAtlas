using System.Text.Json;
using TestAtlas.Core.Analysis;
using TestAtlas.Core.Binding;
using TestAtlas.Core.Model;
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

        new("resolve_step",
            "Resolve a Gherkin step phrase to the EXISTING step definition(s) that would bind it — the same way the runner does " +
            "(regex/cucumber expression, keyword-agnostic). Use this BEFORE writing a new step so an agent reuses what already exists " +
            "instead of authoring a duplicate. status is 'exact' (one binding — reuse it), 'ambiguous' (several match — a conflict to " +
            "resolve), or 'none' (nothing binds; returns near-match suggestions to adapt). Each match returns the expression, the C# " +
            "class/method, the method parameters, the argument values captured from the phrase, and file:line.",
            new
            {
                type = "object",
                properties = new
                {
                    text = new { type = "string", description = "The step phrase to resolve, without the leading Given/When/Then keyword (e.g. \"the customer checks out\")." },
                    keyword = new { type = "string", @enum = new[] { "given", "when", "then" }, description = "Optional; informational only — matching is keyword-agnostic, as in Reqnroll/SpecFlow." },
                },
                required = new[] { "text" },
            },
            ResolveStep),

        new("unbound_steps",
            "Scenario steps that match NO step definition (unbound) — the glue an agent must implement before those scenarios can run. " +
            "Each row: the step text, its keyword, the owning scenario + feature, and file:line. Use this to see exactly what step " +
            "definitions are missing across the suite.",
            new { type = "object", properties = new { limit = new { type = "integer", description = "Max rows (default 50)." } } },
            UnboundSteps),
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

    private string ResolveStep(JsonElement args)
    {
        var text = Arg(args, "text");
        if (string.IsNullOrWhiteSpace(text))
            return Serialize(new { error = "resolve_step requires 'text' (the step phrase to resolve)." });

        // Build + compile candidate bindings from the map's step definitions, tying each back to its
        // StepDefinition id via the binding Reference. This reuses the exact matcher the indexer binds
        // with, so "would this phrase bind?" matches runtime resolution (regex/cucumber, keyword-agnostic).
        var compiled = new List<CompiledBinding>(_doc.StepDefinitions.Count);
        foreach (var sd in _doc.StepDefinitions)
        {
            var binding = new StepBinding(
                ParseBindingKeyword(sd.Keyword),
                sd.Expression,
                sd.ExpressionKind == ExpressionKinds.CucumberExpression ? ExpressionKind.CucumberExpression : ExpressionKind.Regex,
                Reference: sd.Id.ToString());
            var c = StepMatcher.Compile(binding);
            if (c is not null) compiled.Add(c);
        }

        var result = StepMatcher.Match(new ScenarioStepInput(ParseStepKeyword(Arg(args, "keyword")), text), compiled);

        var sdById = _doc.StepDefinitions.ToDictionary(s => s.Id);
        var matches = result.Matches
            .Select(m => int.TryParse(m.Binding.Reference, out var id) && sdById.TryGetValue(id, out var sd) ? (sd, m.Parameters) : default)
            .Where(x => x.sd is not null)
            .Select(x => new
            {
                expression = x.sd!.Expression,
                expressionKind = x.sd.ExpressionKind,
                keyword = x.sd.Keyword,
                capturedArguments = x.Parameters,
                methodParameters = x.sd.Parameters,
                @class = _doc.Classes.FirstOrDefault(c => c.Id == x.sd.ClassId)?.Name,
                method = _doc.Methods.FirstOrDefault(mm => mm.Id == x.sd.MethodId)?.Name,
                location = $"{x.sd.FilePath}:{x.sd.LineStart}",
            })
            .ToList();

        var status = result.Confidence switch
        {
            MatchConfidence.Exact => "exact",
            MatchConfidence.Ambiguous => "ambiguous",
            _ => "none",
        };

        // No binding matched → offer near-matches (FTS over step text) so the agent adapts an existing
        // step instead of authoring a duplicate. Empty when nothing is lexically close — then author anew.
        object? suggestions = null;
        if (result.Confidence == MatchConfidence.Unbound)
        {
            var ids = MapReader.SearchSteps(_dbPath, text!).ToHashSet();
            suggestions = _doc.StepDefinitions.Where(s => ids.Contains(s.Id)).Take(10)
                .Select(s => new { expression = s.Expression, keyword = s.Keyword, location = $"{s.FilePath}:{s.LineStart}" });
        }

        return Serialize(new { status, text, matchCount = matches.Count, matches, suggestions });
    }

    private string UnboundSteps(JsonElement args)
    {
        var limit = LimitArg(args, 50);
        var unboundIds = _doc.Edges
            .Where(e => e.EdgeKind == EdgeKinds.Unbound && e.FromKind == RefKinds.ScenarioStep)
            .Select(e => e.FromId).ToHashSet();

        var scenarioById = _doc.Scenarios.ToDictionary(s => s.Id);
        var featureById = _doc.Features.ToDictionary(f => f.Id);

        var all = _doc.ScenarioSteps.Where(s => unboundIds.Contains(s.Id))
            .OrderBy(s => s.FilePath, StringComparer.Ordinal).ThenBy(s => s.LineStart)
            .ToList();

        var rows = all.Take(limit).Select(st =>
        {
            var sc = scenarioById.TryGetValue(st.ScenarioId, out var s) ? s : null;
            var feature = sc is not null && featureById.TryGetValue(sc.FeatureId, out var f) ? f.Name : null;
            return new
            {
                step = st.Text,
                keyword = st.Keyword,
                scenario = sc?.Name,
                feature,
                location = $"{st.FilePath}:{st.LineStart}",
            };
        });

        return Serialize(new { count = all.Count, truncated = all.Count > limit ? all.Count - limit : 0, steps = rows });
    }

    private static BindingKeyword ParseBindingKeyword(string? k) => (k ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "given" => BindingKeyword.Given,
        "when" => BindingKeyword.When,
        "then" => BindingKeyword.Then,
        _ => BindingKeyword.StepDefinition,
    };

    private static StepKeyword ParseStepKeyword(string? k) => (k ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "when" => StepKeyword.When,
        "then" => StepKeyword.Then,
        _ => StepKeyword.Given,
    };

    private static int LimitArg(JsonElement args, int def)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number
            ? Math.Clamp(l.GetInt32(), 1, MaxRows) : def;

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
