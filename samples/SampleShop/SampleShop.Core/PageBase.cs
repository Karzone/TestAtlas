namespace SampleShop.Core;

/// <summary>
/// Minimal base for page objects — a stand-in for a WebDriver page. Concrete pages in the
/// PageObjects project inherit this, so the map shows an inherits edge Core &lt;- PageObjects.
/// </summary>
public abstract class PageBase
{
    protected readonly string BaseUrl;

    protected PageBase(string baseUrl) => BaseUrl = baseUrl;

    /// <summary>Pretend navigation — no real browser is started.</summary>
    public string Navigate(string route) => $"navigate {BaseUrl}/{route.TrimStart('/')}";

    /// <summary>Pretend "type into field".</summary>
    protected void Type(string field, string value) { /* no-op stand-in */ }

    /// <summary>Pretend "click".</summary>
    protected void Click(string element) { /* no-op stand-in */ }
}
