using System.Net.Http;
using OpenQA.Selenium;
using Reqnroll;
using SampleShop.Api.Cart;
using SampleShop.Api.Identity;
using SampleShop.Ui.Pages;

namespace SampleShop.Tests.E2E.Steps
{
    /// <summary>
    /// End-to-end steps that use API clients (Identity, Cart) AND page objects (Ui.Pages) in the same
    /// journey — so this project connects to four library projects in the map.
    /// </summary>
    [Binding]
    public class PurchaseJourneySteps
    {
        private static readonly HttpClient Http = new() { BaseAddress = new System.Uri("https://api.sampleshop.test") };
        private readonly IWebDriver _driver = null;

        private AuthApiClient _auth;
        private CheckoutApiClient _checkoutApi;
        private ProductPage _productPage;
        private CheckoutPage _checkoutPage;

        [Given("the customer \"(.*)\" is authenticated")]
        public void GivenTheCustomerIsAuthenticated(string email)
        {
            _auth = new AuthApiClient(Http);
            _ = _auth.Login(email, "correct-horse-battery-staple");
        }

        [Given("the product (.*) page is open")]
        public void GivenTheProductPageIsOpen(int productId)
        {
            _productPage = new ProductPage(_driver);
            _productPage.Open(productId);
            _productPage.AddToCartClick();
        }

        [When("the customer checks out with address \"(.*)\"")]
        public void WhenTheCustomerChecksOut(string address)
        {
            _checkoutPage = new CheckoutPage(_driver);
            _checkoutPage.Open();
            _checkoutPage.EnterShippingAddress(address);
            _checkoutPage.PlaceOrder();
        }

        [Then("the order is confirmed")]
        public void ThenTheOrderIsConfirmed()
        {
            _checkoutApi = new CheckoutApiClient(Http);
            _ = _checkoutApi.PlaceOrder("1 King Street");
        }
    }
}
