using System.Net.Http;
using System.Threading.Tasks;
using SampleShop.Core;

namespace SampleShop.Api.Catalog;

/// <summary>Client for the /products endpoints.</summary>
public sealed class ProductsApiClient : ApiClientBase
{
    public ProductsApiClient(HttpClient http) : base(http) { }

    public Task<HttpResponseMessage> ListProducts() => Get("products");

    public Task<HttpResponseMessage> GetProduct(int id) => Get($"products/{id}");
}
