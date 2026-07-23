using System.Net.Http;
using System.Threading.Tasks;

namespace SampleShop.Core;

/// <summary>
/// Base for API clients: holds the <see cref="HttpClient"/>, so concrete clients that inherit it are
/// recognised as API clients and the map draws an inherits edge Core &lt;- (each API project).
/// </summary>
public abstract class ApiClientBase
{
    protected readonly HttpClient Http;

    protected ApiClientBase(HttpClient http) => Http = http;

    protected Task<HttpResponseMessage> Get(string path) => Http.GetAsync(path);

    protected Task<HttpResponseMessage> Post(string path, HttpContent body) => Http.PostAsync(path, body);
}
