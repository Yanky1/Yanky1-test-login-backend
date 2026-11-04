using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Products;
using Solvelt.Api.Services;
using Solvelt.Api.Shipping;

namespace Solvelt.Api.Orders;

public record Res(bool Ok, int Status = 200, string Code = "ok", string Message = "", object? Data = null)
{
    public static Res Success(object? data = null) => new(true, 200, "ok", "", data);
    public static Res Fail(int status, string code, string message) => new(false, status, code, message);
    public static Res Conflict(string message, string code = "invalid_state") => new(false, 409, code, message);
    public static Res Invalid(string message, string code = "invalid") => new(false, 422, code, message);
}

public record QtyLine(Guid LineId, int Quantity);
/// <summary>A carrier and tracking number that come from a purchased label: trusted, so the manual-entry format checks are skipped, and <see cref="Attach"/> links the label in the same transaction.</summary>
public record ShipTrust(string Carrier, string TrackingNumber, string? Service, Action<Shipment> Attach);
public record ShipRequest(int? Version, List<QtyLine>? Lines, string? Carrier, string? Service, string? TrackingNumber, DateTime? ShippedAt);
public record CancelRequest(int? Version, string? Reason, string? Note, bool? Restock);
public record RefundRequest(int? Version, List<QtyLine>? Lines, long? ShippingCents, bool? Full, string? Reason, string? Note, Guid? ReturnId);
public record ReturnDecision(int? Version, string? Reason, string? Note, bool? Restock);

