using OpenQA.Selenium;
using SampleShop.Core;

namespace SampleShop.Ui.Pages;

/// <summary>Page object for a product detail page.</summary>
public sealed class ProductPage : PageBase
{
    private static readonly By AddToCart = By.CssSelector("#add-to-cart");
    private static readonly By Title = By.CssSelector("h1.product-title");

    public ProductPage(IWebDriver driver) : base(driver) { }

    public void Open(int productId) => Navigate($"/products/{productId}");

    public string ReadTitle()
    {
        IWebElement heading = Driver.FindElement(Title);
        return heading.Text;
    }

    public void AddToCartClick() => Driver.FindElement(AddToCart).Click();
}
