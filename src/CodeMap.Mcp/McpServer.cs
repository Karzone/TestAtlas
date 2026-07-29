using System.Text.Json;
using System.Text.RegularExpressions;
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
            "resolve), or 'none' (nothing binds — returns existing step definitions ranked by shared terms, to adapt rather than duplicate). Each match returns the expression, the C# " +
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

        new("get_scenario",
            "Full detail of scenario(s) whose name contains the given text: feature, tags, kind, example-row count, file:line, " +
            "and the ordered steps (keyword + text + doc-string/data-table flags). Use to read an existing scenario before writing a similar one.",
            new
            {
                type = "object",
                properties = new
                {
                    name = new { type = "string", description = "Substring of the scenario name to match (case-insensitive)." },
                    limit = new { type = "integer", description = "Max scenarios (default 10)." },
                },
                required = new[] { "name" },
            },
            GetScenario),

        new("get_step_definition",
            "Full detail of step definition(s) whose expression contains the given text: keyword, expression kind, method parameters, " +
            "C# class/method/signature, file:line, and the scenarios that currently use it (usage count). Use to inspect a step before reusing or changing it.",
            new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "Substring of the step-definition expression to match (case-insensitive)." },
                    limit = new { type = "integer", description = "Max definitions (default 20)." },
                },
                required = new[] { "query" },
            },
            GetStepDefinition),

        new("list_tags",
            "The tag taxonomy across the suite — every scenario tag (e.g. @smoke, @regression, ticket ids) with the number of scenarios " +
            "carrying it, most-used first. Use to tag new scenarios consistently with what already exists.",
            new { type = "object", properties = new { limit = new { type = "integer", description = "Max tags (default 200)." } } },
            ListTags),

        new("step_catalog",
            "The reusable step vocabulary: step definitions with their placeholders and (best-effort) allowed values pulled from the " +
            "expression — cucumber {int}/{string}/{word}, regex alternations like (Auto|Allianz) as enum values, other groups as free " +
            "parameters. Use to compose new scenarios from steps and values that already exist. Optional keyword/query filters.",
            new
            {
                type = "object",
                properties = new
                {
                    keyword = new { type = "string", @enum = new[] { "given", "when", "then", "stepdefinition" }, description = "Optional: only steps declared with this attribute keyword." },
                    query = new { type = "string", description = "Optional: only steps whose expression contains this text." },
                    limit = new { type = "integer", description = "Max steps (default 100)." },
                },
            },
            StepCatalog),

        new("coverage_gaps",
            "Where the suite has holes: HTTP endpoints with zero scenario reach (untested), and step definitions that no scenario binds " +
            "(dead glue). Use to decide what to automate next, or to prune. Counts are exact; lists are capped by 'limit'.",
            new { type = "object", properties = new { limit = new { type = "integer", description = "Max rows per category (default 50)." } } },
            CoverageGaps),

        new("project_dependencies",
            "The project dependency graph the suite implies: for each project, which projects it depends on and which depend on it, " +
            "derived from cross-project binds_to/uses_type/inherits edges (edge counts as weight). Answers e.g. \"what depends on the " +
            "Party project?\". Optional 'project' name filter.",
            new
            {
                type = "object",
                properties = new
                {
                    project = new { type = "string", description = "Optional: substring of a project name to focus on (case-insensitive)." },
                    limit = new { type = "integer", description = "Max projects (default 200)." },
                },
            },
            ProjectDependencies),
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
            // A method decorated with e.g. [Given]+[When] yields several bindings for the SAME reusable
            // step at one location — collapse them so it reads as one step, not a false 'ambiguous'.
            .GroupBy(x => (x.sd!.FilePath, x.sd.LineStart, x.sd.Expression))
            .Select(g => g.First())
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

        // Classify by distinct reusable steps: none / exact (reuse it) / ambiguous (a conflict to fix).
        var status = matches.Count switch { 0 => "none", 1 => "exact", _ => "ambiguous" };

        // Nothing binds → rank existing step defs by how many of the phrase's salient tokens they share
        // (an OR, not an all-tokens AND) so a near-miss that swaps a word still surfaces the closest
        // steps to adapt — reuse-first. Empty only when nothing is lexically close; then author anew.
        object? suggestions = null;
        if (matches.Count == 0)
        {
            var score = new Dictionary<int, int>();
            foreach (var tok in SalientTokens(text!))
                foreach (var id in MapReader.SearchSteps(_dbPath, tok))
                    score[(int)id] = score.TryGetValue((int)id, out var n) ? n + 1 : 1;

            suggestions = score.OrderByDescending(kv => kv.Value)
                .Select(kv => sdById.TryGetValue(kv.Key, out var s) ? s : null)
                .Where(s => s is not null).Take(10)
                .Select(s => new { expression = s!.Expression, keyword = s.Keyword, location = $"{s.FilePath}:{s.LineStart}", sharedTerms = score[s.Id] });
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

    private string GetScenario(JsonElement args)
    {
        var name = Arg(args, "name");
        if (string.IsNullOrWhiteSpace(name)) return Serialize(new { error = "get_scenario requires 'name'." });
        var limit = LimitArg(args, 10);

        var featureById = _doc.Features.ToDictionary(f => f.Id);
        var stepsByScenario = _doc.ScenarioSteps.GroupBy(s => s.ScenarioId)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Ordinal).ToList());

        var matches = _doc.Scenarios
            .Where(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.FilePath, StringComparer.Ordinal).ThenBy(s => s.LineStart)
            .Take(limit)
            .Select(s => new
            {
                scenario = s.Name,
                feature = featureById.TryGetValue(s.FeatureId, out var f) ? f.Name : null,
                kind = s.Kind,
                tags = string.IsNullOrEmpty(s.Tags) ? null : s.Tags,
                exampleRowCount = s.ExampleRowCount,
                location = $"{s.FilePath}:{s.LineStart}",
                steps = (stepsByScenario.TryGetValue(s.Id, out var st) ? st : new List<ScenarioStepRow>())
                    .Select(x => new { keyword = x.Keyword, text = x.Text, hasDocString = x.HasDocString, hasDataTable = x.HasDataTable }),
            })
            .ToList();

        return Serialize(new { count = matches.Count, scenarios = matches });
    }

    private string GetStepDefinition(JsonElement args)
    {
        var query = Arg(args, "query");
        if (string.IsNullOrWhiteSpace(query)) return Serialize(new { error = "get_step_definition requires 'query'." });
        var limit = LimitArg(args, 20);

        // step_definition id -> the scenario names that bind it (via binds_to edges: scenario_step -> step_definition).
        var scenarioByStep = _doc.ScenarioSteps.ToDictionary(s => s.Id, s => s.ScenarioId);
        var scenarioNameById = _doc.Scenarios.ToDictionary(s => s.Id, s => s.Name);
        var scenariosByDef = _doc.Edges
            .Where(e => e.EdgeKind == EdgeKinds.BindsTo && e.ToKind == RefKinds.StepDefinition && e.ToId is not null)
            .GroupBy(e => e.ToId!.Value)
            .ToDictionary(g => g.Key, g => g
                .Select(e => scenarioByStep.TryGetValue(e.FromId, out var sc) ? sc : (int?)null)
                .Where(x => x is not null).Select(x => x!.Value).Distinct().ToList());

        var matches = _doc.StepDefinitions
            .Where(s => s.Expression.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .Select(s =>
            {
                var scenarioIds = scenariosByDef.TryGetValue(s.Id, out var l) ? l : new List<int>();
                var method = _doc.Methods.FirstOrDefault(m => m.Id == s.MethodId);
                return new
                {
                    expression = s.Expression,
                    keyword = s.Keyword,
                    expressionKind = s.ExpressionKind,
                    methodParameters = s.Parameters,
                    @class = _doc.Classes.FirstOrDefault(c => c.Id == s.ClassId)?.Name,
                    method = method?.Name,
                    signature = method?.Signature,
                    location = $"{s.FilePath}:{s.LineStart}",
                    usageCount = scenarioIds.Count,
                    usedByScenarios = scenarioIds.Select(id => scenarioNameById.TryGetValue(id, out var n) ? n : null)
                        .Where(n => n is not null).Take(25),
                };
            })
            .ToList();

        return Serialize(new { count = matches.Count, stepDefinitions = matches });
    }

    private string ListTags(JsonElement args)
    {
        var limit = LimitArg(args, MaxRows);
        // Scenario tags are own + inherited (the model already folds in feature tags), so counting
        // scenarios per tag gives the true reach without double-counting the feature.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in _doc.Scenarios)
        {
            if (string.IsNullOrWhiteSpace(s.Tags)) continue;
            foreach (var tag in s.Tags.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                counts[tag] = counts.TryGetValue(tag, out var n) ? n + 1 : 1;
        }

        var rows = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(limit).Select(kv => new { tag = kv.Key, scenarios = kv.Value });
        return Serialize(new { total = counts.Count, tags = rows });
    }

    private string StepCatalog(JsonElement args)
    {
        var limit = LimitArg(args, 100);
        var keyword = Arg(args, "keyword");
        var query = Arg(args, "query");

        var q = _doc.StepDefinitions.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(keyword))
            q = q.Where(s => string.Equals(s.Keyword, keyword, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(query))
            q = q.Where(s => s.Expression.Contains(query, StringComparison.OrdinalIgnoreCase));

        var all = q.ToList();
        var rows = all.Take(limit).Select(s => new
        {
            expression = s.Expression,
            keyword = s.Keyword,
            expressionKind = s.ExpressionKind,
            methodParameters = s.Parameters,
            placeholders = ExtractPlaceholders(s.Expression, s.ExpressionKind),
            @class = _doc.Classes.FirstOrDefault(c => c.Id == s.ClassId)?.Name,
            location = $"{s.FilePath}:{s.LineStart}",
        });
        return Serialize(new { total = all.Count, count = Math.Min(all.Count, limit), steps = rows });
    }

    private string CoverageGaps(JsonElement args)
    {
        var limit = LimitArg(args, 50);

        var reach = ImpactAnalyzer.EndpointReachAll(_doc);
        bool Untested(EndpointRow e) => !(reach.TryGetValue(e.Id, out var r) && r.ScenarioIds.Count > 0);
        var untested = _doc.Endpoints.Where(Untested).OrderBy(e => e.Route, StringComparer.Ordinal).ToList();

        var boundDefIds = _doc.Edges
            .Where(e => e.EdgeKind == EdgeKinds.BindsTo && e.ToKind == RefKinds.StepDefinition && e.ToId is not null)
            .Select(e => e.ToId!.Value).ToHashSet();
        var unused = _doc.StepDefinitions.Where(s => !boundDefIds.Contains(s.Id)).ToList();

        return Serialize(new
        {
            untestedEndpointCount = untested.Count,
            untestedEndpoints = untested.Take(limit).Select(e => new { verb = e.Verb, route = e.Path ?? e.Route }),
            unusedStepDefinitionCount = unused.Count,
            unusedStepDefinitions = unused.Take(limit).Select(s => new
            {
                expression = s.Expression,
                keyword = s.Keyword,
                @class = _doc.Classes.FirstOrDefault(c => c.Id == s.ClassId)?.Name,
                location = $"{s.FilePath}:{s.LineStart}",
            }),
        });
    }

    private string ProjectDependencies(JsonElement args)
    {
        var filter = Arg(args, "project");
        var limit = LimitArg(args, MaxRows);

        // Resolve each edge endpoint to its owning project, then aggregate cross-project edges into a
        // weighted project graph — the same derivation the CLI `map` (ProjectMapBuilder) uses.
        var stepProj = _doc.ScenarioSteps.ToDictionary(s => s.Id, s => s.ProjectId);
        var stepDefProj = _doc.StepDefinitions.ToDictionary(s => s.Id, s => s.ProjectId);
        var methodProj = _doc.Methods.ToDictionary(m => m.Id, m => m.ProjectId);
        var classProj = _doc.Classes.ToDictionary(c => c.Id, c => c.ProjectId);
        int? ProjectOf(string kind, int? id) => id is not int i ? null : kind switch
        {
            RefKinds.ScenarioStep => stepProj.TryGetValue(i, out var p) ? p : null,
            RefKinds.StepDefinition => stepDefProj.TryGetValue(i, out var p) ? p : null,
            RefKinds.Method => methodProj.TryGetValue(i, out var p) ? p : null,
            RefKinds.Class => classProj.TryGetValue(i, out var p) ? p : null,
            RefKinds.Project => i,
            _ => null,
        };

        var weight = new Dictionary<(int From, int To), int>();
        foreach (var e in _doc.Edges)
        {
            if (e.EdgeKind == EdgeKinds.Unbound) continue;
            if (ProjectOf(e.FromKind, e.FromId) is not int a || ProjectOf(e.ToKind, e.ToId) is not int b || a == b) continue;
            weight[(a, b)] = weight.TryGetValue((a, b), out var w) ? w + 1 : 1;
        }

        var nameById = _doc.Projects.ToDictionary(p => p.Id, p => p.Name);
        var projects = _doc.Projects.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(filter))
            projects = projects.Where(p => p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));

        var rows = projects.OrderBy(p => p.Name, StringComparer.Ordinal).Take(limit).Select(p => new
        {
            project = p.Name,
            dependsOn = weight.Where(kv => kv.Key.From == p.Id).OrderByDescending(kv => kv.Value)
                .Select(kv => new { project = nameById.TryGetValue(kv.Key.To, out var n) ? n : null, edges = kv.Value }),
            dependedOnBy = weight.Where(kv => kv.Key.To == p.Id).OrderByDescending(kv => kv.Value)
                .Select(kv => new { project = nameById.TryGetValue(kv.Key.From, out var n) ? n : null, edges = kv.Value }),
        }).ToList();

        return Serialize(new { count = rows.Count, projects = rows });
    }

    /// <summary>
    /// Best-effort extraction of a step expression's parameters: cucumber <c>{type}</c> placeholders,
    /// regex alternation groups <c>(a|b|c)</c> as enum values, and any other capture group as a free
    /// parameter. Degrades to an empty list rather than throwing on anything it doesn't recognise.
    /// </summary>
    private static IReadOnlyList<object> ExtractPlaceholders(string? expression, string expressionKind)
    {
        var list = new List<object>();
        if (string.IsNullOrEmpty(expression)) return list;

        if (expressionKind == ExpressionKinds.CucumberExpression)
        {
            foreach (Match m in Regex.Matches(expression, @"\{([^}]*)\}"))
            {
                var type = m.Groups[1].Value;
                list.Add(new { kind = "typed", type = string.IsNullOrEmpty(type) ? "any" : type });
            }
            return list;
        }

        // regex: scan non-nested capture groups.
        foreach (Match m in Regex.Matches(expression, @"\(([^()]*)\)"))
        {
            var inner = m.Groups[1].Value;
            if (inner.StartsWith("?:", StringComparison.Ordinal)) inner = inner.Substring(2);
            if (inner.Length == 0) continue;
            var alts = inner.Split('|');
            if (alts.Length > 1 && alts.All(a => a.Length > 0 && Regex.IsMatch(a, @"^[\w .\-]+$")))
                list.Add(new { kind = "enum", values = alts });
            else
                list.Add(new { kind = "free", pattern = "(" + inner + ")" });
        }
        return list;
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

    /// <summary>Distinct alphanumeric tokens (length &gt; 2) from a phrase — the terms worth OR-searching for near-matches.</summary>
    private static IEnumerable<string> SalientTokens(string text)
        => (text ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string(t.Where(char.IsLetterOrDigit).ToArray()))
            .Where(t => t.Length > 2)
            .Distinct(StringComparer.OrdinalIgnoreCase);

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
