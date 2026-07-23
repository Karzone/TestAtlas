using System.Net.Http;
using System.Threading.Tasks;
using SampleShop.Core;

namespace SampleShop.Api.Cart;

/// <summary>Client for the /cart endpoints.</summary>
public sealed class CartApiClient : ApiClientBase
{
    public CartApiClient(HttpClient http) : base(http) { }

    public Task<HttpResponseMessage> AddItem(int productId, int quantity) =>
        Post("cart/items", new StringContent($"{{\"productId\":{productId},\"qty\":{quantity}}}"));

    public Task<HttpResponseMessage> GetCart() => Get("cart");
}
