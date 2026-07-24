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
    private static List<(string Verb, string Route)> Scan(string methodBody, string attrs = "", string members = "")
    {
        var src = $$"""
            class C
            {
                {{members}}
                {{attrs}}
                void M()
                {
                    {{methodBody}}
                }
            }
            """;
        var root = CSharpSyntaxTree.ParseText(src).GetRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var type = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single();
        return SyntaxScan.EndpointCalls(method, type);
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
    // Tier 5 — verb-as-argument (central client wrappers with non-verb method names).
    [InlineData("""client.ExecuteAsync(HttpMethod.Get, "/api/orders");""", "GET", "/api/orders")]
    [InlineData("""api.CallApi("/api/suppliers", Method.POST, body);""", "POST", "/api/suppliers")]
    [InlineData("""SendRequest(HttpMethod.Delete, $"/api/orders/{id}");""", "DELETE", "/api/orders/{id}")]
    // Tier 3b — RestSharp property style (verb unknowable here → ANY).
    [InlineData("""request.Resource = "/api/margins";""", "ANY", "/api/margins")]
    // Tier 6 — generic custom-wrapper fallback (strictly route-like arg).
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
    // XPath selectors are NOT routes (the real-world false positives from a SOAP-testing suite).
    [InlineData("""xml.Get("//myc:changeContractStatus/note");""")]
    [InlineData("""doc.Get("/soapenv:Envelope/soapenv:Body/myc:assign/contractId/text()");""")]
    public void Ignores_non_route_calls(string body)
        => Assert.Empty(Scan(body));

    [Fact]
    public void Route_held_in_a_class_constant_is_resolved()
    {
        var calls = Scan(
            """_http.GetAsync(OrdersRoute);""",
            members: """private const string OrdersRoute = "/api/orders";""");
        Assert.Equal(("GET", "/api/orders"), Assert.Single(calls));
    }

    [Fact]
    public void Route_held_in_a_static_readonly_field_is_resolved()
    {
        var calls = Scan(
            """client.Execute(Method.PUT, SupplierRoute);""",
            members: """private static readonly string SupplierRoute = "/api/suppliers/{id}";""");
        Assert.Equal(("PUT", "/api/suppliers/{id}"), Assert.Single(calls));
    }

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

    // ---- operation-level candidates: new Wrapper<Request>() (slice 4, operation-level) --------------

    private static List<(string Wrapper, string Request)> Operations(string methodBody)
    {
        var src = $$"""
            class C
            {
                void M() { {{methodBody}} }
            }
            """;
        var method = CSharpSyntaxTree.ParseText(src).GetRoot()
            .DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        return SyntaxScan.GenericOperationCandidates(method);
    }

    [Fact]
    public void Generic_construction_yields_the_wrapper_and_request_type()
    {
        // The exact shape the URL-hiding frameworks use: the request type is the operation identity.
        var ops = Operations("""_ = new BaseRequest<GetUserconfigurationRequest>().ExecuteAsync();""");
        Assert.Equal(("BaseRequest", "GetUserconfigurationRequest"), Assert.Single(ops));
    }

    [Fact]
    public void Every_single_type_argument_construction_is_a_candidate_the_api_client_filter_comes_later()
    {
        // The parser is deliberately permissive — whether the wrapper is HTTP-executing is decided
        // solution-wide by the indexer (only it knows class kinds). So `new List<Foo>()` is returned
        // here and simply filtered out later when List is not a classified api_client.
        var ops = Operations("""var x = new List<Foo>();""");
        Assert.Equal(("List", "Foo"), Assert.Single(ops));
    }

    [Theory]
    [InlineData("""var d = new Dictionary<string, int>();""")] // multi-arg generic → not a candidate
    [InlineData("""var f = new Foo();""")]                      // non-generic → not a candidate
    [InlineData("""var n = 5;""")]                              // no construction at all
    public void Non_single_type_argument_constructions_are_ignored(string body)
        => Assert.Empty(Operations(body));

    [Fact]
    public void A_type_parameter_in_scope_is_not_a_request_type()
    {
        // The BaseApiService plumbing shape: `new BaseRequest<TRequest>()` inside a generic method whose
        // own type parameter is TRequest. TRequest is the parameter, not a concrete request — so it must
        // NOT surface as an operation. (Before the type-parameter guard this leaked `TRequest` as a
        // phantom endpoint with a huge blast radius.)
        var src = """
            class BaseApiService
            {
                Task RequestAsync<TRequest, TResponse>(TRequest request)
                    where TRequest : IBaseRequestModel, new()
                { _ = new BaseRequest<TRequest>(); return Task.CompletedTask; }
            }
            """;
        var method = CSharpSyntaxTree.ParseText(src).GetRoot()
            .DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        Assert.Empty(SyntaxScan.GenericOperationCandidates(method));
    }

    [Fact]
    public void An_enclosing_types_type_parameter_is_also_excluded()
    {
        // The type param can be declared on the containing class, not the method — still not a request.
        var src = """
            class Repository<TEntity>
            {
                void Load() { _ = new BaseRequest<TEntity>(); }
            }
            """;
        var method = CSharpSyntaxTree.ParseText(src).GetRoot()
            .DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        Assert.Empty(SyntaxScan.GenericOperationCandidates(method));

        // …but a concrete type argument in that same class is still a real candidate.
        var src2 = """
            class Repository<TEntity>
            {
                void Load() { _ = new BaseRequest<GetSupplierRequest>(); }
            }
            """;
        var method2 = CSharpSyntaxTree.ParseText(src2).GetRoot()
            .DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        Assert.Equal(("BaseRequest", "GetSupplierRequest"), Assert.Single(SyntaxScan.GenericOperationCandidates(method2)));
    }

    private static TypeDeclarationSyntax ParseClass(string src)
        => CSharpSyntaxTree.ParseText(src).GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().First();

    [Fact]
    public void A_member_merely_named_like_a_ui_marker_is_not_a_ui_reference()
    {
        // The collision the type-position rule fixes: `By` is a Selenium marker type, but a property
        // NAMED By (an ApiService building filters with `By = "name"`) must not count as a UI reference
        // and flip the class to page_object.
        var facts = SyntaxScan.GatherClassFacts(ParseClass("""
            class FilterApiService
            {
                public string By { get; set; }         // property NAMED By — not a type
                public void Build() { By = "name"; }    // assignment — expression position
            }
            """));
        Assert.False(facts.ReferencesUiType);
        Assert.Equal(0, facts.UiReferencingMembers);

        // …but `By` in a TYPE position (a parameter type) genuinely is a UI reference.
        var real = SyntaxScan.GatherClassFacts(ParseClass("class LocatorHelper { public void Click(By by) { } }"));
        Assert.True(real.ReferencesUiType);
        Assert.Equal(1, real.UiReferencingMembers);
    }

    [Fact]
    public void Broadened_api_markers_catch_flurl_and_http_message_shapes()
    {
        // Flurl and System.Net.Http types are recognised out of the box now (not just RestSharp/HttpClient).
        var flurl = SyntaxScan.GatherClassFacts(ParseClass("class OrdersClient { private IFlurlClient _c; }"));
        Assert.True(flurl.HoldsOrConstructsApiMarker);
        Assert.True(flurl.ReferencesApiType);
    }

    [Fact]
    public void Marker_type_sets_are_configurable_via_options()
    {
        // A team on a bespoke HTTP stack can extend/replace the API markers through ClassifierOptions.
        var opts = new ClassifierOptions { ApiMarkerTypes = new[] { "MyBespokeHttpClient" } };
        var custom = SyntaxScan.GatherClassFacts(ParseClass("class Foo { private MyBespokeHttpClient _c; }"), opts);
        Assert.True(custom.HoldsOrConstructsApiMarker);

        // …and under that custom set the default HttpClient is NOT a marker — proving it's really config-driven.
        var httpUnderCustom = SyntaxScan.GatherClassFacts(ParseClass("class Bar { private HttpClient _c; }"), opts);
        Assert.False(httpUnderCustom.HoldsOrConstructsApiMarker);
    }

    [Fact]
    public void Held_type_names_cover_field_property_return_and_param_types()
    {
        // The aggregator/DI shape a name-based construction scan misses: the collaborator is the member's
        // DECLARED type, even when the target-typed new() carries no type name.
        var type = CSharpSyntaxTree.ParseText("""
            class GatewayApiService
            {
                private readonly WorkflowApiService _workflow;                    // DI field
                public MotorOrderApiService MotorOrder { get; } = new(context);   // target-typed new() property
                public SupplierProfilePage GetPage(LookupApiService lookup) => null; // return + param types
            }
            """).GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().First();

        var held = SyntaxScan.HeldTypeNames(type);
        Assert.Contains("WorkflowApiService", held);   // field type
        Assert.Contains("MotorOrderApiService", held); // property type — the target-typed new() case
        Assert.Contains("SupplierProfilePage", held);  // return type
        Assert.Contains("LookupApiService", held);     // parameter type
    }

    // ---- request descriptors: route/verb/API read from the request type's getters (slice 5) ---------

    private static (string Verb, string Route, string? TargetApi)? RequestEndpoint(string classSrc)
    {
        var type = CSharpSyntaxTree.ParseText(classSrc).GetRoot()
            .DescendantNodes().OfType<TypeDeclarationSyntax>().First();
        return SyntaxScan.RequestEndpointOf(type);
    }

    [Fact]
    public void Request_descriptor_block_getters_yield_route_verb_and_api()
    {
        // The real 1FAT shape: constant-returning block getters. The {0} template is kept verbatim.
        var re = RequestEndpoint("""
            public class PostSubmitRequest
            {
                public string TargetAPI   { get { return "MotorBff"; } }
                public string ServiceName { get { return "/api/gateway/fnol/{0}/submit"; } }
                public Method Method      { get { return Method.POST; } }
            }
            """);
        Assert.NotNull(re);
        Assert.Equal(("POST", "/api/gateway/fnol/{0}/submit", "MotorBff"),
            (re!.Value.Verb, re.Value.Route, re.Value.TargetApi));
    }

    [Fact]
    public void Request_descriptor_reads_expression_bodied_getters_and_tolerates_a_missing_api()
    {
        var re = RequestEndpoint("""
            public class GetDefinitionRequestBff
            {
                public string ServiceName => "api/gateway/estimate/definition/section/list";
                public Method Method => Method.POST;
            }
            """);
        Assert.NotNull(re);
        Assert.Equal("POST", re!.Value.Verb);
        Assert.Equal("api/gateway/estimate/definition/section/list", re.Value.Route);
        Assert.Null(re.Value.TargetApi); // no TargetAPI getter — still a descriptor, just no API bucket
    }

    [Fact]
    public void A_type_without_a_method_getter_is_not_a_request_descriptor()
    {
        // The verb getter (a Method/HttpMethod member) is the decisive signal — a route string alone,
        // or a plain data class, is not a request descriptor.
        Assert.Null(RequestEndpoint("public class Config { public string ServiceName => \"api/x\"; public int Timeout => 30; }"));
        Assert.Null(RequestEndpoint("public class Dto { public int Id { get; set; } public string Name { get; set; } }"));
    }

    [Theory]
    [InlineData("GetUserRequest", "GET")]
    [InlineData("FetchOrdersQuery", "GET")]
    [InlineData("CreateOrderRequest", "POST")]
    [InlineData("SubmitPaymentCommand", "POST")]
    [InlineData("UpdateSupplierRequest", "PUT")]
    [InlineData("DeleteAccountRequest", "DELETE")]
    [InlineData("RemoveItemRequest", "DELETE")]
    [InlineData("PatchProfileRequest", "PATCH")]
    [InlineData("UserconfigurationRequest", "ANY")]   // no leading verb word → ANY, never a guess
    [InlineData("Getaway", "ANY")]                     // "Get" not followed by an uppercase boundary
    public void Operation_verb_is_inferred_from_the_request_name(string request, string verb)
        => Assert.Equal(verb, SyntaxScan.VerbFromOperationName(request));
}
