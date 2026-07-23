using SampleShop.Core;

namespace SampleShop.ApiClients;

/// <summary>Client for the /cart endpoints.</summary>
public sealed class CartApiClient : ApiClientBase
{
    public CartApiClient(string baseUrl) : base(baseUrl) { }

    public string AddItem(int productId, int quantity) =>
        Post("cart/items", $"{{\"productId\":{productId},\"qty\":{quantity}}}");

    public string GetCart() => Get("cart");
}
