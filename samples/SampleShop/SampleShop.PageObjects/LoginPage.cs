using SampleShop.Core;

namespace SampleShop.PageObjects;

/// <summary>Page object for the login screen.</summary>
public sealed class LoginPage : PageBase
{
    public LoginPage(string baseUrl) : base(baseUrl) { }

    public void Open() => Navigate("login");

    public void LoginAs(string username, string password)
    {
        Type("#username", username);
        Type("#password", password);
        Click("#sign-in");
    }
}
