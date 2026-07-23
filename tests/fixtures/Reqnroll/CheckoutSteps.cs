using System.Net.Http;
using Reqnroll;

namespace Fixture.Reqnroll
{
    // A Reqnroll step class using cucumber-expression bindings ({int}, {string}). Method count 3.
    // Two of the step methods make HTTP calls (slice 4): one via HttpClient (known-client tier),
    // one via the custom ApiExecutor wrapper (generic-fallback tier, interpolated route → template).
    [Binding]
    public class CheckoutSteps
    {
        private readonly HttpClient _http = new HttpClient();

        [Given("a cart with {int} item(s)")]
        public void GivenACartWith(int count) { }

        [When("the customer checks out")]
        public void WhenTheCustomerChecksOut()
        {
            _ = _http.PostAsync("/api/orders", null);
        }

        [Then("the order {string} is placed")]
        public void ThenTheOrderIsPlaced(string reference)
        {
            ApiExecutor.Get($"/api/orders/{reference}");
        }
    }
}
