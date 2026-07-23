using System.Net.Http;
using Reqnroll;
using SampleShop.Api.Identity;

namespace SampleShop.Tests.Api.Steps
{
    /// <summary>Identity API steps — use the Identity client project.</summary>
    [Binding]
    public class IdentityApiSteps
    {
        private static readonly HttpClient Http = new() { BaseAddress = new System.Uri("https://api.sampleshop.test") };

        private AuthApiClient _auth;
        private UsersApiClient _users;

        [When("the user \"(.*)\" signs in")]
        public void WhenTheUserSignsIn(string email)
        {
            _auth = new AuthApiClient(Http);
            _ = _auth.Login(email, "correct-horse-battery-staple");
        }

        [Then("the current user's profile can be read")]
        public void ThenTheProfileCanBeRead()
        {
            _users = new UsersApiClient(Http);
            _ = _users.GetProfile();
        }
    }
}
