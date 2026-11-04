using System.Net;
using System.Text.Json.Nodes;
using Solvelt.Api.Products;
using static Solvelt.Api.Tests.Cat;

namespace Solvelt.Api.Tests;

/// <summary>C7: the item weight (and dimensions) on the catalog listing document, for weight-based shipping at checkout.</summary>
public class CatalogWeightTests(CatalogFactory f) : IClassFixture<CatalogFactory>
{
    [Fact]
    public async Task Weight_and_dimensions_are_null_when_the_product_has_none()
    {
        var s = await Orders.NewShop(f);
        var doc = (await Listing(f, s.Product))!;
        Assert.True(doc.AsObject().ContainsKey("weightGrams")); Assert.Null(doc["weightGrams"]);                 // present, null: never absent
        Assert.True(doc.AsObject().ContainsKey("dimensionsMm")); Assert.Null(doc["dimensionsMm"]);
        Assert.False(doc.AsObject().ContainsKey("shipping"));                                                    // no per-listing free-shipping flag exists, so none is invented
    }

    [Fact]
    public async Task The_listing_carries_its_weight_and_dimensions_and_every_variant_ships_at_the_product_weight()
    {
        var s = await Orders.NewShop(f);
        Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(s.Owner, s.Product, d => { d["weightGrams"] = 420; d["lengthCm"] = 12.5; d["widthCm"] = 8; d["heightCm"] = 9.5; })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Put($"/api/v1/seller/products/{s.Product}/variants", new
        {
            hasVariants = true,
            options = new[] { new { name = "Size", values = new[] { "S", "M" } } },
            variants = new[] { new { values = new[] { "S" }, sku = "V-S", price = 18.5, quantity = 7, barcode = (string?)null, enabled = true }, new { values = new[] { "M" }, sku = "V-M", price = 21.0, quantity = 3, barcode = (string?)null, enabled = true } }
        })).StatusCode);

        var doc = (await Listing(f, s.Product))!;
        Assert.Equal(420, (int)doc["weightGrams"]!);
        Assert.Equal(125, (int)doc["dimensionsMm"]!["l"]!); Assert.Equal(80, (int)doc["dimensionsMm"]!["w"]!); Assert.Equal(95, (int)doc["dimensionsMm"]!["h"]!);
        var variants = doc["variants"]!.AsArray();
        Assert.Equal(2, variants.Count);
        Assert.All(variants, v => Assert.Equal(420, (int)v!["weightGrams"]!));                                   // variants have no weight of their own: the product's applies
        // the same document is on the feed
        var (items, _) = await ReadFeed(f);
        var inFeed = items.Single(i => (string)i["listingId"]! == s.Product.ToString());
        Assert.Equal(420, (int)inFeed["weightGrams"]!); Assert.Equal(420, (int)inFeed["variants"]![0]!["weightGrams"]!);
    }

    [Fact]
    public async Task Dimensions_are_null_unless_all_three_are_set()
    {
        var s = await Orders.NewShop(f);
        Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(s.Owner, s.Product, d => { d["lengthCm"] = 12.5; d["widthCm"] = 8; })).StatusCode);
        Assert.Null((await Listing(f, s.Product))!["dimensionsMm"]);
    }

    [Fact]
    public async Task Changing_the_weight_or_a_dimension_bumps_the_version_and_emits_listing_upserted()
    {
        var s = await Orders.NewShop(f);
        var v0 = Version(f, s.Product); var n0 = ListingEvents(f, s.Product).Count;
        Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(s.Owner, s.Product, d => d["weightGrams"] = 500)).StatusCode);
        var v1 = Version(f, s.Product);
        Assert.True(v1 > v0, "a weight change must bump the version");
        var ev = ListingEvents(f, s.Product);
        Assert.Equal(n0 + 1, ev.Count); Assert.Equal("listing.upserted", ev[^1].Type);                    // events are pointers: the document read at that version carries the weight
        Assert.Equal(500, (int)(await Listing(f, s.Product))!["weightGrams"]!);
        Assert.Equal(v1, (long)ev[^1].Data["version"]!);

        Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(s.Owner, s.Product, d => d["heightCm"] = 3)).StatusCode);       // a dimension: another version
        Assert.True(Version(f, s.Product) > v1);
        var v2 = Version(f, s.Product);
        Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(s.Owner, s.Product, d => { })).StatusCode);                      // nothing changed: no version
        Assert.Equal(v2, Version(f, s.Product));
    }
}
