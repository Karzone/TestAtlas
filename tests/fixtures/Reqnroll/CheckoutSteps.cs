using Reqnroll;

namespace Fixture.Reqnroll
{
    // A Reqnroll step class using cucumber-expression bindings ({int}, {string}). Method count 3.
    [Binding]
    public class CheckoutSteps
    {
        [Given("a cart with {int} item(s)")]
        public void GivenACartWith(int count) { }

        [When("the customer checks out")]
        public void WhenTheCustomerChecksOut() { }

        [Then("the order {string} is placed")]
        public void ThenTheOrderIsPlaced(string reference) { }
    }
}
