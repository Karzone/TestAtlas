using SampleShop.Core;

namespace SampleShop.PageObjects;

/// <summary>Page object for the checkout flow.</summary>
public sealed class CheckoutPage : PageBase
{
    public CheckoutPage(string baseUrl) : base(baseUrl) { }

    public void Open() => Navigate("checkout");

    public void EnterShippingAddress(string address)
    {
        Type("#address", address);
    }

    public void PlaceOrder() => Click("#place-order");
}
