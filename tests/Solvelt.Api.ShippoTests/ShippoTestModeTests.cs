using Microsoft.Extensions.Logging.Abstractions;
using Solvelt.Api.Shipping;

namespace Solvelt.Api.ShippoTests;

/// <summary>Runs only with SHIPPO_API_KEY=shippo_test_... (never a live token). Skipped in THIS project only; the unit-test project has no skipped tests.</summary>
public sealed class ShippoFactAttribute : FactAttribute
{
    public static string? Key => Environment.GetEnvironmentVariable("SHIPPO_API_KEY") is { Length: > 0 } k ? k : null;
    public ShippoFactAttribute()
    {
        if (Key is null) Skip = "no SHIPPO_API_KEY: Shippo test-mode tests are opt-in (docs/SHIPPING.md)";
        else if (!Key.StartsWith("shippo_test_")) Skip = "refusing to run against anything but a Shippo TEST token";
    }
}

[Trait("Category", "ShippoTestMode")]
public class ShippoTestModeTests
{
    private static ShippoShippingProvider Provider() =>
        new(new HttpClient(), Microsoft.Extensions.Options.Options.Create(new ShippingOptions { ShippoApiKey = ShippoFactAttribute.Key! }), NullLogger<ShippoShippingProvider>.Instance);

    private static readonly ShipAddress From = new("Acme Shop", "Acme", "215 Clayton St.", null, "San Francisco", "CA", "94117", "US", "+1 555 341 9393", "shop@example.com");
    private static readonly ShipAddress To = new("Jane Buyer", null, "965 Mission St", null, "San Francisco", "CA", "94103", "US", "+1 555 341 9394", null);

    [ShippoFact]
    public async Task A_real_address_validates_and_a_nonsense_one_does_not()
    {
        var p = Provider();
        Assert.Equal("valid", (await p.ValidateAddressAsync(To)).Status);
        Assert.NotEqual("valid", (await p.ValidateAddressAsync(To with { Line1 = "1 Nowhere Qwerty Blvd", PostalCode = "00001", City = "Nowhere" })).Status);
    }

    [ShippoFact]
    public async Task Rates_a_label_a_refund_request_and_tracking_registration_work_in_test_mode()
    {
        var p = Provider();
        var rates = await p.GetRatesAsync(new RateRequest(From, To, new ParcelSpec(254, 152, 102, 453), null, false));
        Assert.NotEmpty(rates.Rates);
        Assert.All(rates.Rates, r => Assert.True(r.AmountCents > 0 && r.Currency.Length == 3));
        var cheapest = rates.Rates.OrderBy(r => r.AmountCents).First();
        var label = await p.PurchaseAsync(new PurchaseRequest(cheapest.RateId, rates.ProviderShipmentId, "PDF_4x6", "shippo-test-" + Guid.NewGuid().ToString("N")));
        Assert.False(string.IsNullOrEmpty(label.TrackingNumber));
        Assert.True(label.Bytes.Length > 100);
        Assert.True(await p.RegisterTrackingAsync("USPS", "SHIPPO_TRANSIT"));                       // Shippo's documented test tracking number
        var snap = await p.GetTrackingAsync("USPS", "SHIPPO_TRANSIT");
        Assert.NotNull(snap);
        Assert.Contains(snap!.Status, TrackingStatuses.All);
        var refund = await p.RequestRefundAsync(label.TransactionId);
        Assert.Contains(refund.Status, new[] { "pending", "success" });
    }
}
