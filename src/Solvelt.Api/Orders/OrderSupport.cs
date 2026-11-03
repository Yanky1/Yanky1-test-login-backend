using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Orders;

public static class OrderSupport
{
    public static readonly string[] CancelReasons = ["out_of_stock", "cannot_ship_to_address", "pricing_error", "buyer_requested", "fraud_suspected", "other"];
    public static readonly string[] RefundReasons = ["returned_item", "item_not_as_described", "damaged_in_transit", "late_delivery", "goodwill", "other"];
    public static readonly string[] RejectReasons = ["outside_return_window", "item_used", "not_eligible", "wrong_item_returned", "other"];
    public static readonly string[] BuyerCancelReasons = ["changed_mind", "ordered_by_mistake", "found_better_price", "delivery_too_slow", "other"];
    public static readonly string[] BuyerReturnReasons = ["damaged", "not_as_described", "wrong_item", "no_longer_needed", "other"];

    /// <summary>Orders restricted to the caller's account AND brand scope. Use for EVERY order query; anything else is 404.</summary>
    public static IQueryable<SellerOrder> OrdersFor(this AppDbContext db, AccountAccess a)
    {
        var q = db.Orders.Where(o => o.SellerId == a.SellerId);
        if (a.AllBrands || a.Role >= AccountRole.Admin) return q;
        var ids = a.BrandIds.ToList();
        return q.Where(o => ids.Contains(o.BrandId));
    }

    public static IQueryable<Conversation> ConversationsFor(this AppDbContext db, AccountAccess a)
    {
        var q = db.Conversations.Where(c => c.SellerId == a.SellerId);
        if (a.AllBrands || a.Role >= AccountRole.Admin) return q;
        var ids = a.BrandIds.ToList();
        return q.Where(c => ids.Contains(c.BrandId));
    }

    public static void AddEvent(this AppDbContext db, Guid orderId, string type, Party actor, Guid? userId, string? text, DateTime now) =>
        db.OrderEvents.Add(new OrderEvent { OrderId = orderId, Type = type, Actor = actor, ActorUserId = userId, Text = text, At = now });

    /// <summary>Fee = round-half-up(net items x basis points / 10000), pure integer math.</summary>
    public static long Fee(long itemsAfterDiscountCents, int basisPoints) =>
        itemsAfterDiscountCents <= 0 ? 0 : (itemsAfterDiscountCents * basisPoints + 5000) / 10000;

    public static void Recalculate(SellerOrder o, int feeBasisPoints)
    {
        o.ItemsCents = o.Lines.Sum(l => l.UnitPriceCents * l.Quantity);
        o.DiscountCents = o.Lines.Sum(l => l.DiscountCents);
        o.TaxCents = o.Lines.Sum(l => l.TaxCents);
        o.TotalCents = o.ItemsCents - o.DiscountCents + o.ShippingCents + o.TaxCents;
        o.FeeCents = Fee(o.ItemsCents - o.DiscountCents, feeBasisPoints);
        o.NetCents = o.ItemsCents - o.DiscountCents + o.ShippingCents - o.FeeCents;
    }

    /// <summary>Share of a line's discount/tax for a partial quantity (integer, rounds down; the remainder stays on the last unit).</summary>
    public static long Prorate(long total, int qty, int ofQty) => ofQty <= 0 ? 0 : total * qty / ofQty;

    public static long LineNet(OrderLine l) => l.UnitPriceCents * l.Quantity - l.DiscountCents + l.TaxCents;

    public static int Unshipped(OrderLine l) => l.Quantity - l.ShippedQty - l.CancelledQty;



    /// <summary>Normalises any incoming timestamp to UTC (PostgreSQL timestamptz only accepts UTC): offsets are converted, unspecified values are taken as UTC.</summary>
    public static DateTime Utc(DateTime d) => d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d.ToUniversalTime();

    public static string MaskName(string? n) => string.IsNullOrWhiteSpace(n) ? "***" : n.Trim()[0] + "***";
    public static string MaskEmail(string? e) => AuditLog.Mask(e ?? "");

    public static async Task<string> NextNumberAsync(AppDbContext db, DateTime now)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var year = now.Year;
            var cur = await db.OrderCounters.AsNoTracking().Where(c => c.Year == year).Select(c => (long?)c.Next).FirstOrDefaultAsync();
            if (cur is null)
            {
                db.OrderCounters.Add(new OrderCounter { Year = year, Next = 1 });
                try { await db.SaveChangesAsync(); } catch (DbUpdateException) { db.ChangeTracker.Clear(); }
                continue;
            }
            var n = cur.Value;
            var rows = await db.OrderCounters.Where(c => c.Year == year && c.Next == n).ExecuteUpdateAsync(s => s.SetProperty(c => c.Next, n + 1));
            if (rows == 1) return $"SO-{year}-{n:D6}";
        }
        throw new InvalidOperationException("Could not allocate an order number");
    }

    public static string ToJson(object o) => OrderJson.Ser(o);
    public static T? From<T>(string json) => JsonSerializer.Deserialize<T>(json, OrderJson.Opts);
}
