using System.Net.Http;
using System.Threading.Tasks;
using SampleShop.Core;

namespace SampleShop.Api.Identity;

/// <summary>Client for the /users endpoints.</summary>
public sealed class UsersApiClient : ApiClientBase
{
    public UsersApiClient(HttpClient http) : base(http) { }

    public Task<HttpResponseMessage> GetProfile() => Get("users/me");

    public Task<HttpResponseMessage> GetUser(int id) => Get($"users/{id}");
}
