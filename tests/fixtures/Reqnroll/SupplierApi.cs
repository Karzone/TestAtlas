using System.Net.Http;

namespace Fixture.Reqnroll
{
    // A generic HTTP wrapper the way real frameworks hide URLs behind a typed request object: the
    // wrapper executes the HTTP call (so it classifies as an api_client) and the TYPE ARGUMENT is the
    // operation identity. `new BaseRequest<GetSupplierRequest>()` in a step is therefore an
    // operation-level endpoint (slice 4) — verb inferred from the request name ("Get…" → GET), route
    // = the request type name (a bare identity, no URL at the call site). Method count 1.
    //
    // Note the realistic shape (matches 1FrameworkAutomatedTest's BaseRequest): the HTTP client lives
    // in a FIELD and is driven through the variable — the marker type name never appears in a method
    // BODY, so the method-ratio api_client rule alone would MISS it. It's caught because a class that
    // holds a RestSharp/HttpClient marker is an api_client. (1FAT holds an IRestClient here.)
    public class BaseRequest<T>
    {
        private readonly HttpClient _client = new HttpClient();

        public string Execute() => _client.ToString();
    }

    // A RestSharp-style HTTP method shim (the real solution uses RestSharp.Method); a local enum keeps
    // the fixture self-contained and syntax-parseable without a package reference.
    public enum Method { GET, POST, PUT, DELETE }

    // The typed request that names the operation. It is a *request descriptor*: it declares its own
    // route, verb, and API bucket as constant-returning getters (the real 1FAT shape). Extraction reads
    // the getter RETURN expressions — so the operation endpoint surfaces the real GET api/suppliers/{0}
    // (a {0} route template, kept verbatim) on the "SupplierBff" API, not just the type name. Still
    // classified `other` (plain data; the Method type is not an HTTP-client marker). Method count 0.
    public class GetSupplierRequest
    {
        public string TargetAPI   { get { return "SupplierBff"; } }
        public string ServiceName { get { return "api/suppliers/{0}"; } }
        public Method Method      { get { return Method.GET; } }

        public int SupplierId { get; set; }
    }

    // A *consumer* of the API layer: it constructs the request wrapper directly to CALL the operation,
    // exactly as 1FrameworkAutomatedTest's `NetworkUtilities` / `InvoiceUtilities` do. It is therefore
    // NOT itself an api_client — composition of an api_client is usage, not identity. Its name lacks any
    // API suffix (Api/Client/Service/Endpoint), so the name-gated constructs-an-api-client rule leaves
    // it `other`. (Before that gate it mis-classified as api_client — the 40+ *Utilities false positives
    // measured on the real solution.) The call is still real: it contributes a calls_endpoint edge.
    public class PricingUtilities
    {
        public void RefreshSupplier()
        {
            _ = new BaseRequest<GetSupplierRequest>().Execute();
        }
    }
}
