using TechTalk.SpecFlow;

namespace Fixture.SpecFlow
{
    // A SpecFlow step class. Deliberately contains an AMBIGUOUS pair: both SystemReadyExact and
    // SystemReadyPattern bind the step text "the system is ready" (slice 2 records both as
    // binds_to/ambiguous). Method count here is exactly 5.
    [Binding]
    public class LoginSteps
    {
        [Given(@"a user named (.*)")]
        public void GivenAUserNamed(string name) { }

        [When(@"they sign in")]
        public void WhenTheySignIn() { }

        [Then(@"the dashboard is shown")]
        public void ThenTheDashboardIsShown() { }

        // Ambiguity #1: exact literal.
        [Given(@"the system is ready")]
        public void SystemReadyExact() { }

        // Ambiguity #2: pattern that also matches "the system is ready".
        [Given(@"the system is (.*)")]
        public void SystemReadyPattern(string state) { }
    }
}
