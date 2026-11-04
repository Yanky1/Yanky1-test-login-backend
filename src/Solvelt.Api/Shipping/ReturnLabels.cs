using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using Solvelt.Api.Payments;
using Solvelt.Api.Services;

namespace Solvelt.Api.Shipping;

/// <summary>
/// Prepaid return labels: buyer's address to the seller's return address, bought when a return is approved. The seller pays (a debit in the ledger like
/// outbound labels); the marketplace receives a short-lived signed link in the <c>return.label_created</c> outbox event and emails the buyer.
/// </summary>
public class ReturnLabelService(AppDbContext db, IShippingProvider provider, IFileStorage storage, LedgerService ledger, LabelService labels, Outbox outbox, LabelLinks links,
    IOptions<ShippingOptions> opt, TimeProvider time, IAuditLog audit, ShipmentTracker tracker, ILogger<ReturnLabelService> log)
{
    public async Task<ShipFromAddress?> ReturnAddressAsync(SellerOrder o)
    {
        var id = await db.SellerShippingSettings.Where(s => s.SellerId == o.SellerId && (s.ScopeKey == o.BrandId || s.ScopeKey == Guid.Empty) && s.ReturnAddressId != null)
            .OrderByDescending(s => s.ScopeKey).Select(s => s.ReturnAddressId).FirstOrDefaultAsync();
        if (id is { } i && await db.ShipFromAddresses.FirstOrDefaultAsync(a => a.Id == i && a.SellerId == o.SellerId && !a.Archived) is { } a) return a;
        return await labels.FromAddressAsync(o.SellerId, o.BrandId, null);
    }

    public async Task<Res> BuyAsync(SellerOrder o, Guid returnId, Guid actor, ReturnLabelRequest req)
    {
        var ret = await db.Returns.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == returnId && r.OrderId == o.Id);
        if (ret is null) return Res.Fail(404, "not_found", "Return not found.");
        if (ret.Status != ReturnStatus.Approved || o.Status != OrderStatus.ReturnApproved) return Res.Conflict("A return label can only be sent for an approved return.");
        if (await db.ShippingLabels.AnyAsync(l => l.ReturnId == returnId && l.Kind == LabelKind.Return && LabelService.Counting.Contains(l.Status)))
            return Res.Conflict("A return label was already created for this return.", "label_exists");
        var to = await ReturnAddressAsync(o);
        if (to is null) return Res.Invalid("Add a return address in Settings > Shipping first.", "return_address");
        var lines = ret.Lines.Select(l => new QtyLine(l.OrderLineId, l.Quantity)).ToList();
        var (pe, parcel) = await labels.ParcelAsync(o, lines, req.Package);
        if (pe is not null) return pe;
        var buyer = ShippingSupport.BuyerAddress(o);
        var intl = !string.Equals(buyer.Country, to.Country, StringComparison.OrdinalIgnoreCase);
        CustomsSpec? customs = null;
        if (intl)
        {
            if (req.Customs is null) return new(false, 422, "customs_required", "This return crosses a border. Add the customs details.", new { customsRequired = true });
            var (ce, cs) = labels.BuildCustoms(o, lines, req.Customs with { ContentsType = req.Customs.ContentsType ?? "returned_goods" }, buyer.Country);
            if (ce is not null) return ce;
            customs = cs;
        }
        RateSet set;
        try { set = await provider.GetRatesAsync(new RateRequest(buyer, to.ToShip(), parcel!, customs, true)); }
        catch (ShippingProviderException ex) { return LabelService.FromProvider(ex); }
        var pool = set.Rates.Where(r => (req.Carrier is null || r.Carrier.Equals(req.Carrier, StringComparison.OrdinalIgnoreCase)) && (req.Service is null || r.Service.Equals(req.Service, StringComparison.OrdinalIgnoreCase))).ToList();
        var rate = pool.OrderBy(r => r.AmountCents).ThenBy(r => r.EstimatedDays ?? 99).FirstOrDefault();
        if (rate is null) return Res.Conflict("No carrier offered a return rate for this package.", "no_rates");

        var key = $"{o.SellerId:N}:ret:{returnId:N}:" + (string.IsNullOrWhiteSpace(req.IdempotencyKey) ? "default" : req.IdempotencyKey.Trim());
        var label = new ShippingLabel
        {
            SellerId = o.SellerId, BrandId = o.BrandId, OrderId = o.Id, Kind = LabelKind.Return, ReturnId = returnId, Status = LabelStatus.Purchasing, Provider = provider.Name,
            ProviderShipmentId = set.ProviderShipmentId, ProviderRateId = rate.RateId, IdempotencyKey = key, QuoteRateKey = $"return:{returnId:N}", Carrier = ShippingSupport.OrderCarrier(rate.Carrier),
            Service = rate.Service, CostCents = rate.AmountCents, Currency = rate.Currency, LabelFormat = opt.Value.LabelFormat, PurchasedBy = actor, CreatedAt = time.UtcNow(),
            CustomsJson = customs is null ? null : JsonSerializer.Serialize(customs, OrderJson.Opts), EstimatedDelivery = rate.EstimatedDelivery
        };
        var (rerr, dup) = await labels.ReserveAsync(label);
        if (rerr is not null) return rerr;
        if (dup is not null) return labels.Replay(dup);

        PurchasedLabel pl;
        try { pl = await provider.PurchaseAsync(new PurchaseRequest(rate.RateId, set.ProviderShipmentId, label.LabelFormat, key)); }
        catch (ShippingProviderException ex) { await labels.FailAsync(label, ex.Code); return LabelService.FromProvider(ex); }
        catch (Exception ex) when (ex is not OperationCanceledException) { await labels.FailAsync(label, "unexpected"); log.LogError(ex, "Return label purchase failed"); return Res.Fail(502, "provider_error", "The shipping provider could not complete the request. Try again in a moment."); }

        var fileKey = ShippingSupport.StorageKey();
        using (var ms = new MemoryStream(pl.Bytes)) await storage.SaveAsync(fileKey, ms);
        var now = time.UtcNow();
        var tn = Orders.Tracking.Normalize(pl.TrackingNumber);
        var rs = new ReturnShipment { SellerId = o.SellerId, OrderId = o.Id, ReturnId = returnId, Carrier = ShippingSupport.OrderCarrier(pl.Carrier), Service = pl.Service ?? rate.Service, TrackingNumber = tn, LabelId = label.Id, CreatedAt = now };
        db.ReturnShipments.Add(rs);
        label.Status = LabelStatus.Active; label.ProviderTransactionId = pl.TransactionId; label.Carrier = rs.Carrier; label.Service = rs.Service; label.TrackingNumber = tn; label.LabelKey = fileKey;
        label.LabelContentType = pl.ContentType; label.PurchasedAt = now; label.ReturnShipmentId = rs.Id; label.ActiveShipmentKey = rs.Id; label.EstimatedDelivery = pl.EstimatedDelivery ?? label.EstimatedDelivery;
        await ledger.PostAsync(o.SellerId, label.Currency, "shipping_label", $"shipping_label:{label.Id}", $"Return label {label.Carrier} for {o.Number}",
            [new(LedgerAccount.Available, -label.CostCents, "shipping_label"), new(LedgerAccount.Platform, label.CostCents, "shipping_label")], orderId: o.Id, sourceRef: label.ProviderTransactionId, actor: actor);
        var (url, exp) = links.Create(label.Id);
        db.AddEvent(o.Id, "return.label_created", Party.Seller, actor, $"Prepaid return label {rs.Carrier} {tn}", now);
        outbox.Enqueue("return.label_created", o.SellerId, o.MarketplaceOrderId, new
        {
            order = new { sellerOrderId = o.Id, sellerOrderNumber = o.Number, marketplaceOrderId = o.MarketplaceOrderId }, returnId = ret.MarketplaceReturnId, labelId = label.Id,
            labelUrl = url, labelUrlExpiresAt = exp, trackingNumber = tn, carrier = rs.Carrier, service = rs.Service, trackingUrl = Orders.Tracking.Url(rs.Carrier, tn), labelFormat = label.LabelFormat
        });
        if (!await ledger.SaveAsync()) throw new InvalidOperationException("The return label could not be recorded.");
        await audit.WriteAsync("shipping.return_label_bought", actor, null, $"order={o.Number} carrier={rs.Carrier} cost={label.CostCents}{label.Currency}");
        await tracker.RegisterAsync(rs);
        return Res.Success(new { label = labels.View(label), labelUrl = url, labelUrlExpiresAt = exp });
    }
}

