using System.Net.Http;

namespace Fixture.Reqnroll
{
    // A generic HTTP wrapper the way real frameworks hide URLs behind a typed request object: the
    // wrapper executes the HTTP call (so it classifies as an api_client) and the TYPE ARGUMENT is the
    // operation identity. `new BaseRequest<GetSupplierRequest>()` in a step is therefore an
    // operation-level endpoint (slice 4) — verb inferred from the request name ("Get…" → GET), route
    // = the request type name (a bare identity, no URL at the call site). Method count 1.
    public class BaseRequest<T>
    {
        public string Execute()
        {
            var client = new HttpClient();   // references HttpClient → seeds this wrapper as api_client
            return client.ToString();
        }
    }

    // The typed request that names the operation. Plain data, classified `other`; its NAME is what the
    // operation-level endpoint is keyed on. Method count 0.
    public class GetSupplierRequest
    {
        public int SupplierId { get; set; }
    }
}
