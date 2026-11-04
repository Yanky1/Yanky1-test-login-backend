using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using Solvelt.Api.Payments;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Shipping;

public record PackageInput(Guid? PresetId, int? LengthMm, int? WidthMm, int? HeightMm, int? WeightGrams);
public record CustomsInput(string? ContentsType, string? NonDeliveryOption, string? HsCode, Dictionary<Guid, string>? LineHsCodes);
public record RatesRequest(Guid? FromAddressId, PackageInput? Package, List<QtyLine>? Lines, CustomsInput? Customs);
public record ShipAnywayInput(string? Reason);
public record BuyRequest(Guid? QuoteId, string? RateId, string? IdempotencyKey, ShipAnywayInput? ShipAnyway, int? Version);
public record VoidRequest(string? Reason);
public record BulkRequest(List<Guid>? OrderIds, Guid? PackageId, Guid? FromAddressId, string? Strategy, string? IdempotencyKey, int? WeightGrams = null);
public record ReturnLabelRequest(PackageInput? Package, CustomsInput? Customs, string? Carrier, string? Service, string? IdempotencyKey);

public record QuotedRate(string RateId, string Carrier, string Service, long AmountCents, string Currency, int? EstimatedDays, DateTime? EstimatedDelivery, bool MeetsDeliverBy, bool Recommended);

