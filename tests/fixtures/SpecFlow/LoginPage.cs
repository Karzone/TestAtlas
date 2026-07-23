using Microsoft.Playwright;

namespace Fixture.SpecFlow
{
    // A page object classified purely by INHERITANCE: it carries no UI members and no "Page"-suffix
    // UI-type heuristic on its own — it is a page object only because it derives from Navigator, which
    // is one (heuristic #3). This exercises the `inherits` edge (LoginPage -> Navigator) and gives
    // LoginSteps a page object to drive (the `uses_type` edge). Method count here is exactly 1.
    public class LoginPage : Navigator
    {
        public LoginPage(IPage page, ILocator usernameField) : base(page, usernameField) { }

        public void Open() { }
    }
}
