using OpenQA.Selenium;
using Reqnroll;
using SampleShop.Ui.Pages;

namespace SampleShop.Tests.Ui.Steps
{
    /// <summary>UI steps — drive the page objects in the Ui.Pages project.</summary>
    [Binding]
    public class CheckoutUiSteps
    {
        // A real suite would resolve a live driver; the sample never launches a browser.
        private readonly IWebDriver _driver = null;

        private LoginPage _loginPage;
        private CheckoutPage _checkoutPage;

        [Given("the customer \"(.*)\" is signed in")]
        public void GivenTheCustomerIsSignedIn(string email)
        {
            _loginPage = new LoginPage(_driver);
            _loginPage.Open();
            _loginPage.LoginAs(email, "correct-horse-battery-staple");
        }

        [When("they check out with shipping address \"(.*)\"")]
        public void WhenTheyCheckOutWithShippingAddress(string address)
        {
            _checkoutPage = new CheckoutPage(_driver);
            _checkoutPage.Open();
            _checkoutPage.EnterShippingAddress(address);
            _checkoutPage.PlaceOrder();
        }

        [Then("the order is placed")]
        public void ThenTheOrderIsPlaced() { }
    }
}
