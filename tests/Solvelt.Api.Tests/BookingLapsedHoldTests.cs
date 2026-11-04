using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Bookings;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Tests;

/// <summary>C15: confirming a hold that is no longer live fails (409 hold_expired); a final booking cannot be confirmed (409 hold_not_confirmable).</summary>
public class BookingLapsedHoldTests(SvcFactory f) : IClassFixture<SvcFactory>
{
    private BookingTestHooks Hooks => f.Services.GetRequiredService<BookingTestHooks>();
    private async Task RunJobs() => Assert.Equal(HttpStatusCode.OK, (await f.NewClient().PostAsync("/api/dev/bookings/maintenance/run", null)).StatusCode);

    [Fact]
    public async Task A_hold_whose_time_has_passed_is_not_confirmed_even_when_the_slot_is_still_free_and_creates_no_booking_or_event()
    {
        var b = await Svc.NewBiz(f);
        var wed = Svc.Next(f, DayOfWeek.Wednesday); var mid = Svc.BookingId(); var start = Svc.Local(b.Tz, wed, 10);
        Assert.Equal(HttpStatusCode.Created, (await Svc.Post(b, "/bookings/holds", Svc.Body(b, mid, start, "online"))).StatusCode);
        f.Time.Advance(TimeSpan.FromMinutes(30));                               // before the job marks it Expired: Hold with HoldExpiresAt in the past
        var late = await Svc.Post(b, $"/bookings/{mid}/confirm", Svc.Body(b, mid, start, "online"));
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode); Assert.Equal("hold_expired", await late.ErrCode());
        await RunJobs();                                                         // now the job marks it Expired: same answer
        var late2 = await Svc.Post(b, $"/bookings/{mid}/confirm", Svc.Body(b, mid, start, "online"));
        Assert.Equal(HttpStatusCode.Conflict, late2.StatusCode); Assert.Equal("hold_expired", await late2.ErrCode());
        var row = f.WithDb(db => db.Bookings.Single(x => x.MarketplaceBookingId == mid));
        Assert.Equal(BookingStatus.Expired, row.Status);
        Assert.Empty(Svc.Events(f, b.Seller, "booking.confirmed").Where(e => (string)e.Data["booking"]!["marketplaceBookingId"]! == mid));
        Assert.Empty(Svc.Events(f, b.Seller, "booking.requested").Where(e => (string)e.Data["booking"]!["marketplaceBookingId"]! == mid));
    }

    [Fact]
    public async Task A_hold_that_expires_between_the_request_and_taking_the_provider_lock_is_not_confirmed()
    {
        var b = await Svc.NewBiz(f);
        var wed = Svc.Next(f, DayOfWeek.Wednesday); var mid = Svc.BookingId(); var start = Svc.Local(b.Tz, wed, 10);
        Assert.Equal(HttpStatusCode.Created, (await Svc.Post(b, "/bookings/holds", Svc.Body(b, mid, start, "online"))).StatusCode);
        f.Time.Advance(TimeSpan.FromMinutes(5));                                 // still live when the request comes in
        Hooks.AfterConfirmLock = _ => { f.Time.Advance(TimeSpan.FromMinutes(10)); return Task.CompletedTask; };   // ... but not any more by the time the lock is held
        try
        {
            var r = await Svc.Post(b, $"/bookings/{mid}/confirm", Svc.Body(b, mid, start, "online"));
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode); Assert.Equal("hold_expired", await r.ErrCode());
        }
        finally { Hooks.AfterConfirmLock = null; }
        Assert.NotEqual(BookingStatus.Confirmed, f.WithDb(db => db.Bookings.Single(x => x.MarketplaceBookingId == mid)).Status);
        Assert.Empty(Svc.Events(f, b.Seller, "booking.confirmed").Where(e => (string)e.Data["booking"]!["marketplaceBookingId"]! == mid));
    }

    [Fact]
    public async Task A_live_hold_confirms_and_a_second_confirm_returns_the_same_booking()
    {
        var b = await Svc.NewBiz(f);
        var wed = Svc.Next(f, DayOfWeek.Wednesday); var mid = Svc.BookingId(); var start = Svc.Local(b.Tz, wed, 10);
        await Svc.Post(b, "/bookings/holds", Svc.Body(b, mid, start, "online"));
        var c1 = await Svc.Post(b, $"/bookings/{mid}/confirm", Svc.Body(b, mid, start, "online"));
        Assert.Equal(HttpStatusCode.OK, c1.StatusCode);
        var c2 = await Svc.Post(b, $"/bookings/{mid}/confirm", Svc.Body(b, mid, start, "online"));
        Assert.Equal(HttpStatusCode.OK, c2.StatusCode);
        Assert.Single(Svc.Events(f, b.Seller, "booking.confirmed").Where(e => (string)e.Data["booking"]!["marketplaceBookingId"]! == mid));
    }

    [Fact]
    public async Task A_cancelled_booking_cannot_be_confirmed_and_answers_hold_not_confirmable()
    {
        var b = await Svc.NewBiz(f);
        var wed = Svc.Next(f, DayOfWeek.Wednesday); var mid = Svc.BookingId(); var start = Svc.Local(b.Tz, wed, 10);
        await Svc.Post(b, "/bookings/holds", Svc.Body(b, mid, start, "online"));
        Assert.Equal(HttpStatusCode.OK, (await Svc.Post(b, $"/bookings/{mid}/confirm", Svc.Body(b, mid, start, "online"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Svc.Post(b, $"/bookings/{mid}/cancellations", new { reason = "changed_mind" })).StatusCode);
        var r = await Svc.Post(b, $"/bookings/{mid}/confirm", Svc.Body(b, mid, start, "online"));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode); Assert.Equal("hold_not_confirmable", await r.ErrCode());
    }

    [Fact]
    public async Task The_events_of_a_booking_are_written_in_one_ordered_sequence_per_booking()
    {
        var b = await Svc.NewBiz(f);
        var wed = Svc.Next(f, DayOfWeek.Wednesday);
        var mid1 = Svc.BookingId(); var mid2 = Svc.BookingId();
        await Svc.Post(b, "/bookings/holds", Svc.Body(b, mid1, Svc.Local(b.Tz, wed, 9), "online"));
        await Svc.Post(b, $"/bookings/{mid1}/confirm", Svc.Body(b, mid1, Svc.Local(b.Tz, wed, 9), "online"));
        await Svc.Post(b, $"/bookings/{mid1}/cancellations", new { reason = "changed_mind" });
        await Svc.Post(b, "/bookings", Svc.Body(b, mid2, Svc.Local(b.Tz, wed, 14), "online"));
        List<OutboxMessage> Rows(string mid) => f.WithDb(db => { var id = db.Bookings.Single(x => x.MarketplaceBookingId == mid).Id; return db.Outbox.Where(m => m.AggregateId == id.ToString()).OrderBy(m => m.OrderKey).ToList(); });
        var a = Rows(mid1); var c = Rows(mid2);
        Assert.True(a.Count >= 2);
        Assert.Single(a.Select(m => m.SequenceScope).Distinct()); Assert.StartsWith("bk:", a[0].SequenceScope);
        Assert.Equal(Enumerable.Range(1, a.Count).Select(i => (long)i), a.Select(m => m.OrderKey!.Value));
        Assert.NotEqual(a[0].SequenceScope, c[0].SequenceScope); Assert.Equal(1, c[0].OrderKey);      // another booking: its own sequence
    }

    [Fact]
    public async Task A_hold_that_becomes_final_between_the_request_and_the_provider_lock_answers_hold_not_confirmable()
    {
        var b = await Svc.NewBiz(f);
        var wed = Svc.Next(f, DayOfWeek.Wednesday); var mid = Svc.BookingId(); var start = Svc.Local(b.Tz, wed, 10);
        Assert.Equal(HttpStatusCode.Created, (await Svc.Post(b, "/bookings/holds", Svc.Body(b, mid, start, "online"))).StatusCode);
        Hooks.AfterConfirmLock = async db => await db.Bookings.Where(x => x.MarketplaceBookingId == mid).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, BookingStatus.CancelledByBuyer));   // on the request's own connection and transaction
        try
        {
            var r = await Svc.Post(b, $"/bookings/{mid}/confirm", Svc.Body(b, mid, start, "online"));
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode); Assert.Equal("hold_not_confirmable", await r.ErrCode());   // decided under the lock, after re-reading the row
        }
        finally { Hooks.AfterConfirmLock = null; }
    }
}
