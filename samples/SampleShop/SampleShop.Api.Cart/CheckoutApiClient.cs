using System.Net.Http;
using System.Threading.Tasks;
using SampleShop.Core;

namespace SampleShop.Api.Cart;

/// <summary>Client for the /checkout endpoints.</summary>
public sealed class CheckoutApiClient : ApiClientBase
{
    public CheckoutApiClient(HttpClient http) : base(http) { }

    public Task<HttpResponseMessage> PlaceOrder(string address) =>
        Post("checkout", new StringContent($"{{\"address\":\"{address}\"}}"));
}
