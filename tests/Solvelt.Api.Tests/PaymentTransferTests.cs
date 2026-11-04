using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Domain;
using Solvelt.Api.Payments;

namespace Solvelt.Api.Tests;

public class PaymentTransferTests
{
    private static async Task<(Shop S, string Acct)> Ready(ApiFactory f, int orders = 1)
    {
        var s = await Orders.NewShop(f);
        var acct = await Pay.Onboard(s);
        for (var i = 0; i < orders; i++) await Pay.Paid(s);
        Pay.Age(f, 8);
        await Pay.Release(f);
        return (s, acct);
    }

    private static List<PaymentTransfer> All(ApiFactory f, Guid seller) => f.WithDb(db => db.PaymentTransfers.AsNoTracking().Where(t => t.SellerId == seller).OrderBy(t => t.CreatedAt).ToList());

    [Fact]
    public async Task One_transfer_per_seller_and_day_batches_all_released_funds_and_moves_the_ledger()
    {
        using var f = new PayFactory();
        var (s, acct) = await Ready(f, orders: 3);
        var r = await Pay.Transfers(f);
        Assert.Equal((1, 1), (r.Created, r.Sent));
        var t = Assert.Single(All(f, s.Seller));
        Assert.Equal((3 * 2299L, TransferStatus.Created, acct), (t.AmountCents, t.Status, t.DestinationAccountId));
        Assert.StartsWith("tr_fake_", t.StripeTransferId);
        var b = await Pay.Bal(f, s.Seller);
        Assert.Equal((0, 3 * 2299L), (b.Available, b.Settled));
        Assert.Equal(1, f.Fake.TransferCreates);
        // same day again: nothing more, even with new money
        await Pay.Paid(s); Assert.Equal(0, (await Pay.Transfers(f)).Created);
        Assert.Single(All(f, s.Seller));
    }

