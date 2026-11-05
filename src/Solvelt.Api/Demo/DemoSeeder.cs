using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Bookings;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using Solvelt.Api.Payments;
using Solvelt.Api.Products;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Demo;

public record DemoCounts(int Products, int Services, int Orders, int Bookings, int Quotes, int Conversations, int Notifications);

/// <summary>
/// Fills one seller account with a coherent demo data set (test tool, Demo:Enabled only). Everything that has rules (orders, payments, bookings, messages) goes through the
/// same services the portal and the marketplace integration use, so counters, ledger, versions and events are consistent; only plain catalogue and settings rows are written
/// directly. Every record is entered into the demo registry so <see cref="DemoRemover"/> can take exactly it away again. See docs/DECISIONS.md (demo data).
/// </summary>
public class DemoSeeder(AppDbContext db, TimeProvider time, DemoContext demo, IFileStorage storage, OrderIngestService ingest, OrderActions actions, BuyerEventService buyer,
    MessageService messages, PaymentIngestService payments, ReleaseService release, TransferService transfers, BookingService bookings, QuoteService quotes, CalendarService cal,
    IPaymentProvider provider, CategoryService categories, IAuditLog audit, Solvelt.Api.Reviews.ReviewEngine reviewEngine)
{
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);

    private AccountAccess a = null!;
    private Guid batchId;
    private string tag = "";
    private int seq;
    private DateTime now;
    private Brand brand = null!;
    private readonly List<Product> products = [];
    private readonly List<Product> services = [];

    private void Reg(string kind, Guid id, string? key = null)
    {
        db.DemoRecords.Add(new DemoRecord { BatchId = batchId, SellerId = a.SellerId, Kind = kind, EntityId = id, Key = key, Seq = ++seq });
    }

    public record Result(bool Created, DemoCounts? Counts);

    /// <summary>Claims the account's single batch (the unique index decides parallel callers), then fills it. On any failure what was created so far is removed again.</summary>
    public async Task<Result> FillAsync(AccountAccess access, DemoRemover remover)
    {
        a = access; now = time.UtcNow();
        var batch = new DemoBatch { SellerId = a.SellerId, CreatedBy = a.UserId, CreatedAt = now };
        db.DemoBatches.Add(batch);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return new(false, null); }   // somebody else holds the batch (409 demo_data_exists)
        batchId = batch.Id; tag = batch.Id.ToString("N")[..8].ToUpperInvariant();
        demo.SuppressOutbox = true; demo.Mail = DemoMail.Suppress;
        try
        {
            await CatalogueAsync();
            await ServicesAsync();
            await DeliverySettingsAsync();
            var orders = await OrdersAsync();
            await BookingsAsync();
            await MessagesAsync(orders);
            await ReviewsAsync();
            await PaymentsAsync(orders);
            batch = await db.DemoBatches.FirstAsync(b => b.Id == batchId);
            batch.Completed = true;
            await db.SaveChangesAsync();
            await audit.WriteAsync("demo.filled", a.UserId, null, $"seller={a.SellerId};batch={batchId}");
            return new(true, await remover.CountsAsync(a.SellerId));
        }
        catch
        {
            db.ChangeTracker.Clear();
            try { await remover.RemoveAsync(a.SellerId, a.UserId, "fill_failed"); } catch { /* the original failure matters more */ }
            throw;
        }
        finally { demo.SuppressOutbox = false; demo.Mail = DemoMail.Normal; }
    }

    private Guid OwnerId => a.UserId;
    private string Mid(string kind, int n) => $"DEMO-{tag}-{kind}{n}";

    // ---- catalogue -----------------------------------------------------------------------------------------------------------------
    private record Spec(string Title, string Slug, string Sku, long Price, long? Compare, long? Sale, int Stock, int Grams, int Hue, string[] Bullets, string Desc, string Terms, string Cat, string[]? Sizes = null);

    private static readonly Spec[] Specs =
    [
        new("Handwoven Market Tote", "handwoven-market-tote", "DEMO-TOTE", 3800, 4500, null, 42, 380, 28, ["Natural cotton", "Reinforced handles", "Machine washable"], "A sturdy everyday tote, woven by hand from organic cotton.", "tote bag cotton market", "Storage & Organization"),
        new("Ceramic Pour-Over Set", "ceramic-pour-over-set", "DEMO-POUR", 5400, null, 4900, 18, 1100, 20, ["Dripper and carafe", "Dishwasher safe", "Fits standard filters"], "A matte ceramic dripper with a matching carafe.", "coffee ceramic dripper carafe", "Kitchen & Dining"),
        new("Linen Table Runner", "linen-table-runner", "DEMO-RUNNER", 2900, null, null, 60, 240, 150, ["100% European linen", "Hemmed edges"], "A relaxed linen runner that softens with every wash.", "linen table runner dining", "Kitchen & Dining", ["120 cm", "180 cm", "240 cm"]),
        new("Beeswax Candle Trio", "beeswax-candle-trio", "DEMO-CANDLE", 2400, 2800, null, 75, 450, 45, ["Hand-poured", "Burns up to 40 hours each"], "Three tapered candles from local beeswax.", "candle beeswax gift", "Home Décor"),
        new("Walnut Serving Board", "walnut-serving-board", "DEMO-BOARD", 6200, null, null, 12, 1500, 25, ["Solid walnut", "Food-safe oil finish"], "A chunky board for cheese, bread and everything in between.", "walnut board cheese serving", "Kitchen & Dining", ["Small", "Large"]),
        new("Wool Throw Blanket", "wool-throw-blanket", "DEMO-THROW", 8900, 10500, null, 9, 1300, 200, ["Merino blend", "Two-tone edge"], "A warm, soft throw for the sofa or the end of the bed.", "wool blanket throw merino", "Bedding & Bath"),
        new("Botanical Print Set", "botanical-print-set", "DEMO-PRINT", 3200, null, null, 120, 200, 110, ["Set of three A4 prints", "Archival paper"], "Three botanical illustrations, printed on heavy cotton paper.", "art print botanical wall", "Home Décor"),
        new("Stoneware Mug Pair", "stoneware-mug-pair", "DEMO-MUG", 3600, null, null, 3, 700, 340, ["Set of two", "Microwave safe"], "Two chunky mugs with a speckled glaze. Low stock on purpose.", "mug stoneware pair coffee", "Kitchen & Dining"),
    ];

    private async Task CatalogueAsync()
    {
        // one brand, unless the account already has one
        brand = await db.Brands.Where(b => b.SellerId == a.SellerId && b.Status == BrandStatus.Active).OrderByDescending(b => b.IsDefault).ThenBy(b => b.CreatedAt).FirstOrDefaultAsync()
            ?? new Brand { SellerId = a.SellerId, Name = "Demo Atelier", Slug = "demo-atelier-" + tag.ToLowerInvariant(), Description = "A made-up brand with demo products.", CreatedAt = now, UpdatedAt = now };
        if (db.Entry(brand).State == EntityState.Detached) { db.Brands.Add(brand); Reg("brand", brand.Id); await db.SaveChangesAsync(); }
        // Demo products sit in real, sensible categories without required attributes (Home & Kitchen), so they meet the publish rules like real data.
        var cats = (await db.Categories.AsNoTracking().Where(c => c.ParentId != null).Select(c => new { c.Slug, c.Id }).ToListAsync()).ToDictionary(c => c.Slug, c => c.Id);
        Guid? Cat(string name) => cats.TryGetValue(CategorySeed.Slugify(name), out var id) ? id : null;
        var n = 0;
        foreach (var s in Specs)
        {
            var p = new Product
            {
                SellerId = a.SellerId, BrandId = brand.Id, CategoryId = Cat(s.Cat), Status = ProductStatus.Active, Title = s.Title, Handle = s.Slug + "-" + tag.ToLowerInvariant(),
                Condition = ProductCondition.New, Sku = $"{s.Sku}-{tag}", PriceCents = s.Price, CompareAtCents = s.Compare, SaleCents = s.Sale, Stock = s.Sizes is null ? s.Stock : 0,
                LowStockAlert = 5, MaxPerOrder = 10, WeightGrams = s.Grams, LengthMm = 250, WidthMm = 200, HeightMm = 80, HandlingDays = 2, ReturnsDays = 30, ReturnsCondition = "Unused, in original packaging.",
                WarrantyMonths = null, SearchTerms = s.Terms, MetaTitle = s.Title, MetaDescription = s.Desc, Description = s.Desc + "\n\nThis is demo data: it is not a real product.",
                BulletsJson = JsonSerializer.Serialize(s.Bullets), ShippingJson = JsonSerializer.Serialize(new { standardEnabled = true, standardCents = 599, expressEnabled = true, expressCents = 1499, freeOverCents = 7500 }, J),
                HasVariants = s.Sizes is not null, CreatedAt = now.AddDays(-30 + n), UpdatedAt = now, PublishedAt = now.AddDays(-30 + n), CreatedBy = OwnerId, Version = 1
            };
            if (s.Sizes is { } sizes)
            {
                p.Options.Add(new ProductOption { ProductId = p.Id, Name = "Size", ValuesJson = JsonSerializer.Serialize(sizes), SortOrder = 0 });
                var i = 0;
                foreach (var size in sizes)
                    p.Variants.Add(new ProductVariant
                    {
                        ProductId = p.Id, SellerId = a.SellerId, ValuesJson = JsonSerializer.Serialize(new[] { size }), ComboKey = VariantGenerator.Key([size]), Sku = $"{s.Sku}-{tag}-{i + 1}",
                        PriceCents = s.Price + i * 600, Stock = Math.Max(2, s.Stock / sizes.Length - i * 2), Enabled = true, SortOrder = i++
                    });
            }
            p.Faqs.Add(new ProductFaq { ProductId = p.Id, Question = "Is this a real product?", Answer = "No: it is demo data created by the test tool.", SortOrder = 0 });
            for (var k = 0; k < 2; k++)
            {
                var key = Guid.NewGuid().ToString("N");
                var png = DemoAssets.Tile(s.Hue, k);
                await storage.SaveAsync(key, new MemoryStream(png));
                p.Images.Add(new ProductImage
                {
                    ProductId = p.Id, SellerId = a.SellerId, StorageKey = key, ContentType = "image/png", Size = png.Length, Width = DemoAssets.Side, Height = DemoAssets.Side,
                    Alt = $"{s.Title} placeholder image {k + 1}", SortOrder = k, IsMain = k == 0, CreatedAt = now
                });
                Reg("image_file", p.Images[^1].Id, key);
            }
            p.ImageSeq = p.Images.Count;
            await ProductRules.RefreshAsync(db, categories, p);
            await ProductService.SyncSkuClaimsAsync(db, p);
            db.Products.Add(p);
            Reg("product", p.Id);
            products.Add(p);
            n++;
        }
        await db.SaveChangesAsync();
        // a product question, waiting for an answer
        var pq = new ProductQuestion { ProductId = products[0].Id, SellerId = a.SellerId, AskerName = "Riley C.", ExternalId = Mid("Q", 1), Text = "Does the tote fit a 15-inch laptop?", State = QuestionState.Pending, AskedAt = now.AddHours(-6) };
        db.Questions.Add(pq); Reg("question", pq.Id);
        await db.SaveChangesAsync();
    }

    // ---- services ------------------------------------------------------------------------------------------------------------------
    private async Task ServicesAsync()
    {
        // the Owner works as the provider: working hours Monday to Friday, 09:00-17:00 (kept if the account already has hours)
        var zoneId = await cal.AccountZoneIdAsync(a.SellerId);
        var prof = await db.ProviderProfiles.FirstOrDefaultAsync(p => p.MembershipId == a.MembershipId);
        if (prof is null) { prof = new ProviderProfile { MembershipId = a.MembershipId, SellerId = a.SellerId, IsProvider = true, TimeZone = zoneId, UpdatedAt = now }; db.ProviderProfiles.Add(prof); Reg("provider_profile", a.MembershipId); }
        else if (!prof.IsProvider) { prof.IsProvider = true; prof.UpdatedAt = now; Reg("provider_flag", a.MembershipId); }
        if (!await db.WorkingHours.AnyAsync(w => w.MembershipId == a.MembershipId))
            for (var d = 1; d <= 5; d++) { var w = new WorkingHour { MembershipId = a.MembershipId, Day = d, StartMinute = 9 * 60, EndMinute = 17 * 60 }; db.WorkingHours.Add(w); Reg("working_hour", w.Id); }

        var addOns = JsonSerializer.Serialize(new[] { new AddOn("rush", "Same-week scheduling", 2500, 0), new AddOn("notes", "Written summary", 1500, 15) }, J);
        var intake = JsonSerializer.Serialize(new[] { new IntakeQuestion("goal", "What would you like to get out of the session?", "text", null, true), new IntakeQuestion("exp", "Your experience level", "select", ["Beginner", "Some experience", "Advanced"], false) }, J);
        // Demo listings follow the same publish rules as real ones: each service sits in a category of the Local Services department and has a photo.
        ServiceCategories.Ensure(db);
        var serviceCats = (await db.Categories.AsNoTracking().Where(c => c.Slug == CategorySeed.Slugify("Home Services") || c.Slug == CategorySeed.Slugify("Lessons & Classes") || c.Slug == CategorySeed.Slugify("Consulting & Coaching"))
            .Select(c => new { c.Slug, c.Id }).ToListAsync()).ToDictionary(c => c.Slug, c => c.Id);
        Guid? ServiceCat(string name) => serviceCats.TryGetValue(CategorySeed.Slugify(name), out var id) ? id : null;
        var defs = new (string Title, string Slug, PricingModel Model, long Price, BookingMode Mode, string Desc, string Category, int Hue)[]
        {
            ("Home Styling Consultation", "home-styling-consultation", PricingModel.Fixed, 8500, BookingMode.Instant, "A one-hour video or in-person consultation. Demo service.", "Home Services", 15),
            ("Pottery Coaching (hourly)", "pottery-coaching-hourly", PricingModel.Hourly, 6000, BookingMode.Request, "Hands-on coaching billed by the hour. Demo service.", "Lessons & Classes", 175),
            ("Custom Project Quote", "custom-project-quote", PricingModel.Quote, 0, BookingMode.Request, "Tell us about your project and get a quote. Demo service.", "Consulting & Coaching", 265),
        };
        var n = 0;
        foreach (var s in defs)
        {
            var p = new Product
            {
                SellerId = a.SellerId, BrandId = brand.Id, Type = ListingType.Service, Status = ProductStatus.Active, CategoryId = ServiceCat(s.Category), Title = s.Title, Handle = s.Slug + "-" + tag.ToLowerInvariant(),
                PriceCents = s.Model == PricingModel.Quote ? null : s.Price, Description = s.Desc, SearchTerms = "service demo consultation", CreatedAt = now.AddDays(-20 + n), UpdatedAt = now,
                PublishedAt = now.AddDays(-20 + n), CreatedBy = OwnerId, Version = 1, BulletsJson = JsonSerializer.Serialize(new[] { "Demo service", "Flexible scheduling" })
            };
            p.Service = new ServiceDetails
            {
                ProductId = p.Id, SellerId = a.SellerId, PricingModel = s.Model, PriceCents = s.Model == PricingModel.Quote ? null : s.Price, MinMinutes = s.Model == PricingModel.Hourly ? 60 : null,
                MaxMinutes = s.Model == PricingModel.Hourly ? 240 : null, IncrementMinutes = 60, DefaultDurationMinutes = 60, MinNoticeHours = 12, MaxAdvanceDays = 60, SlotGranularityMinutes = 60,
                Online = true, OnlineInstructions = "A video link is sent after confirmation.", AtSeller = true, SellerAddress = "100 Example Street, Brooklyn, NY 11201", BookingMode = s.Mode, ApprovalHours = 24,
                IntakeJson = intake, AddOnsJson = addOns, CancelFreeHours = 24, CancelFeePercent = 50, MaxReschedules = 2, NoShowFeePercent = 100, AssignmentRule = AssignmentRule.BuyerChoice, UpdatedAt = now
            };
            var imageKey = Guid.NewGuid().ToString("N");
            var tile = DemoAssets.Tile(s.Hue);
            await storage.SaveAsync(imageKey, new MemoryStream(tile));
            p.Images.Add(new ProductImage
            {
                ProductId = p.Id, SellerId = a.SellerId, StorageKey = imageKey, ContentType = "image/png", Size = tile.Length, Width = DemoAssets.Side, Height = DemoAssets.Side,
                Alt = $"{s.Title} placeholder image", SortOrder = 0, IsMain = true, CreatedAt = now
            });
            Reg("image_file", p.Images[^1].Id, imageKey);
            p.ImageSeq = p.Images.Count;
            await ProductRules.RefreshAsync(db, categories, p);
            db.Products.Add(p);
            db.ServiceProviders.Add(new ServiceProviderLink { ProductId = p.Id, MembershipId = a.MembershipId });
            Reg("service", p.Id);
            await db.SaveChangesAsync();
            services.Add(p); n++;
        }
        await cal.RefreshReadyProvidersAsync(a.SellerId, services.Select(x => x.Id));
        await db.SaveChangesAsync();
    }

    // ---- delivery settings -----------------------------------------------------------------------------------------------------
    private Guid? windowId;
    private async Task DeliverySettingsAsync()
    {
        var firstShipFrom = !await db.ShipFromAddresses.AnyAsync(x => x.SellerId == a.SellerId && x.ScopeKey == Guid.Empty && x.IsDefault && !x.Archived);
        var addr = new ShipFromAddress
        {
            SellerId = a.SellerId, ScopeKey = Guid.Empty, Label = "Demo workshop", Name = "Demo Atelier", Company = "Demo Atelier", Line1 = "100 Example Street", City = "Brooklyn", Region = "NY",
            PostalCode = "11201", Country = "US", Phone = "+1 555 010 0100", Email = "workshop@example.com", IsDefault = firstShipFrom, DefaultKey = firstShipFrom ? Guid.Empty : null,
            Validation = "valid", ValidatedAt = now, CreatedAt = now, UpdatedAt = now
        };
        db.ShipFromAddresses.Add(addr); Reg("ship_from", addr.Id);
        var firstPack = !await db.PackagePresets.AnyAsync(x => x.SellerId == a.SellerId && x.IsDefault && !x.Archived);
        var pack = new PackagePreset { SellerId = a.SellerId, Name = "Demo small box", Kind = "box", LengthMm = 300, WidthMm = 220, HeightMm = 120, EmptyWeightGrams = 150, IsDefault = firstPack, CreatedAt = now };
        db.PackagePresets.Add(pack); Reg("package", pack.Id);
        var firstProfile = !await db.ShippingProfiles.AnyAsync(x => x.SellerId == a.SellerId && x.BrandId == null && x.IsDefault && !x.Archived);
        var zones = new[] { new { name = "United States", kind = "domestic", countries = new[] { "US" }, speeds = new object[]
            { new { speed = "standard", enabled = true, method = "Ground", flatCents = 599L }, new { speed = "expedited", enabled = true, method = "Express", flatCents = 1499L } } } };
        var prof = new ShippingProfile { SellerId = a.SellerId, Name = "Demo US shipping", IsDefault = firstProfile, FreeShippingThresholdCents = 7500, ZonesJson = JsonSerializer.Serialize(zones, J), CreatedAt = now, UpdatedAt = now };
        db.ShippingProfiles.Add(prof); Reg("shipping_profile", prof.Id);
        var haveSettings = await db.SellerShippingSettings.AnyAsync(x => x.SellerId == a.SellerId && x.ScopeKey == Guid.Empty);
        if (!haveSettings)
        {
            var st = new SellerShippingSettings { SellerId = a.SellerId, ScopeKey = Guid.Empty, HandlingDays = 2, LabelFormat = "PDF_4x6", ReturnAddressId = addr.Id, UpdatedAt = now };
            db.SellerShippingSettings.Add(st); Reg("shipping_settings", st.Id);
        }
        // local delivery: a zone around the workshop with two windows (kept if the account already configured local delivery)
        var existing = await db.DeliverySettings.Include(s => s.Windows).FirstOrDefaultAsync(s => s.SellerId == a.SellerId && s.ScopeKey == Guid.Empty);
        if (existing is null)
        {
            var ds = new DeliverySetting { SellerId = a.SellerId, ScopeKey = Guid.Empty, Enabled = true, TimeZone = await cal.AccountZoneIdAsync(a.SellerId), MinLeadMinutes = 60, MaxDaysAhead = 14, CreatedAt = now, UpdatedAt = now };
            ds.Zones.Add(new DeliveryZone { SettingId = ds.Id, SellerId = a.SellerId, Name = "Brooklyn demo zone", Country = "US", OriginLat = 40.6928, OriginLng = -73.9903, RadiusKm = 8, PostcodesJson = JsonSerializer.Serialize(new[] { "11201", "11215", "11217" }), FeeCents = 599, FreeOverCents = 7500, MinOrderCents = 1500, Priority = 1 });
            ds.Windows.Add(new DeliveryWindow { SettingId = ds.Id, SellerId = a.SellerId, StartMinutes = 9 * 60, EndMinutes = 12 * 60, Capacity = 8, DaysMask = 0b1111111 });
            ds.Windows.Add(new DeliveryWindow { SettingId = ds.Id, SellerId = a.SellerId, StartMinutes = 13 * 60, EndMinutes = 17 * 60, Capacity = 8, DaysMask = 0b1111111 });
            db.DeliverySettings.Add(ds); Reg("delivery_setting", ds.Id);
            windowId = ds.Windows[0].Id;
        }
        else windowId = existing.Windows.FirstOrDefault(w => w.Active)?.Id;
        await db.SaveChangesAsync();
    }

    // ---- orders --------------------------------------------------------------------------------------------------------------------
    public record DemoOrder(Guid Id, string Mid, string Number, string State, bool Paid);

    private static readonly (string Name, string Email, string Phone, string Line1, string City, string Region, string Zip)[] Buyers =
    [
        ("Avery Morgan", "avery.morgan@example.com", "+1 555 010 0111", "12 Sample Avenue", "Brooklyn", "NY", "11201"),
        ("Jordan Lee", "jordan.lee@example.com", "+1 555 010 0112", "48 Placeholder Road", "Queens", "NY", "11101"),
        ("Taylor Brooks", "taylor.brooks@example.com", "+1 555 010 0113", "7 Example Court", "Hoboken", "NJ", "07030"),
        ("Riley Chen", "riley.chen@example.com", "+1 555 010 0114", "301 Demo Street", "Albany", "NY", "12207"),
        ("Casey Rivera", "casey.rivera@example.com", "+1 555 010 0115", "90 Test Lane", "Boston", "MA", "02108"),
        ("Morgan Patel", "morgan.patel@example.com", "+1 555 010 0116", "15 Fictional Way", "Philadelphia", "PA", "19103"),
        ("Quinn Foster", "quinn.foster@example.com", "+1 555 010 0117", "220 Sandbox Blvd", "Stamford", "CT", "06901"),
        ("Sam Okafor", "sam.okafor@example.com", "+1 555 010 0118", "5 Mockingbird Close", "Brooklyn", "NY", "11215"),
        ("Drew Alvarez", "drew.alvarez@example.com", "+1 555 010 0119", "64 Imaginary Drive", "Newark", "NJ", "07102"),
        ("Jamie Walsh", "jamie.walsh@example.com", "+1 555 010 0120", "81 Pretend Place", "Providence", "RI", "02903"),
    ];

    private async Task<DemoOrder> PlaceAsync(int n, int buyerIx, (Product P, int Qty)[] items, string state, TimeSpan age, bool local = false)
    {
        var b = Buyers[buyerIx % Buyers.Length];
        var mid = Mid("O", n);
        var lines = items.Select((it, i) =>
        {
            var v = it.P.HasVariants ? it.P.Variants.OrderBy(x => x.SortOrder).First() : null;
            var unit = v?.PriceCents ?? it.P.SaleCents ?? it.P.PriceCents ?? 1000;
            return new IngestLine($"L-{tag}-{n}-{i}", a.SellerId, brand.Id, it.P.Id, v?.Id, null, it.Qty, unit, 0, unit * it.Qty * 8 / 100);
        }).ToList();
        var order = new IngestOrder(mid, now - age, "USD", new IngestBuyer(b.Name, b.Email, b.Phone),
            new IngestAddress(b.Name, b.Line1, null, b.City, b.Region, b.Zip, "US", local ? 40.6903 : null, local ? -73.9871 : null),
            local ? "Local delivery" : "Standard", "standard", n == 1 ? "Enjoy! (demo gift message)" : null, lines,
            [new IngestShipping(a.SellerId, brand.Id, local ? 599 : 599)],
            local && windowId is { } w ? [new IngestDelivery(a.SellerId, brand.Id, DeliveryMethod.Local, w, DateOnly.FromDateTime(now.AddDays(1)))] : null);
        var r = await ingest.CreateAsync(order);
        if (!r.Ok) throw new InvalidOperationException("Demo order was rejected: " + string.Join("; ", r.Errors.Select(e => e.Code + " " + e.Message)));
        var so = r.Orders.Single();
        Reg("order", so.Id);
        await db.SaveChangesAsync();
        return new DemoOrder(so.Id, mid, so.Number, state, false);
    }

    private async Task<SellerOrder> LoadOrder(Guid id)
    {
        db.ChangeTracker.Clear();
        return await db.Orders.Include(o => o.Lines).Include(o => o.Shipments).AsSplitQuery().FirstAsync(o => o.Id == id);
    }

    private static void Ok(Res r, string what) { if (!r.Ok) throw new InvalidOperationException($"Demo order step '{what}' failed: {r.Code} {r.Message}"); }

    private async Task Backdate(Guid orderId, TimeSpan delivered)
    {
        // a delivery from the past (so the payment release rules are met); services only ever stamp "now"
        var at = now - delivered;
        await db.Orders.Where(o => o.Id == orderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.DeliveredAt, at).SetProperty(o => o.PlacedAt, at.AddDays(-8)));
        await db.Shipments.Where(x => x.OrderId == orderId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ShippedAt, at.AddDays(-4)).SetProperty(x => x.DeliveredAt, at));
    }

    private int trackingCounter;
    private string Tracking() => "1Z999AA1" + (10_000_000 + (Math.Abs(batchId.GetHashCode()) % 80_000_000) + ++trackingCounter * 37).ToString("D8") + "00";

    private async Task<List<DemoOrder>> OrdersAsync()
    {
        var res = new List<DemoOrder>();
        var inStock = products.Where(p => !p.HasVariants).ToList();
        Product P(int i) => inStock[i % inStock.Count];
        (Product, int)[] One(int i, int q = 1) => [(P(i), q)];

        // 1 new + paid
        var o1 = await PlaceAsync(1, 0, One(0, 2), "new", TimeSpan.FromHours(3)); res.Add(o1 with { Paid = true });
        // 2 accepted
        var o2 = await PlaceAsync(2, 1, [(P(1), 1), (P(2), 1)], "accepted", TimeSpan.FromDays(1));
        Ok(await actions.AcceptAsync(await LoadOrder(o2.Id), OwnerId, null), "accept"); res.Add(o2 with { Paid = true });
        // 3 partially shipped
        var o3 = await PlaceAsync(3, 2, [(P(2), 1), (P(3), 2)], "partially_shipped", TimeSpan.FromDays(2));
        Ok(await actions.AcceptAsync(await LoadOrder(o3.Id), OwnerId, null), "accept");
        var so3 = await LoadOrder(o3.Id);
        Ok(await actions.ShipAsync(so3, OwnerId, new ShipRequest(null, [new QtyLine(so3.Lines[0].Id, so3.Lines[0].Quantity)], "UPS", "Ground", Tracking(), null)), "ship partial"); res.Add(o3 with { Paid = true });
        // 4 shipped with tracking
        var o4 = await PlaceAsync(4, 3, One(4), "shipped", TimeSpan.FromDays(3));
        Ok(await actions.AcceptAsync(await LoadOrder(o4.Id), OwnerId, null), "accept");
        Ok(await actions.ShipAsync(await LoadOrder(o4.Id), OwnerId, new ShipRequest(null, null, "UPS", "Ground", Tracking(), null)), "ship"); res.Add(o4 with { Paid = true });
        // 5 delivered long ago (its money is released and transferred)
        var o5 = await PlaceAsync(5, 4, One(5, 2), "delivered", TimeSpan.FromDays(60));
        Ok(await actions.AcceptAsync(await LoadOrder(o5.Id), OwnerId, null), "accept");
        Ok(await actions.ShipAsync(await LoadOrder(o5.Id), OwnerId, new ShipRequest(null, null, "USPS", "Priority", "9400" + (111_899_223_000_000_000L + ++trackingCounter * 977L).ToString("D18")[..16], null)), "ship");
        Ok(await actions.MarkDeliveredAsync(await LoadOrder(o5.Id), OwnerId, null), "deliver");
        await Backdate(o5.Id, TimeSpan.FromDays(45)); res.Add(o5 with { Paid = true });
        // 6 delivered recently (still pending)
        var o6 = await PlaceAsync(6, 5, One(6), "delivered_recent", TimeSpan.FromDays(6));
        Ok(await actions.AcceptAsync(await LoadOrder(o6.Id), OwnerId, null), "accept");
        Ok(await actions.ShipAsync(await LoadOrder(o6.Id), OwnerId, new ShipRequest(null, null, "FedEx", "Ground", "7" + (11_000_000_000L + ++trackingCounter * 131L).ToString("D11"), null)), "ship");
        Ok(await actions.MarkDeliveredAsync(await LoadOrder(o6.Id), OwnerId, null), "deliver"); res.Add(o6 with { Paid = true });
        // 7 cancelled by the seller
        var o7 = await PlaceAsync(7, 6, One(7), "cancelled", TimeSpan.FromDays(4));
        Ok(await actions.CancelAsync(await LoadOrder(o7.Id), OwnerId, new CancelRequest(null, "out_of_stock", "Demo cancellation", true)), "cancel"); res.Add(o7);
        // 8 refunded
        var o8 = await PlaceAsync(8, 7, One(3), "refunded", TimeSpan.FromDays(12));
        Ok(await actions.AcceptAsync(await LoadOrder(o8.Id), OwnerId, null), "accept");
        Ok(await actions.ShipAsync(await LoadOrder(o8.Id), OwnerId, new ShipRequest(null, null, "UPS", "Ground", Tracking(), null)), "ship");
        Ok(await actions.MarkDeliveredAsync(await LoadOrder(o8.Id), OwnerId, null), "deliver");
        Ok(await actions.RefundAsync(await LoadOrder(o8.Id), OwnerId, new RefundRequest(null, null, null, true, "item_not_as_described", "Demo refund", null)), "refund"); res.Add(o8 with { Paid = true });
        // 9 delivered, the buyer asked to return it
        var o9 = await PlaceAsync(9, 8, One(1), "return_requested", TimeSpan.FromDays(9));
        Ok(await actions.AcceptAsync(await LoadOrder(o9.Id), OwnerId, null), "accept");
        Ok(await actions.ShipAsync(await LoadOrder(o9.Id), OwnerId, new ShipRequest(null, null, "UPS", "Ground", Tracking(), null)), "ship");
        Ok(await actions.MarkDeliveredAsync(await LoadOrder(o9.Id), OwnerId, null), "deliver");
        db.ChangeTracker.Clear();
        var so9 = await db.Orders.Include(o => o.Lines).AsNoTracking().FirstAsync(o => o.Id == o9.Id);
        var rr = await buyer.ReturnRequestAsync(o9.Mid, new($"RT-{tag}-9", o9.Number, "not_as_described", "The colour is different from the photos (demo).",
            so9.Lines.Where(l => l.ShippedQty > 0).Select(l => new BuyerReturnLine(l.MarketplaceLineId, l.ShippedQty)).ToList()));
        if (rr.Status >= 400) throw new InvalidOperationException("Demo return request failed: " + rr.Code + " " + rr.Message);
        res.Add(o9 with { Paid = true });
        // 10 local delivery
        if (windowId is not null)
        {
            var o10 = await PlaceAsync(10, 9, One(0), "local_delivery", TimeSpan.FromHours(20), local: true);
            Ok(await actions.AcceptAsync(await LoadOrder(o10.Id), OwnerId, null), "accept"); res.Add(o10 with { Paid = true });
        }
        db.ChangeTracker.Clear();
        return res;
    }

    // ---- bookings & quote -----------------------------------------------------------------------------------------------------
    private DateTime NextSlot(string zoneId, int daysAhead, int hour)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(now, tz).Date.AddDays(daysAhead);
        while (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) local = local.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(local.AddHours(hour), tz);
    }

    private async Task<Booking> MakeBooking(int n, Product svc, int daysAhead, int hour, int buyerIx, int minutes = 60)
    {
        var b = Buyers[buyerIx % Buyers.Length];
        var zone = await cal.AccountZoneIdAsync(a.SellerId);
        var start = NextSlot(zone, daysAhead, hour);
        var r = await bookings.CreateAsync(new BookingIn(Mid("B", n), svc.Id, a.MembershipId, start, minutes, "online", [], new BuyerIn(b.Name, b.Email, b.Phone), null,
            [new IntakeAnswer("goal", "Plan the living room refresh (demo)")]), hold: false);
        if (!r.Ok) throw new InvalidOperationException("Demo booking failed: " + r.Code + " " + r.Message);
        db.ChangeTracker.Clear();
        var bk = await db.Bookings.AsNoTracking().FirstAsync(x => x.MarketplaceBookingId == Mid("B", n));
        Reg("booking", bk.Id);
        await db.SaveChangesAsync();
        return bk;
    }

    private async Task BookingsAsync()
    {
        var actor = new Actor("seller", OwnerId);
        var fixedSvc = services[0]; var hourly = services[1]; var quote = services[2];
        // requested (the hourly service needs approval)
        await MakeBooking(1, hourly, 3, 10, 0);
        // confirmed (instant booking)
        await MakeBooking(2, fixedSvc, 4, 11, 1);
        // completed: booked ahead, then moved into the past and run through start/complete like a real one
        var done = await MakeBooking(3, fixedSvc, 5, 14, 2);
        var past = now.AddDays(-9).Date.AddHours(14);
        await db.Bookings.Where(x => x.Id == done.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.StartUtc, past).SetProperty(x => x.EndUtc, past.AddMinutes(done.DurationMinutes))
            .SetProperty(x => x.BlockStartUtc, past).SetProperty(x => x.BlockEndUtc, past.AddMinutes(done.DurationMinutes)).SetProperty(x => x.CreatedAt, past.AddDays(-3)).SetProperty(x => x.ConfirmedAt, past.AddDays(-3)));
        db.ChangeTracker.Clear();
        var bd = await db.Bookings.FirstAsync(x => x.Id == done.Id);
        BookingOk(await bookings.StartAsync(bd, actor), "start");
        BookingOk(await bookings.CompleteAsync(await db.Bookings.FirstAsync(x => x.Id == done.Id), actor, null), "complete");
        await db.Bookings.Where(x => x.Id == done.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompletedAt, past.AddMinutes(done.DurationMinutes)).SetProperty(x => x.UpdatedAt, past.AddMinutes(done.DurationMinutes)));
        // cancelled by the seller
        var cx = await MakeBooking(4, fixedSvc, 6, 15, 3);
        db.ChangeTracker.Clear();
        BookingOk(await bookings.CancelAsync(await db.Bookings.FirstAsync(x => x.Id == cx.Id), actor, "Demo: the provider is unavailable"), "cancel");
        // one quote request
        var q = await quotes.CreateRequestAsync(new QuoteIn(Mid("Q", 2), quote.Id, new BuyerIn("Quinn Foster", "quinn.foster@example.com", "+1 555 010 0117"),
            "I'd like help planning a ceramic wall display for a 3 m hallway (demo request).", "online", null, [NextSlot(await cal.AccountZoneIdAsync(a.SellerId), 7, 10)]));
        if (!q.Ok) throw new InvalidOperationException("Demo quote request failed: " + q.Code + " " + q.Message);
        var qid = (Guid)q.Data!.GetType().GetProperty("quoteId")!.GetValue(q.Data)!;
        Reg("quote", qid);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static void BookingOk(BRes r, string what) { if (!r.Ok) throw new InvalidOperationException($"Demo booking step '{what}' failed: {r.Code} {r.Message}"); }

    // ---- messages ------------------------------------------------------------------------------------------------------------------
    private async Task MessagesAsync(List<DemoOrder> orders)
    {
        var texts = new[]
        {
            (0, "Hi! Could you add a gift note to my order? Thank you."),
            (3, "Hello, the tracking page hasn't updated for a day. Is the parcel still on its way?"),
            (8, "I'd like to send this back: the colour isn't what I expected. How do I start a return?"),
        };
        var k = 0;
        foreach (var (ix, text) in texts)
        {
            var o = orders[ix];
            var m = await messages.IngestBuyerMessageAsync(new($"MSG-{tag}-{++k}", $"CONV-{tag}-{k}", "order", o.Mid, o.Number, null, Buyers[ix % Buyers.Length].Name, text, now.AddHours(-2 * k)));
            if (m.Status >= 400) throw new InvalidOperationException("Demo message failed: " + m.Code + " " + m.Message);
            Reg("conversation", m.Conversation!.Id);
            await db.SaveChangesAsync();
        }
        // a pre-sale question about a product (a product conversation)
        var pm = await messages.IngestBuyerMessageAsync(new($"MSG-{tag}-P", $"PQ-{tag}-1", "product", null, null, products[1].Id, "Jamie W.", "Is the pour-over set suitable for a gas stove? (demo)", now.AddHours(-1)));
        if (pm.Status >= 400) throw new InvalidOperationException("Demo product message failed: " + pm.Code + " " + pm.Message);
        Reg("conversation", pm.Conversation!.Id);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    // ---- reviews -------------------------------------------------------------------------------------------------------------------
    /// <summary>Three reviews through the same engine the marketplace endpoint uses (5 stars with a reply, 3 stars, 1 star). The fill runs with outbox events suppressed, so the reply sends nothing.</summary>
    private async Task ReviewsAsync()
    {
        var specs = new[]
        {
            (Product: 0, Rating: 5, Name: "Morgan P.", Text: "Beautifully made and it arrived wrapped with care. I use it every day. (demo)", Days: 5, Reply: "Thank you, Morgan! We are so happy it found a good home. (demo)"),
            (Product: 1, Rating: 3, Name: "Alex R.", Text: "Nice design, but the carafe was smaller than I expected. (demo)", Days: 3, Reply: (string?)null),
            (Product: 3, Rating: 1, Name: "Sam T.", Text: "The candles arrived broken and I could not light them. (demo)", Days: 1, Reply: null),
        };
        var k = 0;
        foreach (var s in specs)
        {
            var created = now.AddDays(-s.Days);
            var r = await reviewEngine.IngestAsync(new($"DEMO-{tag}-REV{++k}", products[s.Product].Id, a.SellerId, s.Rating, s.Text, s.Name, created, null));
            if (r.Status >= 400) throw new InvalidOperationException("Demo review failed: " + r.Code + " " + r.Message);
            Reg("review", r.Review!.Id);
            await db.SaveChangesAsync();
            if (s.Reply is not null)
            {
                var rep = await reviewEngine.ReplyAsync(r.Review.Id, a.SellerId, OwnerId, s.Reply);
                if (rep.Status >= 400) throw new InvalidOperationException("Demo reply failed: " + rep.Code + " " + rep.Message);
            }
            db.ChangeTracker.Clear();
        }
    }

    // ---- payments ------------------------------------------------------------------------------------------------------------------
    private async Task PaymentsAsync(List<DemoOrder> orders)
    {
        async Task Event(string type, object data, string key)
        {
            var eventId = $"demo-{batchId:N}-{key}";
            var el = JsonSerializer.SerializeToElement(data, J);
            var r = await payments.HandleAsync(new MarketplacePaymentEvent(eventId, type, el));
            if (r.Status != 200) throw new InvalidOperationException($"Demo payment event {type} failed: {r.Code} {r.Message}");
            Reg("payment_event", Guid.Empty, eventId);
            await db.SaveChangesAsync();   // services clear the change tracker: never leave a registry row pending
        }
        foreach (var o in orders.Where(o => o.Paid))
            await Event("payment.captured", new { kind = "order", marketplaceOrderId = o.Mid, sellerOrderNumber = o.Number, paymentRef = "ch_demo_" + tag + o.Number.GetHashCode().ToString("x"), currency = "USD" }, "cap-" + o.Number);
        // the refunded order: the marketplace refunds the full amount
        var refunded = orders.First(o => o.State == "refunded");
        db.ChangeTracker.Clear();
        var ro = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == refunded.Id);
        await Event("refund.processed", new { refundId = "re_demo_" + tag, marketplaceOrderId = refunded.Mid, sellerOrderNumber = refunded.Number, amountCents = ro.ItemsCents - ro.DiscountCents, shippingCents = ro.ShippingCents, currency = "USD" }, "refund-" + refunded.Number);
        // bookings that are paid (the completed one) and confirmed ones
        foreach (var bk in await db.Bookings.AsNoTracking().Where(b => b.SellerId == a.SellerId && b.MarketplaceBookingId!.StartsWith("DEMO-" + tag + "-B") && (b.Status == BookingStatus.Completed || b.Status == BookingStatus.Confirmed)).ToListAsync())
            await Event("payment.captured", new { kind = "booking", marketplaceBookingId = bk.MarketplaceBookingId, paymentRef = "ch_demo_" + tag + bk.Number.GetHashCode().ToString("x"), currency = "USD" }, "cap-" + bk.Number);

        // release what is old enough (the delivered-long-ago order and the completed booking), then batch it into one transfer: this account only
        if (provider.IsFake && await db.PayoutAccounts.FirstOrDefaultAsync(p => p.SellerId == a.SellerId) is null)
        {
            var acct = $"acct_demo_{tag.ToLowerInvariant()}";
            db.PayoutAccounts.Add(new PayoutAccount { SellerId = a.SellerId, StripeAccountId = acct, DetailsSubmitted = true, ChargesEnabled = true, PayoutsEnabled = true, BankName = "Demo Bank", BankLast4 = "4242", CreatedAt = now, UpdatedAt = now });
            if (provider is FakePaymentProvider fake) fake.Accounts.TryAdd(acct, new AccountSnapshot(acct, true, true, true, [], [], null, null, null, null, null, null));
            Reg("payout_account", a.SellerId);
            await db.SaveChangesAsync();
        }
        // only the demo orders and bookings are released; the transfer batches the account's WHOLE available balance, so it only runs when that balance is demo money alone
        db.ChangeTracker.Clear();
        var sources = await db.DemoRecords.AsNoTracking().Where(r => r.SellerId == a.SellerId && (r.Kind == "order" || r.Kind == "booking")).Select(r => r.EntityId).ToListAsync();
        await release.RunAsync(onlySeller: a.SellerId, onlySources: sources);
        var realAvailable = await db.LedgerEntries.AsNoTracking().Where(e => e.SellerId == a.SellerId && e.Account == LedgerAccount.Available
            && !((e.OrderId != null && sources.Contains(e.OrderId.Value)) || (e.BookingId != null && sources.Contains(e.BookingId.Value)))).SumAsync(e => (long?)e.AmountCents) ?? 0;
        if (provider.IsFake && realAvailable == 0) await transfers.RunAsync(onlySeller: a.SellerId);
        // a later sale stays available (not yet transferred): release the recently delivered order's twin by hand is NOT done; pending + available both exist from the orders above
        db.ChangeTracker.Clear();
        foreach (var t in await db.PaymentTransfers.AsNoTracking().Where(t => t.SellerId == a.SellerId).Select(t => t.Id).ToListAsync()) Reg("transfer", t);
        await db.SaveChangesAsync();
    }
}
