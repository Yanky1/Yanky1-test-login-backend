using System.Net;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Admin;
using Solvelt.Api.Domain;
using Solvelt.Api.Payments;

namespace Solvelt.Api.Tests;

public class PaymentAdminTests
{
    private const string Base = "/api/v1/admin/payments";

    [Fact]
    public async Task Staff_permission_matrix_for_payment_tools()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var ro = await StaffKit.LoginNew(f, StaffRole.ReadOnly);
        var support = await StaffKit.LoginNew(f, StaffRole.Support);
        var mod = await StaffKit.LoginNew(f, StaffRole.Moderator);
        var (sa, saEmail) = await StaffKit.LoginSteppedUp(f, StaffRole.SuperAdmin);

        foreach (var api in new[] { ro, support, mod, sa })
        {
            Assert.Equal(HttpStatusCode.OK, (await api.Get($"{Base}/sellers/{s.Seller}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await api.Get($"{Base}/sellers/{s.Seller}/ledger")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await api.Get($"{Base}/transfers?status=failed")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await api.Get($"{Base}/payouts?status=failed")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await api.Get($"{Base}/accounts/requirements-past-due")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await api.Get($"{Base}/reconciliation")).StatusCode);
        }
        var hold = new { reason = "Chargeback pattern review" };
        foreach (var api in new[] { ro, mod }) Assert.Equal(HttpStatusCode.Forbidden, (await api.Post($"{Base}/sellers/{s.Seller}/hold", hold)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await support.Post($"{Base}/sellers/{s.Seller}/hold", hold)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post($"{Base}/sellers/{s.Seller}/hold/release", hold)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await support.Post($"{Base}/sellers/{s.Seller}/hold/release", hold)).StatusCode);
        var adj = new { direction = "credit", amountCents = 1000, currency = "USD", reason = "Goodwill credit" };
        foreach (var api in new[] { ro, support, mod }) Assert.Equal(HttpStatusCode.Forbidden, (await api.Post($"{Base}/sellers/{s.Seller}/adjustments", adj)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post($"{Base}/transfers/{Guid.NewGuid()}/retry", hold)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mod.Post($"{Base}/reconciliation/run")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sa.Post($"{Base}/reconciliation/run")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await support.Get($"{Base}/sellers/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Seller_sessions_and_anonymous_callers_cannot_reach_the_payment_admin_api()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Get($"{Base}/transfers", false)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Owner.Get($"{Base}/sellers/{s.Seller}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Owner.Post($"{Base}/sellers/{s.Seller}/hold", new { reason = "I hold myself" })).StatusCode);
    }

    [Fact]
    public async Task Adjustment_needs_super_admin_step_up_reason_valid_amount_and_is_audited_idempotent_and_the_seller_is_told()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var email = await StaffKit.Create(f, StaffRole.SuperAdmin);
        var api = await StaffKit.Login(f, email);
        var body = new { direction = "credit", amountCents = 2500, currency = "USD", reason = "Goodwill: late delivery", requestId = "adj-1" };
        var no = await api.Post($"{Base}/sellers/{s.Seller}/adjustments", body);
        Assert.Equal(HttpStatusCode.Forbidden, no.StatusCode);
        Assert.Equal("step_up_required", await StaffKit.Err(no));
        await StaffKit.StepUp(f, api, email);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await api.Post($"{Base}/sellers/{s.Seller}/adjustments", new { direction = "credit", amountCents = 2500, currency = "USD", reason = "x" })).StatusCode);   // reason too short
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post($"{Base}/sellers/{s.Seller}/adjustments", new { direction = "sideways", amountCents = 2500, currency = "USD", reason = StaffKit.Why })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post($"{Base}/sellers/{s.Seller}/adjustments", new { direction = "credit", amountCents = 0, currency = "USD", reason = StaffKit.Why })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post($"{Base}/sellers/{s.Seller}/adjustments", new { direction = "credit", amountCents = 5_000_000_000, currency = "USD", reason = StaffKit.Why })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post($"{Base}/sellers/{s.Seller}/adjustments", new { direction = "credit", amountCents = 100, currency = "usd", reason = StaffKit.Why })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Post($"{Base}/sellers/{Guid.NewGuid()}/adjustments", body)).StatusCode);
        Assert.Empty(Pay.Journals(f, s.Seller));

        Assert.Equal(HttpStatusCode.OK, (await api.Post($"{Base}/sellers/{s.Seller}/adjustments", body)).StatusCode);
        var again = await Pay.Json(await api.Post($"{Base}/sellers/{s.Seller}/adjustments", body));       // same requestId: nothing new
        Assert.False((bool)again["recorded"]!);
        var adjustment = Assert.Single(Pay.Journals(f, s.Seller, "adjustment"));
        Assert.Equal(("Goodwill: late delivery", StaffKit.UserId(f, email)), (adjustment.Reason, adjustment.ActorUserId));
        Assert.Equal(2500, (await Pay.Bal(f, s.Seller)).Available);
        var audit = f.WithDb(db => db.AuditEvents.Single(a => a.Type == "admin.payments.adjustment"));
        Assert.Equal(("seller", s.Seller.ToString(), "Goodwill: late delivery"), (audit.TargetType, audit.TargetId, audit.Reason));
        Assert.Contains(Pay.Mails(f, Pay.OwnerEmail(f, s.Seller)), m => m.Subject.Contains("adjusted"));

        // a debit that pushes the seller negative also raises the negative-balance notice
        Assert.Equal(HttpStatusCode.OK, (await api.Post($"{Base}/sellers/{s.Seller}/adjustments", new { direction = "debit", amountCents = 4000, currency = "USD", reason = "Recover duplicate credit" })).StatusCode);
        Assert.Equal(-1500, (await Pay.Bal(f, s.Seller)).Available);
        Assert.Contains(Pay.Mails(f, Pay.OwnerEmail(f, s.Seller)), m => m.Subject.Contains("negative"));
        // the seller sees it in the ledger as an adjustment
        var ledger = await Pay.Ok(await s.Owner.Get("/api/v1/seller/payments/ledger?type=adjustment"));
        Assert.Equal(new long[] { -4000, 2500 }, ledger["items"]!.AsArray().Select(x => (long)x!["amountCents"]!).Order().ToArray());
    }

    [Fact]
    public async Task Hold_blocks_transfers_is_single_audited_and_visible_to_staff_only()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        await Pay.Onboard(s);
        await Pay.Paid(s); Pay.Age(f, 8); await Pay.Release(f);
        var api = await StaffKit.LoginNew(f, StaffRole.Support);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await api.Post($"{Base}/sellers/{s.Seller}/hold", new { reason = "no" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Post($"{Base}/sellers/{s.Seller}/hold", new { reason = "Suspected fraud ring" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await api.Post($"{Base}/sellers/{s.Seller}/hold", new { reason = "Suspected fraud ring" })).StatusCode);
        Assert.Equal(0, (await Pay.Transfers(f)).Created);
        var detail = await Pay.Ok(await api.Get($"{Base}/sellers/{s.Seller}"));
        Assert.Equal("Suspected fraud ring", (string)detail["activeHold"]!["reason"]!);
        Assert.StartsWith("acct_fake_", (string)detail["stripe"]!["stripeAccountId"]!);
        Assert.Equal(HttpStatusCode.OK, (await api.Post($"{Base}/sellers/{s.Seller}/hold/release", new { reason = "Review finished, fine" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await api.Post($"{Base}/sellers/{s.Seller}/hold/release", new { reason = "Review finished, fine" })).StatusCode);
        Assert.Equal(1, (await Pay.Transfers(f)).Created);
        Assert.Equal(new[] { "admin.payments.hold_placed", "admin.payments.hold_released" }, f.WithDb(db => db.AuditEvents.Where(a => a.Type.StartsWith("admin.payments.hold")).OrderBy(a => a.At).ThenBy(a => a.Id).Select(a => a.Type).ToList()));
    }

    [Fact]
    public async Task Staff_can_list_and_retry_failed_transfers_and_see_past_due_accounts_and_resolve_flags()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var acct = await Pay.Onboard(s);
        await Pay.Paid(s); Pay.Age(f, 8); await Pay.Release(f);
        f.Fake.FailNextTransfers = 1; f.Fake.FailPermanently = true;
        await Pay.Transfers(f);
        f.Fake.FailPermanently = false;
        var api = await StaffKit.LoginNew(f, StaffRole.Support);
        var failed = await Pay.Ok(await api.Get($"{Base}/transfers?status=failed"));
        var t = Assert.Single(failed["items"]!.AsArray())!;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await api.Post($"{Base}/transfers/{t["id"]}/retry", new { reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Post($"{Base}/transfers/{t["id"]}/retry", new { reason = "Bank issue was fixed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await api.Post($"{Base}/transfers/{t["id"]}/retry", new { reason = "Bank issue was fixed" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Post($"{Base}/transfers/{Guid.NewGuid()}/retry", new { reason = "Bank issue was fixed" })).StatusCode);
        await Pay.Transfers(f);
        Assert.Empty((await Pay.Ok(await api.Get($"{Base}/transfers?status=failed")))["items"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Get($"{Base}/transfers?status=bogus")).StatusCode);

        await Pay.Hook(f, "account.updated", Pay.AccountObj(acct, payouts: false, past: ["company.tax_id"], disabled: "requirements.past_due"), acct, created: f.Time.GetUtcNow().ToUnixTimeSeconds() + 5);
        var past = await Pay.Ok(await api.Get($"{Base}/accounts/requirements-past-due"));
        Assert.Equal(s.Seller.ToString(), (string)past["items"]![0]!["sellerId"]!);

        f.Fake.Transfers[f.WithDb(db => db.PaymentTransfers.First().StripeTransferId!)] = f.Fake.Transfers.Values.First() with { AmountCents = 3 };
        await Pay.Reconcile(f);
        var flag = (await Pay.Ok(await api.Get($"{Base}/reconciliation")))["items"]![0]!;
        Assert.Equal(HttpStatusCode.OK, (await api.Post($"{Base}/reconciliation/{flag["id"]}/resolve", new { reason = "Stripe test data edited" })).StatusCode);
        Assert.Empty((await Pay.Ok(await api.Get($"{Base}/reconciliation")))["items"]!.AsArray());
    }
}
