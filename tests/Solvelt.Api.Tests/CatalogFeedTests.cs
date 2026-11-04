using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Admin;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using Solvelt.Api.Products;
using Solvelt.Api.Services;
using static Solvelt.Api.Tests.Cat;

namespace Solvelt.Api.Tests;

public class CatalogFeedTests(CatalogFactory f) : IClassFixture<CatalogFactory>
{
    private static readonly string[] Paths = ["/listings", "/listings/00000000-0000-0000-0000-000000000001", "/categories", "/brands", "/images/00000000-0000-0000-0000-000000000001"];

    // ---- signing: every endpoint -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_catalog_endpoint_requires_a_valid_signature_a_fresh_timestamp_and_an_unused_nonce()
    {
        foreach (var p in Paths)
        {
            var path = Base + p;
            Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().SendAsync(Orders.Signed(f, "GET", path, null, sign: false))).StatusCode);                 // unsigned
            Assert.Equal(HttpStatusCode.Unauthorized, (await SignedGet(f, path, secret: "not-the-secret-0123456789abcdef0123")).StatusCode);                       // wrong key
            var stale = await SignedGet(f, path, ts: f.Time.GetUtcNow().ToUnixTimeSeconds() - 3600);
            Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode); Assert.Equal("stale", await Orders.Code(stale));
            var nonce = Guid.NewGuid().ToString("N");
            Assert.NotEqual(HttpStatusCode.Unauthorized, (await SignedGet(f, path, nonce)).StatusCode);                                                              // first use passes the gate (200 or 404)
            var replay = await SignedGet(f, path, nonce);
            Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode); Assert.Equal("replayed", await Orders.Code(replay));
        }
        // the query string is part of the signature: a signature for limit=1 is no good for limit=200
        var req = Orders.Signed(f, "GET", Base + "/listings?limit=1", null);
        req.RequestUri = new Uri("https://localhost" + Base + "/listings?limit=200");
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().SendAsync(req)).StatusCode);
    }

    // ---- what is listed ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_feed_contains_only_active_items_of_approved_sellers_and_never_drafts_hidden_or_suspended_listings()
    {
        var s = await Orders.NewShop(f);                                                         // an active, listed product
        var draft = Guid.Parse((string)(await ProdApi.Create(s.Owner, "Unfinished draft"))["id"]!);
        var inactive = await ProdApi.ReadyProduct(s.Owner, "Paused listing", s.Brand, "SKU-" + Guid.NewGuid().ToString("N")[..8]);
        var hidden = await Orders.ActiveProduct(s.Owner, "Hidden by staff", s.Brand, "SKU-" + Guid.NewGuid().ToString("N")[..8]);
        var archived = await Orders.ActiveProduct(s.Owner, "Archived listing", s.Brand, "SKU-" + Guid.NewGuid().ToString("N")[..8]);
        var mod = await Staff(f, StaffRole.Moderator);
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/listings/{hidden}/hide", new { reason = "Counterfeit brand name in the title" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Post($"/api/v1/seller/products/{archived}/status", new { status = "archived" })).StatusCode);

        var other = await Orders.NewShop(f);                                                     // a second seller that gets suspended
        await SetSellerStatus(f, other.Seller, "Suspended");

        var (items, _) = await ReadFeed(f);
        Assert.True(InFeed(items, s.Product));
        foreach (var hiddenOne in new[] { draft, inactive, hidden, archived, other.Product }) Assert.False(InFeed(items, hiddenOne), hiddenOne + " must not be listed");
        foreach (var hiddenOne in new[] { draft, inactive, hidden, archived, other.Product }) Assert.Null(await Listing(f, hiddenOne));
        Assert.NotNull(await Listing(f, s.Product));
        // the brand is only published while it has a visible listing
        var brands = await GetOk(f, Base + "/brands");
        Assert.Contains(brands["items"]!.AsArray(), b => (string)b!["brandId"]! == s.Brand.ToString());
        Assert.DoesNotContain(brands["items"]!.AsArray(), b => (string)b!["brandId"]! == other.Brand.ToString());
    }

    [Fact]
    public async Task A_listing_document_has_prices_in_minor_units_enabled_variants_availability_category_brand_seller_attributes_faqs_images_and_published_questions_only()
    {
        var s = await Orders.NewShop(f);
        var id = s.Product;
        // two variants, one disabled
        var put = await s.Owner.Put($"/api/v1/seller/products/{id}/variants", new
        {
            hasVariants = true,
            options = new[] { new { name = "Size", values = new[] { "S", "M" } } },
            variants = new[] { new { values = new[] { "S" }, sku = "V-S", price = 18.5, quantity = 7, barcode = (string?)null, enabled = true }, new { values = new[] { "M" }, sku = "V-M", price = 21.0, quantity = 3, barcode = (string?)null, enabled = false } }
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Put($"/api/v1/seller/products/{id}/faqs", new[] { new { question = "Dishwasher safe?", answer = "Yes." } })).StatusCode);
        var q = await Question(id, "Is it microwave safe?", "Zed Buyerson");
        Assert.Equal(HttpStatusCode.Created, q.StatusCode);
        var qid = (await ProdApi.Node(q))["id"]!.GetValue<Guid>();
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Put($"/api/v1/seller/questions/{qid}/answer", new { answer = "Yes, up to 90 seconds.", publish = true })).StatusCode);

        var doc = (await Listing(f, id))!;
        Assert.Equal(id.ToString(), (string)doc["listingId"]!); Assert.Equal("product", (string)doc["type"]!); Assert.Equal("USD", (string)doc["price"]!["currency"]!);
        var variants = doc["variants"]!.AsArray();
        Assert.Single(variants);                                                              // enabled only
        Assert.Equal("V-S", (string)variants[0]!["sku"]!); Assert.Equal(1850, (long)variants[0]!["price"]!["amountMinor"]!);
        Assert.Equal("in_stock", (string)variants[0]!["availability"]!["state"]!); Assert.Equal("in_stock", (string)doc["availability"]!["state"]!);
        Assert.NotNull(doc["category"]!["slug"]); Assert.Equal(s.Brand.ToString(), (string)doc["brand"]!["brandId"]!);
        Assert.Equal("Dishwasher safe?", (string)doc["faqs"]![0]!["question"]!);
        Assert.Equal("Is it microwave safe?", (string)doc["questions"]![0]!["question"]!); Assert.Equal("Yes, up to 90 seconds.", (string)doc["questions"]![0]!["answer"]!);
        var img = doc["images"]![0]!;
        Assert.Equal(1200, (int)img["width"]!); Assert.Equal(1200, (int)img["height"]!); Assert.NotNull(img["imageId"]);
        var sellerKeys = ((JsonObject)doc["seller"]!).Select(k => k.Key).OrderBy(k => k).ToArray();
        Assert.Equal(["country", "displayName", "sellerId"], sellerKeys);                     // public seller fields only
        Assert.Empty(((JsonObject)doc["brand"]!).Select(k => k.Key).Except(["brandId", "name", "slug", "description", "website"]));
    }

    [Fact]
    public async Task Search_terms_are_a_string_array_in_the_feed_and_the_single_listing_empty_when_there_are_none()
    {
        var s = await Orders.NewShop(f);
        Assert.Empty((await Listing(f, s.Product))!["searchTerms"]!.AsArray());                  // none set: an empty array, never null or absent
        var v0 = Version(f, s.Product);
        Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(s.Owner, s.Product, d => d["searchTerms"] = "mug, Coffee cup ; MUG\nceramic , ,")).StatusCode);
        Assert.True(Version(f, s.Product) > v0);                                                  // a keyword edit is a catalog change
        var expected = new[] { "mug", "Coffee cup", "ceramic" };                                  // split, trimmed, blanks and case-insensitive duplicates dropped
        Assert.Equal(expected, (await Listing(f, s.Product))!["searchTerms"]!.AsArray().Select(x => (string)x!).ToArray());
        var inFeed = (await ReadFeed(f)).Items.Last(i => (string)i["listingId"]! == s.Product.ToString());
        Assert.Equal(expected, inFeed["searchTerms"]!.AsArray().Select(x => (string)x!).ToArray());
    }

    [Fact]
    public async Task Nothing_private_ever_appears_in_the_feed_no_costs_no_buyer_names_no_contact_data_no_moderation_text()
    {
        var s = await Orders.NewShop(f);
        Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(s.Owner, s.Product, d => d["cost"] = 7.77)).StatusCode);
        var q = await Question(s.Product, "Does it chip?", "Zed Buyerson");
        var qid = (await ProdApi.Node(q))["id"]!.GetValue<Guid>();
        await s.Owner.Put($"/api/v1/seller/questions/{qid}/answer", new { answer = "Not so far.", publish = true });
        var email = s.Owner.Session!.Email;
        var raw = await (await SignedGet(f, $"{Base}/listings/{s.Product}")).Content.ReadAsStringAsync();
        var feed = await (await SignedGet(f, $"{Base}/listings?limit=200")).Content.ReadAsStringAsync();
        foreach (var body in new[] { raw, feed })
        {
            Assert.DoesNotContain("Zed", body);                     // the asker's name is buyer data
            Assert.DoesNotContain(email, body);
            Assert.DoesNotMatch(@"[:,\[]777[,}\]]", body);       // cost 7.77 -> 777 cents as a JSON number (a bare "777" can occur inside random ids)
            Assert.DoesNotContain("cost", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("qualityScore", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("reserved", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("contactPhone", body, StringComparison.OrdinalIgnoreCase);
        }
        // staff hide it: the moderation reason is not in the tombstone either
        var mod = await Staff(f, StaffRole.Moderator);
        await mod.Post($"/api/v1/admin/listings/{s.Product}/hide", new { reason = "Secret internal moderation reason" });
        var after = await (await SignedGet(f, $"{Base}/listings?since=0&limit=200")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Secret internal moderation reason", after);
    }

    // ---- cursor ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_cursor_resumes_pages_without_gaps_or_repeats_and_returns_changes_again_with_a_higher_version()
    {
        var s = await Orders.NewShop(f);
        var ids = new List<Guid> { s.Product };
        for (var i = 0; i < 4; i++) ids.Add(await Orders.ActiveProduct(s.Owner, "Mug no " + i, s.Brand, "SKU-" + Guid.NewGuid().ToString("N")[..8]));
        var start = (await ReadFeed(f)).Cursor;                                                 // everything created so far (this fixture is shared)

        var more = new List<Guid>();
        for (var i = 0; i < 5; i++) more.Add(await Orders.ActiveProduct(s.Owner, "Later mug " + i, s.Brand, "SKU-" + Guid.NewGuid().ToString("N")[..8]));
        // page through with limit 2
        var seen = new List<JsonNode>(); var cursor = start; var pages = 0;
        while (pages < 50)
        {
            var page = await GetOk(f, $"{Base}/listings?since={cursor}&limit=2");
            Assert.True(page["items"]!.AsArray().Count <= 2);
            seen.AddRange(page["items"]!.AsArray()!);
            cursor = (string)page["nextCursor"]!; pages++;
            if (!(bool)page["hasMore"]!) break;
        }
        Assert.True(pages >= 3);
        Assert.Equal(more.Select(m => m.ToString()).OrderBy(x => x), seen.Select(i => (string)i["listingId"]!).OrderBy(x => x));      // each new listing exactly once
        var versions = seen.Select(i => (long)i["version"]!).ToList();
        Assert.Equal(versions.OrderBy(v => v), versions);                                                                              // served in version order
        Assert.Equal(versions.Count, versions.Distinct().Count());
        Assert.Equal(versions.Max().ToString(), cursor);

        // nothing changed: an empty page that keeps the cursor
        var idle = await GetOk(f, $"{Base}/listings?since={cursor}");
        Assert.Empty(idle["items"]!.AsArray()); Assert.Equal(cursor, (string)idle["nextCursor"]!); Assert.False((bool)idle["hasMore"]!);

        // a change shows up again, with a version above the cursor
        await SetPrice(s.Owner, more[2], 33.33);
        var changed = await ReadFeed(f, cursor);
        var one = Assert.Single(changed.Items);
        Assert.Equal(more[2].ToString(), (string)one["listingId"]!); Assert.True((long)one["version"]! > long.Parse(cursor));
        Assert.Equal(3333, (long)one["price"]!["amountMinor"]!);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=201")]
    [InlineData("limit=-5")]
    [InlineData("since=abc")]
    [InlineData("since=-1")]
    [InlineData("since=1.5")]
    public async Task Pagination_parameters_are_bounds_checked(string query)
    {
        var r = await SignedGet(f, $"{Base}/listings?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains((string)(await Orders.Node(r))["code"]!, new[] { "invalid_limit", "invalid_cursor" });
        Assert.Equal(HttpStatusCode.BadRequest, (await SignedGet(f, $"{Base}/brands?{query.Replace("since=abc", "since=zzz").Replace("since=-1", "since=zzz").Replace("since=1.5", "since=zzz")}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SignedGet(f, $"{Base}/listings?limit=200")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SignedGet(f, $"{Base}/listings?limit=1")).StatusCode);
    }

    [Fact]
    public async Task Versions_are_unique_and_strictly_increase_with_every_change_and_events_carry_them_in_order()
    {
        var s = await Orders.NewShop(f);
        var v0 = Version(f, s.Product);
        Assert.True(v0 > 0);
        await SetPrice(s.Owner, s.Product, 20); var v1 = Version(f, s.Product);
        await SetPrice(s.Owner, s.Product, 21); var v2 = Version(f, s.Product);
        Assert.True(v0 < v1 && v1 < v2);
        // a change that is not visible in the catalog (a cost price) does not move the version
        Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(s.Owner, s.Product, d => d["cost"] = 3.5)).StatusCode);
        Assert.Equal(v2, Version(f, s.Product));
        var ev = ListingEvents(f, s.Product);
        Assert.All(ev, e => Assert.Equal("listing.upserted", e.Type));
        var evVersions = ev.Select(e => (long)e.Data["version"]!).ToList();
        Assert.Equal(evVersions.OrderBy(v => v), evVersions); Assert.Equal(evVersions.Count, evVersions.Distinct().Count());
        Assert.Equal(v2, evVersions.Last());
        Assert.All(ev, e => Assert.Equal(s.Product.ToString(), (string)e.Data["listingId"]!));
        // every version in the database is unique across listings
        var dup = f.WithDb(db => db.Products.AsNoTracking().Where(p => p.CatalogVersion > 0).GroupBy(p => p.CatalogVersion).Count(g => g.Count() > 1));
        Assert.Equal(0, dup);
    }

    [Fact]
    public async Task Versions_are_handed_out_by_the_counter_row_which_always_equals_the_highest_version()
    {
        // The counter row is what serializes catalog-changing transactions on PostgreSQL (a row lock held until commit); a version taken any other way
        // (MAX()+1, a sequence) would not be visible in commit order. SQLite cannot show that race, so this pins the mechanism itself.
        // Every version handed out lives on in a product row or, for a deleted listing, in its tombstone, so the counter is exactly the highest of them.
        var s = await Orders.NewShop(f);
        long Counter() => f.WithDb(db => db.CatalogCounters.AsNoTracking().Select(c => c.Value).Single());
        var before = Counter();
        await SetPrice(s.Owner, s.Product, 12); await SetPrice(s.Owner, s.Product, 13);
        var after = Counter();
        Assert.True(after - before >= 2, $"two visible changes must advance the counter by at least 2 (was {before}, now {after})");
        var highest = f.WithDb(db => Math.Max(db.Products.AsNoTracking().Max(p => p.CatalogVersion), db.CatalogTombstones.AsNoTracking().Select(t => (long?)t.Version).Max() ?? 0));
        Assert.Equal(highest, after);
    }

    [Fact]
    public async Task After_a_burst_of_concurrent_updates_the_feed_returns_every_change_and_any_resumed_cursor_converges_to_the_final_state()
    {
        var s = await Orders.NewShop(f);
        var products = new List<Guid> { s.Product };
        for (var i = 0; i < 7; i++) products.Add(await Orders.ActiveProduct(s.Owner, "Burst mug " + i, s.Brand, "SKU-" + Guid.NewGuid().ToString("N")[..8]));
        var start = (await ReadFeed(f)).Cursor;

        // each product is updated 5 times, all products in parallel, while a reader keeps polling the feed and remembering cursors
        var stop = false; var snapshots = new List<(string Cursor, Dictionary<string, long> State)>(); var polled = new Dictionary<string, JsonNode>(); var cur = start;
        var reader = Task.Run(async () =>
        {
            while (!Volatile.Read(ref stop))
            {
                var page = await GetOk(f, $"{Base}/listings?since={cur}&limit=3");
                foreach (var i in page["items"]!.AsArray()) polled[(string)i!["listingId"]!] = i!;
                cur = (string)page["nextCursor"]!;
                snapshots.Add((cur, polled.ToDictionary(k => k.Key, k => (long)k.Value["version"]!)));
                await Task.Delay(5);
            }
        });
        var writers = products.Select((p, n) => Task.Run(async () =>
        {
            for (var k = 1; k <= 5; k++) { var r = await ProdApi.Put(s.Owner, p, d => d["price"] = 100 + n * 10 + k); Assert.Equal(HttpStatusCode.OK, r.StatusCode); }
        })).ToArray();
        await Task.WhenAll(writers);
        Volatile.Write(ref stop, true); await reader;

        // 1. every listing's final state is in the feed from the start cursor, at the version stored in the database
        var (all, _) = await ReadFeed(f, start, 3);
        var final = all.GroupBy(i => (string)i["listingId"]!).ToDictionary(g => g.Key, g => g.OrderBy(i => (long)i["version"]!).Last());
        foreach (var (p, n) in products.Select((p, n) => (p, n)))
        {
            Assert.Equal(Version(f, p), (long)final[p.ToString()]["version"]!);
            Assert.Equal((100 + n * 10 + 5) * 100L, (long)final[p.ToString()]["price"]!["amountMinor"]!);
        }
        // 2. the poller that ran DURING the burst, followed by a resume from its last cursor, ends with exactly the same final state (nothing skipped)
        var (rest, _) = await ReadFeed(f, cur, 3);
        foreach (var i in rest) polled[(string)i["listingId"]!] = i;
        foreach (var p in products) Assert.Equal(Version(f, p), (long)polled[p.ToString()]["version"]!);
        // 3. resuming from ANY cursor the poller ever held reaches the same final state
        foreach (var (c, state) in snapshots.Where((_, n) => n % 3 == 0))
        {
            var replay = new Dictionary<string, long>(state);
            foreach (var i in (await ReadFeed(f, c, 5)).Items) replay[(string)i["listingId"]!] = (long)i["version"]!;
            foreach (var p in products) Assert.Equal(Version(f, p), replay[p.ToString()]);
        }
        // 4. versions stayed unique and the events carry each listing's versions in increasing order
        Assert.Equal(0, f.WithDb(db => db.Products.AsNoTracking().Where(p => p.CatalogVersion > 0).GroupBy(p => p.CatalogVersion).Count(g => g.Count() > 1)));
        foreach (var p in products)
        {
            var vs = ListingEvents(f, p).Select(e => (long)e.Data["version"]!).ToList();
            Assert.Equal(vs.OrderBy(v => v), vs); Assert.Equal(Version(f, p), vs.Last());
        }
    }

    // ---- unlisted --------------------------------------------------------------------------------------------------------------------------

    private async Task<(Shop Shop, Guid Product)> Listed(string title = "Unlisting test mug")
    {
        var s = await Orders.NewShop(f);
        var id = await Orders.ActiveProduct(s.Owner, title, s.Brand, "SKU-" + Guid.NewGuid().ToString("N")[..8]);
        return (s, id);
    }

    private (string Type, JsonNode Data) LastEvent(Guid id) => ListingEvents(f, id).Last();

    private async Task AssertUnlisted(Guid id, string reason)
    {
        var (type, data) = LastEvent(id);
        Assert.Equal("listing.unlisted", type);
        Assert.Equal(reason, (string)data["reason"]!); Assert.Equal(id.ToString(), (string)data["listingId"]!); Assert.Equal(Version(f, id), (long)data["version"]!);
        Assert.Null(await Listing(f, id));
        var (items, _) = await ReadFeed(f);
        var tomb = items.Last(i => (string)i["listingId"]! == id.ToString());                 // the feed reports the removal too, for consumers that missed the event
        Assert.False((bool)tomb["listed"]!); Assert.Equal(reason, (string)tomb["reason"]!); Assert.Null(tomb["title"]);
    }

    [Fact]
    public async Task Unlisted_fires_with_reason_not_active_when_the_seller_deactivates_and_upserted_when_it_comes_back()
    {
        var (s, id) = await Listed();
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Post($"/api/v1/seller/products/{id}/status", new { status = "inactive" })).StatusCode);
        await AssertUnlisted(id, "not_active");
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Post($"/api/v1/seller/products/{id}/status", new { status = "active" })).StatusCode);
        Assert.Equal("listing.upserted", LastEvent(id).Type); Assert.NotNull(await Listing(f, id));
    }

    [Fact]
    public async Task Unlisted_fires_with_reason_archived()
    {
        var (s, id) = await Listed();
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Post($"/api/v1/seller/products/{id}/status", new { status = "archived" })).StatusCode);
        await AssertUnlisted(id, "archived");
    }

    [Fact]
    public async Task Unlisted_fires_with_reason_seller_suspended_for_every_listing_of_the_seller_and_they_return_on_approval()
    {
        var (s, id) = await Listed();
        var second = await Orders.ActiveProduct(s.Owner, "Second mug", s.Brand, "SKU-" + Guid.NewGuid().ToString("N")[..8]);
        await SetSellerStatus(f, s.Seller, "Suspended");
        await AssertUnlisted(id, "seller_suspended"); await AssertUnlisted(second, "seller_suspended");
        await SetSellerStatus(f, s.Seller, "Approved");
        Assert.Equal("listing.upserted", LastEvent(id).Type); Assert.Equal("listing.upserted", LastEvent(second).Type);
        Assert.NotNull(await Listing(f, id));
    }

    [Fact]
    public async Task Unlisted_fires_with_reason_moderated_when_staff_hide_the_listing_and_upserted_when_restored()
    {
        var (_, id) = await Listed();
        var mod = await Staff(f, StaffRole.Moderator);
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/listings/{id}/hide", new { reason = "Counterfeit brand name in the title" })).StatusCode);
        await AssertUnlisted(id, "moderated");
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/listings/{id}/unhide", new { reason = "Title corrected" })).StatusCode);
        Assert.Equal("listing.upserted", LastEvent(id).Type); Assert.NotNull(await Listing(f, id));
    }

    [Fact]
    public async Task Unlisted_fires_with_reason_brand_removed_when_the_brand_is_archived()
    {
        var (s, _) = await Listed();
        var brand = await TeamHelpers.NewBrand(s.Owner, "Doomed brand " + Guid.NewGuid().ToString("N")[..6]);
        var id = await Orders.ActiveProduct(s.Owner, "Doomed brand mug", brand, "SKU-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.NotNull(await Listing(f, id));
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Post($"/api/v1/brands/{brand}/archive")).StatusCode);
        await AssertUnlisted(id, "brand_removed");
        var brands = await GetOk(f, Base + "/brands");
        Assert.DoesNotContain(brands["items"]!.AsArray(), b => (string)b!["brandId"]! == brand.ToString());
    }

    [Fact]
    public async Task A_rejected_change_writes_neither_a_new_version_nor_an_event()
    {
        var (s, id) = await Listed();
        var before = (Version(f, id), ListingEvents(f, id).Count);
        var stale = await ProdApi.Get(s.Owner, id);
        await SetPrice(s.Owner, id, 44);                                                       // bumps the version once, legitimately
        var mid = (Version(f, id), ListingEvents(f, id).Count);
        Assert.Equal(before.Item2 + 1, mid.Item2);
        var d = (JsonObject)stale; d["price"] = 99;                                            // a write based on the stale document is refused (409)...
        var r = await s.Owner.Put($"/api/v1/seller/products/{id}", System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(d.ToJsonString()));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal(mid, (Version(f, id), ListingEvents(f, id).Count));                       // ...and leaves no trace in the catalog
    }

    // ---- delivery order of events -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_older_listing_event_is_never_delivered_after_a_newer_one()
    {
        var wf = new WebhookFactory();
        var s = await Orders.NewShop(wf);
        await SetPrice(s.Owner, s.Product, 20); await SetPrice(s.Owner, s.Product, 21);       // listing.upserted v1, v2, v3 pending
        var pending = ListingEvents(wf, s.Product);
        Assert.True(pending.Count >= 3);
        var oldest = wf.WithDb(db => db.Outbox.AsNoTracking().Where(m => m.AggregateId == s.Product.ToString()).OrderBy(m => m.OrderKey).First());
        // the marketplace fails the OLDEST event only
        wf.Handler = req => new HttpResponseMessage(wf.Calls.Count > 0 && req.Headers.GetValues(Signing.EventIdHeader).First() == oldest.Id.ToString() ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        wf.Handler = req => new HttpResponseMessage(req.Headers.GetValues(Signing.EventIdHeader).First() == oldest.Id.ToString() ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        await wf.Services.CreateScope().ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchDueAsync();
        var delivered = wf.WithDb(db => db.Outbox.AsNoTracking().Where(m => m.AggregateId == s.Product.ToString() && m.Status == OutboxStatus.Delivered).Select(m => m.OrderKey).ToList());
        Assert.Empty(delivered);                                                                // newer events wait for the older one
        wf.Handler = _ => new HttpResponseMessage(HttpStatusCode.OK);
        wf.Time.Advance(TimeSpan.FromHours(1));
        for (var i = 0; i < 3; i++) await wf.Services.CreateScope().ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchDueAsync();
        var order = wf.Calls.Where(c => c.Body.Contains(s.Product.ToString())).Select(c => (long)JsonNode.Parse(c.Body)!["data"]!["version"]!).ToList();
        Assert.Equal(order.OrderBy(v => v), order);                                             // delivered strictly in version order
        Assert.Equal(pending.Select(p => (long)p.Data["version"]!).OrderBy(v => v), order.Distinct());
        // an old event that is re-queued after newer ones went out is obsolete and is never sent (admins can re-queue dead events)
        var callsBefore = wf.Calls.Count;
        wf.WithDb(db => db.Outbox.Where(m => m.Id == oldest.Id).ExecuteUpdate(u => u.SetProperty(m => m.Status, OutboxStatus.Pending).SetProperty(m => m.NextAttemptAt, wf.Time.GetUtcNow().UtcDateTime)));
        await wf.Services.CreateScope().ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchDueAsync();
        Assert.Equal(callsBefore, wf.Calls.Count);
        Assert.Equal(OutboxStatus.Dead, wf.WithDb(db => db.Outbox.AsNoTracking().Single(m => m.Id == oldest.Id).Status));
    }

    // ---- availability ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Availability_changes_move_the_version_but_selling_one_unit_of_many_does_not()
    {
        var s = await Orders.NewShop(f, stock: 3);
        var v0 = Version(f, s.Product);
        var inv = f.Services.CreateScope().ServiceProvider.GetRequiredService<IInventoryService>();
        Assert.True((await inv.ReserveAsync(s.Seller, s.Product, null, 1)).Ok);               // 3 -> 2 available: still in stock
        Assert.Equal(v0, Version(f, s.Product));
        Assert.True((await inv.ReserveAsync(s.Seller, s.Product, null, 2)).Ok);               // 0 available: out of stock
        var v1 = Version(f, s.Product);
        Assert.True(v1 > v0);
        Assert.Equal("out_of_stock", (string)(await Listing(f, s.Product))!["availability"]!["state"]!);
        Assert.Equal("listing.upserted", LastEvent(s.Product).Type); Assert.Equal(v1, (long)LastEvent(s.Product).Data["version"]!);
        Assert.True((await inv.ReleaseAsync(s.Seller, s.Product, null, 1)).Ok);               // back in stock
        Assert.True(Version(f, s.Product) > v1);
        Assert.Equal("in_stock", (string)(await Listing(f, s.Product))!["availability"]!["state"]!);
    }

    // ---- categories, brands -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Categories_and_brands_are_served_with_public_fields_only()
    {
        var cats = await GetOk(f, Base + "/categories");
        var first = cats["items"]!.AsArray().First(c => (string)c!["slug"]! == "kitchen-dining")!;
        Assert.NotNull(first["categoryId"]); Assert.NotNull(first["attributes"]);
        var s = await Orders.NewShop(f);
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Put($"/api/v1/brands/{s.Brand}", new { name = "Fancy Brand", contactEmail = "private-brand-contact@example.com", website = "https://example.com" })).StatusCode);
        var raw = await (await SignedGet(f, Base + "/brands?limit=200")).Content.ReadAsStringAsync();
        Assert.Contains("Fancy Brand", raw); Assert.DoesNotContain("private-brand-contact", raw);
        // brands page with a keyset cursor
        var p1 = await GetOk(f, Base + "/brands?limit=1");
        if ((bool)p1["hasMore"]!) Assert.NotEqual(p1["items"]![0]!["brandId"]!.ToString(), (await GetOk(f, Base + $"/brands?limit=1&since={p1["nextCursor"]}"))["items"]![0]!["brandId"]!.ToString());
    }

    // ---- images --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Images_are_served_for_listed_items_only_with_nosniff_and_anything_else_is_a_404()
    {
        var s = await Orders.NewShop(f);
        var doc = (await Listing(f, s.Product))!;
        var imageId = Guid.Parse((string)doc["images"]![0]!["imageId"]!);
        var ok = await SignedGet(f, $"{Base}/images/{imageId}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("nosniff", ok.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("image/png", ok.Content.Headers.ContentType!.MediaType);
        var bytes = await ok.Content.ReadAsByteArrayAsync();
        var key = f.WithDb(db => db.ProductImages.AsNoTracking().Where(i => i.Id == imageId).Select(i => i.StorageKey).Single());
        using var stored = (await f.Services.CreateScope().ServiceProvider.GetRequiredService<IFileStorage>().OpenReadAsync(key))!;
        using var ms = new MemoryStream(); await stored.CopyToAsync(ms);
        Assert.Equal(ms.ToArray(), bytes);                                                       // the original (stored) bytes
        Assert.NotEmpty(bytes);

        // an image of a DRAFT product, a random id, and an image of a product that stops being listed are all 404
        var draft = Guid.Parse((string)(await ProdApi.Create(s.Owner, "Draft with photo"))["id"]!);
        Assert.Equal(HttpStatusCode.Created, (await ProdApi.Upload(s.Owner, draft, TestImages.Png())).StatusCode);
        var draftImage = f.WithDb(db => db.ProductImages.AsNoTracking().Where(i => i.ProductId == draft).Select(i => i.Id).Single());
        Assert.Equal(HttpStatusCode.NotFound, (await SignedGet(f, $"{Base}/images/{draftImage}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SignedGet(f, $"{Base}/images/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Post($"/api/v1/seller/products/{s.Product}/status", new { status = "inactive" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SignedGet(f, $"{Base}/images/{imageId}")).StatusCode);
        // the same for staff-hidden and suspended
        var (s2, hidden) = await Listed("Hidden photo");
        var hiddenImage = f.WithDb(db => db.ProductImages.AsNoTracking().Where(i => i.ProductId == hidden).Select(i => i.Id).Single());
        Assert.Equal(HttpStatusCode.OK, (await SignedGet(f, $"{Base}/images/{hiddenImage}")).StatusCode);
        await SetSellerStatus(f, s2.Seller, "Suspended");
        Assert.Equal(HttpStatusCode.NotFound, (await SignedGet(f, $"{Base}/images/{hiddenImage}")).StatusCode);
    }

    // ---- questions ----------------------------------------------------------------------------------------------------------------------

    private Task<HttpResponseMessage> Question(Guid product, string text, string? name = "Sam", string? questionId = null) =>
        PostSigned(f, $"/api/internal/v1/products/{product}/questions", new { questionId = questionId ?? "Q-" + Guid.NewGuid().ToString("N")[..12], text, buyerDisplayName = name });

    [Fact]
    public async Task Questions_are_idempotent_on_questionId_and_feed_the_seller_inbox_once()
    {
        var s = await Orders.NewShop(f);
        var qid = "Q-" + Guid.NewGuid().ToString("N");
        var first = await Question(s.Product, "Is the glaze food safe?", "Sam", qid);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var id1 = (string)(await Orders.Node(first))["id"]!;
        var again = await Question(s.Product, "Is the glaze food safe?", "Sam", qid);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var j = await Orders.Node(again); Assert.True((bool)j["duplicate"]!); Assert.Equal(id1, (string)j["id"]!);
        Assert.Equal(1, f.WithDb(db => db.Questions.Count(q => q.ExternalId == qid)));
        // in the seller's existing Q&A inbox, unanswered, exactly once
        var inbox = await s.Owner.JsonBody(await s.Owner.Get($"/api/v1/seller/questions?productId={s.Product}"));
        var rows = inbox.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("text").GetString() == "Is the glaze food safe?").ToList();
        Assert.Single(rows); Assert.Equal("Sam", rows[0].GetProperty("askerName").GetString()); Assert.False(rows[0].GetProperty("answered").GetBoolean());
        // the same questionId for another listing is a conflict, not a second question
        var other = await Orders.NewShop(f);
        var conflict = await Question(other.Product, "Different product?", "Sam", qid);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode); Assert.Equal("question_id_conflict", await Orders.Code(conflict));
    }

    [Fact]
    public async Task Parallel_deliveries_of_the_same_question_store_one_row()
    {
        var s = await Orders.NewShop(f);
        var qid = "Q-" + Guid.NewGuid().ToString("N");
        var res = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Question(s.Product, "Parallel question?", "Sam", qid)));
        Assert.All(res, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK }));
        Assert.Single(res, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(1, f.WithDb(db => db.Questions.Count(q => q.ExternalId == qid)));
    }

    [Fact]
    public async Task Questions_are_validated_signed_and_only_accepted_for_listed_items()
    {
        var s = await Orders.NewShop(f);
        var path = $"/api/internal/v1/products/{s.Product}/questions";
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().SendAsync(Orders.Signed(f, "POST", path, new { questionId = "x", text = "y" }, sign: false))).StatusCode);
        var stale = await PostSigned(f, path, new { questionId = "Q1", text = "y" }, ts: f.Time.GetUtcNow().ToUnixTimeSeconds() - 3600);
        Assert.Equal("stale", await Orders.Code(stale));
        var nonce = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.Created, (await PostSigned(f, path, new { questionId = "Q-" + nonce, text = "y" }, nonce)).StatusCode);
        var replay = await PostSigned(f, path, new { questionId = "Q-" + nonce, text = "y" }, nonce);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode); Assert.Equal("replayed", await Orders.Code(replay));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Question(s.Product, "")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Question(s.Product, new string('x', 1001))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Question(s.Product, "ok?", new string('n', 81))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostSigned(f, path, new { questionId = "", text = "ok?" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Question(Guid.NewGuid(), "ok?")).StatusCode);
        var draft = Guid.Parse((string)(await ProdApi.Create(s.Owner, "Draft"))["id"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await Question(draft, "ok?")).StatusCode);
    }

    [Fact]
    public async Task A_pending_question_does_not_change_the_listing_but_a_published_answer_does()
    {
        var s = await Orders.NewShop(f);
        var v0 = Version(f, s.Product);
        var q = await Question(s.Product, "Can I use it outdoors?", "Zed Buyerson");
        Assert.Equal(v0, Version(f, s.Product));
        var qid = (await Orders.Node(q))["id"]!.GetValue<Guid>();
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Put($"/api/v1/seller/questions/{qid}/answer", new { answer = "Yes.", publish = false })).StatusCode);   // answered but not published
        Assert.Equal(v0, Version(f, s.Product));
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Post($"/api/v1/seller/questions/{qid}/publish")).StatusCode);
        var v1 = Version(f, s.Product);
        Assert.True(v1 > v0);
        Assert.Single((await Listing(f, s.Product))!["questions"]!.AsArray());
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Post($"/api/v1/seller/questions/{qid}/hide")).StatusCode);                 // hiding removes it again, with a new version
        Assert.True(Version(f, s.Product) > v1);
        Assert.Empty((await Listing(f, s.Product))!["questions"]!.AsArray());
    }

    [Fact]
    public async Task The_dev_question_endpoint_still_works_for_dev_and_tests()
    {
        var s = await Orders.NewShop(f);
        var r = await f.NewClient().PostAsJsonAsync("/api/dev/questions", new { productId = s.Product, askerName = "Sam", text = "Dev question?" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
    }
}

/// <summary>The marketplace is ONE caller for all shoppers: limits are keyed on (verified caller, subject), never on the IP alone (docs/CATALOG.md section 8).</summary>
public class MarketplaceWriteLimitTests
{
    private static Task<HttpResponseMessage> Ask(ApiFactory f, Guid listing, string? id = null) =>
        PostSigned(f, $"/api/internal/v1/products/{listing}/questions", new { questionId = id ?? "RL-" + Guid.NewGuid().ToString("N"), text = "How big is it?" });

    private static Task<HttpResponseMessage> Rep(ApiFactory f, string target) =>
        PostSigned(f, "/api/internal/v1/content/reports", new { reportId = "RL-" + Guid.NewGuid().ToString("N"), kind = "review", targetId = target, reason = "spam", reporterRef = "u1" });

    private static Task<HttpResponseMessage> Unsigned(ApiFactory f, string path) =>
        f.NewClient().SendAsync(Orders.Signed(f, "POST", path, new { questionId = "x", text = "y" }, sign: false));

    [Fact]
    public async Task Many_questions_and_reports_across_different_targets_from_the_signed_caller_are_not_throttled_at_120()
    {
        using var f = new CatalogDefaultLimitsFactory();
        // 150 questions for 150 different listings (unknown ids answer 404, but each one was admitted by the limiter first): the old per-IP key stopped at 120
        var q = new List<HttpStatusCode>();
        for (var i = 0; i < 150; i++) q.Add((await Ask(f, Guid.NewGuid())).StatusCode);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, q);
        Assert.All(q, c => Assert.Equal(HttpStatusCode.NotFound, c));
        var r = new List<HttpStatusCode>();
        for (var i = 0; i < 130; i++) r.Add((await Rep(f, "review-" + i)).StatusCode);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, r);
        Assert.All(r, c => Assert.Equal(HttpStatusCode.Created, c));
    }

    [Fact]
    public async Task One_listing_or_one_target_is_throttled_at_its_own_limit_while_others_are_not()
    {
        using var f = new CatalogTightLimitsFactory();
        var s = await Orders.NewShop(f); var s2 = await Orders.NewShop(f);
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++) codes.Add((await Ask(f, s.Product)).StatusCode);
        Assert.Equal(5, codes.Count(c => c == HttpStatusCode.Created));                          // the per-listing limit
        Assert.Equal(3, codes.Count(c => c == HttpStatusCode.TooManyRequests));
        var limited = await Ask(f, s.Product);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode); Assert.Equal("rate_limited", await Orders.Code(limited)); Assert.True(limited.Headers.Contains("Retry-After"));
        Assert.Equal(HttpStatusCode.Created, (await Ask(f, s2.Product)).StatusCode);              // another listing is not affected
        // the same for reports, keyed on the target
        var rep = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) rep.Add((await Rep(f, "rev-hot")).StatusCode);
        Assert.Equal(3, rep.Count(c => c == HttpStatusCode.Created)); Assert.Equal(2, rep.Count(c => c == HttpStatusCode.TooManyRequests));
        Assert.Equal(HttpStatusCode.Created, (await Rep(f, "rev-other")).StatusCode);
    }

    [Fact]
    public async Task The_per_caller_ceiling_caps_the_whole_marketplace_even_across_many_listings()
    {
        using var f = new CatalogTightLimitsFactory();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 45; i++) codes.Add((await Ask(f, Guid.NewGuid())).StatusCode);       // 40 per caller per minute
        Assert.Equal(40, codes.Count(c => c == HttpStatusCode.NotFound));
        Assert.Equal(5, codes.Count(c => c == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task Unsigned_and_forged_floods_are_throttled_per_IP_and_never_touch_the_signed_bucket()
    {
        using var f = new CatalogTightLimitsFactory();
        var s = await Orders.NewShop(f);
        var path = $"/api/internal/v1/products/{s.Product}/questions";
        var flood = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++) flood.Add((await Unsigned(f, path)).StatusCode);
        Assert.Equal(4, flood.Count(c => c == HttpStatusCode.Unauthorized));                     // the small per-IP bucket, then 429
        Assert.Equal(4, flood.Count(c => c == HttpStatusCode.TooManyRequests));
        // forged signatures and stale timestamps draw from the same per-IP bucket; reports share it
        Assert.Equal(HttpStatusCode.TooManyRequests, (await f.NewClient().SendAsync(Orders.Signed(f, "POST", "/api/internal/v1/content/reports", new { reportId = "x" }, secret: "forged-secret-0123456789abcdef0123456"))).StatusCode);
        // ...and from the very same address the signed caller is NOT affected: all of its requests are still admitted
        var signed = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++) signed.Add((await Ask(f, s.Product)).StatusCode);
        Assert.All(signed, c => Assert.Equal(HttpStatusCode.Created, c));
        Assert.Equal(HttpStatusCode.Created, (await Rep(f, "rev-after-flood")).StatusCode);
    }
}
