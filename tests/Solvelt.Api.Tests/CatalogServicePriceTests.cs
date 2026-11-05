using System.Text.Json.Nodes;
using static Solvelt.Api.Tests.Cat;

namespace Solvelt.Api.Tests;

/// <summary>#91: how a service is priced in the catalog feed (docs/CATALOG.md): hourly and fixed services carry a price, a quote-priced one carries a price object whose amount is null.</summary>
public class CatalogServicePriceTests(CatalogFactory f) : IClassFixture<CatalogFactory>
{
    [Fact]
    public async Task A_quote_service_has_a_price_object_with_a_null_amount_and_no_service_price()
    {
        var b = await Svc.NewBiz(f, d => { d["pricingModel"] = "quote"; d.Remove("price"); d.Remove("travelFee"); });
        var l = (await Listing(f, b.Service))!;
        Assert.Equal("service", (string?)l["type"]);
        Assert.NotNull(l["price"]);                                   // the object is always there (a client reading price.amountMinor never meets a null price)
        Assert.True(l["price"]!["amountMinor"] is null);
        Assert.Equal("USD", (string?)l["price"]!["currency"]);
        Assert.Equal("quote", (string?)l["service"]!["pricingModel"]);
        Assert.True(l["service"]!["priceMinor"] is null);
        Assert.Empty(l["variants"]!.AsArray());
        var inFeed = (await ReadFeed(f)).Items.Single(i => (string)i["listingId"]! == b.Service.ToString());
        Assert.True(inFeed["price"]!["amountMinor"] is null);
        Assert.NotNull(inFeed["category"]);
    }

    [Fact]
    public async Task A_service_price_amount_is_null_only_for_the_quote_pricing_model()
    {
        await Svc.NewBiz(f, d => { d["pricingModel"] = "quote"; d.Remove("price"); d.Remove("travelFee"); });
        await Svc.NewBiz(f, d => { d["pricingModel"] = "hourly"; d["price"] = 45; });
        await Svc.NewBiz(f, d => { d["pricingModel"] = "fixed"; d["price"] = 85; d.Remove("minMinutes"); d.Remove("maxMinutes"); d.Remove("incrementMinutes"); });
        var services = (await ReadFeed(f)).Items.Where(i => (string?)i["type"] == "service").ToList();
        Assert.True(services.Count >= 3);
        foreach (var l in services)
        {
            var model = (string?)l["service"]!["pricingModel"];
            var amountNull = l["price"]!["amountMinor"] is null;
            Assert.Equal(model == "quote", amountNull);
            Assert.Equal(amountNull, l["service"]!["priceMinor"] is null);   // service.priceMinor follows price.amountMinor
        }
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
