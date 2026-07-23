namespace SampleShop.Core;

/// <summary>
/// Minimal base for API clients — a stand-in for the real HTTP plumbing. Concrete clients in the
/// ApiClients project inherit this, so the map shows an inherits edge Core &lt;- ApiClients.
/// </summary>
public abstract class ApiClientBase
{
    protected readonly string BaseUrl;

    protected ApiClientBase(string baseUrl) => BaseUrl = baseUrl;

    /// <summary>Pretend GET — returns a canned body so the sample builds with no real network.</summary>
    protected string Get(string path) => $"GET {BaseUrl}/{path.TrimStart('/')}";

    /// <summary>Pretend POST.</summary>
    protected string Post(string path, string body) => $"POST {BaseUrl}/{path.TrimStart('/')} :: {body}";
}
