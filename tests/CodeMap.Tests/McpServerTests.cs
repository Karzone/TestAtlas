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