/// <summary>Housekeeping the webhooks can't do: register tracking that failed earlier, poll silent shipments (24 h), stop tracking (delivered / 60 days), confirm label refunds, void orphaned labels.</summary>
public record MaintenanceSummary(int Registered, int Polled, int Stopped, int RefundsChecked, int RefundsConfirmed, int OrphansVoided, int PurchasesRecovered = 0);

/// <summary>Test seam: lets a test hold two job instances between "selected the shipment" and "claimed it" so the lease is exercised deterministically.</summary>
public class ShippingTestHooks { public Func<Guid, Task>? AfterPollCandidate { get; set; } }

public class ShippingMaintenance(AppDbContext db, IShippingProvider provider, TrackingService tracking, ShipmentTracker tracker, LabelService labels, IOptions<ShippingOptions> opt, TimeProvider time, ShippingTestHooks hooks, ILogger<ShippingMaintenance> log)
{
    public async Task<MaintenanceSummary> RunAsync(CancellationToken ct = default)
    {
        var o = opt.Value; var now = time.UtcNow();
        int registered = 0, polled = 0, stopped = 0, checks = 0, confirmed = 0, orphans = 0, recovered = 0;

        // 1. stop tracking after the cut-off
        var cutoff = now.AddDays(-o.TrackingStopDays);
        stopped += await db.Shipments.Where(s => s.TrackingStoppedAt == null && s.DeliveredAt == null && s.ShippedAt < cutoff).ExecuteUpdateAsync(u => u.SetProperty(s => s.TrackingStoppedAt, now), ct);
        stopped += await db.ReturnShipments.Where(s => s.TrackingStoppedAt == null && s.DeliveredAt == null && s.CreatedAt < cutoff).ExecuteUpdateAsync(u => u.SetProperty(s => s.TrackingStoppedAt, now), ct);

        // 2. registrations that failed earlier
        foreach (var s in await db.Shipments.Where(s => s.Method == "carrier" && s.TrackingRegisteredAt == null && s.TrackingStoppedAt == null && s.DeliveredAt == null).OrderBy(s => s.CreatedAt).Take(o.PollBatch).ToListAsync(ct))
        { await tracker.RegisterAsync(s); if (s.TrackingRegisteredAt != null) registered++; }

        // 3. poll shipments with no word for PollAfterHours (a per-row lease makes this safe with several instances)
        var due = now.AddHours(-o.TrackingPollAfterHours);
        var ids = await db.Shipments.Where(s => s.Method == "carrier" && s.TrackingStoppedAt == null && s.DeliveredAt == null && (s.LastTrackingAt ?? s.ShippedAt) < due && (s.PollLockedUntil == null || s.PollLockedUntil < now))
            .OrderBy(s => s.LastTrackingAt ?? s.ShippedAt).Select(s => s.Id).Take(o.PollBatch).ToListAsync(ct);
        foreach (var id in ids)
        {
            if (hooks.AfterPollCandidate is { } h) await h(id);
            if (await PollAsync(id, false, due, ct)) polled++;
        }
        var rids = await db.ReturnShipments.Where(s => s.TrackingStoppedAt == null && s.DeliveredAt == null && (s.LastTrackingAt ?? s.CreatedAt) < due && (s.PollLockedUntil == null || s.PollLockedUntil < now))
            .Select(s => s.Id).Take(o.PollBatch).ToListAsync(ct);
        foreach (var id in rids)
            if (await PollAsync(id, true, due, ct)) polled++;

        // 4. label refunds waiting for the provider
        var refundIds = await db.ShippingLabels.Where(l => l.Status == LabelStatus.Voiding && l.NextRefundCheckAt != null && l.NextRefundCheckAt <= now).Select(l => l.Id).Take(o.PollBatch).ToListAsync(ct);
        foreach (var id in refundIds)
        {
            var next = now.AddMinutes(o.RefundCheckMinutes);
            if (await db.ShippingLabels.Where(l => l.Id == id && l.Status == LabelStatus.Voiding && l.NextRefundCheckAt <= now).ExecuteUpdateAsync(u => u.SetProperty(l => l.NextRefundCheckAt, next), ct) != 1) continue;   // another instance has it
            db.ChangeTracker.Clear();
            var l = await db.ShippingLabels.FirstAsync(x => x.Id == id, ct);
            checks++;
            try
            {
                var r = l.ProviderRefundId is null ? null : await provider.GetRefundAsync(l.ProviderRefundId, ct);
                if (r is null) { await labels.RequestRefundAsync(l, "Label refund retried"); }
                else if (r.Status == "success") { if ((await labels.CompleteRefundAsync(l, null, "Label refunded")).Ok) confirmed++; }
                else if (r.Status == "error") { l.Status = LabelStatus.RefundFailed; l.RefundStatus = "failed"; l.RefundError = r.Error ?? "refused"; l.NextRefundCheckAt = null; await db.SaveChangesAsync(ct); }
                else if (l.RefundRequestedAt is { } at && at < now.AddDays(-14)) { l.Status = LabelStatus.RefundFailed; l.RefundStatus = "failed"; l.RefundError = "unconfirmed after 14 days"; l.NextRefundCheckAt = null; await db.SaveChangesAsync(ct); }
            }
            catch (ShippingProviderException ex) { log.LogInformation("Refund check deferred ({Code})", ex.Code); }
        }

        // 5. labels that were bought but never attached to a shipment (crash or failure after the purchase): void them
        var orphanIds = await db.ShippingLabels.Where(l => l.Status == LabelStatus.Active && l.Kind == LabelKind.Outbound && l.ShipmentId == null && l.PurchasedAt < now.AddMinutes(-5)).Select(l => l.Id).Take(o.PollBatch).ToListAsync(ct);
        foreach (var id in orphanIds)
        {
            db.ChangeTracker.Clear();
            var l = await db.ShippingLabels.FirstAsync(x => x.Id == id, ct);
            if (l.Status != LabelStatus.Active) continue;
            await labels.RequestRefundAsync(l, "Label was never attached to a shipment"); orphans++;
        }

        // 6. purchases stuck in "Purchasing" (crash between the provider call and the save): ask the provider by idempotency key. Found: record it (the orphan sweep voids it if it never ships).
        //    Not found: the provider never charged, so fail the row (it stops counting against the spend limit).
        var stuckIds = await db.ShippingLabels.Where(l => l.Status == LabelStatus.Purchasing && l.CreatedAt < now.AddMinutes(-o.PurchaseStuckMinutes)).Select(l => l.Id).Take(o.PollBatch).ToListAsync(ct);
        foreach (var id in stuckIds)
        {
            db.ChangeTracker.Clear();
            var l = await db.ShippingLabels.FirstAsync(x => x.Id == id, ct);
            if (l.Status != LabelStatus.Purchasing) continue;
            try
            {
                var pl = await provider.FindPurchaseAsync(l.IdempotencyKey, l.LabelFormat, ct);
                if (pl is null) { l.Status = LabelStatus.Failed; l.RefundError = "abandoned"; await db.SaveChangesAsync(ct); }
                else
                {
                    var number = await db.Orders.Where(x => x.Id == l.OrderId).Select(x => x.Number).FirstOrDefaultAsync(ct) ?? "";
                    await labels.RecordPurchaseAsync(l, pl, number); recovered++;
                }
            }
            catch (ShippingProviderException ex) { log.LogInformation("Stuck purchase check deferred ({Code})", ex.Code); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Could not recover stuck label purchase {LabelId}", id); }
        }
        return new(registered, polled, stopped, checks, confirmed, orphans, recovered);
    }

    /// <summary>Claims the parcel with ONE atomic UPDATE that re-checks both "not leased" and "still due": an instance that selected it before another one polled it (and released its lease) must not poll it again.</summary>
    private async Task<bool> PollAsync(Guid id, bool isReturn, DateTime due, CancellationToken ct)
    {
        var now = time.UtcNow(); var until = now.AddMinutes(opt.Value.PollLeaseMinutes);
        var claimed = isReturn
            ? await db.ReturnShipments.Where(s => s.Id == id && (s.PollLockedUntil == null || s.PollLockedUntil < now) && (s.LastTrackingAt ?? s.CreatedAt) < due).ExecuteUpdateAsync(u => u.SetProperty(s => s.PollLockedUntil, until), ct)
            : await db.Shipments.Where(s => s.Id == id && (s.PollLockedUntil == null || s.PollLockedUntil < now) && (s.LastTrackingAt ?? s.ShippedAt) < due).ExecuteUpdateAsync(u => u.SetProperty(s => s.PollLockedUntil, until), ct);
        if (claimed != 1) return false;
        db.ChangeTracker.Clear();
        string carrier, number;
        if (isReturn) { var r = await db.ReturnShipments.AsNoTracking().FirstAsync(s => s.Id == id, ct); (carrier, number) = (r.Carrier, r.TrackingNumber); }
        else { var s = await db.Shipments.AsNoTracking().FirstAsync(x => x.Id == id, ct); (carrier, number) = (s.Carrier, s.TrackingNumber); }
        try
        {
            var snap = await provider.GetTrackingAsync(carrier, number, ct);
            if (snap is not null) await tracking.ApplyAsync(number, snap, "poll", id);
            else await Touch(id, isReturn, now, ct);   // nothing to learn (unsupported carrier / unknown parcel): ask again after the next interval
        }
        catch (ShippingProviderException ex) { log.LogInformation("Tracking poll deferred ({Code})", ex.Code); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Tracking poll failed for parcel {ParcelId}; continuing with the next one", id); }   // one bad parcel never stops the run
        finally
        {
            db.ChangeTracker.Clear();
            if (isReturn) await db.ReturnShipments.Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.PollLockedUntil, (DateTime?)null), CancellationToken.None);
            else await db.Shipments.Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.PollLockedUntil, (DateTime?)null), CancellationToken.None);
        }
        return true;
    }

    private async Task Touch(Guid id, bool isReturn, DateTime now, CancellationToken ct)
    {
        if (isReturn) await db.ReturnShipments.Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.LastTrackingAt, now), ct);
        else await db.Shipments.Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.LastTrackingAt, now), ct);
    }
}

public class ShippingWorker(IServiceScopeFactory scopes, IOptions<ShippingOptions> opt, ILogger<ShippingWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var rec = scope.ServiceProvider.GetRequiredService<Solvelt.Api.Admin.JobRecorder>();
                await rec.RunAsync("shipping-maintenance", async () => await scope.ServiceProvider.GetRequiredService<ShippingMaintenance>().RunAsync(stop));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Shipping background job failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(opt.Value.JobIntervalSeconds), stop); } catch (OperationCanceledException) { }
        }
    }
}
