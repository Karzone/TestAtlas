using SampleShop.Core;

namespace SampleShop.ApiClients;

/// <summary>Client for the /products endpoints.</summary>
public sealed class ProductsApiClient : ApiClientBase
{
    public ProductsApiClient(string baseUrl) : base(baseUrl) { }

    public string ListProducts() => Get("products");

    public string GetProduct(int id) => Get($"products/{id}");
}