/// <summary>
/// Every seller-side change of an order. Each method validates against the state machine and the numbers, applies stock effects through the
/// inventory service, writes the timeline event + outbox event + audit entry, and saves everything in one SaveChanges.
/// An order is "claimed" with an atomic version bump first, so two concurrent actions on the same order can never both apply their side effects.
/// </summary>
public class OrderActions(AppDbContext db, IInventoryService inventory, Outbox outbox, TimeProvider time,
    IAuditLog audit, OrderNotifier notifier, ShipmentTracker tracker, Shipping.ShippingTestHooks? hooks = null)
{

    public IQueryable<SellerOrder> Full(IQueryable<SellerOrder> q) => q.Include(o => o.Lines).Include(o => o.Shipments).ThenInclude(s => s.Lines);

    /// <summary>Atomically takes the order (optimistic concurrency): false means somebody else changed it since the caller loaded it.</summary>
    private Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? _tx;

    private async Task<bool> ClaimAsync(SellerOrder o, int? expectedVersion)
    {
        if (expectedVersion is { } ev && ev != o.Version) return false;
        var v = o.Version;
        // The claim is the first statement of a transaction that also carries the stock changes and the final SaveChanges: a competing action
        // blocks on this row until we commit, then finds the version moved and loses cleanly.
        _tx = await db.Database.BeginTransactionAsync();
        var n = await db.Orders.Where(x => x.Id == o.Id && x.Version == v).ExecuteUpdateAsync(s => s.SetProperty(x => x.Version, v + 1));
        if (n != 1) { await _tx.RollbackAsync(); await _tx.DisposeAsync(); _tx = null; return false; }
        o.Version = v + 1;
        db.Entry(o).Property(x => x.Version).OriginalValue = v + 1;
        o.UpdatedAt = time.UtcNow();
        return true;
    }

    private async Task EndTxAsync(bool rollback)
    {
        if (_tx is null) return;
        if (rollback) { try { await _tx.RollbackAsync(); } catch (InvalidOperationException) { /* already completed */ } }
        await _tx.DisposeAsync();
        _tx = null;
    }

    private static Res Stale() => Res.Fail(409, "version_conflict", "This order was changed somewhere else. Reload to see the latest version.");

    private async Task<Res> SaveAsync(SellerOrder o, string live)
    {
        try
        {
            await db.SaveChangesAsync();
            if (_tx is not null) await _tx.CommitAsync();
        }
        catch (DbUpdateConcurrencyException) { await EndTxAsync(rollback: true); db.ChangeTracker.Clear(); return Stale(); }
        catch (DbUpdateException ex) when (ex.ToString().Contains("UX_order_shipments_live_tracking") || ex.ToString().Contains("order_shipments.TrackingNumber"))
        {   // two parallel entries of the same number: the database picks one
            await EndTxAsync(rollback: true); db.ChangeTracker.Clear();
            return Res.Conflict("This tracking number can't be used. Check the number and try again.", "tracking_in_use");
        }
        catch { await EndTxAsync(rollback: true); throw; }
        await EndTxAsync(rollback: false);
        notifier.Live(live, o.SellerId, o.BrandId, new { orderId = o.Id });
        return Res.Success();
    }

    public void LiveChanged(SellerOrder o) => notifier.Live("order.changed", o.SellerId, o.BrandId, new { orderId = o.Id });

    private Task Audit(string type, Guid actor, SellerOrder o, string? extra = null) =>
        audit.WriteAsync(type, actor, null, $"order={o.Number}" + (extra is null ? "" : " " + extra));

    private object Ref(SellerOrder o) => new { sellerOrderId = o.Id, sellerOrderNumber = o.Number, marketplaceOrderId = o.MarketplaceOrderId };

    // ---- accept / hold ---------------------------------------------------------------------------------------------------
    public async Task<Res> AcceptAsync(SellerOrder o, Guid actor, int? version, Party by = Party.Seller)
    {
        if (!OrderStateMachine.Can(o.Status, OrderStatus.Accepted)) return Res.Conflict($"This order is {OrderStateMachine.Label(o.Status)} and can't be accepted.");
        if (!await ClaimAsync(o, version)) return Stale();
        var now = time.UtcNow();
        OrderStateMachine.Move(o, OrderStatus.Accepted);
        o.AcceptedAt = now;
        db.AddEvent(o.Id, "order.accepted", by, by == Party.Seller ? actor : null, by == Party.System ? "Accepted automatically" : "Order accepted", now);
        outbox.Enqueue("order.accepted", o.SellerId, o.MarketplaceOrderId, new { order = Ref(o), acceptedAt = now, auto = by == Party.System });
        var r = await SaveAsync(o, "order.changed");
        if (r.Ok && by == Party.Seller) await Audit("order.accept", actor, o);
        return r;
    }

    public async Task<Res> HoldAsync(SellerOrder o, Guid actor, int? version, string? note)
    {
        if (o.Status is not (OrderStatus.New or OrderStatus.Accepted or OrderStatus.PartiallyShipped)) return Res.Conflict($"This order is {OrderStateMachine.Label(o.Status)} and can't be put on hold.");
        if (note is { Length: > 500 }) return Res.Invalid("The note is too long.");
        if (!await ClaimAsync(o, version)) return Stale();
        o.HeldFrom = o.Status;
        OrderStateMachine.Move(o, OrderStatus.OnHold);
        db.AddEvent(o.Id, "order.held", Party.Seller, actor, note, time.UtcNow());
        outbox.Enqueue("order.held", o.SellerId, o.MarketplaceOrderId, new { order = Ref(o), note });
        return await SaveAsync(o, "order.changed");
    }

    public async Task<Res> ResumeAsync(SellerOrder o, Guid actor, int? version)
    {
        if (o.Status != OrderStatus.OnHold || o.HeldFrom is not { } back) return Res.Conflict("This order isn't on hold.");
        if (!await ClaimAsync(o, version)) return Stale();
        OrderStateMachine.Move(o, back);
        o.HeldFrom = null;
        db.AddEvent(o.Id, "order.resumed", Party.Seller, actor, "Hold removed", time.UtcNow());
        outbox.Enqueue("order.resumed", o.SellerId, o.MarketplaceOrderId, new { order = Ref(o), status = o.Status.ToString() });
        return await SaveAsync(o, "order.changed");
    }

    // ---- shipping --------------------------------------------------------------------------------------------------------
    /// <param name="localCode">Set only by local delivery (never reachable from the API): ships with carrier "Local", the delivery code as tracking number, method local.</param>
    /// <param name="trust">Set only by label purchase: a carrier and tracking number from a purchased label (format checks skipped, the label is linked in the same transaction).</param>
    public async Task<Res> ShipAsync(SellerOrder o, Guid actor, ShipRequest req, string? localCode = null, ShipTrust? trust = null)
    {
        if (o.Status == OrderStatus.New) return Res.Conflict("Accept this order before shipping it.");
        if (o.Status == OrderStatus.OnHold) return Res.Conflict("This order is on hold. Remove the hold before shipping.");
        if (!OrderStateMachine.CanShip(o.Status)) return Res.Conflict($"This order is {OrderStateMachine.Label(o.Status)} and can't be shipped.");
        if (o.CancelRequested) return Res.Conflict("The buyer asked to cancel this order. Approve or reject the request first.", "cancel_requested");
        if (localCode is null && await db.LocalDeliveries.AnyAsync(d => d.OrderId == o.Id && d.Status != LocalStatus.Cancelled && d.Status != LocalStatus.Returned))
            return Res.Conflict("This order is set up for local delivery. Remove it from the dispatch board before shipping it with a carrier.", "local_delivery");
        var carrier = localCode is null ? (trust?.Carrier ?? Tracking.Carrier(req.Carrier)) : "Local";
        if (carrier is null) return Res.Invalid("Choose a carrier: " + string.Join(", ", Tracking.Carriers) + ".", "carrier");
        var tn = localCode ?? trust?.TrackingNumber ?? Tracking.Normalize(req.TrackingNumber);
        if (localCode is null && trust is null && !Tracking.Valid(carrier, tn)) return Res.Invalid($"That doesn't look like a {carrier} tracking number.", "tracking_format");
        if (localCode is null && trust is null && await DuplicateTrackingAsync(carrier, tn, null) is { } inUse) return inUse;
        if ((trust?.Service ?? req.Service) is { Length: > 60 }) return Res.Invalid("The service name is too long.", "service");
        var now = time.UtcNow();
        var shippedAt = req.ShippedAt is { } s ? OrderSupport.Utc(s) : now;
        if (shippedAt > now.AddMinutes(5)) return Res.Invalid("The ship date can't be in the future.", "shipped_at");
        if (shippedAt < o.PlacedAt.AddMinutes(-1)) return Res.Invalid("The ship date can't be before the order was placed.", "shipped_at");

        var wanted = req.Lines is { Count: > 0 } ? req.Lines : o.Lines.Where(l => OrderSupport.Unshipped(l) > 0).Select(l => new QtyLine(l.Id, OrderSupport.Unshipped(l))).ToList();
        if (wanted.Count == 0) return Res.Conflict("Everything in this order has already shipped.");
        if (wanted.GroupBy(w => w.LineId).Any(g => g.Count() > 1)) return Res.Invalid("Each item can appear only once.", "lines");
        foreach (var w in wanted)
        {
            var line = o.Lines.FirstOrDefault(l => l.Id == w.LineId);
            if (line is null) return Res.Invalid("An item doesn't belong to this order.", "lines");
            if (w.Quantity < 1 || w.Quantity > OrderSupport.Unshipped(line))
                return Res.Invalid($"You can ship at most {OrderSupport.Unshipped(line)} of \"{line.Title}\".", "quantity");
        }
        if (!await ClaimAsync(o, req.Version)) return Stale();

        var ship = new Shipment { OrderId = o.Id, Carrier = carrier, Service = (trust?.Service ?? req.Service)?.Trim(), TrackingNumber = tn, ShippedAt = shippedAt, CreatedAt = now };
        if (localCode is not null) { ship.Method = DeliveryMethod.Local; ship.TrackingStatus = Domain.TrackingStatus.InTransit; ship.TrackingStatusAt = now; }
        trust?.Attach(ship);
        var mismatches = new List<string>();
        foreach (var w in wanted)
        {
            var line = o.Lines.First(l => l.Id == w.LineId);
            ship.Lines.Add(new ShipmentLine { ShipmentId = ship.Id, OrderLineId = line.Id, Quantity = w.Quantity });
            line.ShippedQty += w.Quantity;
            var consume = Math.Min(w.Quantity, line.ReservedQty);
            if (consume > 0)
            {
                var c = await inventory.CommitAsync(o.SellerId, line.ProductId, line.VariantId, consume);
                if (!c.Ok) mismatches.Add($"{line.Sku ?? line.Title}: {consume} reserved unit(s) could not be consumed from stock");
                line.ReservedQty -= consume;
            }
            // units that were never reserved (oversold, stock added later) still leave on-hand stock when they ship (best effort: never below zero)
            var unreserved = w.Quantity - consume;
            if (unreserved > 0)
            {
                // on-hand may not drop below what is reserved for other orders: take what can be taken and flag the difference
                var snap = await inventory.AdjustAsync(o.SellerId, line.ProductId, line.VariantId, 0);
                var take = snap.Ok ? Math.Min(unreserved, Math.Max(0, snap.OnHand - snap.Reserved)) : 0;
                if (take > 0) await inventory.AdjustAsync(o.SellerId, line.ProductId, line.VariantId, -take);
                if (take < unreserved) mismatches.Add($"{line.Sku ?? line.Title}: {unreserved - take} unit(s) not deducted from stock");
            }
        }
        db.Shipments.Add(ship);
        o.Shipments.Add(ship);
        var all = o.Lines.All(l => OrderSupport.Unshipped(l) == 0);
        OrderStateMachine.Move(o, all ? OrderStatus.Shipped : OrderStatus.PartiallyShipped);
        if (o.FirstShippedAt is null) { o.FirstShippedAt = shippedAt; o.ShippedLate = shippedAt > o.ShipByAt; }
        if (all) o.ShippedAt = shippedAt;
        db.AddEvent(o.Id, all ? "order.shipped" : "order.partially_shipped", Party.Seller, actor, $"{carrier} {tn}", now);
        if (mismatches.Count > 0) db.AddEvent(o.Id, "stock.mismatch", Party.System, null, string.Join("; ", mismatches) + ". Check your stock count.", now);
        outbox.Enqueue("order.shipped", o.SellerId, o.MarketplaceOrderId, new
        {
            order = Ref(o), partial = !all, shipmentId = ship.Id, carrier, service = ship.Service, trackingNumber = tn, trackingUrl = Tracking.Url(carrier, tn), shippedAt,
            lines = wanted.Select(w => new { marketplaceLineId = o.Lines.First(l => l.Id == w.LineId).MarketplaceLineId, sku = o.Lines.First(l => l.Id == w.LineId).Sku, quantity = w.Quantity })
        });
        var r = await SaveAsync(o, "order.changed");
        if (r.Ok)
        {
            await Audit("order.ship", actor, o, $"carrier={carrier} partial={!all}" + (mismatches.Count > 0 ? " stock_mismatch=true" : ""));
            await tracker.RegisterAsync(ship);   // best effort: manual, CSV and label shipments all get live tracking where the carrier is supported
        }
        return r;
    }

    /// <summary>A carrier tracking number typed in by hand may not already sit on another live shipment (any account): the carrier's updates for it would land on the wrong order.</summary>
    private async Task<Res?> DuplicateTrackingAsync(string carrier, string tn, Guid? exceptShipment)
    {
        var taken = await db.Shipments.AnyAsync(s => s.TrackingNumber == tn && s.Method == "carrier" && s.Id != exceptShipment && s.DeliveredAt == null && s.TrackingStoppedAt == null && (s.Carrier == carrier || s.Carrier == "Other" || carrier == "Other"));
        // generic on purpose: the answer must not tell a seller that ANOTHER account is using the number
        if (!taken && hooks?.AfterTrackingCheck is { } pause) await pause();
        return taken ? Res.Conflict("This tracking number can't be used. Check the number and try again.", "tracking_in_use") : null;
    }

    public async Task<Res> EditTrackingAsync(SellerOrder o, Guid shipmentId, Guid actor, int? version, string? carrierIn, string? trackingIn, string? service)
    {
        var ship = o.Shipments.FirstOrDefault(s => s.Id == shipmentId);
        if (ship is null) return Res.Fail(404, "not_found", "Shipment not found.");
        if (ship.DeliveredAt is not null) return Res.Conflict("This shipment was already delivered.");
        var carrier = Tracking.Carrier(carrierIn ?? ship.Carrier);
        if (carrier is null) return Res.Invalid("Choose a carrier.", "carrier");
        var tn = Tracking.Normalize(trackingIn ?? ship.TrackingNumber);
        if (!Tracking.Valid(carrier, tn)) return Res.Invalid($"That doesn't look like a {carrier} tracking number.", "tracking_format");
        if (ship.LabelId is null && (ship.Carrier != carrier || ship.TrackingNumber != tn) && await DuplicateTrackingAsync(carrier, tn, ship.Id) is { } inUse) return inUse;
        if (!await ClaimAsync(o, version)) return Stale();
        var changed = ship.Carrier != carrier || ship.TrackingNumber != tn;
        ship.Carrier = carrier; ship.TrackingNumber = tn; if (service is not null) ship.Service = service.Trim();
        if (changed) { ship.TrackingStatus = null; ship.TrackingStatusDetail = null; ship.Eta = null; ship.TrackingStatusAt = null; ship.TrackingRegisteredAt = null; ship.LastTrackingAt = null; }
        db.AddEvent(o.Id, "order.tracking_updated", Party.Seller, actor, $"{carrier} {tn}", time.UtcNow());
        outbox.Enqueue("order.tracking_updated", o.SellerId, o.MarketplaceOrderId, new
        {
            order = Ref(o), shipmentId = ship.Id, carrier, trackingNumber = tn, trackingUrl = Tracking.Url(carrier, tn),
            status = ship.TrackingStatus, statusDetail = ship.TrackingStatusDetail, eta = ship.Eta, events = Array.Empty<object>()
        });
        var res = await SaveAsync(o, "order.changed");
        if (res.Ok && changed) await tracker.RegisterAsync(ship);
        return res;
    }

    public async Task<Res> MarkDeliveredAsync(SellerOrder o, Guid actor, int? version, Party by = Party.Seller, string? tracking = null, DateTime? at = null)
    {
        var now = time.UtcNow();
        var when = at is { } a && a <= now.AddMinutes(5) ? OrderSupport.Utc(a) : now;
        if (o.Status == OrderStatus.PartiallyShipped && by == Party.Buyer && tracking is not null)
        {
            var s = o.Shipments.FirstOrDefault(x => x.TrackingNumber == Tracking.Normalize(tracking));
            if (s is null) return Res.Fail(404, "not_found", "No shipment with that tracking number.");
            if (!await ClaimAsync(o, version)) return Stale();
            s.DeliveredAt ??= when < s.ShippedAt ? s.ShippedAt : when;   // a reported time can't precede the shipment (it would start the payout hold early)
            db.AddEvent(o.Id, "shipment.delivered", by, null, s.TrackingNumber, now);
            return await SaveAsync(o, "order.changed");
        }
        if (o.Status != OrderStatus.Shipped) return Res.Conflict($"This order is {OrderStateMachine.Label(o.Status)}. Only a fully shipped order can be marked delivered.");
        if (!await ClaimAsync(o, version)) return Stale();
        if (o.Shipments.Count > 0 && o.Shipments.Max(x => x.ShippedAt) is var lastShipped && when < lastShipped) when = lastShipped;   // never earlier than the last shipment left
        OrderStateMachine.Move(o, OrderStatus.Delivered);
        o.DeliveredAt = when;
        foreach (var s in o.Shipments) s.DeliveredAt ??= when;
        db.AddEvent(o.Id, "order.delivered", by, by == Party.Seller ? actor : null, by == Party.Buyer ? "Delivery confirmed by the marketplace" : "Marked delivered", now);
        outbox.Enqueue("order.delivered", o.SellerId, o.MarketplaceOrderId, new { order = Ref(o), deliveredAt = when, by = by.ToString().ToLowerInvariant() });
        return await SaveAsync(o, "order.changed");
    }

    // ---- tracking-driven changes (workstream K) --------------------------------------------------------------------------
    /// <summary>
    /// A carrier reported this shipment delivered. The caller has already staged the shipment's tracking fields and history in the context; this claims the order,
    /// marks the shipment, moves the order to Delivered (through the state machine) when every shipment has arrived, queues the outbox events and saves it all at once.
    /// Payments release timing keeps working: it reads Status + DeliveredAt, which are set exactly as for a manual "mark delivered".
    /// </summary>
    public async Task<Res> ShipmentDeliveredAsync(SellerOrder o, Shipment ship, DateTime when)
    {
        if (!await ClaimAsync(o, null)) return Stale();
        var now = time.UtcNow();
        when = when > now ? now : when < ship.ShippedAt ? ship.ShippedAt : when;   // like MarkDelivered: a carrier clock can't deliver before shipping (which would release the payout early) or in the future
        ship.DeliveredAt ??= when;
        ship.TrackingStoppedAt ??= now;
        var orderDelivered = false;
        if (o.Status == OrderStatus.Shipped && o.Shipments.All(s => s.DeliveredAt != null) && OrderStateMachine.Can(o.Status, OrderStatus.Delivered))
        {
            OrderStateMachine.Move(o, OrderStatus.Delivered);
            o.DeliveredAt = when;
            orderDelivered = true;
            db.AddEvent(o.Id, "order.delivered", Party.System, null, "Delivered (carrier tracking)", now);
            outbox.Enqueue("order.delivered", o.SellerId, o.MarketplaceOrderId, new { order = Ref(o), deliveredAt = when, by = "carrier" });
        }
        else db.AddEvent(o.Id, "shipment.delivered", Party.System, null, $"{ship.Carrier} {ship.TrackingNumber}", now);
        outbox.Enqueue("shipment.delivered", o.SellerId, o.MarketplaceOrderId, new
        {
            order = Ref(o), shipmentId = ship.Id, carrier = ship.Carrier, trackingNumber = ship.TrackingNumber, deliveredAt = when, orderDelivered
        });
        return await SaveAsync(o, "order.changed");
    }

    /// <summary>
    /// Undoes a shipment whose label was refunded: units go back on hand, the order returns to Accepted / Partially shipped and the shipment is removed.
    /// The caller stages its own changes (label status, ledger credit) first; they commit together with this. Conflict when the order has moved on.
    /// </summary>
    public async Task<Res> ReopenShipmentAsync(SellerOrder o, Guid shipmentId, Guid? actor, string text)
    {
        var ship = o.Shipments.FirstOrDefault(s => s.Id == shipmentId);
        if (ship is null) return Res.Fail(404, "not_found", "Shipment not found.");
        if (o.Status is not (OrderStatus.Shipped or OrderStatus.PartiallyShipped) || ship.DeliveredAt is not null) return Res.Conflict("This order has moved on, so the shipment stays as it is.");
        if (!await ClaimAsync(o, null)) return Stale();
        var now = time.UtcNow();
        foreach (var sl in ship.Lines)
        {
            var line = o.Lines.First(l => l.Id == sl.OrderLineId);
            line.ShippedQty -= sl.Quantity;
            await inventory.AdjustAsync(o.SellerId, line.ProductId, line.VariantId, sl.Quantity);   // back on hand (they left stock when the label shipped)
        }
        o.Shipments.Remove(ship);
        db.Shipments.Remove(ship);
        o.Status = o.Lines.Any(l => l.ShippedQty > 0) ? OrderStatus.PartiallyShipped : OrderStatus.Accepted;   // deliberate: a refunded label is not a state-machine move
        o.ShippedAt = null;
        db.AddEvent(o.Id, "shipment.voided", actor is null ? Party.System : Party.Seller, actor, text, now);
        outbox.Enqueue("shipment.voided", o.SellerId, o.MarketplaceOrderId, new { order = Ref(o), shipmentId = ship.Id, carrier = ship.Carrier, trackingNumber = ship.TrackingNumber });
        return await SaveAsync(o, "order.changed");
    }

    // ---- cancellation -----------------------------------------------------------------------------------------------------
    public async Task<Res> CancelAsync(SellerOrder o, Guid actor, CancelRequest req, Party by = Party.Seller)
    {
        var partial = o.Lines.Any(l => l.ShippedQty > 0);
        if (partial)
        {
            // part of the order already left: only the unshipped remainder can be cancelled
            var shippable = o.Status == OrderStatus.PartiallyShipped || (o.Status == OrderStatus.OnHold && o.HeldFrom == OrderStatus.PartiallyShipped);
            if (!shippable || !o.Lines.Any(l => OrderSupport.Unshipped(l) > 0))
                return Res.Conflict($"This order is {OrderStateMachine.Label(o.Status)}; there is nothing left to cancel.");
        }
        else if (!OrderStateMachine.Can(o.Status, OrderStatus.Cancelled))
            return Res.Conflict($"This order is {OrderStateMachine.Label(o.Status)} and can't be cancelled.");
        var reason = req.Reason?.Trim().ToLowerInvariant();
        if (reason is null || !OrderSupport.CancelReasons.Contains(reason)) return Res.Invalid("Choose a cancellation reason.", "reason");
        if (req.Note is { Length: > 1000 }) return Res.Invalid("The note is too long.", "note");
        if (!await ClaimAsync(o, req.Version)) return Stale();
        return partial ? await CancelRemainderAsync(o, actor, reason, req.Note, req.Restock ?? true) : await DoCancelAsync(o, actor, reason, req.Note, req.Restock ?? true, by, null);
    }

    /// <summary>Cancels the units that have not shipped yet of a partly shipped order: stock released, refund for those units recorded, the order continues as Shipped.</summary>
    private async Task<Res> CancelRemainderAsync(SellerOrder o, Guid actor, string reason, string? note, bool restock)
    {
        var now = time.UtcNow();
        var refund = new OrderRefund { OrderId = o.Id, Reason = "cancelled_remainder", Note = note?.Trim(), CreatedAt = now, CreatedBy = actor };
        var priorPerLine = await db.RefundLines.Where(x => db.Refunds.Any(r => r.Id == x.RefundId && r.OrderId == o.Id)).GroupBy(x => x.OrderLineId).Select(g => new { Id = g.Key, Cents = g.Sum(x => x.AmountCents) }).ToListAsync();
        foreach (var l in o.Lines)
        {
            var open = OrderSupport.Unshipped(l);
            if (open <= 0) continue;
            if (l.ReservedQty > 0)
            {
                if (restock) await inventory.ReleaseAsync(o.SellerId, l.ProductId, l.VariantId, l.ReservedQty);
                else await inventory.CommitAsync(o.SellerId, l.ProductId, l.VariantId, l.ReservedQty);
                l.ReservedQty = 0;
            }
            // the last open units of a line take the rounding remainder, so shipped + cancelled shares add up to the line total
            var done = priorPerLine.FirstOrDefault(p => p.Id == l.Id)?.Cents ?? 0;
            var left = l.Quantity - l.RefundedQty - l.CancelledQty;
            var cents = open == left ? OrderSupport.LineNet(l) - done : OrderSupport.Prorate(OrderSupport.LineNet(l), open, l.Quantity);
            l.CancelledQty += open;
            refund.Lines.Add(new RefundLine { RefundId = refund.Id, OrderLineId = l.Id, Quantity = open, AmountCents = Math.Max(0, cents) });
        }
        refund.AmountCents = refund.Lines.Sum(x => x.AmountCents);
        db.Refunds.Add(refund);
        o.RefundedCents += refund.AmountCents;
        if (o.Status == OrderStatus.OnHold) { OrderStateMachine.Move(o, OrderStatus.PartiallyShipped); o.HeldFrom = null; }
        OrderStateMachine.Move(o, OrderStatus.Shipped);       // nothing left to ship
        o.ShippedAt = o.Shipments.Max(sh => sh.ShippedAt);
        db.AddEvent(o.Id, "order.remainder_cancelled", Party.Seller, actor, reason, now);
        outbox.Enqueue("order.cancelled", o.SellerId, o.MarketplaceOrderId, new
        {
            order = Ref(o), partial = true, reason, note, restocked = restock, cancelledBy = "seller", refundDueCents = refund.AmountCents,
            lines = refund.Lines.Select(x => new { marketplaceLineId = o.Lines.First(l => l.Id == x.OrderLineId).MarketplaceLineId, quantity = x.Quantity, amountCents = x.AmountCents })
        });
        var r = await SaveAsync(o, "order.changed");
        if (r.Ok) await Audit("order.cancel_remainder", actor, o, $"reason={reason} restock={restock} refund={refund.AmountCents}");
        return r;
    }

    private async Task<Res> DoCancelAsync(SellerOrder o, Guid actor, string reason, string? note, bool restock, Party by, OrderCancellation? request)
    {
        var now = time.UtcNow();
        foreach (var l in o.Lines)
        {
            var open = OrderSupport.Unshipped(l);
            if (l.ReservedQty > 0)
            {
                // restock: the hold goes back to available stock. Not restocking (damaged/lost): the units leave on-hand stock too.
                if (restock) await inventory.ReleaseAsync(o.SellerId, l.ProductId, l.VariantId, l.ReservedQty);
                else await inventory.CommitAsync(o.SellerId, l.ProductId, l.VariantId, l.ReservedQty);
                l.ReservedQty = 0;
            }
            l.CancelledQty += open;
        }
        o.HeldFrom = null;
        OrderStateMachine.Move(o, OrderStatus.Cancelled);
        o.CancelledAt = now; o.CancelReason = reason; o.CancelledBy = by; o.CancelRequested = false;
        foreach (var c in await db.Cancellations.Where(c => c.OrderId == o.Id && c.Status == CancelRequestStatus.Requested).ToListAsync())
        { c.Status = CancelRequestStatus.Approved; c.ResolvedAt = now; c.ResolvedBy = actor; c.Restock = restock; }
        if (request is not null) { request.Status = CancelRequestStatus.Approved; request.ResolvedAt = now; request.ResolvedBy = actor; request.Restock = restock; request.DecisionNote = note; }
        db.AddEvent(o.Id, "order.cancelled", by == Party.Buyer ? Party.Buyer : Party.Seller, by == Party.Seller ? actor : null, $"{reason}" + (note is null ? "" : $": {note}"), now);
        outbox.Enqueue("order.cancelled", o.SellerId, o.MarketplaceOrderId, new
        {
            order = Ref(o), reason, note, restocked = restock, cancelledBy = by.ToString().ToLowerInvariant(), refundDueCents = o.TotalCents - o.RefundedCents
        });
        var r = await SaveAsync(o, "order.changed");
        if (r.Ok) await Audit("order.cancel", actor, o, $"reason={reason} restock={restock}");
        return r;
    }

    public async Task<Res> DecideCancellationAsync(SellerOrder o, Guid cancellationId, Guid actor, bool approve, CancelRequest req)
    {
        var c = await db.Cancellations.FirstOrDefaultAsync(x => x.Id == cancellationId && x.OrderId == o.Id);
        if (c is null) return Res.Fail(404, "not_found", "Cancellation request not found.");
        if (c.Status != CancelRequestStatus.Requested) return Res.Conflict("This request was already answered.");
        if (req.Note is { Length: > 1000 }) return Res.Invalid("The note is too long.", "note");
        if (approve)
        {
            if (!OrderStateMachine.Can(o.Status, OrderStatus.Cancelled) || o.Lines.Any(l => l.ShippedQty > 0))
                return Res.Conflict("This order can no longer be cancelled; reject the request instead.");
            if (!await ClaimAsync(o, req.Version)) return Stale();
            return await DoCancelAsync(o, actor, "buyer_requested", req.Note, req.Restock ?? true, Party.Buyer, c);
        }
        if (string.IsNullOrWhiteSpace(req.Note)) return Res.Invalid("Tell the buyer why the order can't be cancelled.", "note");
        if (!await ClaimAsync(o, req.Version)) return Stale();
        var now = time.UtcNow();
        c.Status = CancelRequestStatus.Rejected; c.ResolvedAt = now; c.ResolvedBy = actor; c.DecisionNote = req.Note.Trim();
        o.CancelRequested = await db.Cancellations.AnyAsync(x => x.OrderId == o.Id && x.Status == CancelRequestStatus.Requested && x.Id != c.Id);
        db.AddEvent(o.Id, "cancellation.rejected", Party.Seller, actor, req.Note.Trim(), now);
        outbox.Enqueue("cancellation.decided", o.SellerId, o.MarketplaceOrderId, new { order = Ref(o), requestId = c.MarketplaceRequestId, decision = "rejected", note = c.DecisionNote });
        var r = await SaveAsync(o, "order.changed");
        if (r.Ok) await Audit("order.cancel_request_reject", actor, o);
        return r;
    }

    // ---- returns ----------------------------------------------------------------------------------------------------------
    public async Task<Res> DecideReturnAsync(SellerOrder o, Guid returnId, Guid actor, bool approve, ReturnDecision req)
    {
        var r = await db.Returns.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == returnId && x.OrderId == o.Id);
        if (r is null) return Res.Fail(404, "not_found", "Return not found.");
        if (r.Status != ReturnStatus.Requested || o.Status != OrderStatus.ReturnRequested) return Res.Conflict("This return was already decided.");
        var reason = req.Reason?.Trim().ToLowerInvariant();
        if (!approve && (reason is null || !OrderSupport.RejectReasons.Contains(reason))) return Res.Invalid("Choose a reason for rejecting the return.", "reason");
        if (req.Note is { Length: > 1000 }) return Res.Invalid("The note is too long.", "note");
        if (!await ClaimAsync(o, req.Version)) return Stale();
        var now = time.UtcNow();
        r.Status = approve ? ReturnStatus.Approved : ReturnStatus.Rejected; r.DecidedAt = now; r.DecidedBy = actor;
        r.DecisionReason = approve ? req.Note : (reason + (string.IsNullOrWhiteSpace(req.Note) ? "" : ": " + req.Note));
        OrderStateMachine.Move(o, approve ? OrderStatus.ReturnApproved : OrderStatus.ReturnRejected);
        db.AddEvent(o.Id, approve ? "return.approved" : "return.rejected", Party.Seller, actor, r.DecisionReason, now);
        outbox.Enqueue("return.decided", o.SellerId, o.MarketplaceOrderId, new { order = Ref(o), returnId = r.MarketplaceReturnId, decision = approve ? "approved" : "rejected", reason, note = req.Note });
        var res = await SaveAsync(o, "order.changed");
        if (res.Ok) await Audit(approve ? "order.return_approve" : "order.return_reject", actor, o, reason is null ? null : $"reason={reason}");
        return res;
    }

    public async Task<Res> ReceiveReturnAsync(SellerOrder o, Guid returnId, Guid actor, ReturnDecision req)
    {
        var r = await db.Returns.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == returnId && x.OrderId == o.Id);
        if (r is null) return Res.Fail(404, "not_found", "Return not found.");
        if (r.Status != ReturnStatus.Approved || o.Status != OrderStatus.ReturnApproved) return Res.Conflict("Only an approved return can be marked as received.");
        if (!await ClaimAsync(o, req.Version)) return Stale();
        var now = time.UtcNow();
        var restock = req.Restock ?? false;
        foreach (var rl in r.Lines)
        {
            var line = o.Lines.First(l => l.Id == rl.OrderLineId);
            line.ReturnedQty += rl.Quantity;
            if (restock) await inventory.AdjustAsync(o.SellerId, line.ProductId, line.VariantId, rl.Quantity);
        }
        r.Status = ReturnStatus.Received; r.ReceivedAt = now; r.Restock = restock;
        OrderStateMachine.Move(o, OrderStatus.ReturnReceived);
        db.AddEvent(o.Id, "return.received", Party.Seller, actor, restock ? "Returned items received and restocked" : "Returned items received", now);
        outbox.Enqueue("return.received", o.SellerId, o.MarketplaceOrderId, new { order = Ref(o), returnId = r.MarketplaceReturnId, restocked = restock });
        var res = await SaveAsync(o, "order.changed");
        if (res.Ok) await Audit("order.return_received", actor, o, $"restock={restock}");
        return res;
    }

    // ---- refunds ----------------------------------------------------------------------------------------------------------
    /// <summary>What would be refunded for the request (null when invalid), used both to validate and to enforce the Manager limit before committing.</summary>
    public async Task<(Res? Error, long Amount, long Shipping, List<(OrderLine Line, int Qty, long Cents)> Lines)> PlanRefundAsync(SellerOrder o, RefundRequest req)
    {
        var prior = await db.RefundLines.Where(x => db.Refunds.Any(r => r.Id == x.RefundId && r.OrderId == o.Id)).GroupBy(x => x.OrderLineId)
            .Select(g => new { Id = g.Key, Cents = g.Sum(x => x.AmountCents) }).ToListAsync();
        var priorShip = await db.Refunds.Where(r => r.OrderId == o.Id).SumAsync(r => (long?)r.ShippingCents) ?? 0;
        var plan = new List<(OrderLine, int, long)>();
        var ids = new HashSet<Guid>();
        if (!o.Lines.Any(l => l.ShippedQty > 0))
            return (Res.Conflict("Nothing has shipped yet. Cancel the order instead of refunding it.", "refund_before_ship"), 0, 0, plan);
        // only units that actually shipped can be refunded; unshipped units are cancelled (stock + reservation are handled there)
        static int Refundable(OrderLine l) => l.ShippedQty - l.RefundedQty;
        long Cents(OrderLine l, int q, long done) => q == l.Quantity - l.RefundedQty - l.CancelledQty
            ? OrderSupport.LineNet(l) - done            // everything that is left of the line: takes the rounding remainder
            : OrderSupport.Prorate(OrderSupport.LineNet(l), q, l.Quantity);
        if (req.Full == true)
        {
            foreach (var l in o.Lines)
            {
                var q = Refundable(l);
                if (q > 0) plan.Add((l, q, Math.Max(0, Cents(l, q, prior.FirstOrDefault(p => p.Id == l.Id)?.Cents ?? 0))));
            }
            var ship = o.ShippingCents - priorShip;
            var amt = plan.Sum(p => p.Item3) + ship;
            return (null, amt, ship, plan);
        }
        foreach (var w in req.Lines ?? [])
        {
            var l = o.Lines.FirstOrDefault(x => x.Id == w.LineId);
            if (l is null || !ids.Add(l.Id)) return (Res.Invalid("An item doesn't belong to this order, or appears twice.", "lines"), 0, 0, plan);
            var avail = Refundable(l);
            if (w.Quantity < 1 || w.Quantity > avail) return (Res.Invalid($"You can refund at most {Math.Max(0, avail)} shipped unit(s) of \"{l.Title}\".", "quantity"), 0, 0, plan);
            var done = prior.FirstOrDefault(p => p.Id == l.Id)?.Cents ?? 0;
            plan.Add((l, w.Quantity, Math.Max(0, Cents(l, w.Quantity, done))));
        }
        var shipping = req.ShippingCents ?? 0;
        if (shipping < 0 || shipping > o.ShippingCents - priorShip) return (Res.Invalid("The shipping refund can't exceed the shipping that was charged.", "shipping"), 0, 0, plan);
        return (null, plan.Sum(p => p.Item3) + shipping, shipping, plan);
    }

    public async Task<Res> RefundAsync(SellerOrder o, Guid actor, RefundRequest req, Func<long, bool>? allowed = null)
    {
        if (o.Status == OrderStatus.Cancelled) return Res.Conflict("This order was cancelled; the marketplace refunds cancelled orders in full.");
        var reason = req.Reason?.Trim().ToLowerInvariant();
        if (reason is null || !OrderSupport.RefundReasons.Contains(reason)) return Res.Invalid("Choose a refund reason.", "reason");
        if (req.Note is { Length: > 1000 }) return Res.Invalid("The note is too long.", "note");
        var (err, amount, shipping, lines) = await PlanRefundAsync(o, req);
        if (err is not null) return err;
        if (amount <= 0) return Res.Invalid("Nothing to refund: choose items or shipping.", "amount");
        if (amount > o.TotalCents - o.RefundedCents) return Res.Invalid($"You can refund at most {(o.TotalCents - o.RefundedCents) / 100m:0.00} more on this order.", "amount_exceeds");
        if (allowed is not null && !allowed(amount)) return Res.Fail(403, "refund_limit", "Only an Owner or Admin can issue a refund of this size.");
        ReturnRequest? ret = null;
        if (req.ReturnId is { } rid)
        {
            ret = await db.Returns.FirstOrDefaultAsync(x => x.Id == rid && x.OrderId == o.Id);
            if (ret is null) return Res.Fail(404, "not_found", "Return not found.");
            if (ret.Status != ReturnStatus.Received) return Res.Conflict("Mark the return as received before refunding it.");
        }
        if (!await ClaimAsync(o, req.Version)) return Stale();
        var now = time.UtcNow();
        var refund = new OrderRefund { OrderId = o.Id, ReturnId = ret?.Id, AmountCents = amount, ShippingCents = shipping, Reason = reason, Note = req.Note?.Trim(), CreatedAt = now, CreatedBy = actor };
        foreach (var (line, qty, cents) in lines)
        {
            refund.Lines.Add(new RefundLine { RefundId = refund.Id, OrderLineId = line.Id, Quantity = qty, AmountCents = cents });
            line.RefundedQty += qty;
        }
        db.Refunds.Add(refund);
        o.RefundedCents += amount;
        var full = o.RefundedCents >= o.TotalCents;
        if (ret is not null) { ret.Status = ReturnStatus.Refunded; ret.RefundCents += amount; }
        if (o.Status == OrderStatus.ReturnReceived || (full && OrderStateMachine.Can(o.Status, OrderStatus.Refunded))) OrderStateMachine.Move(o, OrderStatus.Refunded);
        db.AddEvent(o.Id, "order.refunded", Party.Seller, actor, $"{amount / 100m:0.00} {o.Currency} ({(full ? "full" : "partial")}) - {reason}", now);
        outbox.Enqueue("refund.issued", o.SellerId, o.MarketplaceOrderId, new
        {
            order = Ref(o), refundId = refund.Id, amountCents = amount, shippingCents = shipping, currency = o.Currency, reason, note = refund.Note, full,
            totalRefundedCents = o.RefundedCents, returnId = ret?.MarketplaceReturnId,
            lines = lines.Select(x => new { marketplaceLineId = x.Line.MarketplaceLineId, sku = x.Line.Sku, quantity = x.Qty, amountCents = x.Cents })
        });
        var r = await SaveAsync(o, "order.changed");
        if (r.Ok) { await Audit("order.refund", actor, o, $"amount={amount} full={full}"); r = r with { Data = new { refundId = refund.Id, amountCents = amount } }; }
        return r;
    }

    public async Task<Res> AddNoteAsync(SellerOrder o, Guid actor, string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t) || t.Length > 2000) return Res.Invalid("A note needs 1 to 2000 characters.", "text");
        db.OrderNotes.Add(new OrderNote { OrderId = o.Id, AuthorId = actor, Text = t, CreatedAt = time.UtcNow() });
        await db.SaveChangesAsync();
        return Res.Success();
    }
}
