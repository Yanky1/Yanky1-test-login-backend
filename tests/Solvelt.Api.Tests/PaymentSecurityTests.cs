using Microsoft.Extensions.DependencyInjection;
using System.Net;
using Solvelt.Api.Domain;
using Solvelt.Api.Payments;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Tests;

public class PaymentSecurityTests
{
    public static IEnumerable<object[]> SellerGets() =>
    [
        ["/api/v1/seller/payments/summary"], ["/api/v1/seller/payments/ledger"], ["/api/v1/seller/payments/transfers"], ["/api/v1/seller/payments/payouts"],
        ["/api/v1/seller/payments/disputes"], ["/api/v1/seller/payments/statements"], ["/api/v1/seller/payments/statements/2026-01"], ["/api/v1/seller/payments/statements/2026-01/csv"],
        ["/api/v1/seller/payments/onboarding"]
    ];

    [Theory]
    [MemberData(nameof(SellerGets))]
    public async Task Only_owners_and_admins_read_payments_managers_viewers_and_anonymous_get_nothing(string url)
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var (admin, _) = await TeamHelpers.JoinAs(f, s.Owner, "Admin");
        var (manager, _) = await TeamHelpers.JoinAs(f, s.Owner, "Manager");
        var (viewer, _) = await TeamHelpers.JoinAs(f, s.Owner, "Viewer");
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Get(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.Get(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.Get(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Get(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Get(url, false)).StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/seller/payments/onboarding")]
    [InlineData("/api/v1/seller/payments/onboarding/sync")]
    [InlineData("/api/v1/seller/payments/dashboard-link")]
    public async Task Manager_and_viewer_cannot_start_onboarding_or_open_the_dashboard(string url)
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var (manager, _) = await TeamHelpers.JoinAs(f, s.Owner, "Manager");
        var (viewer, _) = await TeamHelpers.JoinAs(f, s.Owner, "Viewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.Post(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post(url)).StatusCode);
        Assert.Empty(f.Fake.Accounts);
    }

    [Fact]
    public void Central_permission_matrix_payments_rows()
    {
        Assert.True(Permissions.Has(AccountRole.Owner, Permission.PaymentsRead));
        Assert.True(Permissions.Has(AccountRole.Owner, Permission.PaymentsManage));
        Assert.True(Permissions.Has(AccountRole.Admin, Permission.PaymentsRead));
        Assert.False(Permissions.Has(AccountRole.Admin, Permission.PaymentsManage));
        foreach (var r in new[] { AccountRole.Manager, AccountRole.Viewer })
        {
            Assert.False(Permissions.Has(r, Permission.PaymentsRead));
            Assert.False(Permissions.Has(r, Permission.PaymentsManage));
        }
    }

    [Fact]
    public async Task Another_accounts_payment_data_is_never_visible_through_any_listing()
    {
        using var f = new PayFactory();
        var a = await Orders.NewShop(f);
        var b = await Orders.NewShop(f);
        await Pay.Onboard(a);
        var o = await Pay.Paid(a); Pay.Age(f, 8); await Pay.Release(f); await Pay.Transfers(f);
        await Pay.Event(f, "dispute.created", new { disputeId = "dp_a", marketplaceOrderId = o.Mid, sellerOrderNumber = o.Number, amountCents = 100L });
        await Pay.Relog(a); await Pay.Relog(b);
        foreach (var url in new[] { "ledger", "transfers", "payouts", "disputes" })
        {
            Assert.NotEmpty((await Pay.Ok(await a.Owner.Get("/api/v1/seller/payments/" + url)))["items"]!.AsArray().Where(_ => url is "ledger" or "transfers" or "disputes").DefaultIfEmpty(new System.Text.Json.Nodes.JsonObject()).ToArray());
            Assert.Empty((await Pay.Ok(await b.Owner.Get("/api/v1/seller/payments/" + url)))["items"]!.AsArray());
        }
        var summary = await Pay.Ok(await b.Owner.Get("/api/v1/seller/payments/summary"));
        Assert.Empty(summary["balances"]!.AsArray());
        Assert.Equal("not_started", (string)summary["onboarding"]!["state"]!);
        Assert.Empty((await Pay.Ok(await b.Owner.Get("/api/v1/seller/payments/statements")))["months"]!.AsArray());
    }

    [Fact]
    public async Task Statement_csv_is_formula_safe_keeps_numbers_numeric_and_is_scoped()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var other = await Orders.NewShop(f);
        await Pay.Paid(s);
        var now = f.Time.GetUtcNow().UtcDateTime;
        Pay.Run(f, async sp =>
        {
            var l = sp.GetRequiredService<LedgerService>();
            await l.PostAsync(s.Seller, "USD", "adjustment", "k-evil", "=HYPERLINK(\"http://evil\",\"x\")", [new(LedgerAccount.Available, -700, "adjustment"), new(LedgerAccount.Platform, 700, "adjustment")]);
            await l.PostAsync(other.Seller, "USD", "adjustment", "k-other", "OTHER-SELLER-ROW", [new(LedgerAccount.Available, 5, "adjustment"), new(LedgerAccount.Platform, -5, "adjustment")]);
            await l.SaveAsync(); return 0;
        }).GetAwaiter().GetResult();
        var month = now.ToString("yyyy-MM");
        var r = await s.Owner.Get($"/api/v1/seller/payments/statements/{month}/csv");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.StartsWith("text/csv", r.Content.Headers.ContentType!.ToString());
        var csv = await r.Content.ReadAsStringAsync();
        Assert.Contains("\"'=HYPERLINK(", csv);
        Assert.DoesNotContain("\"=HYPERLINK", csv);
        Assert.DoesNotContain("OTHER-SELLER-ROW", csv);
        Assert.Contains(",-7.00", csv);
        Assert.Contains(",22.99", csv);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Owner.Get("/api/v1/seller/payments/statements/2026-13/csv")).StatusCode);
        var st = await Pay.Ok(await s.Owner.Get($"/api/v1/seller/payments/statements/{month}"));
        var c = st["currencies"]![0]!;
        Assert.Equal((2499L, -200L, -700L, 2299L - 700), ((long)c["salesCents"]!, (long)c["feesCents"]!, (long)c["adjustmentsCents"]!, (long)c["closingCents"]!));
    }

