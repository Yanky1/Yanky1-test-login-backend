using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Products;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Orders;

public record IngestBuyer(string? DisplayName, string? RelayEmail, string? Phone);
public record IngestAddress(string? Name, string? Line1, string? Line2, string? City, string? Region, string? PostalCode, string? Country, double? Lat = null, double? Lng = null);
public record IngestLine(string? MarketplaceLineId, Guid SellerId, Guid BrandId, Guid? ProductId, Guid? VariantId, string? Sku, int Quantity,
    long UnitPriceCents, long DiscountCents, long TaxCents);
public record IngestShipping(Guid SellerId, Guid BrandId, long AmountCents, string? Speed = null, string? Method = null, long? TaxCents = null);
/// <summary>Additive: the buyer chose local delivery from this seller (and brand) for this date and window (see docs/LOCAL-DELIVERY.md).</summary>
public record IngestDelivery(Guid SellerId, Guid BrandId, string? Method, Guid? WindowId, DateOnly? Date);
public record IngestOrder(string? MarketplaceOrderId, DateTime? PlacedAt, string? Currency, IngestBuyer? Buyer, IngestAddress? ShippingAddress,
    string? ShippingMethod, string? ShippingSpeed, string? GiftMessage, List<IngestLine>? Lines, List<IngestShipping>? Shipping, List<IngestDelivery>? Deliveries = null);

public record LineError(int Index, string Code, string Message);
public record IngestResult(bool Created, List<SellerOrder> Orders, List<LineError> Errors)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>Creates seller orders from a marketplace order: validation, splitting per (account, brand), stock reservation, SLA dates, fees, number.</summary>
public class OrderIngestService(AppDbContext db, IInventoryService inventory, TimeProvider time, IOptions<OrderOptions> opt, OrderNotifier notifier, InflightHolds inflight, Solvelt.Api.Delivery.DeliveryService delivery)
{
    private static readonly Dictionary<string, int> Transit = new() { ["standard"] = 5, ["expedited"] = 2, ["overnight"] = 1 };
    /// <summary>Upper bound for any single money field (10,000,000.00): keeps every sum far inside <c>long</c> (100 lines x 10,000 units).</summary>
    public const long MaxCents = 1_000_000_000;
    public static readonly string[] Speeds = ["standard", "expedited", "overnight"];

    private sealed record GroupShipping(long AmountCents, long TaxCents, string? Speed, string? Method);

    /// <summary>The longest shipping method label: the <c>seller_orders.ShippingMethod</c> column is varchar(64), so the validation and the column agree (PostgreSQL rejects more).</summary>
    public const int MaxMethodLength = 64;

    /// <summary>At most this many <c>shipping[]</c> entries: a group's summed amount and tax are then bounded as well.</summary>
    public const int MaxShippingEntries = 50;

    /// <summary>A shipping method label: trimmed, control characters removed, 1 to <see cref="MaxMethodLength"/> characters (null when empty or too long).</summary>
    public static string? CleanMethod(string? s)
    {
        var t = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return t.Length is >= 1 and <= MaxMethodLength ? t : null;
    }

    private static bool Bad(string? s, int max, bool required = true) => string.IsNullOrWhiteSpace(s) ? required : s.Length > max;

