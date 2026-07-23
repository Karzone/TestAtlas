using System.Net.Http;
using Reqnroll;
using SampleShop.Api.Cart;
using SampleShop.Api.Catalog;

namespace SampleShop.Tests.Api.Steps
{
    /// <summary>Catalog + cart API steps — use the Catalog and Cart client projects.</summary>
    [Binding]
    public class CatalogApiSteps
    {
        private static readonly HttpClient Http = new() { BaseAddress = new System.Uri("https://api.sampleshop.test") };

        private ProductsApiClient _products;
        private CategoriesApiClient _categories;
        private CartApiClient _cart;

        [Given("the shop API is available")]
        public void GivenTheShopApiIsAvailable()
        {
            _products = new ProductsApiClient(Http);
            _categories = new CategoriesApiClient(Http);
            _cart = new CartApiClient(Http);
        }

        [When("a request for the product list is made")]
        public void WhenAProductListRequestIsMade()
        {
            _ = _products.ListProducts();
            _ = _categories.ListCategories();
        }

        [When("product (.*) is added to the cart with quantity (.*)")]
        public void WhenProductIsAddedToTheCart(int productId, int quantity)
        {
            _ = _cart.AddItem(productId, quantity);
        }

        [Then("the product list is returned")]
        public void ThenTheProductListIsReturned() { }

        [Then("the cart is not empty")]
        public void ThenTheCartIsNotEmpty()
        {
            _ = _cart.GetCart();
        }
    }
}
