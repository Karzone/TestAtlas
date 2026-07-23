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

    // The typed request that names the operation. Plain data, classified `other`; its NAME is what the
    // operation-level endpoint is keyed on. Method count 0.
    public class GetSupplierRequest
    {
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
