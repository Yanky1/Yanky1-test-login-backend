using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Email;
using Solvelt.Api.Options;

namespace Solvelt.Api.Orders;

public enum NotifyKind { NewOrder, CancelRequest, ReturnRequest, BuyerMessage, OverdueReply, DeliveryAlert }

/// <summary>In-app (SSE) signals and opt-in emails. Mail failures never break an order flow (Mailer swallows and logs them).</summary>
public class OrderNotifier(AppDbContext db, Mailer mailer, EventHub hub, IOptions<AppOptions> app, ILogger<OrderNotifier> log)
{
    public static bool Default(AccountRole r) => r >= AccountRole.Admin;

    public static bool Wants(NotificationPreference? p, AccountRole role, NotifyKind k)
    {
        var v = k switch
        {
            NotifyKind.NewOrder => p?.NewOrder, NotifyKind.CancelRequest => p?.CancelRequest, NotifyKind.ReturnRequest => p?.ReturnRequest,
            NotifyKind.BuyerMessage => p?.BuyerMessage, NotifyKind.DeliveryAlert => null, _ => p?.OverdueReply
        };
        return v ?? Default(role);
    }

    /// <summary>Emails of active members that can see the brand and want this kind of notification.</summary>
    public async Task<List<string>> RecipientsAsync(Guid sellerId, Guid brandId, NotifyKind kind, CancellationToken ct = default)
    {
        var members = await db.Memberships.AsNoTracking().Where(m => m.SellerId == sellerId && m.Status == MembershipStatus.Active).ToListAsync(ct);
        var scoped = await db.MembershipBrands.AsNoTracking().Where(b => b.BrandId == brandId).Select(b => b.MembershipId).ToListAsync(ct);
        var prefs = await db.NotificationPreferences.AsNoTracking().Where(p => p.SellerId == sellerId).ToListAsync(ct);
        var wanted = members.Where(m => (m.AllBrands || m.Role >= AccountRole.Admin || scoped.Contains(m.Id))
            && Wants(prefs.FirstOrDefault(p => p.UserId == m.UserId), m.Role, kind)).Select(m => m.UserId).ToList();
        return await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id) && u.Email != null).Select(u => u.Email!).ToListAsync(ct);
    }

    private async Task Mail(Guid sellerId, Guid brandId, NotifyKind kind, string mailKind, string subject, string heading, string[] lines, string path, string button, CancellationToken ct)
    {
        try
        {
            var url = app.Value.FrontendBaseUrl.TrimEnd('/') + path;
            foreach (var to in await RecipientsAsync(sellerId, brandId, kind, ct))
                await mailer.SendAsync(Templates.Notification(to, mailKind, subject, heading, lines, button, url));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Notification {Kind} failed", mailKind); }
    }

    /// <summary>Local delivery alerts (failed attempt, late, route finished): in-app signal plus email to Owners/Admins (role default; Managers who can see the brand opt in through the delivery-alert default).</summary>
    public async Task DeliveryAlertAsync(Guid sellerId, Guid brandId, string type, string subject, string heading, string line, string path, object? data = null)
    {
        Live(type, sellerId, brandId, data);
        await Mail(sellerId, brandId, NotifyKind.DeliveryAlert, "delivery-alert", subject, heading, [line], path, "Open dispatch board", CancellationToken.None);
    }

    public void Live(string type, Guid sellerId, Guid brandId, object? data = null) => hub.Publish(new LiveEvent(type, sellerId, brandId, data));

    public async Task OrderPlacedAsync(SellerOrder o, CancellationToken ct = default)
    {
        Live("order.new", o.SellerId, o.BrandId, new { orderId = o.Id });
        await Mail(o.SellerId, o.BrandId, NotifyKind.NewOrder, "order-new", $"New order {o.Number}", "You have a new order",
            [$"Order {o.Number} was placed. Ship by {o.ShipByAt:yyyy-MM-dd}." + (o.Oversold ? " Some items are out of stock and need your attention." : "")], $"/orders/{o.Id}", "View order", ct);
    }

    public async Task CancelRequestedAsync(SellerOrder o, CancellationToken ct = default)
    {
        Live("order.cancel_requested", o.SellerId, o.BrandId, new { orderId = o.Id });
        await Mail(o.SellerId, o.BrandId, NotifyKind.CancelRequest, "order-cancel-request", $"Cancellation request for {o.Number}", "The buyer asked to cancel",
            [$"The buyer asked to cancel order {o.Number}. Approve or reject the request."], $"/orders/{o.Id}", "Review request", ct);
    }

    public async Task ReturnRequestedAsync(SellerOrder o, CancellationToken ct = default)
    {
        Live("order.return_requested", o.SellerId, o.BrandId, new { orderId = o.Id });
        await Mail(o.SellerId, o.BrandId, NotifyKind.ReturnRequest, "order-return-request", $"Return request for {o.Number}", "The buyer asked to return items",
            [$"The buyer asked to return items from order {o.Number}. Approve or reject the return."], $"/orders/{o.Id}", "Review return", ct);
    }

    public async Task BuyerMessageAsync(Conversation c, CancellationToken ct = default)
    {
        Live("message.new", c.SellerId, c.BrandId, new { conversationId = c.Id });
        await Mail(c.SellerId, c.BrandId, NotifyKind.BuyerMessage, "buyer-message", "New message from a buyer", "You have a new buyer message",
            [$"{c.BuyerName} wrote to you about: {c.Subject}. Please reply within 24 hours."], $"/messages/{c.Id}", "Open conversation", ct);
    }

    public async Task OverdueAsync(Conversation c, CancellationToken ct = default)
    {
        Live("message.overdue", c.SellerId, c.BrandId, new { conversationId = c.Id });
        await Mail(c.SellerId, c.BrandId, NotifyKind.OverdueReply, "message-overdue", "A buyer message needs a reply", "A reply is overdue",
            [$"{c.BuyerName} is still waiting for an answer about: {c.Subject}."], $"/messages/{c.Id}", "Reply now", ct);
    }
}
