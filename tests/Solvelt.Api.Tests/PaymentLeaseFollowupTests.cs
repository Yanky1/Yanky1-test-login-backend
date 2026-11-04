using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Domain;
using Solvelt.Api.Payments;

namespace Solvelt.Api.Tests;

public class ShortLeaseReserveFactory : PayFactory
{
    public ShortLeaseReserveFactory() { Extra["Payments:LeaseSeconds"] = "60"; Extra["Payments:NewSellerReservePercent"] = "10"; Extra["Payments:ReserveReleaseDays"] = "30"; }
}

/// <summary>Follow-ups of the round-4 verification: every loop of the release job renews the lease, and a lapsed lease is never revived.</summary>
public class PaymentLeaseFollowupTests
{
    // each lease check advances the clock by 40 s against a 60 s lease: a loop that does not renew lets the lease lapse (and the hook's "nobody else may take it" assertion fails)
    private static async Task<int> RunWithSlowSteps(ApiFactory f)
    {
        var hooks = f.Services.GetRequiredService<PaymentTestHooks>();
        var steps = 0;
        hooks.AfterLeaseRenew = async () =>
        {
            steps++;
            f.Time.Advance(TimeSpan.FromSeconds(40));
            Assert.Null(await Pay.Lease(f));
        };
        await Pay.Run(f, async sp =>
        {
            Assert.True(await sp.GetRequiredService<JobLeaseService>().TryAcquireAsync(TransferService.JobName));
            return await sp.GetRequiredService<ReleaseService>().RunAsync();
        });
        return steps;
    }

    [Fact]
    public async Task A_long_scan_over_bookings_that_are_not_eligible_keeps_the_lease_alive()
    {
        using var f = new ShortLeaseFactory();                                                  // 60 s lease
        var s = await Orders.NewShop(f);
        for (var i = 0; i < 3; i++)
        {
            var bid = Guid.NewGuid(); var mb = "MB-" + Guid.NewGuid().ToString("N")[..10]; var now = f.Time.GetUtcNow().UtcDateTime;
            f.WithDb(db => { db.Bookings.Add(new Booking { Id = bid, SellerId = s.Seller, BrandId = s.Brand, ProductId = s.Product, Number = "BK-L" + i + mb[3..8], MarketplaceBookingId = mb, Status = BookingStatus.Completed, TotalCents = 10000, CompletedAt = now, UpdatedAt = now }); db.SaveChanges(); return 0; });
            Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "payment.captured", new { kind = "booking", marketplaceBookingId = mb, paymentRef = "ch_" + mb, currency = "USD" })).StatusCode);   // pending, completed today: not eligible
        }
        var steps = await RunWithSlowSteps(f);
        Assert.True(steps >= 4, $"only {steps} lease checks for the start of the run + 3 bookings");   // the renewal runs at the top of EVERY booking iteration, eligible or not
    }

    [Fact]
    public async Task A_long_reserve_release_keeps_the_lease_alive()
    {
        using var f = new ShortLeaseReserveFactory();                                           // 60 s lease, new sellers keep 10 % in reserve for 30 days
        var s = await Orders.NewShop(f);
        await Pay.Paid(s); await Pay.Paid(s); await Pay.Paid(s);
        Pay.Age(f, 40);
        Assert.Equal(3, (await Pay.Release(f)).Released);                                       // a new seller: 10 % of each sale is reserved
        Assert.Equal(3, f.WithDb(db => db.LedgerEntries.Count(e => e.Account == LedgerAccount.Reserved && e.Kind == "reserve" && e.AmountCents > 0)));
        Pay.Age(f, 31);                                                                         // past ReserveReleaseDays (30)
        var steps = 0;
        var hooks = f.Services.GetRequiredService<PaymentTestHooks>();
        hooks.AfterLeaseRenew = async () => { steps++; f.Time.Advance(TimeSpan.FromSeconds(40)); Assert.Null(await Pay.Lease(f)); };
        ReleaseSummary run = null!;
        await Pay.Run(f, async sp =>
        {
            Assert.True(await sp.GetRequiredService<JobLeaseService>().TryAcquireAsync(TransferService.JobName));
            run = await sp.GetRequiredService<ReleaseService>().RunAsync();
            return 0;
        });
        Assert.Equal(3, run.ReserveReleased);
        Assert.True(steps >= 4, $"only {steps} lease checks for the start of the run + 3 reserves");   // the renewal runs before EVERY reserve release
    }

    [Fact]
    public async Task A_lapsed_lease_with_no_new_owner_is_never_revived_by_the_old_runner()
    {
        using var f = new PayFactory();                                                         // 300 s lease
        await Pay.Run(f, async sp =>
        {
            var runner = sp.GetRequiredService<JobLeaseService>();
            Assert.True(await runner.TryAcquireAsync(TransferService.JobName));
            var until = f.WithDb(db => db.PaymentJobLeases.AsNoTracking().Single().LockedUntil);
            f.Time.Advance(TimeSpan.FromSeconds(400));                                          // the lease lapsed and nobody took it over
            Assert.False(await runner.RenewAsync());                                            // the owner token still matches, but an expired lease is not revived
            Assert.Equal(until, f.WithDb(db => db.PaymentJobLeases.AsNoTracking().Single().LockedUntil));
            await Assert.ThrowsAsync<LeaseLostException>(() => runner.EnsureAsync());           // the run stops instead of carrying on without a lease
            return 0;
        });
        Assert.NotNull(await Pay.Lease(f));                                                     // and a fresh runner can take it
    }
}
