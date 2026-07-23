using OpenQA.Selenium;
using SampleShop.Core;

namespace SampleShop.Ui.Pages;

/// <summary>Page object for the checkout flow.</summary>
public sealed class CheckoutPage : PageBase
{
    private static readonly By Address = By.CssSelector("#address");
    private static readonly By PlaceOrderButton = By.CssSelector("#place-order");

    public CheckoutPage(IWebDriver driver) : base(driver) { }

    public void Open() => Navigate("/checkout");

    public void EnterShippingAddress(string address)
    {
        IWebElement field = Driver.FindElement(Address);
        field.SendKeys(address);
    }

    public void PlaceOrder() => Driver.FindElement(PlaceOrderButton).Click();
}
