using System.Net.Http;
using System.Threading.Tasks;
using SampleShop.Core;

namespace SampleShop.Api.Catalog;

/// <summary>Client for the /categories endpoints.</summary>
public sealed class CategoriesApiClient : ApiClientBase
{
    public CategoriesApiClient(HttpClient http) : base(http) { }

    public Task<HttpResponseMessage> ListCategories() => Get("categories");

    public Task<HttpResponseMessage> GetCategory(int id) => Get($"categories/{id}");
}