    [Fact]
    public async Task Below_the_minimum_amount_waits_for_more_funds()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        await Pay.Onboard(s);
        await Pay.Paid(s, price: 300, shipping: 0);     // net 270 < 500
        Pay.Age(f, 8); await Pay.Release(f);
        var r = await Pay.Transfers(f);
        Assert.Equal((0, 1), (r.Created, r.Skipped));
        Assert.Empty(All(f, s.Seller));
    }

    [Fact]
    public async Task Transfers_are_paused_without_a_payout_account_or_with_payouts_disabled()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        await Pay.Paid(s); Pay.Age(f, 8); await Pay.Release(f);
        Assert.Equal(1, (await Pay.Transfers(f)).Paused);                              // no connected account yet
        var acct = await Pay.Onboard(s, complete: false);
        Assert.Equal(1, (await Pay.Transfers(f)).Paused);                              // account exists, payouts not enabled
        Assert.Empty(All(f, s.Seller));
        await Pay.Hook(f, "account.updated", Pay.AccountObj(acct), acct);
        f.Services.GetRequiredService<FakePaymentProvider>().Complete(acct);
        Assert.Equal(1, (await Pay.Transfers(f)).Created);
        // Stripe disables payouts later: the next batch (next day) is paused
        await Pay.Paid(s); Pay.Age(f, 8); await Pay.Release(f);
        await Pay.Hook(f, "account.updated", Pay.AccountObj(acct, payouts: false, disabled: "rejected.fraud"), acct, created: f.Time.GetUtcNow().ToUnixTimeSeconds() + 5);
        Assert.Equal(1, (await Pay.Transfers(f)).Paused);
        Assert.Single(All(f, s.Seller));
    }

    [Fact]
    public async Task Suspended_sellers_and_staff_holds_pause_transfers_until_lifted()
    {
        using var f = new PayFactory();
        var (s, _) = await Ready(f);
        f.WithDb(db => { db.Sellers.First(x => x.Id == s.Seller).Status = SellerStatus.Suspended; db.SaveChanges(); return 0; });
        Assert.Equal((0, 1), ((await Pay.Transfers(f)).Created, 1));
        Assert.Empty(All(f, s.Seller));
        f.WithDb(db => { db.Sellers.First(x => x.Id == s.Seller).Status = SellerStatus.Approved; db.SaveChanges(); return 0; });

        var hold = new PayoutHold { SellerId = s.Seller, Reason = "chargeback pattern", PlacedBy = Guid.NewGuid(), PlacedAt = f.Time.GetUtcNow().UtcDateTime };
        f.WithDb(db => { db.PayoutHolds.Add(hold); db.SaveChanges(); return 0; });
        Assert.Equal(0, (await Pay.Transfers(f)).Created);
        Assert.Empty(All(f, s.Seller));
        await Pay.Relog(s);
        var sum = await Pay.Ok(await s.Owner.Get("/api/v1/seller/payments/summary"));
        Assert.True((bool)sum["payoutsPaused"]!);
        Assert.Equal("under_review", (string)sum["pausedReason"]!);
        Assert.DoesNotContain("chargeback", sum.ToJsonString());                        // the staff reason never reaches the seller

        f.WithDb(db => { db.PayoutHolds.First(h => h.Id == hold.Id).ReleasedAt = f.Time.GetUtcNow().UtcDateTime; db.SaveChanges(); return 0; });
        Assert.Equal(1, (await Pay.Transfers(f)).Created);
    }

    [Fact]
    public async Task Parallel_job_runs_produce_exactly_one_transfer_and_one_stripe_call()
    {
        using var f = new PayFactory();
        var (s, _) = await Ready(f, orders: 2);
        var gate = new TaskCompletionSource();
        f.Fake.BeforeTransfer = async _ => await gate.Task;      // hold the first send so the runs really overlap
        var runs = Enumerable.Range(0, 5).Select(_ => Task.Run(() => Pay.Transfers(f))).ToArray();
        await Task.Delay(500); gate.SetResult();
        var results = await Task.WhenAll(runs);
        Assert.Single(All(f, s.Seller));
        Assert.Equal(1, results.Sum(r => r.Created));
        Assert.Equal(1, f.Fake.TransferCreates);
        Assert.Equal(2 * 2299, All(f, s.Seller)[0].AmountCents);
        Assert.Equal((0, 2 * 2299L), ((await Pay.Bal(f, s.Seller)).Available, (await Pay.Bal(f, s.Seller)).Settled));
    }

    [Fact]
    public async Task The_job_lease_lets_only_one_instance_in_and_expires()
    {
        using var f = new PayFactory();
        Assert.NotNull(await Pay.Lease(f, "payments-transfers"));
        Assert.Null(await Pay.Lease(f, "payments-transfers"));
        f.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.NotNull(await Pay.Lease(f, "payments-transfers"));
    }

    [Fact]
    public async Task Transient_failures_are_retried_with_backoff_using_the_same_idempotency_key()
    {
        using var f = new PayFactory();
        var (s, _) = await Ready(f);
        f.Fake.FailNextTransfers = 2;
        var r1 = await Pay.Transfers(f);
        Assert.Equal((1, 0), (r1.Created, r1.Sent));
        var t = All(f, s.Seller)[0];
        Assert.Equal((TransferStatus.Pending, 1), (t.Status, t.Attempts));
        Assert.Equal(0, (await Pay.Transfers(f)).Sent);                 // backoff not over
        f.Time.Advance(TimeSpan.FromSeconds(61));
        await Pay.Transfers(f);
        Assert.Equal(2, All(f, s.Seller)[0].Attempts);
        f.Time.Advance(TimeSpan.FromSeconds(121));
        var r3 = await Pay.Transfers(f);
        Assert.Equal(1, r3.Sent);
        t = All(f, s.Seller)[0];
        Assert.Equal((TransferStatus.Created, 3), (t.Status, t.Attempts));
        Assert.Equal(1, f.Fake.TransferCreates);
        Assert.Equal((0, 2299L), ((await Pay.Bal(f, s.Seller)).Available, (await Pay.Bal(f, s.Seller)).Settled));
    }

    [Fact]
    public async Task A_permanent_failure_returns_the_funds_alerts_the_owner_and_staff_can_retry_it()
    {
        using var f = new PayFactory();
        var (s, _) = await Ready(f);
        f.Fake.FailNextTransfers = 1; f.Fake.FailPermanently = true;
        var r = await Pay.Transfers(f);
        Assert.Equal(1, r.Failed);
        var t = All(f, s.Seller)[0];
        Assert.Equal(TransferStatus.Failed, t.Status);
        var b = await Pay.Bal(f, s.Seller);
        Assert.Equal((2299, 0), (b.Available, b.Settled));              // money is back with the seller, nothing is stuck
        Assert.Contains(Pay.Mails(f, Pay.OwnerEmail(f, s.Seller)), m => m.Subject.Contains("transfer"));

        f.Fake.FailPermanently = false;
        var (ok, code) = await Pay.Run(f, sp => sp.GetRequiredService<TransferService>().RetryAsync(t.Id));
        Assert.True(ok, code);
        Assert.False((await Pay.Run(f, sp => sp.GetRequiredService<TransferService>().RetryAsync(t.Id))).Ok);   // no longer failed
        await Pay.Transfers(f);
        t = All(f, s.Seller)[0];
        Assert.Equal((TransferStatus.Created, 1), (t.Status, t.Retries));
        Assert.Equal((0, 2299L), ((await Pay.Bal(f, s.Seller)).Available, (await Pay.Bal(f, s.Seller)).Settled));
        Assert.Equal(1, f.Fake.TransferCreates);
    }

    [Fact]
    public async Task Transfer_webhooks_are_idempotent_order_independent_and_a_reversal_returns_the_funds()
    {
        using var f = new PayFactory();
        var (s, _) = await Ready(f);
        await Pay.Transfers(f);
        var t = All(f, s.Seller)[0];
        var now = f.Time.GetUtcNow().ToUnixTimeSeconds();
        var obj = new { id = t.StripeTransferId, metadata = new { transferId = t.Id.ToString() }, reversed = false };
        // reversal arrives first, then an older "created": the older event must not bring the transfer back to life
        Assert.Equal(HttpStatusCode.OK, (await Pay.Hook(f, "transfer.reversed", new { id = t.StripeTransferId, reversed = true }, created: now + 10, id: "evt_rev")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Hook(f, "transfer.created", obj, created: now)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Hook(f, "transfer.reversed", new { id = t.StripeTransferId, reversed = true }, created: now + 10, id: "evt_rev")).StatusCode);   // replay
        Assert.Equal(TransferStatus.Reversed, All(f, s.Seller)[0].Status);
        var b = await Pay.Bal(f, s.Seller);
        Assert.Equal((2299, 0), (b.Available, b.Settled));
        Assert.Single(Pay.Journals(f, s.Seller, "transfer_reversed"));
        // unknown transfer ids are acknowledged and ignored
        Assert.Equal(HttpStatusCode.OK, (await Pay.Hook(f, "transfer.created", new { id = "tr_unknown" })).StatusCode);
    }

    [Fact]
    public async Task Reconciliation_flags_a_ledger_transfer_that_stripe_does_not_match_once()
    {
        using var f = new PayFactory();
        var (s, _) = await Ready(f);
        await Pay.Transfers(f);
        Assert.Equal(0, (await Pay.Reconcile(f)).Flagged);
        var t = All(f, s.Seller)[0];
        f.Fake.Transfers[t.StripeTransferId!] = f.Fake.Transfers[t.StripeTransferId!] with { AmountCents = 1 };
        var r = await Pay.Reconcile(f);
        Assert.Equal(1, r.Flagged);
        Assert.Equal(0, (await Pay.Reconcile(f)).Flagged);                        // the open flag is not duplicated
        var flag = f.WithDb(db => db.ReconciliationFlags.Single());
        Assert.Equal((t.Id, "transfer_mismatch"), (flag.TransferId, flag.Kind));
        f.Fake.Transfers.TryRemove(t.StripeTransferId!, out _);
        Assert.Equal(0, (await Pay.Reconcile(f)).Flagged);                        // still the same open flag
    }

    [Fact]
    public async Task Seller_sees_transfers_and_payouts_with_status_and_payout_webhooks_drive_the_informational_ledger()
    {
        using var f = new PayFactory();
        var (s, acct) = await Ready(f);
        await Pay.Transfers(f);
        var now = f.Time.GetUtcNow().ToUnixTimeSeconds();
        var po = new { id = "po_1", amount = 2299, currency = "usd", status = "paid", arrival_date = now + 86400 };
        Assert.Equal(HttpStatusCode.OK, (await Pay.Hook(f, "payout.created", new { id = "po_1", amount = 2299, currency = "usd", status = "pending" }, acct, now)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Hook(f, "payout.paid", po, acct, now + 5)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Hook(f, "payout.created", new { id = "po_1", amount = 2299, currency = "usd", status = "pending" }, acct, now + 1)).StatusCode);   // late, older event
        var b = await Pay.Bal(f, s.Seller);
        Assert.Equal((0, 2299L), (b.Settled, b.PaidOut));
        Assert.Equal("paid", f.WithDb(db => db.PaymentPayouts.Single().Status));

        await Pay.Relog(s);
        var tr = await Pay.Ok(await s.Owner.Get("/api/v1/seller/payments/transfers"));
        Assert.Equal("sent", (string)tr["items"]![0]!["status"]!);
        var pays = await Pay.Ok(await s.Owner.Get("/api/v1/seller/payments/payouts"));
        Assert.Equal("paid", (string)pays["items"]![0]!["status"]!);

        // the bank returns it: the payout fails after being paid
        Assert.Equal(HttpStatusCode.OK, (await Pay.Hook(f, "payout.failed", new { id = "po_1", amount = 2299, currency = "usd", failure_code = "account_closed" }, acct, now + 60)).StatusCode);
        b = await Pay.Bal(f, s.Seller);
        Assert.Equal((2299L, 0), (b.Settled, b.PaidOut));
        Assert.Contains(Pay.Mails(f, Pay.OwnerEmail(f, s.Seller)), m => m.Subject.Contains("payout"));
    }
}
