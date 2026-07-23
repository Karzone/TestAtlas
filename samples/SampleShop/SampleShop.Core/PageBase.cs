using OpenQA.Selenium;

namespace SampleShop.Core;

/// <summary>
/// Base for page objects: holds the <see cref="IWebDriver"/>. Concrete pages inherit it, so the map
/// draws an inherits edge Core &lt;- (the UI project).
/// </summary>
public abstract class PageBase
{
    protected readonly IWebDriver Driver;

    protected PageBase(IWebDriver driver) => Driver = driver;

    public void Navigate(string route) => Driver.Navigate().GoToUrl(route);
}
