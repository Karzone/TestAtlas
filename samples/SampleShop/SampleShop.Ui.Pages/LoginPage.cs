using OpenQA.Selenium;
using SampleShop.Core;

namespace SampleShop.Ui.Pages;

/// <summary>Page object for the login screen.</summary>
public sealed class LoginPage : PageBase
{
    private static readonly By Username = By.CssSelector("#username");
    private static readonly By Password = By.CssSelector("#password");
    private static readonly By SignIn = By.CssSelector("#sign-in");

    public LoginPage(IWebDriver driver) : base(driver) { }

    public void Open() => Navigate("/login");

    public void LoginAs(string username, string password)
    {
        Driver.FindElement(Username).SendKeys(username);
        Driver.FindElement(Password).SendKeys(password);
        Driver.FindElement(SignIn).Click();
    }
}