    public async Task<IngestResult> CreateAsync(IngestOrder req, CancellationToken ct = default)
    {
        var errors = new List<LineError>();
        void Err(int i, string code, string msg) => errors.Add(new(i, code, msg));
        var o = opt.Value;

        if (Bad(req.MarketplaceOrderId, 64)) Err(-1, "marketplaceOrderId", "marketplaceOrderId is required (max 64 characters).");
        var currency = (req.Currency ?? "USD").ToUpperInvariant();
        if (currency.Length != 3) Err(-1, "currency", "currency must be a 3-letter code.");
        if (Bad(req.Buyer?.DisplayName, 120)) Err(-1, "buyer.displayName", "buyer.displayName is required (max 120 characters).");
        if (req.Buyer?.RelayEmail is { } re && (re.Length > 254 || !re.Contains('@'))) Err(-1, "buyer.relayEmail", "buyer.relayEmail is not valid.");
        if (req.Buyer?.Phone is { Length: > 30 }) Err(-1, "buyer.phone", "buyer.phone is too long.");
        var ad = req.ShippingAddress;
        if (ad is null || Bad(ad.Name, 120) || Bad(ad.Line1, 200) || Bad(ad.City, 100) || Bad(ad.PostalCode, 20) || Bad(ad.Country, 2) || ad.Country?.Length != 2
            || Bad(ad.Line2, 200, false) || Bad(ad.Region, 100, false))
            Err(-1, "shippingAddress", "shippingAddress needs name, line1, city, postalCode and a 2-letter country.");
        var speed = (req.ShippingSpeed ?? "standard").ToLowerInvariant();
        if (!Speeds.Contains(speed)) Err(-1, "shippingSpeed", "shippingSpeed must be standard, expedited or overnight.");
        if (req.GiftMessage is { Length: > 500 }) Err(-1, "giftMessage", "giftMessage is too long.");
        if (req.Lines is null or { Count: 0 } or { Count: > 100 }) Err(-1, "lines", "Between 1 and 100 lines are required.");
        if (errors.Count > 0) return new(false, [], errors);

        var mid = req.MarketplaceOrderId!.Trim();
        var existing = await db.Orders.AsNoTracking().Where(x => x.MarketplaceOrderId == mid).ToListAsync(ct);
        if (existing.Count > 0) return new(false, existing, []); // idempotent: same marketplace order id returns what was created before

        var now = time.UtcNow();
        var placed = req.PlacedAt is { } p && p <= now.AddMinutes(5) && p > now.AddYears(-1) ? OrderSupport.Utc(p) : now;
        if ((req.Shipping?.Count ?? 0) > MaxShippingEntries) Err(-1, "shipping", $"At most {MaxShippingEntries} shipping entries are accepted.");
        if (req.ShippingMethod is { } om && CleanMethod(om) is null && !string.IsNullOrWhiteSpace(om)) Err(-1, "shippingMethod", $"shippingMethod must be at most {MaxMethodLength} printable characters.");
        foreach (var s in (req.Shipping ?? []).Take(MaxShippingEntries))
        {
            if (s.AmountCents is < 0 or > MaxCents) Err(-1, "shipping", "Shipping amounts must be between 0 and 10,000,000.00.");
            if (s.TaxCents is < 0 or > MaxCents) Err(-1, "shipping.taxCents", "shipping[].taxCents must be between 0 and 10,000,000.00.");
            if (s.Speed is { } sp && !Speeds.Contains(sp.Trim().ToLowerInvariant())) Err(-1, "shipping.speed", "shipping[].speed must be standard, expedited or overnight.");
            if (s.Method is { } m && CleanMethod(m) is null) Err(-1, "shipping.method", $"shipping[].method must be 1 to {MaxMethodLength} printable characters.");
        }
        // one entry per (seller, brand) group; if a group is repeated, amounts and tax add up and the entries must agree on speed and method
        var shippingBy = new Dictionary<(Guid, Guid), GroupShipping>();
        foreach (var s in (req.Shipping ?? []).Take(MaxShippingEntries))
        {
            if (s.AmountCents is < 0 or > MaxCents || s.TaxCents is < 0 or > MaxCents) continue;
            var key = (s.SellerId, s.BrandId);
            var speedOf = s.Speed?.Trim().ToLowerInvariant(); var methodOf = s.Method is null ? null : CleanMethod(s.Method);
            if (!shippingBy.TryGetValue(key, out var gs)) { shippingBy[key] = new GroupShipping(s.AmountCents, s.TaxCents ?? 0, speedOf, methodOf); continue; }
            if ((gs.Speed is not null && speedOf is not null && gs.Speed != speedOf) || (gs.Method is not null && methodOf is not null && gs.Method != methodOf))
                Err(-1, "shipping", "The shipping entries of one seller/brand group disagree on speed or method.");
            shippingBy[key] = new GroupShipping(gs.AmountCents + s.AmountCents, gs.TaxCents + (s.TaxCents ?? 0), gs.Speed ?? speedOf, gs.Method ?? methodOf);
        }
        foreach (var (_, gs) in shippingBy) if (gs.AmountCents > MaxCents || gs.TaxCents > MaxCents) Err(-1, "shipping", "The shipping of one seller/brand group must not exceed 10,000,000.00 (amount and tax each).");
        foreach (var (_, gs) in shippingBy) if (gs.TaxCents > 0 && gs.AmountCents == 0) Err(-1, "shipping.taxCents", "shipping[].taxCents needs a shipping amount above zero.");

        // resolve + validate lines
        var resolved = new List<(IngestLine L, Product P, ProductVariant? V, int Index)>();
        var sellers = new Dictionary<Guid, SellerStatus?>();
        for (var i = 0; i < req.Lines!.Count; i++)
        {
            var l = req.Lines[i];
            if (l.Quantity is < 1 or > 10000) { Err(i, "quantity", "quantity must be between 1 and 10000."); continue; }
            if (l.UnitPriceCents is < 0 or > MaxCents || l.DiscountCents is < 0 or > MaxCents * 10000L || l.TaxCents is < 0 or > MaxCents * 10000L
                || l.DiscountCents > l.UnitPriceCents * l.Quantity) { Err(i, "amount", "Amounts must be >= 0 and the discount can't exceed the line price."); continue; }
            if (l.MarketplaceLineId is { Length: > 64 }) { Err(i, "marketplaceLineId", "marketplaceLineId is too long."); continue; }
            if (!sellers.TryGetValue(l.SellerId, out var st))
                sellers[l.SellerId] = st = await db.Sellers.AsNoTracking().Where(s => s.Id == l.SellerId).Select(s => (SellerStatus?)s.Status).FirstOrDefaultAsync(ct);
            if (st is null || !AccountContextService.Usable(st.Value)) { Err(i, "seller", "Unknown or inactive seller account."); continue; }
            var brand = await db.Brands.AsNoTracking().FirstOrDefaultAsync(b => b.Id == l.BrandId && b.SellerId == l.SellerId, ct);
            if (brand is null) { Err(i, "brand", "The brand does not belong to that seller account."); continue; }

            Guid? pid = l.ProductId, vid = l.VariantId;
            if (pid is null)
            {
                if (string.IsNullOrWhiteSpace(l.Sku)) { Err(i, "product", "productId or sku is required."); continue; }
                var hit = await inventory.FindBySkuAsync(l.SellerId, l.Sku, ct);
                if (hit is null) { Err(i, "sku", "No product or variant with this SKU in the seller account."); continue; }
                (pid, vid) = (hit.Value.ProductId, hit.Value.VariantId);
            }
            var prod = await db.Products.AsNoTracking().Include(x => x.Variants).Include(x => x.Options).Include(x => x.Images)
                .FirstOrDefaultAsync(x => x.Id == pid && x.SellerId == l.SellerId && x.BrandId == l.BrandId, ct);
            if (prod is null) { Err(i, "product", "The product does not belong to that seller account and brand."); continue; }
            if (prod.Type == ListingType.Service) { Err(i, "not_a_product", "Services are booked, not ordered."); continue; }
            if (prod.Status != ProductStatus.Active) { Err(i, "product_inactive", "The product is not active."); continue; }
            ProductVariant? variant = null;
            if (prod.HasVariants)
            {
                variant = vid is null ? null : prod.Variants.FirstOrDefault(v => v.Id == vid);
                if (variant is null) { Err(i, "variant", "A valid variantId is required for this product."); continue; }
                if (!variant.Enabled) { Err(i, "variant_inactive", "The variant is not available."); continue; }
            }
            else if (vid is not null) { Err(i, "variant", "This product has no variants."); continue; }
            resolved.Add((l, prod, variant, i));
        }
        if (errors.Count > 0) return new(false, [], errors);

        // reserve stock (single atomic UPDATE per line; a shortage never fails the order, it flags it)
        var reservedLog = new List<(Guid SellerId, Guid ProductId, Guid? VariantId, int Qty)>();
        using var holds = inflight.Begin();   // visible to the reconciliation job until the order is saved (or the holds are given back)
        var orders = new List<SellerOrder>();
        var booked = new List<LocalDelivery>();
        try
        {
            foreach (var g in resolved.GroupBy(r => (r.L.SellerId, r.L.BrandId)))
            {
                var gShip = shippingBy.GetValueOrDefault(g.Key);
                var gSpeed = gShip?.Speed ?? speed;                                         // a group's own speed/method wins over the order-level ones
                var order = new SellerOrder
                {
                    SellerId = g.Key.SellerId, BrandId = g.Key.BrandId, MarketplaceOrderId = mid, PlacedAt = placed, Currency = currency,
                    BuyerName = req.Buyer!.DisplayName!.Trim(), BuyerRelayEmail = req.Buyer.RelayEmail?.Trim(), BuyerPhone = req.Buyer.Phone?.Trim(),
                    ShipName = ad!.Name!.Trim(), ShipLine1 = ad.Line1!.Trim(), ShipLine2 = ad.Line2?.Trim(), ShipCity = ad.City!.Trim(),
                    ShipRegion = ad.Region?.Trim(), ShipPostalCode = ad.PostalCode!.Trim(), ShipCountry = ad.Country!.ToUpperInvariant(),
                    ShippingMethod = gShip?.Method ?? CleanMethod(req.ShippingMethod) ?? gSpeed, ShippingSpeed = gSpeed, GiftMessage = req.GiftMessage?.Trim(),
                    ShippingCents = gShip?.AmountCents ?? 0, ShippingTaxCents = gShip?.TaxCents ?? 0, CreatedAt = now, UpdatedAt = now
                };
                var sellerHandling = await db.SellerShippingSettings.AsNoTracking().Where(x => x.SellerId == g.Key.SellerId && x.HandlingDays != null && (x.ScopeKey == g.Key.BrandId || x.ScopeKey == Guid.Empty))
                    .OrderByDescending(x => x.ScopeKey).Select(x => x.HandlingDays).FirstOrDefaultAsync();   // brand setting, else account setting (Settings > Shipping)
                var handling = g.Max(r => r.P.HandlingDays ?? sellerHandling ?? o.DefaultHandlingDays);
                order.ShipByAt = placed.AddDays(handling);
                order.DeliverByAt = order.ShipByAt.AddDays(Transit[gSpeed]);
                foreach (var (l, prod, variant, _) in g)
                {
                    var line = new OrderLine
                    {
                        OrderId = order.Id, MarketplaceLineId = l.MarketplaceLineId?.Trim(), ProductId = prod.Id, VariantId = variant?.Id,
                        Title = prod.Title, Sku = variant?.Sku ?? prod.Sku, OptionsText = OptionsText(prod, variant),
                        UnitPriceCents = l.UnitPriceCents, Quantity = l.Quantity, DiscountCents = l.DiscountCents, TaxCents = l.TaxCents
                    };
                    var img = prod.Images.Where(x => variant != null && x.VariantId == variant.Id).OrderBy(x => x.SortOrder).FirstOrDefault()
                              ?? prod.Images.Where(x => x.VariantId == null).OrderByDescending(x => x.IsMain).ThenBy(x => x.SortOrder).FirstOrDefault();
                    if (img is not null) { line.ImageKey = img.StorageKey; line.ImageContentType = img.ContentType; }
                    holds.Add(prod.Id, variant?.Id, l.Quantity);   // announced BEFORE the stock moves: the repair job must never see a hold it can't explain
                    var r = await inventory.ReserveAsync(prod.SellerId, prod.Id, variant?.Id, l.Quantity, CancellationToken.None);
                    if (!r.Ok) holds.Add(prod.Id, variant?.Id, -l.Quantity);
                    if (r.Ok) { line.ReservedQty = l.Quantity; reservedLog.Add((prod.SellerId, prod.Id, variant?.Id, l.Quantity)); }
                    else order.Oversold = true;
                    order.Lines.Add(line);
                }
                if (ad is { Lat: { } alat, Lng: { } alng } && Solvelt.Api.Delivery.Geo.ValidLatLng(alat, alng)) { order.ShipLat = alat; order.ShipLng = alng; }
                OrderSupport.Recalculate(order, await Solvelt.Api.Admin.PlatformSettings.FeeAsync(db, o.FeeBasisPoints));
                order.Number = await OrderSupport.NextNumberAsync(db, now);
                db.AddEvent(order.Id, "order.placed", Party.Buyer, null, "Order placed", now);
                if (order.Oversold) db.AddEvent(order.Id, "order.oversold", Party.System, null, "Not enough stock for every item: needs attention", now);
                db.Orders.Add(order);
                orders.Add(order);
                if (req.Deliveries?.FirstOrDefault(x => x.SellerId == g.Key.SellerId && x.BrandId == g.Key.BrandId) is { } dl)
                {
                    if (!string.Equals(dl.Method, "local", StringComparison.OrdinalIgnoreCase) || dl.WindowId is null || dl.Date is null) errors.Add(new(-1, "delivery", "deliveries[] needs method \"local\", windowId and date."));
                    else
                    {
                        var (d, code, msg) = await delivery.PrepareAsync(order, dl.WindowId.Value, dl.Date.Value, order.ShipLat, order.ShipLng, converted: false);
                        if (d is null) errors.Add(new(-1, code!, msg!));
                        else { booked.Add(d); order.ShippingMethod = "local"; db.AddEvent(order.Id, "order.local_delivery", Party.Buyer, null, $"Local delivery chosen ({d.Code})", now); }
                    }
                }
            }
            if (req.Deliveries?.Any(x => !resolved.Any(r => r.L.SellerId == x.SellerId && r.L.BrandId == x.BrandId)) == true) errors.Add(new(-1, "delivery", "deliveries[] names a seller/brand that has no lines in this order."));
            if (errors.Count > 0)
            {
                // nothing is created: give back the stock holds and the window capacity
                db.ChangeTracker.Clear();
                foreach (var b in booked) await delivery.ReleaseAsync(b);
                await ReleaseAllAsync(reservedLog);
                return new(false, [], errors);
            }
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException)
        {
            // lost the idempotency race to a concurrent identical request: undo our reservations and return what the winner created
            db.ChangeTracker.Clear();
            foreach (var b in booked) await delivery.ReleaseAsync(b);
            await ReleaseAllAsync(reservedLog);
            var winner = await db.Orders.AsNoTracking().Where(x => x.MarketplaceOrderId == mid).ToListAsync(ct);
            if (winner.Count > 0) return new(false, winner, []);
            throw;
        }
        catch
        {
            // anything else (overflow, cancellation, bug): nothing was saved, so give every hold back before surfacing the error
            db.ChangeTracker.Clear();
            foreach (var b in booked) await delivery.ReleaseAsync(b);
            await ReleaseAllAsync(reservedLog);
            throw;
        }
        foreach (var order in orders) await notifier.OrderPlacedAsync(order, CancellationToken.None);   // the order exists: a cancelled request must not skip the notifications
        return new(true, orders, []);
    }

    /// <summary>Gives every hold back; one failing release never stops the others (the reconciliation job repairs anything left behind).</summary>
    private async Task ReleaseAllAsync(List<(Guid SellerId, Guid ProductId, Guid? VariantId, int Qty)> held)
    {
        foreach (var r in held)
        {
            try { await inventory.ReleaseAsync(r.SellerId, r.ProductId, r.VariantId, r.Qty, CancellationToken.None); }
            catch (Exception) { /* keep releasing the rest */ }
        }
    }

    private static string? OptionsText(Product p, ProductVariant? v)
    {
        if (v is null) return null;
        var vals = ProductJson.Values(v.ValuesJson);
        var names = p.Options.OrderBy(x => x.SortOrder).Select(x => x.Name).ToList();
        return string.Join(", ", vals.Select((val, i) => i < names.Count ? $"{names[i]}: {val}" : val));
    }
}
