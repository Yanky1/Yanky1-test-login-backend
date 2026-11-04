using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Payments;

namespace Solvelt.Api.Tests;

public class PayFactory : OrdersFactory
{
    public const string HookSecret = "whsec_test_0123456789abcdef";
    public PayFactory()
    {
        Extra["Stripe:WebhookSecret"] = HookSecret;
        Extra["Payments:NewSellerReservePercent"] = "0";
        Extra["Payments:ReleaseDays"] = "7";
        Extra["Payments:RequireReturnWindowClosed"] = "false";   // the product default is true: tests that need it use StrictFactory
        Extra["Payments:MinTransferCents"] = "500";
        Extra["Payments:TransferBackoffSeconds"] = "60";
        Extra["RateLimit:PaymentsPerMinute"] = "100000";
    }
    public FakePaymentProvider Fake => Services.GetRequiredService<FakePaymentProvider>();
}

public static class Pay
{
    public static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);

    public static async Task<T> Run<T>(ApiFactory f, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = f.Services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    /// <summary>Takes the job lease in its own scope; returns the owner token (null: someone else holds it). Release with <see cref="Unlease"/>.</summary>
    public static Task<string?> Lease(ApiFactory f, string name = TransferService.JobName) =>
        Run(f, async sp => { var l = sp.GetRequiredService<JobLeaseService>(); return await l.TryAcquireAsync(name) ? l.Token : null; });

    public static Task<int> Unlease(ApiFactory f, string token, string name = TransferService.JobName) =>
        Run(f, async sp => { await sp.GetRequiredService<JobLeaseService>().ReleaseAsync(name, token); return 0; });

    public static Task<ReleaseSummary> Release(ApiFactory f) => Run(f, sp => sp.GetRequiredService<ReleaseService>().RunAsync());
    public static Task<TransferSummary> Transfers(ApiFactory f) => Run(f, sp => sp.GetRequiredService<TransferService>().RunAsync());
    public static Task<ReconcileSummary> Reconcile(ApiFactory f) => Run(f, sp => sp.GetRequiredService<ReconciliationService>().RunAsync());

    public static Task<Balances> Bal(ApiFactory f, Guid seller, string cur = "USD") => Run(f, sp => sp.GetRequiredService<LedgerService>().BalanceAsync(seller, cur));

    /// <summary>Seller order accepted, shipped and delivered (price 1999 x1 + 500 shipping: items 1999, fee 200, net 2299).</summary>
    public static async Task<(string Mid, Guid Id, string Number)> Delivered(Shop s, int qty = 1, long price = 1999, long shipping = 500)
    {
        await Fresh(s);
        var (mid, id, number, _) = await Orders.Place(s, qty, price, shipping);
        await Orders.Ok(Orders.Act(s.Owner, id, "accept"));
        await Orders.Ok(Orders.Act(s.Owner, id, "ship", Orders.Ship()));
        await Orders.Ok(Orders.Act(s.Owner, id, "deliver"));
        return (mid, id, number);
    }

    public static Task<HttpResponseMessage> Event(ApiFactory f, string type, object data, string? eventId = null) =>
        Orders.Inbound(f, "/api/internal/v1/payment-events", new { eventId = eventId ?? "ev-" + Guid.NewGuid().ToString("N"), type, data });

    public static async Task<HttpResponseMessage> Capture(ApiFactory f, string mid, string? number = null, string? eventId = null, string? paymentRef = null) =>
        await Event(f, "payment.captured", new { kind = "order", marketplaceOrderId = mid, sellerOrderNumber = number, paymentRef = paymentRef ?? "ch_" + mid, currency = "USD" }, eventId);

    public static async Task<(string Mid, Guid Id, string Number)> Paid(Shop s, int qty = 1, long price = 1999, long shipping = 500)
    {
        var o = await Delivered(s, qty, price, shipping);
        var r = await Capture(s.F, o.Mid, o.Number);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return o;
    }

    public static void Age(ApiFactory f, int days) => f.Time.Advance(TimeSpan.FromDays(days));

    /// <summary>After the fake clock jumped days ahead the old access token and session are expired: sign in again (same account, the owner).</summary>
    public static async Task Relog(Shop s)
    {
        var email = OwnerEmail(s.F, s.Seller);
        await s.Owner.LoginAs(email, ApiFactory.Strong);
        await s.Owner.Switch(s.Seller);
        Stamps.AddOrUpdate(s.Owner, new StrongBox<DateTimeOffset>(s.F.Time.GetUtcNow()));
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Api, StrongBox<DateTimeOffset>> Stamps = new();

    /// <summary>Signs in again when the fake clock has moved on since the owner last authenticated (tokens and sessions expire on fake time too).</summary>
    public static async Task Fresh(Shop s)
    {
        if (!Stamps.TryGetValue(s.Owner, out var at)) { Stamps.Add(s.Owner, new StrongBox<DateTimeOffset>(s.F.Time.GetUtcNow())); return; }
        if (s.F.Time.GetUtcNow() - at.Value > TimeSpan.FromMinutes(5)) await Relog(s);
    }

    public static async Task<HttpResponseMessage> Refund(ApiFactory f, string mid, string number, long amount, long shipping = 0, string? refundId = null) =>
        await Event(f, "refund.processed", new { refundId = refundId ?? "re_" + Guid.NewGuid().ToString("N")[..12], marketplaceOrderId = mid, sellerOrderNumber = number, amountCents = amount, shippingCents = shipping });

    // ---- Stripe webhooks ------------------------------------------------------------------------------------------------------
    public static string HookBody(string type, object obj, string? account = null, long? created = null, string? id = null, ApiFactory? f = null) =>
        JsonSerializer.Serialize(new { id = id ?? "evt_" + Guid.NewGuid().ToString("N")[..16], type, created = created ?? f?.Time.GetUtcNow().ToUnixTimeSeconds() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(), account, data = new { @object = obj } }, J);

    public static async Task<HttpResponseMessage> PostHook(ApiFactory f, string body, string? secret = PayFactory.HookSecret, long? ts = null, string? header = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/stripe") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        var t = ts ?? f.Time.GetUtcNow().ToUnixTimeSeconds();
        if (header is not null) req.Headers.Add("Stripe-Signature", header);
        else if (secret is not null) req.Headers.Add("Stripe-Signature", StripeSignature.Header(secret, t, body));
        return await f.NewClient().SendAsync(req);
    }

    public static async Task<HttpResponseMessage> Hook(ApiFactory f, string type, object obj, string? account = null, long? created = null, string? id = null) =>
        await PostHook(f, HookBody(type, obj, account, created, id, f));

    public static object AccountObj(string id, bool details = true, bool payouts = true, string[]? due = null, string[]? past = null, string? disabled = null, string last4 = "4242") => new
    {
        id, details_submitted = details, charges_enabled = payouts, payouts_enabled = payouts,
        requirements = new { currently_due = due ?? [], past_due = past ?? [], disabled_reason = disabled, current_deadline = (long?)null },
        external_accounts = new { data = new[] { new { last4, bank_name = "STRIPE TEST BANK" } } },
        settings = new { payouts = new { schedule = new { interval = "daily", delay_days = 2 } } }
    };

    /// <summary>Owner starts onboarding, then a signed account.updated webhook says everything is fine. Returns the Stripe account id.</summary>
    public static async Task<string> Onboard(Shop s, bool complete = true)
    {
        await Fresh(s);
        var r = await s.Owner.Post("/api/v1/seller/payments/onboarding");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var acct = s.F.WithDb(db => db.PayoutAccounts.AsNoTracking().First(a => a.SellerId == s.Seller).StripeAccountId);
        if (complete)
        {
            Assert.Equal(HttpStatusCode.OK, (await Hook(s.F, "account.updated", AccountObj(acct), acct)).StatusCode);
            s.F.Services.GetRequiredService<FakePaymentProvider>().Complete(acct);
        }
        return acct;
    }

    public static async Task<JsonNode> Json(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
    public static async Task<JsonNode> Ok(HttpResponseMessage r) { Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync()); return await Json(r); }

    public static IReadOnlyList<Solvelt.Api.Email.EmailMessage> Mails(ApiFactory f, string to) => f.Mails(to, "payments");

    public static string OwnerEmail(ApiFactory f, Guid seller) => f.WithDb(db =>
        (from m in db.Memberships join u in db.Users on m.UserId equals u.Id where m.SellerId == seller && m.Role == AccountRole.Owner select u.Email!).First());

    public static List<LedgerJournal> Journals(ApiFactory f, Guid seller, string? type = null) => f.WithDb(db =>
        db.LedgerJournals.AsNoTracking().Include(j => j.Entries).Where(j => j.SellerId == seller && (type == null || j.Type == type)).OrderBy(j => j.CreatedAt).ToList());

    public static long Sum(ApiFactory f, Guid seller, LedgerAccount a) => f.WithDb(db => db.LedgerEntries.Where(e => e.SellerId == seller && e.Account == a).Sum(e => (long?)e.AmountCents) ?? 0);
}
