using System.Text.Json.Nodes;
using static Solvelt.Api.Tests.Cat;

namespace Solvelt.Api.Tests;

/// <summary>#91: how a service is priced in the catalog feed (docs/CATALOG.md): hourly and fixed services carry a price, a quote-priced one carries none.</summary>
public class CatalogServicePriceTests(CatalogFactory f) : IClassFixture<CatalogFactory>
{
    [Fact]
    public async Task A_quote_service_has_no_price_object_and_no_price_in_its_service_block()
    {
        var b = await Svc.NewBiz(f, d => { d["pricingModel"] = "quote"; d.Remove("price"); d.Remove("travelFee"); });
        var l = (await Listing(f, b.Service))!;
        Assert.Equal("service", (string?)l["type"]);
        Assert.True(l["price"] is null, "price must be null (not an object with a null amount): " + l["price"]);
        Assert.Equal("quote", (string?)l["service"]!["pricingModel"]);
        Assert.True(l["service"]!["priceMinor"] is null);
        Assert.Empty(l["variants"]!.AsArray());
        // the same document is in the paged feed, with the same shape
        var inFeed = (await ReadFeed(f)).Items.Single(i => (string)i["listingId"]! == b.Service.ToString());
        Assert.True(inFeed["price"] is null);
        Assert.NotNull(inFeed["category"]);
    }

    [Fact]
    public async Task An_hourly_service_has_the_hourly_rate_as_its_price()
    {
        var b = await Svc.NewBiz(f, d => { d["pricingModel"] = "hourly"; d["price"] = 60; });
        var l = (await Listing(f, b.Service))!;
        Assert.Equal(6000, (int)l["price"]!["amountMinor"]!);
        Assert.Equal("USD", (string?)l["price"]!["currency"]);
        Assert.Equal("hourly", (string?)l["service"]!["pricingModel"]);
        Assert.Equal(6000, (int)l["service"]!["priceMinor"]!);
    }

    [Fact]
    public async Task A_fixed_price_service_has_that_price()
    {
        var b = await Svc.NewBiz(f, d => { d["pricingModel"] = "fixed"; d["price"] = 85; d.Remove("minMinutes"); d.Remove("maxMinutes"); d.Remove("incrementMinutes"); });
        var l = (await Listing(f, b.Service))!;
        Assert.Equal(8500, (int)l["price"]!["amountMinor"]!);
        Assert.Equal("fixed", (string?)l["service"]!["pricingModel"]);
    }
}
