using Microsoft.Playwright;

namespace Fixture.SpecFlow
{
    // A page-object-shaped class that deliberately does NOT use a "Page" suffix — it is named
    // "Navigator". Slice-2 page-object detection must catch it via its heavy use of Playwright
    // types (IPage/ILocator), not by name. Method count here is exactly 2.
    public class Navigator
    {
        private readonly IPage _page;
        private readonly ILocator _usernameField;

        public Navigator(IPage page, ILocator usernameField)
        {
            _page = page;
            _usernameField = usernameField;
        }

        public void NavigateToLogin() { }

        public void EnterUsername(string value) { }
    }
}
