using Reqnroll;
using SampleShop.PageObjects;

namespace SampleShop.Tests.Steps
{
    /// <summary>UI-facing steps — bind to the PageObjects project (page-object mapping).</summary>
    [Binding]
    public class CheckoutSteps
    {
        private const string BaseUrl = "https://www.sampleshop.test";

        private LoginPage _loginPage;
        private CheckoutPage _checkoutPage;

        [Given("the customer \"(.*)\" is signed in")]
        public void GivenTheCustomerIsSignedIn(string email)
        {
            _loginPage = new LoginPage(BaseUrl);
            _loginPage.Open();
            _loginPage.LoginAs(email, "correct-horse-battery-staple");
        }

        [When("they check out with shipping address \"(.*)\"")]
        public void WhenTheyCheckOutWithShippingAddress(string address)
        {
            _checkoutPage = new CheckoutPage(BaseUrl);
            _checkoutPage.Open();
            _checkoutPage.EnterShippingAddress(address);
            _checkoutPage.PlaceOrder();
        }

        [Then("the order is placed")]
        public void ThenTheOrderIsPlaced()
        {
            // Placeholder assertion — the sample has no real backend.
        }
    }
}
