using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Admin;
using Solvelt.Api.Domain;
using static Solvelt.Api.Tests.Cat;

namespace Solvelt.Api.Tests;

/// <summary>Brief N3: the fixes from the review of the first marketplace contract PR (docs/DECISIONS.md M22 and following).</summary>
public class CatalogReviewFixTests(CatalogFactory f) : IClassFixture<CatalogFactory>
{
    private static JsonNode? Entry(IEnumerable<JsonNode> items, Guid id) => items.LastOrDefault(i => (string)i["listingId"]! == id.ToString());

    // ---- 1. deleted listings ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_deleted_listing_is_reported_by_the_feed_to_a_reader_whose_cursor_is_behind_it()
    {
        var s = await Orders.NewShop(f);
        var cursor = (await ReadFeed(f)).Cursor;                                                        // the marketplace has seen the listing and holds cursor C
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Post($"/api/v1/seller/products/{s.Product}/status", new { status = "inactive" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.Delete($"/api/v1/seller/products/{s.Product}")).StatusCode);
        Assert.Equal(0, f.WithDb(db => db.Products.Count(p => p.Id == s.Product)));                     // the product row is really gone

        var (items, next) = await ReadFeed(f, cursor);                                                  // reading from C: the removal must still be there
        var gone = Entry(items, s.Product);
        Assert.NotNull(gone);
        Assert.False((bool)gone!["listed"]!); Assert.Equal("deleted", (string)gone["reason"]!); Assert.Null(gone["title"]);
        Assert.Equal(1, items.Count(i => (string)i["listingId"]! == s.Product.ToString()));             // once (the deactivation's tombstone went away with the row)
        Assert.True(long.Parse(next) >= (long)gone["version"]!);
        Assert.Null(await Listing(f, s.Product));                                                       // and the single-listing endpoint says 404
        var ev = ListingEvents(f, s.Product).Last();
        Assert.Equal("listing.unlisted", ev.Type); Assert.Equal("deleted", (string)ev.Data["reason"]!); Assert.Equal((long)gone["version"]!, (long)ev.Data["version"]!);
        // it is a version like any other: unique, and above everything before it
        Assert.Equal(1, f.WithDb(db => db.CatalogTombstones.Count(t => t.ListingId == s.Product && t.Version == (long)gone["version"]!)));
        Assert.Equal(0, f.WithDb(db => db.Products.Count(p => p.CatalogVersion == (long)gone["version"]!)));
    }

