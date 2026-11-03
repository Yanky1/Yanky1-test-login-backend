using System.Net;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;
using Solvelt.Api.Payments;

namespace Solvelt.Api.Tests;

public class PaymentWebhookTests
{
    [Fact]
    public async Task Valid_signature_is_accepted_wrong_missing_malformed_and_stale_are_rejected_without_effects()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var acct = await Pay.Onboard(s, complete: false);
        var body = Pay.HookBody("account.updated", Pay.AccountObj(acct), acct, f: f);
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay.PostHook(f, body, secret: "whsec_other")).StatusCode);                 // wrong secret
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay.PostHook(f, body, secret: null)).StatusCode);                          // no header
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay.PostHook(f, body, header: "t=abc,v1=zz")).StatusCode);                 // malformed
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay.PostHook(f, body, ts: f.Time.GetUtcNow().ToUnixTimeSeconds() - 3600)).StatusCode);   // stale (validly signed an hour ago)
        var tampered = await Pay.PostHook(f, body, header: StripeSignature.Header(PayFactory.HookSecret, f.Time.GetUtcNow().ToUnixTimeSeconds(), body.Replace("true", "false")));
        Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);                                                             // signature of a different body
        Assert.False(f.WithDb(db => db.PayoutAccounts.Single().PayoutsEnabled));
        Assert.Equal(0, f.WithDb(db => db.ProcessedPaymentEvents.Count()));
        var ok = await Pay.PostHook(f, body);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True(f.WithDb(db => db.PayoutAccounts.Single().PayoutsEnabled));
        // a header with several v1 values (secret rotation) is fine when one matches
        var body2 = Pay.HookBody("account.updated", Pay.AccountObj(acct), acct, f: f);
        var ts = f.Time.GetUtcNow().ToUnixTimeSeconds();
        var multi = $"t={ts},v1=deadbeef,v1={StripeSignature.Sign(PayFactory.HookSecret, ts, body2)}";
        Assert.Equal(HttpStatusCode.OK, (await Pay.PostHook(f, body2, header: multi)).StatusCode);
    }

    [Fact]
    public async Task Replayed_event_ids_are_acknowledged_but_applied_once()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var acct = await Pay.Onboard(s, complete: false);
        var body = Pay.HookBody("account.updated", Pay.AccountObj(acct, payouts: false, due: ["external_account"]), acct, f: f);
        var first = await Pay.Json(await Pay.PostHook(f, body));
        Assert.Equal("ok", (string)first["status"]!);
        var again = await Pay.Json(await Pay.PostHook(f, body));
        Assert.Equal("duplicate", (string)again["status"]!);
        Assert.Equal(1, f.WithDb(db => db.ProcessedPaymentEvents.Count(p => p.Source == "stripe")));
        // an old, validly signed capture of the same bytes inside the window is the same event id: still a duplicate
        Assert.Equal("duplicate", (string)(await Pay.Json(await Pay.PostHook(f, body, ts: f.Time.GetUtcNow().ToUnixTimeSeconds() - 120)))["status"]!);
    }

    [Fact]
    public async Task Without_a_configured_secret_the_endpoint_refuses_everything()
    {
        using var f = new ApiFactory();     // no Stripe:WebhookSecret
        var r = await Pay.PostHook(f, Pay.HookBody("account.updated", new { id = "acct_x" }), secret: "whatever");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
    }

    [Fact]
    public async Task Bad_json_unknown_types_and_unknown_accounts_are_handled_safely()
    {
        using var f = new PayFactory();
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay.PostHook(f, "not json")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay.PostHook(f, "{\"id\":\"e\"}")).StatusCode);
        var r = await Pay.Json(await Pay.Hook(f, "customer.created", new { id = "cus_1" }));
        Assert.Equal("ignored_type", (string)r["outcome"]!);
        var u = await Pay.Json(await Pay.Hook(f, "account.updated", Pay.AccountObj("acct_unknown"), "acct_unknown"));
        Assert.Equal("ignored_unknown_account", (string)u["outcome"]!);
        var p = await Pay.Json(await Pay.Hook(f, "payout.paid", new { id = "po_x", amount = 100, currency = "usd" }, "acct_unknown"));
        Assert.Equal("ignored_unknown_account", (string)p["outcome"]!);
    }

    [Fact]
    public async Task Stripe_dispute_events_are_matched_through_the_payment_reference_and_debit_the_seller()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Delivered(s);
        await Pay.Capture(f, o.Mid, o.Number, paymentRef: "pi_777");
        var due = f.Time.GetUtcNow().ToUnixTimeSeconds() + 7 * 86400;
        var dp = new { id = "dp_1", charge = "ch_other", payment_intent = "pi_777", amount = 2499, currency = "usd", status = "needs_response", reason = "fraudulent", evidence_details = new { due_by = due } };
        Assert.Equal(HttpStatusCode.OK, (await Pay.Hook(f, "charge.dispute.created", dp)).StatusCode);
        var b = await Pay.Bal(f, s.Seller);
        Assert.Equal(-2499 - 1500, b.Available);                                  // amount + default dispute fee
        Assert.Equal(2299, b.Pending);
        var row = f.WithDb(db => db.PaymentDisputes.Single());
        Assert.Equal(("needs_response", o.Id), (row.Status, row.OrderId));
        Assert.Equal(due, new DateTimeOffset(DateTime.SpecifyKind(row.EvidenceDueBy!.Value, DateTimeKind.Utc)).ToUnixTimeSeconds());
        Assert.Contains(Pay.Mails(f, Pay.OwnerEmail(f, s.Seller)), m => m.Subject.Contains("dispute"));

        // evidence submitted, then won: the amount comes back (the dispute fee does not)
        await Pay.Hook(f, "charge.dispute.updated", new { id = "dp_1", payment_intent = "pi_777", amount = 2499, currency = "usd", status = "under_review" }, created: f.Time.GetUtcNow().ToUnixTimeSeconds() + 10);
        Assert.Equal("under_review", f.WithDb(db => db.PaymentDisputes.Single().Status));
        await Pay.Hook(f, "charge.dispute.closed", new { id = "dp_1", payment_intent = "pi_777", amount = 2499, currency = "usd", status = "won" }, created: f.Time.GetUtcNow().ToUnixTimeSeconds() + 20);
        b = await Pay.Bal(f, s.Seller);
        Assert.Equal(-1500, b.Available);
        Assert.Equal("won", f.WithDb(db => db.PaymentDisputes.Single().Status));
        // a late "under_review" after the close must not reopen it
        await Pay.Hook(f, "charge.dispute.updated", new { id = "dp_1", payment_intent = "pi_777", amount = 2499, currency = "usd", status = "under_review" }, created: f.Time.GetUtcNow().ToUnixTimeSeconds() + 15);
        Assert.Equal("won", f.WithDb(db => db.PaymentDisputes.Single().Status));
        Assert.Equal(-1500, (await Pay.Bal(f, s.Seller)).Available);
        Assert.Equal(0, Pay.Journals(f, s.Seller).Sum(j => j.Entries.Sum(e => e.AmountCents)));
    }

    [Fact]
    public async Task A_dispute_that_cannot_be_attributed_is_flagged_for_staff_not_guessed()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var dp = new { id = "dp_x", payment_intent = "pi_never_seen", amount = 1000, currency = "usd", status = "needs_response" };
        var r = await Pay.Json(await Pay.Hook(f, "charge.dispute.created", dp));
        Assert.Equal("flagged_unattributed", (string)r["outcome"]!);
        Assert.Equal("dispute_unattributed", f.WithDb(db => db.ReconciliationFlags.Single().Kind));
        Assert.Empty(Pay.Journals(f, s.Seller));
    }

    [Fact]
    public async Task Marketplace_dispute_events_debit_credit_and_are_idempotent_and_order_independent()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Paid(s);
        Pay.Age(f, 8); await Pay.Release(f);
        var created = new { disputeId = "dp_m1", marketplaceOrderId = o.Mid, sellerOrderNumber = o.Number, amountCents = 2499L, feeCents = 1500L, reason = "product_not_received", evidenceDueBy = f.Time.GetUtcNow().UtcDateTime.AddDays(7) };
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "dispute.created", created, "d-1")).StatusCode);
        Assert.Equal("duplicate", (string)(await Pay.Json(await Pay.Event(f, "dispute.created", created, "d-1")))["status"]!);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "dispute.created", created, "d-1b")).StatusCode);   // another delivery of the same dispute under a new event id
        Assert.Equal(2299 - 2499 - 1500, (await Pay.Bal(f, s.Seller)).Available);
        Assert.Single(Pay.Journals(f, s.Seller, "dispute")); Assert.Single(Pay.Journals(f, s.Seller, "dispute_fee"));

        // the close arrives (won), then a stale update: won stays, credit once
        var won = new { disputeId = "dp_m1", status = "won" };
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "dispute.closed", won, "d-2")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "dispute.closed", won, "d-3")).StatusCode);
        Assert.Equal(2299 - 1500, (await Pay.Bal(f, s.Seller)).Available);
        Assert.Single(Pay.Journals(f, s.Seller, "dispute_won"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Pay.Event(f, "dispute.closed", new { disputeId = "dp_m1", status = "pending" }, "d-4")).StatusCode);

        // a dispute whose first event is the closing one (lost) is still debited
        var lost = new { disputeId = "dp_m2", marketplaceOrderId = o.Mid, sellerOrderNumber = o.Number, amountCents = 100L, status = "lost" };
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "dispute.closed", lost, "d-5")).StatusCode);
        Assert.Equal(2299 - 1500 - 100 - 1500, (await Pay.Bal(f, s.Seller)).Available);
        await Pay.Relog(s);
        var list = await Pay.Ok(await s.Owner.Get("/api/v1/seller/payments/disputes"));
        Assert.Equal(2, list["items"]!.AsArray().Count);
        var d1 = list["items"]!.AsArray().First(x => (long)x!["amountCents"]! == 2499)!;
        Assert.Equal(("won", o.Number, false), (((string)d1["status"]!), (string)d1["orderNumber"]!, (bool)d1["canRespond"]!));
        Assert.NotNull(d1["evidenceDueBy"]);
    }

    [Fact]
    public async Task The_same_dispute_reported_by_the_marketplace_and_by_stripe_is_one_dispute()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Delivered(s);
        await Pay.Capture(f, o.Mid, o.Number, paymentRef: "ch_same");
        await Pay.Event(f, "dispute.created", new { disputeId = "dp_same", marketplaceOrderId = o.Mid, sellerOrderNumber = o.Number, amountCents = 500L });
        await Pay.Hook(f, "charge.dispute.created", new { id = "dp_same", charge = "ch_same", amount = 500, currency = "usd", status = "needs_response" });
        Assert.Equal(1, f.WithDb(db => db.PaymentDisputes.Count()));
        Assert.Equal(-500 - 1500, (await Pay.Bal(f, s.Seller)).Available);
    }
}