    [Theory]
    [InlineData("sk_test_abc", "Production", false, false, true)]     // test key in production: refused
    [InlineData("sk_test_abc", "Production", true, false, false)]     // ... unless allowed on purpose
    [InlineData("sk_live_abc", "Production", false, false, false)]
    [InlineData("sk_live_abc", "Staging", false, false, true)]        // live key outside production: refused
    [InlineData("sk_live_abc", "Staging", false, true, false)]
    [InlineData("sk_test_abc", "Development", false, false, false)]
    [InlineData("pk_test_abc", "Development", false, false, true)]    // a publishable key is not a secret key
    [InlineData("", "Production", false, false, false)]               // no key: unconfigured provider, not a crash
    public void Key_guard(string key, string env, bool allowTest, bool allowLive, bool throws)
    {
        var call = () => PaymentsModule.ValidateKey(key, env == "Production", allowTest, allowLive);
        if (throws) Assert.Throws<InvalidOperationException>(call); else call();
    }

    [Fact]
    public async Task Webhook_and_internal_endpoints_do_not_accept_seller_tokens_and_the_dev_helpers_exist_only_in_testing()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/stripe") { Content = new StringContent("{}") };
        req.Headers.Authorization = new("Bearer", s.Owner.Session!.AccessToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.NewClient().SendAsync(req)).StatusCode);   // judged by its signature only
        Assert.Equal(HttpStatusCode.OK, (await f.NewClient().PostAsync("/api/dev/payments/run", null)).StatusCode);
    }

    [Fact]
    public async Task Payment_emails_go_to_owners_and_opted_in_admins_only_once_per_event()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var (admin, adminEmail) = await TeamHelpers.JoinAs(f, s.Owner, "Admin");
        var (optIn, optInEmail) = await TeamHelpers.JoinAs(f, s.Owner, "Admin");
        var (_, managerEmail) = await TeamHelpers.JoinAs(f, s.Owner, "Manager");
        Assert.Equal(HttpStatusCode.OK, (await optIn.Put("/api/v1/seller/notification-preferences", new { paymentEmails = true })).StatusCode);
        var pref = await Pay.Ok(await optIn.Get("/api/v1/seller/notification-preferences"));
        Assert.True((bool)pref["paymentEmails"]!);
        Assert.False((bool)(await Pay.Ok(await admin.Get("/api/v1/seller/notification-preferences")))["paymentEmails"]!);
        Assert.True((bool)(await Pay.Ok(await s.Owner.Get("/api/v1/seller/notification-preferences")))["paymentEmails"]!);

        Assert.True(await Pay.Run(f, sp => sp.GetRequiredService<PaymentNotifier>().NotifyAsync(s.Seller, "k1", "Subject one", "Heading", ["Line"])));
        Assert.False(await Pay.Run(f, sp => sp.GetRequiredService<PaymentNotifier>().NotifyAsync(s.Seller, "k1", "Subject one", "Heading", ["Line"])));   // same key: not again
        Assert.Single(Pay.Mails(f, Pay.OwnerEmail(f, s.Seller)));
        Assert.Single(Pay.Mails(f, optInEmail));
        Assert.Empty(Pay.Mails(f, adminEmail));
        Assert.Empty(Pay.Mails(f, managerEmail));
    }
}