/// <summary>Quotes, purchases, voids and refunds of labels. The provider is called outside database transactions; everything that follows is recorded durably before the next step.</summary>
public class LabelService(AppDbContext db, IShippingProvider provider, IFileStorage storage, LedgerService ledger, OrderActions actions, IOptions<ShippingOptions> opt,
    TimeProvider time, IAuditLog audit, ILogger<LabelService> log)
{
    private static readonly string[] ContentsTypes = ["merchandise", "gift", "documents", "sample", "returned_goods", "other"];
    private static readonly JsonSerializerOptions J = OrderJson.Opts;

    public static Res FromProvider(ShippingProviderException ex) => ex.Code switch
    {
        "rate_expired" => new(false, 409, "rate_expired", "That rate is no longer available. Get new rates and try again.", new { requote = true }),
        "not_configured" => Res.Fail(503, "shipping_not_configured", "Shipping labels are not configured on this server."),
        "invalid_address" => Res.Invalid("The carrier could not accept this address.", "address_invalid"),
        _ => Res.Fail(502, "provider_error", "The shipping provider could not complete the request. Try again in a moment.")
    };

    public static string Money(long cents, string cur) => $"{cents / 100}.{cents % 100:D2} {cur}";

    // ---- resolving what is being shipped ---------------------------------------------------------------------------------
    public record Context(ShipFromAddress From, ShipAddress FromAddr, ShipAddress ToAddr, ParcelSpec Parcel, List<QtyLine> Lines, CustomsSpec? Customs, bool International);

    public async Task<ShipFromAddress?> FromAddressAsync(Guid sellerId, Guid brandId, Guid? id)
    {
        var q = db.ShipFromAddresses.Where(a => a.SellerId == sellerId && !a.Archived && (a.BrandId == null || a.BrandId == brandId));
        if (id is { } i) return await q.FirstOrDefaultAsync(a => a.Id == i);
        return await q.FirstOrDefaultAsync(a => a.IsDefault && a.ScopeKey == brandId) ?? await q.FirstOrDefaultAsync(a => a.IsDefault && a.ScopeKey == Guid.Empty);
    }

    public async Task<(Res? Error, ParcelSpec? Parcel)> ParcelAsync(SellerOrder o, List<QtyLine> lines, PackageInput? pkg)
    {
        PackagePreset? preset = null;
        if (pkg?.PresetId is { } pid)
        {
            preset = await db.PackagePresets.FirstOrDefaultAsync(p => p.Id == pid && p.SellerId == o.SellerId && !p.Archived);
            if (preset is null) return (Res.Invalid("That package preset doesn't exist.", "package"), null);
        }
        else if (pkg is null || (pkg.LengthMm is null && pkg.WidthMm is null && pkg.HeightMm is null))
        {
            var productIds = o.Lines.Where(l => lines.Any(x => x.LineId == l.Id)).Select(l => l.ProductId).ToList();
            var pp = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id) && p.DefaultPackageId != null).Select(p => p.DefaultPackageId).FirstOrDefaultAsync();
            preset = pp is { } d ? await db.PackagePresets.FirstOrDefaultAsync(p => p.Id == d && p.SellerId == o.SellerId && !p.Archived) : null;
            preset ??= await db.PackagePresets.FirstOrDefaultAsync(p => p.SellerId == o.SellerId && p.IsDefault && !p.Archived);
        }
        int l, w, h, empty = 0;
        if (preset is not null) { l = preset.LengthMm; w = preset.WidthMm; h = preset.HeightMm; empty = preset.EmptyWeightGrams; }
        else
        {
            if (pkg?.LengthMm is not > 0 || pkg.WidthMm is not > 0 || pkg.HeightMm is not > 0) return (Res.Invalid("Choose a package preset or enter the package size.", "package"), null);
            (l, w, h) = (pkg.LengthMm.Value, pkg.WidthMm.Value, pkg.HeightMm.Value);
        }
        if (Math.Max(l, Math.Max(w, h)) > 2000) return (Res.Invalid("The package is larger than 2 m.", "package"), null);
        int items;
        if (pkg?.WeightGrams is { } wg) items = wg;
        else
        {
            var ids = o.Lines.Select(x => x.ProductId).Distinct().ToList();
            var weights = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.WeightGrams }).ToDictionaryAsync(p => p.Id, p => p.WeightGrams);
            long sum = 0;
            foreach (var q in lines)
            {
                var line = o.Lines.First(x => x.Id == q.LineId);
                if (weights.GetValueOrDefault(line.ProductId) is not > 0) return (Res.Invalid($"Enter the package weight ({line.Title} has no weight).", "weight_required"), null);
                sum += (long)weights[line.ProductId]!.Value * q.Quantity;
            }
            items = (int)Math.Min(sum, int.MaxValue / 2);
        }
        var total = items + empty;
        if (total < 1 || total > 70_000) return (Res.Invalid("The package weight must be between 1 g and 70 kg.", "weight"), null);
        return (null, new ParcelSpec(l, w, h, total));
    }

    public (Res? Error, List<QtyLine>? Lines) PickLines(SellerOrder o, List<QtyLine>? requested)
    {
        var wanted = requested is { Count: > 0 } ? requested : o.Lines.Where(l => OrderSupport.Unshipped(l) > 0).Select(l => new QtyLine(l.Id, OrderSupport.Unshipped(l))).ToList();
        if (wanted.Count == 0) return (Res.Conflict("Everything in this order has already shipped."), null);
        if (wanted.GroupBy(x => x.LineId).Any(g => g.Count() > 1)) return (Res.Invalid("Each item can appear only once.", "lines"), null);
        foreach (var w in wanted)
        {
            var line = o.Lines.FirstOrDefault(l => l.Id == w.LineId);
            if (line is null) return (Res.Invalid("An item doesn't belong to this order.", "lines"), null);
            if (w.Quantity < 1 || w.Quantity > OrderSupport.Unshipped(line)) return (Res.Invalid($"You can ship at most {OrderSupport.Unshipped(line)} of \"{line.Title}\".", "quantity"), null);
        }
        return (null, wanted);
    }

    public Res? CheckShippable(SellerOrder o)
    {
        if (o.Status == OrderStatus.New) return Res.Conflict("Accept this order before buying a label.");
        if (o.Status == OrderStatus.OnHold) return Res.Conflict("This order is on hold. Remove the hold before buying a label.");
        if (!OrderStateMachine.CanShip(o.Status)) return Res.Conflict($"This order is {OrderStateMachine.Label(o.Status)} and can't be shipped.");
        if (o.CancelRequested) return Res.Conflict("The buyer asked to cancel this order. Approve or reject the request first.", "cancel_requested");
        return null;
    }

    public (Res? Error, CustomsSpec? Customs) BuildCustoms(SellerOrder o, List<QtyLine> lines, CustomsInput? c, string origin)
    {
        var type = (c?.ContentsType ?? "merchandise").ToLowerInvariant();
        if (!ContentsTypes.Contains(type)) return (Res.Invalid("Choose what the package contains.", "customs_contents"), null);
        var nd = (c?.NonDeliveryOption ?? "return").ToLowerInvariant();
        if (nd is not ("return" or "abandon")) return (Res.Invalid("Choose return or abandon if the parcel can't be delivered.", "customs_non_delivery"), null);
        static bool Hs(string? h) => h is null || System.Text.RegularExpressions.Regex.IsMatch(h, @"^\d{4}([. ]?\d{2}){0,3}$");
        if (!Hs(c?.HsCode)) return (Res.Invalid("The HS code looks wrong (4 to 10 digits).", "customs_hs"), null);
        var items = new List<CustomsItem>();
        foreach (var q in lines)
        {
            var line = o.Lines.First(l => l.Id == q.LineId);
            var hs = c?.LineHsCodes is { } m && m.TryGetValue(line.Id, out var x) ? x : c?.HsCode;
            if (!Hs(hs)) return (Res.Invalid("The HS code looks wrong (4 to 10 digits).", "customs_hs"), null);
            var value = line.UnitPriceCents * q.Quantity - OrderSupport.Prorate(line.DiscountCents, q.Quantity, line.Quantity);   // from the order snapshot, tax excluded
            items.Add(new CustomsItem(line.Title.Length > 100 ? line.Title[..100] : line.Title, q.Quantity, Math.Max(value, 0), o.Currency, 0, hs, origin));
        }
        return (null, new CustomsSpec(type, nd, items));
    }

    public async Task<(Res? Error, Context? Ctx)> ResolveAsync(SellerOrder o, RatesRequest req)
    {
        if (CheckShippable(o) is { } bad) return (bad, null);
        var (le, lines) = PickLines(o, req.Lines); if (le is not null) return (le, null);
        var from = await FromAddressAsync(o.SellerId, o.BrandId, req.FromAddressId);
        if (from is null) return (Res.Invalid(req.FromAddressId is null ? "Add a ship-from address in Settings > Shipping first." : "That ship-from address isn't available for this order.", "ship_from"), null);
        var (pe, parcel) = await ParcelAsync(o, lines!, req.Package); if (pe is not null) return (pe, null);
        var intl = !string.Equals(from.Country, o.ShipCountry, StringComparison.OrdinalIgnoreCase);
        CustomsSpec? customs = null;
        if (intl)
        {
            if (req.Customs is null) return (new(false, 422, "customs_required", "This order ships to another country. Add the customs details.", new { customsRequired = true }), null);
            var (ce, cs) = BuildCustoms(o, lines!, req.Customs, from.Country); if (ce is not null) return (ce, null);
            customs = cs;
        }
        return (null, new Context(from, from.ToShip(), ShippingSupport.BuyerAddress(o), parcel!, lines!, customs, intl));
    }

    // ---- address check + rates --------------------------------------------------------------------------------------------
    public async Task<(Res? Error, AddressCheck? Check)> CheckBuyerAddressAsync(SellerOrder o)
    {
        try { return (null, await provider.ValidateAddressAsync(ShippingSupport.BuyerAddress(o))); }
        catch (ShippingProviderException ex) { log.LogInformation("Address check failed ({Code}) for {Addr}", ex.Code, ShippingSupport.MaskAddress(ShippingSupport.BuyerAddress(o))); return (FromProvider(ex), null); }
    }

    public static List<QuotedRate> Rank(IEnumerable<RateOffer> rates, DateTime deliverBy)
    {
        var list = rates.Select(r => new QuotedRate(r.RateId, r.Carrier, r.Service, r.AmountCents, r.Currency, r.EstimatedDays, r.EstimatedDelivery,
            r.EstimatedDelivery is { } d && d.Date <= deliverBy.Date, false)).OrderBy(r => r.AmountCents).ThenBy(r => r.EstimatedDays ?? 99).ToList();
        var best = list.Where(r => r.MeetsDeliverBy).OrderBy(r => r.AmountCents).ThenBy(r => r.EstimatedDays ?? 99).FirstOrDefault();   // cheapest that arrives in time
        return list.Select(r => best is not null && r.RateId == best.RateId ? r with { Recommended = true } : r).ToList();
    }

    public async Task<Res> QuoteAsync(SellerOrder o, Guid actor, RatesRequest req)
    {
        var (err, ctx) = await ResolveAsync(o, req); if (err is not null) return err;
        var (ae, check) = await CheckBuyerAddressAsync(o); if (ae is not null) return ae;
        RateSet set;
        try { set = await provider.GetRatesAsync(new RateRequest(ctx!.FromAddr, ctx.ToAddr, ctx.Parcel, ctx.Customs, false)); }
        catch (ShippingProviderException ex) { return FromProvider(ex); }
        var rates = Rank(set.Rates, o.DeliverByAt);
        if (rates.Count == 0) return Res.Conflict("No carrier offered a rate for this package. Check the address and package size.", "no_rates");
        var now = time.UtcNow();
        var q = new ShippingQuote
        {
            SellerId = o.SellerId, OrderId = o.Id, CreatedBy = actor, CreatedAt = now, ExpiresAt = now.AddMinutes(opt.Value.QuoteMinutes), ProviderShipmentId = set.ProviderShipmentId,
            AddressStatus = check!.Status, RequestJson = JsonSerializer.Serialize(new { fromAddressId = ctx.From.Id, lines = ctx.Lines, parcel = ctx.Parcel, customs = ctx.Customs, international = ctx.International }, J),
            RatesJson = JsonSerializer.Serialize(rates, J)
        };
        db.ShippingQuotes.Add(q);
        await db.SaveChangesAsync();
        return Res.Success(new
        {
            quoteId = q.Id, expiresAt = q.ExpiresAt, addressStatus = check.Status, addressMessage = check.Message, international = ctx.International, deliverBy = o.DeliverByAt,
            parcel = ctx.Parcel, lines = ctx.Lines, rates
        });
    }

    // ---- spend limit ---------------------------------------------------------------------------------------------------------
    public static readonly LabelStatus[] Counting = [LabelStatus.Purchasing, LabelStatus.Active, LabelStatus.Voiding, LabelStatus.RefundFailed];

    public async Task<long> SpentThisMonthAsync(Guid sellerId)
    {
        var now = time.UtcNow(); var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return await db.ShippingLabels.Where(l => l.SellerId == sellerId && l.CreatedAt >= start && Counting.Contains(l.Status)).SumAsync(l => (long?)l.CostCents) ?? 0;
    }

    // ---- purchase ------------------------------------------------------------------------------------------------------------
    public object View(ShippingLabel l) => new
    {
        id = l.Id, orderId = l.OrderId, kind = l.Kind.ToString().ToLowerInvariant(), status = l.Status.ToString().ToLowerInvariant(), shipmentId = l.ShipmentId, returnId = l.ReturnId,
        carrier = l.Carrier, service = l.Service, trackingNumber = l.TrackingNumber, costCents = l.CostCents, currency = l.Currency, format = l.LabelFormat,
        purchasedAt = l.PurchasedAt, printedAt = l.PrintedAt, estimatedDelivery = l.EstimatedDelivery, refundStatus = l.RefundStatus, refundError = l.RefundError,
        hasFile = l.LabelKey != null, canVoid = l.Status is LabelStatus.Active or LabelStatus.RefundFailed,
        fileUrl = l.LabelKey != null ? $"/api/v1/seller/shipping/labels/{l.Id}/file" : null
    };

    /// <summary>The provider issued the label: store the file, mark it Active and book the debit durably (the money is spent). Shared by a normal purchase and by recovery of a purchase that crashed midway.</summary>
    public async Task RecordPurchaseAsync(ShippingLabel label, PurchasedLabel pl, string orderNumber)
    {
        var fileKey = ShippingSupport.StorageKey();
        using (var ms = new MemoryStream(pl.Bytes)) await storage.SaveAsync(fileKey, ms);
        var now = time.UtcNow();
        label.Status = LabelStatus.Active; label.ProviderTransactionId = pl.TransactionId; label.Carrier = ShippingSupport.OrderCarrier(pl.Carrier); label.Service = pl.Service ?? label.Service;
        label.TrackingNumber = Orders.Tracking.Normalize(pl.TrackingNumber); label.LabelKey = fileKey; label.LabelContentType = pl.ContentType; label.PurchasedAt = now;
        label.EstimatedDelivery = pl.EstimatedDelivery ?? label.EstimatedDelivery;
        // the debit is recorded NOW and durably, before the order flow runs (a failure there voids the label and credits it back)
        await ledger.PostAsync(label.SellerId, label.Currency, "shipping_label", $"shipping_label:{label.Id}", $"Shipping label {label.Carrier} for {orderNumber}",
            [new(LedgerAccount.Available, -label.CostCents, "shipping_label"), new(LedgerAccount.Platform, label.CostCents, "shipping_label")], orderId: label.OrderId, sourceRef: label.ProviderTransactionId, actor: label.PurchasedBy);
        if (!await ledger.SaveAsync()) throw new InvalidOperationException("The label could not be recorded.");
    }

    public async Task<Res> BuyAsync(SellerOrder o, Guid actor, BuyRequest req)
    {
        if (req.QuoteId is not { } qid || string.IsNullOrWhiteSpace(req.RateId)) return Res.Invalid("Choose a rate first.", "rate");
        var quote = await db.ShippingQuotes.FirstOrDefaultAsync(q => q.Id == qid && q.SellerId == o.SellerId && q.OrderId == o.Id);
        if (quote is null) return Res.Invalid("Get rates first.", "quote");
        var rates = JsonSerializer.Deserialize<List<QuotedRate>>(quote.RatesJson, J)!;
        var rate = rates.FirstOrDefault(r => r.RateId == req.RateId);
        if (rate is null) return Res.Invalid("That rate isn't part of this quote.", "rate");
        var qrk = $"{quote.Id:N}:{rate.RateId}";
        var key = $"{o.SellerId:N}:" + (string.IsNullOrWhiteSpace(req.IdempotencyKey) ? ShippingSupport.Hash($"{o.Id}|{qrk}")[..32] : req.IdempotencyKey.Trim());
        if (key.Length > 160) return Res.Invalid("The idempotency key is too long.", "idempotency_key");

        // replay of an earlier request with the same key / the same quoted rate: answer with what that purchase became, never buy again
        var existing = await db.ShippingLabels.FirstOrDefaultAsync(l => l.IdempotencyKey == key || l.QuoteRateKey == qrk);
        if (existing is not null) return Replay(existing);

        if (CheckShippable(o) is { } notShippable) return notShippable;
        var rq = JsonSerializer.Deserialize<StoredRequest>(quote.RequestJson, J)!;
        var (le, lines) = PickLines(o, rq.Lines); if (le is not null) return le;
        if (time.UtcNow() > quote.ExpiresAt) return new(false, 409, "rate_expired", "These rates expired. Get new rates and try again.", new { requote = true });
        string? overrideReason = null;
        if (quote.AddressStatus != "valid")
        {
            overrideReason = req.ShipAnyway?.Reason?.Trim();
            if (string.IsNullOrEmpty(overrideReason) || overrideReason.Length < 5)
                return new(false, 422, "address_unverified", "The buyer's address could not be verified. Check it, or ship anyway and say why.", new { addressStatus = quote.AddressStatus });
        }
        var format = (await db.SellerShippingSettings.Where(s => s.SellerId == o.SellerId && (s.ScopeKey == o.BrandId || s.ScopeKey == Guid.Empty) && s.LabelFormat != null)
            .OrderByDescending(s => s.ScopeKey).Select(s => s.LabelFormat).FirstOrDefaultAsync()) ?? opt.Value.LabelFormat;

        var label = new ShippingLabel
        {
            SellerId = o.SellerId, BrandId = o.BrandId, OrderId = o.Id, Kind = LabelKind.Outbound, Status = LabelStatus.Purchasing, Provider = provider.Name, ProviderShipmentId = quote.ProviderShipmentId,
            ProviderRateId = rate.RateId, QuoteId = quote.Id, IdempotencyKey = key, QuoteRateKey = qrk, Carrier = ShippingSupport.OrderCarrier(rate.Carrier), Service = rate.Service,
            CostCents = rate.AmountCents, Currency = rate.Currency, LabelFormat = format, PurchasedBy = actor, CreatedAt = time.UtcNow(), CustomsJson = rq.Customs is null ? null : JsonSerializer.Serialize(rq.Customs, J),
            AddressOverrideReason = overrideReason, EstimatedDelivery = rate.EstimatedDelivery
        };
        var (rerr, dup) = await ReserveAsync(label);
        if (rerr is not null) return rerr;
        if (dup is not null) return Replay(dup);

        PurchasedLabel pl;
        try { pl = await provider.PurchaseAsync(new PurchaseRequest(rate.RateId, quote.ProviderShipmentId ?? "", format, key)); }
        catch (ShippingProviderException ex)
        {
            await FailAsync(label, ex.Code);
            return FromProvider(ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { await FailAsync(label, "unexpected"); log.LogError(ex, "Label purchase failed"); return Res.Fail(502, "provider_error", "The shipping provider could not complete the request. Try again in a moment."); }

        await RecordPurchaseAsync(label, pl, o.Number);

        var ship = await actions.ShipAsync(o, actor, new ShipRequest(null, lines, null, null, null, null), trust:
            new ShipTrust(label.Carrier, label.TrackingNumber!, label.Service, s => { s.LabelId = label.Id; label.ShipmentId = s.Id; label.ActiveShipmentKey = s.Id; }));
        if (!ship.Ok)
        {
            db.ChangeTracker.Clear();
            var fresh = await db.ShippingLabels.FirstAsync(l => l.Id == label.Id);
            await RequestRefundAsync(fresh, "The order could not be shipped: " + ship.Code);
            return new Res(false, ship.Status, ship.Code, ship.Message + " The label was purchased and is being voided automatically; the cost will be credited back once the carrier confirms.", ship.Data);
        }
        await audit.WriteAsync("shipping.label_bought", actor, null, $"order={o.Number} carrier={label.Carrier} cost={label.CostCents}{label.Currency}" + (overrideReason is null ? "" : " address_override=true"));
        if (overrideReason is not null) await audit.WriteAsync("shipping.address_override", actor, null, $"order={o.Number} status={quote.AddressStatus} reason={overrideReason}");
        return Res.Success(new { label = View(label), replayed = false });
    }

    private record StoredRequest(Guid FromAddressId, List<QtyLine> Lines, ParcelSpec Parcel, CustomsSpec? Customs, bool International);

    public Res Replay(ShippingLabel l) => l.Status switch
    {
        LabelStatus.Purchasing => Res.Conflict("This label is being purchased right now. Try again in a moment.", "purchase_in_progress"),
        LabelStatus.Failed => Res.Conflict("That purchase failed. Get new rates and try again.", "purchase_failed"),
        _ => Res.Success(new { label = View(l), replayed = true })
    };

    /// <summary>Inserts the Purchasing row. The unique keys decide races: the loser gets the winner's row back. The spend limit is checked in the same serializable transaction.</summary>
    public async Task<(Res? Error, ShippingLabel? Existing)> ReserveAsync(ShippingLabel label)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                var limit = opt.Value.SpendLimitCentsPerMonth;
                if (limit > 0)
                {
                    var spent = await SpentThisMonthAsync(label.SellerId);
                    if (spent + label.CostCents > limit)
                    {
                        await tx.RollbackAsync();
                        return (new(false, 422, "spend_limit", $"This label would take you over your monthly label limit ({Money(limit, label.Currency)}). {Money(Math.Max(limit - spent, 0), label.Currency)} left this month.",
                            new { limitCents = limit, spentCents = spent }), null);
                    }
                }
                // one purchase at a time per order: a second buy (another rate, another quote) must not reach the provider while the first is in flight or not yet shipped
                if (label.Kind == LabelKind.Outbound && await db.ShippingLabels.AnyAsync(l => l.OrderId == label.OrderId && l.Kind == LabelKind.Outbound
                        && (l.Status == LabelStatus.Purchasing || (l.Status == LabelStatus.Active && l.ShipmentId == null))))
                {
                    await tx.RollbackAsync();
                    return (Res.Conflict("A label for this order is being purchased right now. Wait for it to finish.", "purchase_in_progress"), null);
                }
                db.ShippingLabels.Add(label);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return (null, null);
            }
            catch (DbUpdateException ex)
            {
                await tx.RollbackAsync(); db.ChangeTracker.Clear();
                if (DbErrors.IsUnique(ex))
                {
                    var won = await db.ShippingLabels.FirstOrDefaultAsync(l => l.IdempotencyKey == label.IdempotencyKey || (label.QuoteRateKey != null && l.QuoteRateKey == label.QuoteRateKey));
                    if (won is not null) return (null, won);
                }
                if (attempt == 1) return (Res.Conflict("Another purchase was in progress. Try again.", "concurrent_update"), null);
            }
        }
        return (Res.Conflict("Another purchase was in progress. Try again.", "concurrent_update"), null);
    }

    public async Task FailAsync(ShippingLabel l, string why)
    {
        l.Status = LabelStatus.Failed; l.Error = why; l.QuoteRateKey = null; l.IdempotencyKey = $"{l.IdempotencyKey}:failed:{l.Id:N}";
        if (l.IdempotencyKey.Length > 160) l.IdempotencyKey = l.IdempotencyKey[^160..];
        await db.SaveChangesAsync();
    }

    // ---- void / refund ---------------------------------------------------------------------------------------------------------
    /// <summary>Asks the provider to refund the label. Money only moves when the provider CONFIRMS (immediately or on a later poll); until then the label is Voiding.</summary>
    public async Task<Res> RequestRefundAsync(ShippingLabel l, string reason, Guid? actor = null)
    {
        if (l.ProviderTransactionId is null) return Res.Conflict("This label was never issued.", "not_issued");
        RefundResult r;
        try { r = await provider.RequestRefundAsync(l.ProviderTransactionId); }
        catch (ShippingProviderException ex)
        {
            l.RefundAttempts++; l.RefundError = ex.Code; l.NextRefundCheckAt = time.UtcNow().AddMinutes(opt.Value.RefundCheckMinutes);
            if (l.Status == LabelStatus.Active) { l.Status = LabelStatus.Voiding; l.RefundStatus = "pending"; l.RefundRequestedAt = time.UtcNow(); }
            await db.SaveChangesAsync();
            return FromProvider(ex);
        }
        l.RefundAttempts++; l.ProviderRefundId = r.RefundId; l.RefundRequestedAt ??= time.UtcNow(); l.RefundError = null;
        if (r.Status == "success") return await CompleteRefundAsync(l, actor, reason);
        if (r.Status == "error")
        {
            l.Status = LabelStatus.RefundFailed; l.RefundStatus = "failed"; l.RefundError = r.Error ?? "refused"; l.NextRefundCheckAt = null;
            await db.SaveChangesAsync();
            return Res.Conflict("The carrier refused to refund this label (it may already be in use).", "refund_refused");
        }
        l.Status = LabelStatus.Voiding; l.RefundStatus = "pending"; l.NextRefundCheckAt = time.UtcNow().AddMinutes(opt.Value.RefundCheckMinutes);
        await db.SaveChangesAsync();
        return Res.Success(new { label = View(l), pending = true });
    }

    /// <summary>The provider confirmed the refund: credit the ledger (idempotent on the label id), release the shipment back to the order, stop tracking.</summary>
    public async Task<Res> CompleteRefundAsync(ShippingLabel l, Guid? actor, string text)
    {
        if (l.Status == LabelStatus.Voided) return Res.Success(new { label = View(l) });
        var now = time.UtcNow(); var shipmentId = l.ShipmentId; var returnShipmentId = l.ReturnShipmentId;
        l.Status = LabelStatus.Voided; l.RefundStatus = "success"; l.RefundedAt = now; l.RefundError = null; l.NextRefundCheckAt = null; l.ActiveShipmentKey = null; l.QuoteRateKey = null;
        l.IdempotencyKey = ($"{l.IdempotencyKey}:voided:{l.Id:N}") is { Length: > 160 } k ? k[^160..] : $"{l.IdempotencyKey}:voided:{l.Id:N}";   // a new purchase may reuse the quote
        var order = await actions.Full(db.Orders).FirstOrDefaultAsync(o => o.Id == l.OrderId);
        await ledger.PostAsync(l.SellerId, l.Currency, "shipping_label_refund", $"shipping_label_refund:{l.Id}", $"Shipping label refunded ({l.Carrier})",
            [new(LedgerAccount.Available, l.CostCents, "shipping_label"), new(LedgerAccount.Platform, -l.CostCents, "shipping_label")], orderId: l.OrderId, sourceRef: l.ProviderRefundId, actor: actor);
        if (returnShipmentId is { } rs)
            await db.ReturnShipments.Where(x => x.Id == rs).ExecuteUpdateAsync(u => u.SetProperty(x => x.TrackingStoppedAt, now));
        if (order is not null && l.Kind == LabelKind.Return) db.AddEvent(order.Id, "return.label_voided", actor is null ? Party.System : Party.Seller, actor, "Return label voided and refunded", now);
        if (order is not null && l.Kind == LabelKind.Outbound && shipmentId is { } sid && order.Shipments.Any(s => s.Id == sid))
        {
            var res = await actions.ReopenShipmentAsync(order, sid, actor, text);
            if (res.Ok) return Res.Success(new { label = View(l) });
            db.AddEvent(order.Id, "label.voided", Party.System, null, "Label refunded. " + res.Message, now);   // the order moved on: the shipment stays, the money still comes back
        }
        if (!await ledger.SaveAsync()) { db.ChangeTracker.Clear(); return Res.Success(new { duplicate = true }); }
        return Res.Success(new { label = View(l) });
    }

    public async Task<Res> VoidAsync(ShippingLabel l, Guid actor, string? reason)
    {
        if (l.Status is not (LabelStatus.Active or LabelStatus.RefundFailed)) return Res.Conflict(l.Status == LabelStatus.Voiding ? "A refund for this label was already requested." : "This label can't be voided.", "invalid_state");
        // a parcel the carrier already scanned can't be refunded
        var status = l.ShipmentId is { } sid ? await db.Shipments.Where(s => s.Id == sid).Select(s => s.TrackingStatus).FirstOrDefaultAsync()
            : l.ReturnShipmentId is { } rid ? await db.ReturnShipments.Where(s => s.Id == rid).Select(s => s.TrackingStatus).FirstOrDefaultAsync() : null;
        if (status is not (null or TrackingStatuses.PreTransit or TrackingStatuses.Unknown)) return Res.Conflict("The carrier already has this parcel, so the label can't be voided.", "label_in_use");
        var r = await RequestRefundAsync(l, "Label voided" + (string.IsNullOrWhiteSpace(reason) ? "" : ": " + reason.Trim()), actor);
        if (r.Ok || r.Code == "refund_refused") await audit.WriteAsync("shipping.label_void", actor, null, $"label={l.Id} result={(r.Ok ? "ok" : "refused")}" + (string.IsNullOrWhiteSpace(reason) ? "" : $" reason={reason.Trim()}"));
        return r;
    }

    // ---- bulk ------------------------------------------------------------------------------------------------------------------
    public async Task<Res> BulkAsync(AccountAccess a, BulkRequest req)
    {
        var ids = (req.OrderIds ?? []).Distinct().ToList();
        if (ids.Count == 0) return Res.Invalid("Select at least one order.", "order_ids");
        if (ids.Count > opt.Value.MaxBulk) return Res.Invalid($"You can buy at most {opt.Value.MaxBulk} labels at once.", "order_ids");
        var strategy = (req.Strategy ?? "recommended").ToLowerInvariant();
        if (strategy is not ("recommended" or "cheapest")) return Res.Invalid("Use recommended or cheapest.", "strategy");
        var batchKey = string.IsNullOrWhiteSpace(req.IdempotencyKey) ? Guid.NewGuid().ToString("N") : req.IdempotencyKey.Trim();
        var results = new List<object>();
        foreach (var id in ids)
        {
            db.ChangeTracker.Clear();
            var o = await actions.Full(db.OrdersFor(a)).FirstOrDefaultAsync(x => x.Id == id);
            if (o is null || !a.CanWorkOnBrand(o.BrandId, true)) { results.Add(new { orderId = id, ok = false, code = "not_found", message = "Order not found." }); continue; }
            var q = await QuoteAsync(o, a.UserId, new RatesRequest(req.FromAddressId, new PackageInput(req.PackageId, null, null, null, req.WeightGrams), null, null));
            if (!q.Ok) { results.Add(new { orderId = id, number = o.Number, ok = false, code = q.Code, message = q.Message }); continue; }
            var json = JsonSerializer.SerializeToElement(q.Data!, J);
            var rates = json.GetProperty("rates").EnumerateArray().ToList();
            var pick = (strategy == "recommended" ? rates.FirstOrDefault(r => r.GetProperty("recommended").GetBoolean()) : default);
            if (pick.ValueKind == JsonValueKind.Undefined) pick = rates[0];
            var buy = await BuyAsync(o, a.UserId, new BuyRequest(json.GetProperty("quoteId").GetGuid(), pick.GetProperty("rateId").GetString(), $"bulk:{batchKey}:{id:N}",
                null, null));   // unverified addresses are never bulk-overridden: the seller reviews those orders one by one
            results.Add(new { orderId = id, number = o.Number, ok = buy.Ok, code = buy.Code, message = buy.Message, data = buy.Ok ? buy.Data : null });
        }
        return Res.Success(new { results, bought = results.Count(r => (bool)r.GetType().GetProperty("ok")!.GetValue(r)!), total = results.Count });
    }
}
