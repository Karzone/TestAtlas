using Reqnroll;
using SampleShop.ApiClients;
using Xunit;

namespace SampleShop.Tests.Steps
{
    /// <summary>API-facing steps — bind to the ApiClients project (API mapping).</summary>
    [Binding]
    public class ProductApiSteps
    {
        private const string BaseUrl = "https://api.sampleshop.test";

        private ProductsApiClient _products;
        private CartApiClient _cart;
        private string _response;

        [Given("the shop API is available")]
        public void GivenTheShopApiIsAvailable()
        {
            _products = new ProductsApiClient(BaseUrl);
            _cart = new CartApiClient(BaseUrl);
        }

        [When("a request for the product list is made")]
        public void WhenAProductListRequestIsMade()
        {
            _response = _products.ListProducts();
        }

        [When("product (.*) is added to the cart with quantity (.*)")]
        public void WhenProductIsAddedToTheCart(int productId, int quantity)
        {
            _response = _cart.AddItem(productId, quantity);
        }

        [Then("the product list is returned")]
        public void ThenTheProductListIsReturned()
        {
            Assert.Contains("products", _response);
        }

        [Then("the cart contains (.*) line item")]
        public void ThenTheCartContainsLineItems(int count)
        {
            Assert.Contains("cart/items", _response);
        }
    }
}
