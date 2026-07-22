// Minimal Playwright marker interfaces (real Microsoft.Playwright namespace) so a page-object-shaped
// class can reference IPage/ILocator without the Playwright package. Empty by design — no members,
// so they contribute zero methods to the map (keeps fixture counts easy to reason about).
namespace Microsoft.Playwright
{
    public interface IPage { }

    public interface ILocator { }
}
