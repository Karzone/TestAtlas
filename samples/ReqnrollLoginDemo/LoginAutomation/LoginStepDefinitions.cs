using Reqnroll;

namespace LoginAutomation
{
    // A genuine Reqnroll binding class using real Reqnroll attributes + cucumber expressions.
    [Binding]
    public class LoginStepDefinitions
    {
        [Given("a registered user {string}")]
        public void GivenARegisteredUser(string user) { }

        [When("she signs in with password {string}")]
        public void WhenSheSignsInWithPassword(string password) { }

        [When("he signs in with the wrong password {int} times")]
        public void WhenHeSignsInWithTheWrongPassword(int attempts) { }

        [Then("she sees the dashboard")]
        public void ThenSheSeesTheDashboard() { }

        [Then("the account is locked")]
        public void ThenTheAccountIsLocked() { }
    }
}
