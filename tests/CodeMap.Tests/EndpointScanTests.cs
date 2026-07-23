using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TestAtlas.Core.Indexing;
using Xunit;

namespace TestAtlas.Tests;

/// <summary>
/// Pure parser-level tests for the endpoint-extraction ladder (spec §5.1, slice 4) — each tier
/// exercised on raw source, no Roslyn workspace. Solution-agnostic by construction: known client
/// libraries by shape, custom wrappers by the generic verb-name fallback.
/// </summary>
public sealed class EndpointScanTests
{
    private static List<(string Verb, string Route)> Scan(string methodBody, string attrs = "")
    {
        var src = $$"""
            class C
            {
                {{attrs}}
                void M()
                {
                    {{methodBody}}
                }
            }
            """;
        var root = CSharpSyntaxTree.ParseText(src).GetRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        return SyntaxScan.EndpointCalls(method);
    }

    [Theory]
    // Tier 1 — HttpClient / typed-client method names (relative routes allowed).
    [InlineData("""_http.GetAsync("/api/orders");""", "GET", "/api/orders")]
    [InlineData("""_http.PostAsJsonAsync("api/orders", body);""", "POST", "api/orders")]
    [InlineData("""client.DeleteAsync("/api/orders/1");""", "DELETE", "/api/orders/1")]
    [InlineData("""await _http.PutAsync("/api/x", content);""", "PUT", "/api/x")]
    // Tier 2 — SendAsync with HttpRequestMessage.
    [InlineData("""_http.SendAsync(new HttpRequestMessage(HttpMethod.Patch, "/api/orders/9"));""", "PATCH", "/api/orders/9")]
    // Tier 3 — RestSharp.
    [InlineData("""var r = new RestRequest("/api/suppliers", Method.Post);""", "POST", "/api/suppliers")]
    [InlineData("""var r = new RestRequest("/api/suppliers");""", "GET", "/api/suppliers")]
    // Tier 5 — generic custom-wrapper fallback (strictly route-like arg).
    [InlineData("""Api.Get("/partners/list");""", "GET", "/partners/list")]
    [InlineData("""executor.PostJson("/api/estimate", body);""", "POST", "/api/estimate")]
    public void Extracts_the_verb_and_route(string body, string verb, string route)
    {
        var calls = Scan(body);
        var one = Assert.Single(calls);
        Assert.Equal(verb, one.Verb);
        Assert.Equal(route, one.Route);
    }

    [Fact]
    public void Refit_style_attribute_on_the_method_is_an_endpoint()
    {
        var calls = Scan("", attrs: """[Get("/api/orders/{id}")]""");
        var one = Assert.Single(calls);
        Assert.Equal(("GET", "/api/orders/{id}"), one);
    }

    [Fact]
    public void Interpolated_route_becomes_a_template()
    {
        var calls = Scan("""_http.GetAsync($"/api/orders/{orderId}/items/{n}");""");
        Assert.Equal(("GET", "/api/orders/{orderId}/items/{n}"), Assert.Single(calls));
    }

    [Theory]
    // Non-route strings must NOT be misread as endpoints.
    [InlineData("""dict.Get("some key");""")]                       // has a space → not a route
    [InlineData("""cache.Get("orders");""")]                        // no '/' at all
    [InlineData("""files.GetFiles("*.feature");""")]                // glob, no '/'
    [InlineData("""wrapper.Get("a/b");""")]                         // generic tier requires leading '/' etc.
    [InlineData("""log.Delete(records);""")]                        // no string arg
    public void Ignores_non_route_calls(string body)
        => Assert.Empty(Scan(body));

    [Fact]
    public void A_method_with_multiple_calls_yields_all_of_them()
    {
        var calls = Scan("""
            _http.GetAsync("/api/a");
            Api.Post("/api/b", body);
            """);
        Assert.Equal(2, calls.Count);
        Assert.Contains(("GET", "/api/a"), calls);
        Assert.Contains(("POST", "/api/b"), calls);
    }
}
