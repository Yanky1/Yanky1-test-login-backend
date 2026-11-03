using System.Net;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Tests;

public class PaymentOnboardingTests
{
    [Fact]
    public async Task Owner_starts_onboarding_creates_one_express_account_per_seller_and_reuses_it()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var before = await Pay.Json(await s.Owner.Get("/api/v1/seller/payments/onboarding"));
        Assert.Equal("not_started", (string)before["state"]!);

        var r1 = await Pay.Ok(await s.Owner.Post("/api/v1/seller/payments/onboarding"));
        Assert.StartsWith("https://connect.fake-stripe.test/setup/acct_fake_", (string)r1["url"]!);
        var r2 = await Pay.Ok(await s.Owner.Post("/api/v1/seller/payments/onboarding"));
        Assert.Equal((string)r1["url"]!, (string)r2["url"]!);
        Assert.Equal(1, f.WithDb(db => db.PayoutAccounts.Count(a => a.SellerId == s.Seller)));
        Assert.Equal(1, f.Fake.Accounts.Count);
        Assert.Equal("in_progress", (string)r2["onboarding"]!["state"]!);
    }

    [Fact]
    public async Task Only_owners_start_onboarding_or_open_the_dashboard_admins_only_read()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var (admin, _) = await TeamHelpers.JoinAs(f, s.Owner, "Admin");
        Assert.Equal(HttpStatusCode.OK, (await admin.Get("/api/v1/seller/payments/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.Get("/api/v1/seller/payments/onboarding")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Post("/api/v1/seller/payments/onboarding")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Post("/api/v1/seller/payments/dashboard-link")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Post("/api/v1/seller/payments/onboarding/sync")).StatusCode);
        Assert.Equal(0, f.Fake.Accounts.Count);
    }

    [Fact]
    public async Task Account_updated_webhook_syncs_status_and_next_steps_with_one_email()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var acct = await Pay.Onboard(s, complete: false);
        var obj = Pay.AccountObj(acct, details: true, payouts: false, due: ["individual.verification.document", "external_account"], past: []);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Hook(f, "account.updated", obj, acct)).StatusCode);

        var st = await Pay.Json(await s.Owner.Get("/api/v1/seller/payments/onboarding"));
        Assert.Equal("action_required", (string)st["state"]!);
        Assert.Contains(st["nextSteps"]!.AsArray(), x => ((string)x!).Contains("bank account"));
        Assert.Contains(st["nextSteps"]!.AsArray(), x => ((string)x!).Contains("document"));
        Assert.Equal("4242", (string)st["payoutSettings"]!["bankLast4"]!);
        Assert.DoesNotContain("acct_", st.ToJsonString().Replace(acct, "")); // only the connected account id could appear, and the seller API never returns it
        Assert.Single(Pay.Mails(f, Pay.OwnerEmail(f, s.Seller)));

        Assert.Equal(HttpStatusCode.OK, (await Pay.Hook(f, "account.updated", obj, acct)).StatusCode);   // same requirements again: no second mail
        Assert.Single(Pay.Mails(f, Pay.OwnerEmail(f, s.Seller)));

        await Pay.Hook(f, "account.updated", Pay.AccountObj(acct, past: ["company.tax_id"], payouts: false, disabled: "requirements.past_due"), acct);
        var st2 = await Pay.Json(await s.Owner.Get("/api/v1/seller/payments/onboarding"));
        Assert.Equal("restricted", (string)st2["state"]!);
        Assert.Equal(2, Pay.Mails(f, Pay.OwnerEmail(f, s.Seller)).Count);

        await Pay.Hook(f, "account.updated", Pay.AccountObj(acct), acct);
        Assert.Equal("active", (string)(await Pay.Json(await s.Owner.Get("/api/v1/seller/payments/onboarding")))["state"]!);
    }

    [Fact]
    public async Task An_older_account_updated_never_overwrites_newer_state()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var acct = await Pay.Onboard(s, complete: false);
        var now = f.Time.GetUtcNow().ToUnixTimeSeconds();
        await Pay.Hook(f, "account.updated", Pay.AccountObj(acct), acct, created: now);
        var stale = await Pay.Hook(f, "account.updated", Pay.AccountObj(acct, payouts: false, due: ["external_account"]), acct, created: now - 100);
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        Assert.True(f.WithDb(db => db.PayoutAccounts.Single(a => a.SellerId == s.Seller).PayoutsEnabled));
    }

    [Fact]
    public async Task Dashboard_link_needs_finished_onboarding_and_sync_pulls_from_stripe()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        await Pay.Onboard(s, complete: false);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Owner.Post("/api/v1/seller/payments/dashboard-link")).StatusCode);
        var acct = f.WithDb(db => db.PayoutAccounts.Single().StripeAccountId);
        f.Fake.Complete(acct);
        var sync = await Pay.Ok(await s.Owner.Post("/api/v1/seller/payments/onboarding/sync"));
        Assert.Equal("active", (string)sync["state"]!);
        var link = await Pay.Ok(await s.Owner.Post("/api/v1/seller/payments/dashboard-link"));
        Assert.Contains("/express/", (string)link["url"]!);
    }

    [Fact]
    public async Task Onboarding_is_refused_until_the_seller_is_approved()
    {
        using var f = new PayFactory();
        var (api, _) = await ProdApi.Approved(f, approve: false);
        var r = await api.Post("/api/v1/seller/payments/onboarding");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("not_approved", await Orders.Code(r));
    }
}
