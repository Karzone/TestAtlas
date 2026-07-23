using System.Net.Http;
using System.Threading.Tasks;
using SampleShop.Core;

namespace SampleShop.Api.Identity;

/// <summary>Client for the /auth endpoints.</summary>
public sealed class AuthApiClient : ApiClientBase
{
    public AuthApiClient(HttpClient http) : base(http) { }

    public Task<HttpResponseMessage> Login(string email, string password) =>
        Post("auth/login", new StringContent($"{{\"email\":\"{email}\",\"password\":\"{password}\"}}"));

    public Task<HttpResponseMessage> Logout() => Post("auth/logout", new StringContent(""));
}
