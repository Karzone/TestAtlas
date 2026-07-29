using System.Text.Json;
using TestAtlas.Mcp;
using Xunit;

namespace TestAtlas.Tests;

/// <summary>
/// The MCP server surface, driven through <see cref="McpServer.HandleLine"/> against the real indexed
/// fixture map (so the FTS-backed search tools hit a genuine db, not a stub). Asserts the JSON-RPC
/// envelope and each tool's payload — never just "it returned something".
/// </summary>
public sealed class McpServerTests : IClassFixture<IndexedFixtureSolution>
{
    private readonly McpServer _server;
    public McpServerTests(IndexedFixtureSolution fx) => _server = new McpServer(fx.DbPath, fx.Doc);

    private JsonElement Call(string json) => JsonDocument.Parse(_server.HandleLine(json)!).RootElement;

    /// <summary>The text payload inside a tools/call result, parsed back to JSON.</summary>
    private JsonElement ToolCall(string name, string argsJson = "{}")
    {
        var req = "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\""
                  + name + "\",\"arguments\":" + argsJson + "}}";
        var res = Call(req);
        var text = res.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        return JsonDocument.Parse(text).RootElement;
    }

    [Fact]
    public void Initialize_returns_server_info_and_capabilities()
    {
        var res = Call("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""");
        Assert.Equal("2.0", res.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, res.GetProperty("id").GetInt32());
        var result = res.GetProperty("result");
        Assert.False(string.IsNullOrEmpty(result.GetProperty("protocolVersion").GetString()));
        Assert.Equal("testatlas", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
    }

    [Fact]
    public void Tools_list_advertises_the_read_only_tools()
    {
        var res = Call("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var names = res.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToHashSet();
        Assert.Superset(new HashSet<string> { "stats", "impact", "search_steps", "search_scenarios", "list_endpoints" }, names);
        // Each tool advertises a JSON-schema input contract.
        Assert.All(res.GetProperty("result").GetProperty("tools").EnumerateArray(),
            t => Assert.Equal("object", t.GetProperty("inputSchema").GetProperty("type").GetString()));
    }

    [Fact]
    public void Stats_tool_returns_the_map_summary()
    {
        var stats = ToolCall("stats");
        Assert.Equal(2, stats.GetProperty("projects").GetInt32());     // Fixture.SpecFlow + Fixture.Reqnroll
        Assert.Equal(20, stats.GetProperty("classes").GetInt32());
        Assert.Equal(3, stats.GetProperty("endpoints").GetInt32());
        Assert.True(stats.GetProperty("classKinds").TryGetProperty("page_object", out _));
    }

    [Fact]
    public void Impact_tool_returns_the_blast_radius_of_a_class()
    {
        // LoginPage is driven by a step method the "Successful sign in" scenario binds — so it's affected.
        var r = ToolCall("impact", """{"target":"class","value":"LoginPage"}""");
        Assert.True(r.GetProperty("found").GetBoolean());
        var scenarios = r.GetProperty("affected").EnumerateArray().Select(s => s.GetProperty("scenario").GetString());
        Assert.Contains("Successful sign in", scenarios);
    }

    [Fact]
    public void Search_steps_tool_finds_a_matching_definition()
    {
        // 'dashboard' resolves to exactly the "the dashboard is shown" step definition.
        var r = ToolCall("search_steps", """{"query":"dashboard"}""");
        Assert.Equal(1, r.GetProperty("count").GetInt32());
        var exprs = r.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("expression").GetString());
        Assert.Contains("the dashboard is shown", exprs);
    }

    [Fact]
    public void List_endpoints_tool_surfaces_the_operation_with_its_real_route()
    {
        var r = ToolCall("list_endpoints");
        Assert.Equal(3, r.GetProperty("total").GetInt32());
        // The GetSupplierRequest operation resolves to its real route + request type.
        var op = r.GetProperty("endpoints").EnumerateArray()
            .Single(e => e.GetProperty("route").GetString() == "api/suppliers/{0}");
        Assert.Equal("GET", op.GetProperty("verb").GetString());
        Assert.Equal("GetSupplierRequest", op.GetProperty("requestType").GetString());
        Assert.Equal("SupplierBff", op.GetProperty("targetApi").GetString());
    }

    [Fact]
    public void New_authoring_tools_are_advertised()
    {
        var res = Call("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var names = res.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToHashSet();
        Assert.Superset(new HashSet<string?> { "resolve_step", "unbound_steps" }, names);
    }

    [Fact]
    public void Resolve_step_binds_a_phrase_to_the_existing_definition_cross_project()
    {
        // "the customer checks out" is only defined in the Reqnroll project (CheckoutSteps) — a
        // SpecFlow feature's step still resolves to it. Exactly one binding → 'exact'.
        var r = ToolCall("resolve_step", """{"text":"the customer checks out"}""");
        Assert.Equal("exact", r.GetProperty("status").GetString());
        Assert.Equal(1, r.GetProperty("matchCount").GetInt32());
        var m = r.GetProperty("matches")[0];
        Assert.Equal("the customer checks out", m.GetProperty("expression").GetString());
        Assert.Equal("CheckoutSteps", m.GetProperty("class").GetString());
    }

    [Fact]
    public void Resolve_step_captures_the_argument_values_from_the_phrase()
    {
        // "a user named (.*)" binds "a user named Alice", capturing "Alice".
        var r = ToolCall("resolve_step", """{"text":"a user named Alice"}""");
        Assert.Equal("exact", r.GetProperty("status").GetString());
        var captured = r.GetProperty("matches")[0].GetProperty("capturedArguments").EnumerateArray()
            .Select(a => a.GetString());
        Assert.Contains("Alice", captured);
    }

    [Fact]
    public void Resolve_step_flags_an_ambiguous_phrase_matching_two_definitions()
    {
        // Both "the system is ready" and "the system is (.*)" match — a conflict to resolve.
        var r = ToolCall("resolve_step", """{"text":"the system is ready"}""");
        Assert.Equal("ambiguous", r.GetProperty("status").GetString());
        Assert.Equal(2, r.GetProperty("matchCount").GetInt32());
    }

    [Fact]
    public void Resolve_step_returns_none_when_nothing_binds()
    {
        // "pigs can fly" is the fixture's deliberately-unbound step — no definition matches.
        var r = ToolCall("resolve_step", """{"text":"pigs can fly"}""");
        Assert.Equal("none", r.GetProperty("status").GetString());
        Assert.Equal(0, r.GetProperty("matchCount").GetInt32());
    }

    [Fact]
    public void Resolve_step_suggests_the_closest_existing_step_for_a_near_miss()
    {
        // "the customer checks in" binds nothing, but shares "customer"/"checks" with the existing
        // "the customer checks out" — so the ranked suggestions must surface it (reuse-first).
        var r = ToolCall("resolve_step", """{"text":"the customer checks in"}""");
        Assert.Equal("none", r.GetProperty("status").GetString());
        var suggested = r.GetProperty("suggestions").EnumerateArray().Select(s => s.GetProperty("expression").GetString());
        Assert.Contains("the customer checks out", suggested);
    }

    [Fact]
    public void Unbound_steps_lists_the_deliberately_unbound_step()
    {
        var r = ToolCall("unbound_steps");
        Assert.True(r.GetProperty("count").GetInt32() >= 1);
        var texts = r.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("step").GetString());
        Assert.Contains("pigs can fly", texts);
        var pig = r.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("step").GetString() == "pigs can fly");
        Assert.Equal("Successful sign in", pig.GetProperty("scenario").GetString());
        Assert.Equal("Login", pig.GetProperty("feature").GetString());
    }