    [Fact]
    public async Task Deleting_a_listing_that_was_never_visible_leaves_nothing_behind_and_paging_with_tombstones_has_no_gaps_or_repeats()
    {
        var s = await Orders.NewShop(f);
        var draft = Guid.Parse((string)(await ProdApi.Create(s.Owner, "Never listed"))["id"]!);
        var cursor = (await ReadFeed(f)).Cursor;
        Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.Delete($"/api/v1/seller/products/{draft}")).StatusCode);
        Assert.Equal(0, f.WithDb(db => db.CatalogTombstones.Count(t => t.ListingId == draft)));
        Assert.Empty(ListingEvents(f, draft));
        Assert.Empty((await ReadFeed(f, cursor)).Items);

        var doomed = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var id = await Orders.ActiveProduct(s.Owner, "Doomed " + i, s.Brand, "SKU-" + Guid.NewGuid().ToString("N")[..8]);
            await s.Owner.Post($"/api/v1/seller/products/{id}/status", new { status = "inactive" });
            Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.Delete($"/api/v1/seller/products/{id}")).StatusCode);
            doomed.Add(id);
        }
        var seen = (await ReadFeed(f, cursor, limit: 1)).Items;                                         // one item per page, tombstones included
        foreach (var id in doomed) Assert.Equal("deleted", (string)Entry(seen, id)!["reason"]!);
        var versions = seen.Select(i => (long)i["version"]!).ToList();
        Assert.Equal(versions.OrderBy(v => v), versions); Assert.Equal(versions.Count, versions.Distinct().Count());
        Assert.Equal(doomed.Count, seen.Count(i => doomed.Contains(Guid.Parse((string)i["listingId"]!))));
    }

    // ---- 2. concurrent decisions ------------------------------------------------------------------------------------------------------

    private async Task<string> Report(string kind, string target, string? rid = null)
    {
        rid ??= "R-" + Guid.NewGuid().ToString("N")[..14];
        var r = await PostSigned(f, "/api/internal/v1/content/reports", new { reportId = rid, kind, targetId = target, reason = "Counterfeit", reporterRef = "buyer-ref-" + rid });
        Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK });
        return rid;
    }

    private Guid Queue(string rid) => f.WithDb(db => db.ContentReports.AsNoTracking().Where(c => c.ReportId == rid).Select(c => c.ModerationReportId).Single());
    private List<JsonNode> Decided(string rid) => Events(f, f.WithDb(db => db.ContentReports.AsNoTracking().Where(c => c.ReportId == rid).Select(c => c.Id).Single()).ToString(), "content.report_decided");

    private async Task<List<Api>> Moderators(int n)
    {
        var res = new List<Api>();
        for (var i = 0; i < n; i++) res.Add(await Staff(f, StaffRole.Moderator));
        return res;
    }

    [Fact]
    public async Task Six_moderators_dismissing_the_same_report_in_parallel_produce_one_decision_and_one_event()
    {
        var rid = await Report("review", "rev-race-" + Guid.NewGuid().ToString("N")[..8]);
        var queue = Queue(rid);
        var mods = await Moderators(6);
        var res = await Task.WhenAll(mods.Select(m => m.Post($"/api/v1/admin/moderation/{queue}/dismiss", new { reason = StaffKit.Why })));
        Assert.Equal(1, res.Count(r => r.StatusCode == HttpStatusCode.OK));                              // exactly one winner
        Assert.Equal(5, res.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        foreach (var loser in res.Where(r => r.StatusCode == HttpStatusCode.Conflict)) Assert.Equal("already_resolved", await StaffKit.Err(loser));
        var ev = Assert.Single(Decided(rid));                                                            // ...and ONE event
        Assert.Equal("dismissed", (string)ev["data"]!["decision"]!);
        // the decision was audited once, too
        Assert.Equal(1, f.WithDb(db => db.AuditEvents.Count(a => a.Type == "admin.moderation.dismissed" && a.TargetId == queue.ToString())));
    }

    [Fact]
    public async Task Racing_different_decisions_on_one_report_has_one_winner_and_each_marketplace_report_is_decided_once()
    {
        var target = "rev-mixed-" + Guid.NewGuid().ToString("N")[..8];
        var r1 = await Report("review", target); var r2 = await Report("review", target);              // two reports, one queue row
        var queue = Queue(r1); Assert.Equal(queue, Queue(r2));
        var mods = await Moderators(6);
        string[] actions = ["dismiss", "hide-content", "remove-content", "dismiss", "hide-content", "remove-content"];
        var res = await Task.WhenAll(mods.Select((m, i) => m.Post($"/api/v1/admin/moderation/{queue}/{actions[i]}", new { reason = StaffKit.Why })));
        Assert.Equal(1, res.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(5, res.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var d1 = Assert.Single(Decided(r1)); var d2 = Assert.Single(Decided(r2));                       // one event per report, not one per click
        Assert.Equal((string)d1["data"]!["decision"]!, (string)d2["data"]!["decision"]!);              // both carry the winner's decision
        Assert.Equal(2, f.WithDb(db => db.Outbox.AsNoTracking().Count(m => m.EventType == "content.report_decided" && (m.PayloadJson.Contains(r1) || m.PayloadJson.Contains(r2)))));
    }

    // ---- 3. a report on a listing staff already hid -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_report_on_an_already_hidden_listing_is_decided_as_hidden_without_hiding_it_again()
    {
        var s = await Orders.NewShop(f);
        var mod = await Staff(f, StaffRole.Moderator);
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/listings/{s.Product}/hide", new { reason = "Counterfeit brand name in the title" })).StatusCode);
        var hiddenAt = f.WithDb(db => db.Products.AsNoTracking().Single(p => p.Id == s.Product)).ModeratedAt;
        var mails = f.Mails(s.Owner.Session!.Email, "listing-hidden").Count;
        var rid = await Report("listing", s.Product.ToString());                                         // the marketplace reports it AFTER staff hid it
        var queue = Queue(rid);
        var res = await mod.Post($"/api/v1/admin/moderation/{queue}/hide-content", new { reason = StaffKit.Why, reasonShownToUser = "Already hidden." });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);                                                 // was 409 already_hidden
        var d = Assert.Single(Decided(rid))["data"]!;
        Assert.Equal("hidden", (string)d["decision"]!); Assert.Equal("Already hidden.", (string)d["reasonShownToUser"]!);
        var after = f.WithDb(db => db.Products.AsNoTracking().Single(p => p.Id == s.Product));
        Assert.Equal(hiddenAt, after.ModeratedAt);                                                       // not hidden a second time
        Assert.Equal("Counterfeit brand name in the title", after.ModerationReason);
        Assert.Equal(mails, f.Mails(s.Owner.Session!.Email, "listing-hidden").Count);                    // and the seller is not mailed again
        Assert.Equal("Actioned", f.WithDb(db => db.ModerationReports.AsNoTracking().Single(r => r.Id == queue).Status.ToString()));
        // the explicit hide endpoint keeps saying so
        Assert.Equal("already_hidden", await StaffKit.Err(await mod.Post($"/api/v1/admin/listings/{s.Product}/hide", new { reason = StaffKit.Why })));
        // removing a hidden listing is still possible: it is archived and the report is decided as removed
        var rid2 = await Report("listing", s.Product.ToString());
        var q2 = Queue(rid2);
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/moderation/{q2}/remove-content", new { reason = StaffKit.Why })).StatusCode);
        Assert.Equal("removed", (string)Assert.Single(Decided(rid2))["data"]!["decision"]!);
        Assert.Equal(ProductStatus.Archived, f.WithDb(db => db.Products.AsNoTracking().Single(p => p.Id == s.Product).Status));
    }

    // ---- 4. visibility race -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_child_row_change_writes_the_product_row_so_it_cannot_slip_past_a_racing_change_of_that_row()
    {
        // The catalog decides "is this listing visible?" from the product row. A published answer (a child row) that read the row BEFORE a racing change of it (activation, hiding,
        // a suspension...) must not commit without a version bump: it writes the product row too, so the two serialize and the loser fails the Version token (a retryable 409).
        var s = await Orders.NewShop(f);
        var asked = await PostSigned(f, $"/api/internal/v1/products/{s.Product}/questions", new { questionId = "Q-" + Guid.NewGuid().ToString("N"), text = "Is it sturdy?" });
        var qid = (await Orders.Node(asked))["id"]!.GetValue<Guid>();

        using var scope = f.Services.CreateScope();
        var db1 = scope.ServiceProvider.GetRequiredService<Solvelt.Api.Data.AppDbContext>();
        var question = await db1.Questions.FirstAsync(x => x.Id == qid);
        await db1.Products.FirstAsync(x => x.Id == s.Product);                                           // this context has seen the product row as it is NOW
        await SetPrice(s.Owner, s.Product, 88);                                                          // ...then somebody else changes the same row (the racing writer)
        question.Answer = "Yes."; question.State = QuestionState.Published;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db1.SaveChangesAsync());            // the stale writer is refused instead of committing silently

        // with a fresh view the same change goes through, writes the product row and bumps the version
        db1.ChangeTracker.Clear();
        var v0 = Version(f, s.Product); var up0 = f.WithDb(db => db.Products.AsNoTracking().Single(p => p.Id == s.Product).UpdatedAt);
        f.Time.Advance(TimeSpan.FromMinutes(1));
        var again = await db1.Questions.FirstAsync(x => x.Id == qid);
        again.Answer = "Yes."; again.State = QuestionState.Published;
        await db1.SaveChangesAsync();
        Assert.True(Version(f, s.Product) > v0);
        Assert.True(f.WithDb(db => db.Products.AsNoTracking().Single(p => p.Id == s.Product).UpdatedAt) > up0);
    }

    // ---- 5. search terms --------------------------------------------------------------------------------------------------------------

    private static string Terms(int n, int len = 5) => string.Join(", ", Enumerable.Range(1, n).Select(i => ("t" + i).PadRight(len, 'x')));

    [Fact]
    public async Task Search_terms_are_capped_free_of_contact_data_and_published_cleaned()
    {
        var s = await Orders.NewShop(f);
        async Task<HttpResponseMessage> Put(string terms) => await ProdApi.Put(s.Owner, s.Product, d => d["searchTerms"] = terms);
        async Task Rejected(string terms)
        {
            var r = await Put(terms);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains("searchTerms", await r.Content.ReadAsStringAsync());
        }
        await Rejected(Terms(21));                                              // at most 20 terms
        await Rejected(new string('x', 51));                                    // at most 50 characters each
        await Rejected("mug, write to me@example.com");                         // no e-mail addresses...
        await Rejected("jane@example.com");
        await Rejected("mug, +1 (555) 123-4567");                               // ...and no phone numbers
        await Rejected("555-123-4567");
        Assert.Equal(HttpStatusCode.OK, (await Put(Terms(20, 8))).StatusCode);                     // exactly 20 terms is fine
        Assert.Equal(HttpStatusCode.OK, (await Put(new string('y', 50))).StatusCode);              // exactly 50 characters is fine
        Assert.Equal(HttpStatusCode.OK, (await Put("model 3000, size 12, 2 pack")).StatusCode);    // digits alone are not a phone number

        // control characters are stripped, terms trimmed, duplicates (any case) removed
        Assert.Equal(HttpStatusCode.OK, (await Put("tea\u0007,   Coffee Cup , coffee cup,\u0001")).StatusCode);
        Assert.Equal(["tea", "Coffee Cup"], (await Listing(f, s.Product))!["searchTerms"]!.AsArray().Select(x => (string)x!).ToArray());
    }

    [Fact]
    public async Task Legacy_search_terms_are_cleaned_when_published()
    {
        var s = await Orders.NewShop(f);
        var legacy = Terms(24) + ", bob@example.com, 555 123 4567, " + new string('z', 60) + ", kept";
        f.WithDb(db => db.Products.Where(p => p.Id == s.Product).ExecuteUpdate(u => u.SetProperty(p => p.SearchTerms, legacy)));   // written before the rules existed
        var terms = (await Listing(f, s.Product))!["searchTerms"]!.AsArray().Select(x => (string)x!).ToList();
        Assert.Equal(20, terms.Count);                                                                   // capped
        Assert.DoesNotContain(terms, t => t.Contains('@') || t.Contains("555") || t.Length > 50);       // contact data and over-long terms never reach the marketplace
        Assert.Equal("t1xxx", terms[0]);
    }

    [Fact]
    public async Task The_csv_template_separates_search_terms_with_commas_and_imports_validate_them()
    {
        var (api, _) = await ProdApi.Approved(f, false);
        var template = await api.Get("/api/v1/seller/products/csv/template");
        var text = await template.Content.ReadAsStringAsync();
        Assert.Contains("mug, coffee cup, tea, ceramic", text);                                          // one term per comma, not "mug cup coffee tea ceramic" (a single term)
        var ok = await api.JsonBody(await ProdApi.UploadCsv(api, "/api/v1/seller/products/csv/validate", text));
        Assert.True(ok.GetProperty("rows")[0].GetProperty("ok").GetBoolean());
        var csv = "title,category,price,quantity,search_terms\nCSV mug,kitchen-dining,10,5,\"mug, call me 555-123-4567\"\n";
        var bad = await api.JsonBody(await ProdApi.UploadCsv(api, "/api/v1/seller/products/csv/validate", csv));
        Assert.False(bad.GetProperty("rows")[0].GetProperty("ok").GetBoolean());
        Assert.Contains(bad.GetProperty("rows")[0].GetProperty("errors").EnumerateArray(), e => e.GetProperty("field").GetString() == "searchTerms");
    }

    // ---- 6 + 7. images: disabled variants, separate key ------------------------------------------------------------------------------------

    private static string DerivedImageKey() => Solvelt.Api.Orders.Signing.ImageKey(new Solvelt.Api.Orders.OrderOptions { InboundSecret = OrdersFactory.InSecret });

    private static async Task<Guid> ImageOf(ApiFactory fx, Guid product) => Guid.Parse((string)(await Listing(fx, product))!["images"]![0]!["imageId"]!);

    [Fact]
    public async Task The_image_key_is_a_derived_subkey_that_signs_image_downloads_only_and_the_inbound_key_still_works_for_them()
    {
        var s = await Orders.NewShop(f);
        var image = await ImageOf(f, s.Product);
        var path = $"{Base}/images/{image}";
        Assert.Equal(HttpStatusCode.OK, (await SignedGet(f, path, secret: DerivedImageKey())).StatusCode);        // the subkey signs image downloads
        Assert.Equal(HttpStatusCode.OK, (await SignedGet(f, path)).StatusCode);                                    // backward compatible: the inbound secret still does
        Assert.NotEqual(OrdersFactory.InSecret, DerivedImageKey());
        // ...and the subkey signs NOTHING else: whoever holds it (an edge proxy) cannot read the feed or write anything
        foreach (var other in new[] { $"{Base}/listings", $"{Base}/listings/{s.Product}", $"{Base}/categories", $"{Base}/brands" })
        {
            var r = await SignedGet(f, other, secret: DerivedImageKey());
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode); Assert.Equal("bad_signature", await Orders.Code(r));
        }
        var post = Orders.Signed(f, "POST", $"/api/internal/v1/products/{s.Product}/questions", new { questionId = "Q-" + Guid.NewGuid().ToString("N"), text = "x" }, secret: DerivedImageKey());
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().SendAsync(post)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignedGet(f, path, secret: "some-other-key-0123456789abcdef0123456")).StatusCode);   // any other key is still refused
        // a request signed with the subkey is replay-protected like any other
        var nonce = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.OK, (await SignedGet(f, path, nonce, secret: DerivedImageKey())).StatusCode);
        Assert.Equal("replayed", await Orders.Code(await SignedGet(f, path, nonce, secret: DerivedImageKey())));
    }

    [Fact]
    public async Task With_the_inbound_key_switched_off_for_images_only_the_image_key_downloads_them()
    {
        using var strict = new CatalogStrictImageKeyFactory();
        var s = await Orders.NewShop(strict);
        var path = $"{Base}/images/{await ImageOf(strict, s.Product)}";
        Assert.Equal(HttpStatusCode.OK, (await SignedGet(strict, path, secret: DerivedImageKey())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignedGet(strict, path)).StatusCode);                    // the inbound secret no longer reads images
        Assert.Equal(HttpStatusCode.OK, (await SignedGet(strict, $"{Base}/listings/{s.Product}")).StatusCode);     // and still reads everything else
    }

    [Fact]
    public async Task A_configured_image_key_replaces_the_derived_one()
    {
        using var configured = new CatalogConfiguredImageKeyFactory();
        var s = await Orders.NewShop(configured);
        var path = $"{Base}/images/{await ImageOf(configured, s.Product)}";
        Assert.Equal(HttpStatusCode.OK, (await SignedGet(configured, path, secret: CatalogConfiguredImageKeyFactory.Key)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignedGet(configured, path, secret: DerivedImageKey())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignedGet(configured, path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignedGet(configured, $"{Base}/categories", secret: CatalogConfiguredImageKeyFactory.Key)).StatusCode);   // the configured key signs images only, too
    }

    [Fact]
    public async Task An_image_of_a_disabled_variant_is_served_by_id_while_the_listing_still_does_not_list_it()
    {
        var s = await Orders.NewShop(f);
        object Variants(bool secondEnabled) => new
        {
            hasVariants = true, options = new[] { new { name = "Size", values = new[] { "S", "M" } } },
            variants = new[] { new { values = new[] { "S" }, sku = "VS-" + s.Sku, price = 18.5, quantity = 5, barcode = (string?)null, enabled = true },
                               new { values = new[] { "M" }, sku = "VM-" + s.Sku, price = 19.5, quantity = 5, barcode = (string?)null, enabled = secondEnabled } }
        };
        var put = await s.Owner.Put($"/api/v1/seller/products/{s.Product}/variants", Variants(true));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var m = (await ProdApi.Node(put))["variants"]!.AsArray().First(v => (string)v!["sku"]! == "VM-" + s.Sku)!;
        var upload = await ProdApi.Upload(s.Owner, s.Product, TestImages.Png(800, 800), "m.png", "Variant M", Guid.Parse((string)m["id"]!));
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var imageId = f.WithDb(db => db.ProductImages.AsNoTracking().Single(i => i.ProductId == s.Product && i.VariantId != null).Id);
        Assert.Equal(HttpStatusCode.OK, (await SignedGet(f, $"{Base}/images/{imageId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Put($"/api/v1/seller/products/{s.Product}/variants", Variants(false))).StatusCode);   // variant M is disabled
        Assert.Equal(HttpStatusCode.OK, (await SignedGet(f, $"{Base}/images/{imageId}")).StatusCode);                  // was 404 before: the filter is gone
        Assert.DoesNotContain((await Listing(f, s.Product))!["images"]!.AsArray(), i => (string)i!["imageId"]! == imageId.ToString());   // the document still lists only enabled variants' images
    }

    // ---- 8. retention ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Decided_reports_lose_the_reporters_reference_reason_and_note_after_the_retention_period_and_open_ones_are_kept()
    {
        using var fx = new CatalogFactory();                                                              // its own clock: this test moves time by months
        async Task<string> Rep(string target, string reporter)
        {
            var rid = "R-" + Guid.NewGuid().ToString("N")[..14];
            var r = await PostSigned(fx, "/api/internal/v1/content/reports", new { reportId = rid, kind = "review", targetId = target, reason = "Contains my neighbour's phone number", note = "see line 3", reporterRef = reporter });
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            return rid;
        }
        Guid QueueOf(string rid) => fx.WithDb(db => db.ContentReports.AsNoTracking().Where(c => c.ReportId == rid).Select(c => c.ModerationReportId).Single());
        ContentReport Row(string rid) => fx.WithDb(db => db.ContentReports.AsNoTracking().Single(c => c.ReportId == rid));

        var old = await Rep("rev-old", "buyer-ref-old"); var open = await Rep("rev-open", "buyer-ref-open");
        var mod = await Staff(fx, StaffRole.Moderator);
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/moderation/{QueueOf(old)}/dismiss", new { reason = StaffKit.Why })).StatusCode);
        fx.Time.Advance(TimeSpan.FromDays(50));
        var recent = await Rep("rev-recent", "buyer-ref-recent");
        var mod2 = await Staff(fx, StaffRole.Moderator);                                                  // (sessions are short: a fresh login after 50 days)
        Assert.Equal(HttpStatusCode.OK, (await mod2.Post($"/api/v1/admin/moderation/{QueueOf(recent)}/dismiss", new { reason = StaffKit.Why })).StatusCode);
        fx.Time.Advance(TimeSpan.FromDays(50));                                                           // old: decided 100 days ago, recent: 50, open: still open

        var run = await Orders.Node(await fx.NewClient().PostAsync("/api/dev/maintenance/run", null));
        Assert.Equal(1, (int)run["contentReportsRedacted"]!);
        var o = Row(old);
        Assert.Equal("[redacted]", o.ReporterRef); Assert.Equal("[redacted]", o.Reason); Assert.Null(o.Note);
        Assert.Equal("dismissed", o.Decision); Assert.Equal("review", o.Kind); Assert.Equal("rev-old", o.TargetRef); Assert.Equal(old, o.ReportId);   // what carries no personal data stays
        var oldQueue = QueueOf(old);
        Assert.Equal("[redacted]", fx.WithDb(db => db.ModerationReports.AsNoTracking().Single(r => r.Id == oldQueue).Reason));              // the queue row's copy too
        var r2 = Row(recent); Assert.Equal("buyer-ref-recent", r2.ReporterRef); Assert.Equal("see line 3", r2.Note);                        // inside the retention period
        var r3 = Row(open); Assert.Equal("buyer-ref-open", r3.ReporterRef); Assert.Equal("see line 3", r3.Note); Assert.Null(r3.Decision);    // an open report is never redacted
        Assert.Equal(0, (int)(await Orders.Node(await fx.NewClient().PostAsync("/api/dev/maintenance/run", null)))["contentReportsRedacted"]!);   // idempotent
    }

    // ---- 9. message ids are unique per seller only ----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_same_marketplace_message_id_for_two_sellers_is_two_messages_and_a_report_naming_it_is_refused_as_ambiguous()
    {
        var a = await Orders.NewShop(f); var b = await Orders.NewShop(f);
        var (midA, _, numA, _) = await Orders.Place(a); var (midB, _, numB, _) = await Orders.Place(b);
        var shared = "shared-msg-" + Guid.NewGuid().ToString("N")[..8];
        Task<HttpResponseMessage> Send(string mid, string number, string conversation) => Orders.Inbound(f, "/api/internal/v1/messages",
            new { messageId = shared, conversationId = conversation, kind = "order", marketplaceOrderId = mid, sellerOrderNumber = number, buyerName = "Jane B.", text = "Hello, is it still available?" });
        Assert.Equal(HttpStatusCode.Created, (await Send(midA, numA, "conv-" + shared + "-a")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(midB, numB, "conv-" + shared + "-b")).StatusCode);   // was 200 "duplicate": seller B's message was silently dropped
        Assert.Equal(HttpStatusCode.OK, (await Send(midA, numA, "conv-" + shared + "-a")).StatusCode);        // a real repeat for the same seller is still a duplicate
        Assert.Equal(2, f.WithDb(db => db.Messages.Count(m => m.MarketplaceMessageId == shared)));
        Assert.Equal(new[] { a.Seller, b.Seller }.OrderBy(x => x), f.WithDb(db => db.Messages.AsNoTracking().Where(m => m.MarketplaceMessageId == shared).Select(m => m.SellerId).ToList()).OrderBy(x => x));

        // a report that names only the marketplace message id cannot be matched to ONE message: 422, nothing flagged, no queue row
        var viaMessages = await Orders.Inbound(f, $"/api/internal/v1/messages/{shared}/reports", new { reason = "abusive" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, viaMessages.StatusCode); Assert.Equal("ambiguous_message", await Orders.Code(viaMessages));
        var rid = "R-" + Guid.NewGuid().ToString("N")[..14];
        var viaReports = await PostSigned(f, "/api/internal/v1/content/reports", new { reportId = rid, kind = "message", targetId = shared, reason = "abusive", reporterRef = "u1" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, viaReports.StatusCode); Assert.Equal("ambiguous_target", await Orders.Code(viaReports));
        Assert.Equal(0, f.WithDb(db => db.ContentReports.Count(c => c.ReportId == rid)));
        Assert.DoesNotContain(f.WithDb(db => db.Messages.AsNoTracking().Where(m => m.MarketplaceMessageId == shared).ToList()), m => m.Reported);

        // an unambiguous id still works
        var unique = "unique-msg-" + Guid.NewGuid().ToString("N")[..8];
        Assert.Equal(HttpStatusCode.Created, (await Orders.Inbound(f, "/api/internal/v1/messages", new { messageId = unique, conversationId = "conv-" + unique, kind = "order", marketplaceOrderId = midA, sellerOrderNumber = numA, buyerName = "Jane B.", text = "Hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostSigned(f, "/api/internal/v1/content/reports", new { reportId = rid + "u", kind = "message", targetId = unique, reason = "abusive", reporterRef = "u1" })).StatusCode);
    }

    // ---- 11. the counter lock -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_catalog_changing_save_takes_the_counter_row_lock_before_it_allocates_versions_and_before_it_writes_the_product()
    {
        // SQLite serializes writers by itself, so no test here can SHOW the race that the lock prevents on PostgreSQL (a later version becoming visible before an earlier one). What can be
        // pinned is the mechanism: the counter row is locked first (UPDATE ... SET Value = Value), then versions are taken from it, and only then is the product row written, all in one transaction.
        using var fx = new CatalogSqlLogFactory();
        var s = await Orders.NewShop(fx);
        fx.Log.Clear();
        await SetPrice(s.Owner, s.Product, 55);
        var sql = fx.Log.All();
        int At(Func<string, bool> match) => sql.FindIndex(x => match(x));
        var lockAt = At(x => x.Contains("UPDATE catalog_counter SET \"Value\" = \"Value\" WHERE \"Id\" = 1"));
        var takeAt = At(x => x.Contains("UPDATE catalog_counter SET \"Value\" = \"Value\" +"));
        var writeAt = At(x => x.Contains("UPDATE \"products\"") && x.Contains("\"CatalogVersion\""));
        Assert.True(lockAt >= 0, "the counter row lock is not issued:\n" + string.Join("\n", sql));
        Assert.True(takeAt > lockAt, "versions are taken before the lock:\n" + string.Join("\n", sql));
        Assert.True(writeAt > takeAt, "the product row is written before its version was taken:\n" + string.Join("\n", sql));
        Assert.Equal(1, sql.Count(x => x.Contains("UPDATE catalog_counter SET \"Value\" = \"Value\" WHERE")));   // locked once per save, not once per listing
    }

    [Fact]
    public async Task A_save_that_cannot_change_what_the_marketplace_sees_does_not_touch_the_counter()
    {
        using var fx = new CatalogSqlLogFactory();
        var s = await Orders.NewShop(fx);
        fx.Log.Clear();
        Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(s.Owner, s.Product, d => d["cost"] = 4.25)).StatusCode);     // a cost price is private
        Assert.DoesNotContain(fx.Log.All(), x => x.Contains("catalog_counter"));                                              // no lock, no version: drafts and private edits never serialize on it
    }
}