    [Fact]
    public void Get_scenario_returns_the_ordered_steps_and_feature()
    {
        var r = ToolCall("get_scenario", """{"name":"Successful sign in"}""");
        Assert.Equal(1, r.GetProperty("count").GetInt32());
        var sc = r.GetProperty("scenarios")[0];
        Assert.Equal("Login", sc.GetProperty("feature").GetString());
        var steps = sc.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("text").GetString()).ToList();
        Assert.Equal(4, steps.Count); // Given/When/Then/And
        Assert.Contains("the dashboard is shown", steps);
        Assert.Contains("pigs can fly", steps);
    }

    [Fact]
    public void Get_step_definition_returns_detail_and_the_scenarios_that_use_it()
    {
        var r = ToolCall("get_step_definition", """{"query":"dashboard"}""");
        Assert.Equal(1, r.GetProperty("count").GetInt32());
        var d = r.GetProperty("stepDefinitions")[0];
        Assert.Equal("the dashboard is shown", d.GetProperty("expression").GetString());
        Assert.Equal("LoginSteps", d.GetProperty("class").GetString());
        var users = d.GetProperty("usedByScenarios").EnumerateArray().Select(s => s.GetString());
        Assert.Contains("Successful sign in", users);
    }

    [Fact]
    public void List_tags_counts_the_smoke_tag()
    {
        var r = ToolCall("list_tags");
        var smoke = r.GetProperty("tags").EnumerateArray().SingleOrDefault(t => t.GetProperty("tag").GetString() == "@smoke");
        Assert.Equal(JsonValueKind.Object, smoke.ValueKind);
        Assert.True(smoke.GetProperty("scenarios").GetInt32() >= 1);
    }

    [Fact]
    public void Step_catalog_extracts_a_cucumber_typed_placeholder()
    {
        var r = ToolCall("step_catalog", """{"query":"cart"}""");
        var step = r.GetProperty("steps").EnumerateArray()
            .Single(s => s.GetProperty("expression").GetString() == "a cart with {int} item(s)");
        var ph = step.GetProperty("placeholders").EnumerateArray().First();
        Assert.Equal("typed", ph.GetProperty("kind").GetString());
        Assert.Equal("int", ph.GetProperty("type").GetString());
    }

    [Fact]
    public void Coverage_gaps_reports_consistent_counts_and_lists()
    {
        var r = ToolCall("coverage_gaps");
        // Counts are exact; the returned lists are capped but consistent when under the cap.
        Assert.True(r.GetProperty("untestedEndpointCount").GetInt32() >= 0);
        Assert.True(r.GetProperty("unusedStepDefinitionCount").GetInt32() >= 0);
        Assert.True(r.GetProperty("untestedEndpoints").GetArrayLength() <= r.GetProperty("untestedEndpointCount").GetInt32());
        Assert.True(r.GetProperty("unusedStepDefinitions").GetArrayLength() <= r.GetProperty("unusedStepDefinitionCount").GetInt32());
    }

    [Fact]
    public void Project_dependencies_shows_the_cross_project_edge()
    {
        // The SpecFlow feature's "the customer checks out" binds to the Reqnroll CheckoutSteps —
        // so Fixture.SpecFlow depends on Fixture.Reqnroll.
        var r = ToolCall("project_dependencies", """{"project":"SpecFlow"}""");
        var sf = r.GetProperty("projects").EnumerateArray().Single(p => p.GetProperty("project").GetString() == "Fixture.SpecFlow");
        var deps = sf.GetProperty("dependsOn").EnumerateArray().Select(d => d.GetProperty("project").GetString());
        Assert.Contains("Fixture.Reqnroll", deps);
    }

    [Fact]
    public void A_notification_without_an_id_gets_no_response()
        => Assert.Null(_server.HandleLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));

    [Fact]
    public void An_unknown_method_is_a_json_rpc_method_not_found_error()
    {
        var res = Call("""{"jsonrpc":"2.0","id":9,"method":"does/notexist"}""");
        Assert.Equal(-32601, res.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void Malformed_json_is_a_parse_error()
    {
        var res = JsonDocument.Parse(_server.HandleLine("not json at all")!).RootElement;
        Assert.Equal(-32700, res.GetProperty("error").GetProperty("code").GetInt32());
    }
}
