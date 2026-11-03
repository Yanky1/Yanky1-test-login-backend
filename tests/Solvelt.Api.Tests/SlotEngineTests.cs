using Solvelt.Api.Bookings;

namespace Solvelt.Api.Tests;

/// <summary>Pure tests of the slot engine: no database, fixed clock. New York is used for DST (2026: spring forward Sun 8 Mar, fall back Sun 1 Nov).</summary>
public class SlotEngineTests
{
    private static readonly TimeZoneInfo Ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ProviderCalendar Cal(TimeZoneInfo? zone = null, int start = 9 * 60, int end = 17 * 60, params DayOfWeek[] days)
    {
        var c = new ProviderCalendar { Zone = zone ?? Ny };
        foreach (var d in days.Length == 0 ? [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday] : days) c.Weekly[d] = [(start, end)];
        return c;
    }

    private static ServiceTiming T(int dur = 60, int before = 0, int after = 0, int gran = 60, int noticeMin = 0, int days = 365, int travel = 0) => new(dur, before, after, gran, noticeMin, days, travel);
    private static DateTime U(TimeZoneInfo z, int y, int m, int d, int h, int min = 0) => TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, m, d, h, min, 0, DateTimeKind.Unspecified), z);
    private static List<DateTime> Day(ProviderCalendar c, ServiceTiming t, int y, int m, int d, int dur = 60, bool customer = false, DateTime? now = null) =>
        SlotEngine.Slots(c, t, now ?? Now, U(c.Zone, y, m, d, 0), U(c.Zone, y, m, d, 0).AddDays(1), dur, customer);

    [Fact]
    public void Weekly_hours_give_one_slot_per_step_and_skip_closed_days()
    {
        var c = Cal();
        var mon = Day(c, T(), 2026, 1, 12);            // Monday
        Assert.Equal(8, mon.Count);
        Assert.Equal(U(Ny, 2026, 1, 12, 9), mon[0]);
        Assert.Equal(U(Ny, 2026, 1, 12, 16), mon[^1]);
        Assert.Empty(Day(c, T(), 2026, 1, 10));         // Saturday
    }

    [Fact]
    public void Multiple_intervals_per_day_and_a_duration_that_must_fit()
    {
        var c = new ProviderCalendar { Zone = Ny };
        c.Weekly[DayOfWeek.Monday] = [(9 * 60, 12 * 60), (13 * 60, 15 * 60)];
        var slots = Day(c, T(dur: 120, gran: 60), 2026, 1, 12, dur: 120);
        // 9-12: starts 9,10 ; 13-15: start 13
        Assert.Equal([U(Ny, 2026, 1, 12, 9), U(Ny, 2026, 1, 12, 10), U(Ny, 2026, 1, 12, 13)], slots);
    }

    [Fact]
    public void Date_override_replaces_the_weekly_hours_and_an_empty_override_closes_the_day()
    {
        var c = Cal();
        c.Overrides[new DateOnly(2026, 1, 12)] = [(10 * 60, 12 * 60)];
        c.Overrides[new DateOnly(2026, 1, 13)] = [];
        Assert.Equal([U(Ny, 2026, 1, 12, 10), U(Ny, 2026, 1, 12, 11)], Day(c, T(), 2026, 1, 12));
        Assert.Empty(Day(c, T(), 2026, 1, 13));
        Assert.Equal(8, Day(c, T(), 2026, 1, 14).Count);
        // an override can also OPEN a normally closed day
        c.Overrides[new DateOnly(2026, 1, 10)] = [(9 * 60, 10 * 60)];
        Assert.Single(Day(c, T(), 2026, 1, 10));
    }

    [Fact]
    public void Time_off_removes_overlapping_starts_all_day_or_partial()
    {
        var c = Cal();
        c.Off.Add(new Span(U(Ny, 2026, 1, 12, 11), U(Ny, 2026, 1, 12, 13, 30)));
        var slots = Day(c, T(), 2026, 1, 12);
        Assert.DoesNotContain(U(Ny, 2026, 1, 12, 11), slots);
        Assert.DoesNotContain(U(Ny, 2026, 1, 12, 12), slots);
        Assert.DoesNotContain(U(Ny, 2026, 1, 12, 13), slots);   // 13:00-14:00 overlaps until 13:30
        Assert.Contains(U(Ny, 2026, 1, 12, 14), slots);
        c.Off.Add(new Span(U(Ny, 2026, 1, 13, 0), U(Ny, 2026, 1, 14, 0)));
        Assert.Empty(Day(c, T(), 2026, 1, 13));
    }

    [Fact]
    public void Existing_bookings_block_slots_and_buffers_widen_the_footprint()
    {
        var c = Cal();
        c.Busy.Add(new Span(U(Ny, 2026, 1, 12, 10), U(Ny, 2026, 1, 12, 11)));    // a booking 10:00-11:00 (footprint without buffers)
        var plain = Day(c, T(), 2026, 1, 12);
        Assert.Equal(7, plain.Count);
        Assert.DoesNotContain(U(Ny, 2026, 1, 12, 10), plain);
        // 30 minutes buffer before and after the NEW booking: 9:00 conflicts (its footprint ends 10:30), so does 11:00 (starts footprint 10:30)
        var buffered = Day(c, T(before: 30, after: 30), 2026, 1, 12);
        Assert.DoesNotContain(U(Ny, 2026, 1, 12, 9), buffered);
        Assert.DoesNotContain(U(Ny, 2026, 1, 12, 11), buffered);
        Assert.Contains(U(Ny, 2026, 1, 12, 12), buffered);
        // back to back is fine without buffers
        Assert.Contains(U(Ny, 2026, 1, 12, 9), plain); Assert.Contains(U(Ny, 2026, 1, 12, 11), plain);
    }

    [Fact]
    public void Buffers_may_extend_outside_working_hours_but_the_service_itself_may_not()
    {
        var c = Cal();
        var slots = Day(c, T(before: 60, after: 60), 2026, 1, 12);
        Assert.Contains(U(Ny, 2026, 1, 12, 9), slots);      // buffer before starts at 8:00, outside hours: allowed
        Assert.Contains(U(Ny, 2026, 1, 12, 16), slots);
        Assert.Equal(8, slots.Count);
    }

    [Fact]
    public void Travel_buffer_applies_to_customer_location_jobs_only()
    {
        var c = Cal();
        c.Busy.Add(new Span(U(Ny, 2026, 1, 12, 10), U(Ny, 2026, 1, 12, 11)));
        var atSeller = Day(c, T(travel: 45), 2026, 1, 12, customer: false);
        var atHome = Day(c, T(travel: 45), 2026, 1, 12, customer: true);
        Assert.Contains(U(Ny, 2026, 1, 12, 9), atSeller);
        Assert.DoesNotContain(U(Ny, 2026, 1, 12, 9), atHome);    // would need to leave before the next job ends
        Assert.DoesNotContain(U(Ny, 2026, 1, 12, 11), atHome);
        Assert.Contains(U(Ny, 2026, 1, 12, 13), atHome);
    }

    [Fact]
    public void Minimum_notice_and_maximum_window_are_enforced()
    {
        var c = Cal();
        var now = U(Ny, 2026, 1, 12, 8);   // Monday 08:00
        var t = T(noticeMin: 24 * 60, days: 3);
        var all = SlotEngine.Slots(c, t, now, now, now.AddDays(10), 60, false);
        Assert.All(all, s => Assert.True(s >= now.AddHours(24) && s <= now.AddDays(3)));
        Assert.Equal(U(Ny, 2026, 1, 13, 9), all[0]);       // 24 h after Monday 08:00 is Tuesday 08:00; the first slot inside working hours is 09:00
        Assert.DoesNotContain(all, s => s > now.AddDays(3));
    }

    [Fact]
    public void Slot_granularity_steps_through_the_interval()
    {
        var c = Cal();
        var slots = Day(c, T(dur: 60, gran: 30), 2026, 1, 12);
        Assert.Equal(15, slots.Count);     // 9:00 ... 16:00 every 30 min
        Assert.Equal(TimeSpan.FromMinutes(30), slots[1] - slots[0]);
    }

    [Fact]
    public void Provider_time_zone_decides_which_instants_are_working_hours()
    {
        var la = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        var ny = Day(Cal(Ny), T(), 2026, 1, 12)[0];
        var l = Day(Cal(la), T(), 2026, 1, 12)[0];
        var t = Day(Cal(tokyo), T(), 2026, 1, 12)[0];
        Assert.Equal(new DateTime(2026, 1, 12, 14, 0, 0, DateTimeKind.Utc), ny);
        Assert.Equal(new DateTime(2026, 1, 12, 17, 0, 0, DateTimeKind.Utc), l);
        Assert.Equal(new DateTime(2026, 1, 12, 0, 0, 0, DateTimeKind.Utc), t);
        // Kolkata (+5:30) keeps working with a half-hour offset
        var kolkata = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        Assert.Equal(new DateTime(2026, 1, 12, 3, 30, 0, DateTimeKind.Utc), Day(Cal(kolkata), T(), 2026, 1, 12)[0]);
    }

    [Fact]
    public void Nine_to_five_keeps_its_local_time_across_the_spring_forward_week()
    {
        var c = Cal();
        var before = Day(c, T(), 2026, 3, 6)[0];    // Fri, EST (UTC-5)
        var after = Day(c, T(), 2026, 3, 9)[0];     // Mon, EDT (UTC-4)
        Assert.Equal(new DateTime(2026, 3, 6, 14, 0, 0, DateTimeKind.Utc), before);
        Assert.Equal(new DateTime(2026, 3, 9, 13, 0, 0, DateTimeKind.Utc), after);
    }

    [Fact]
    public void Spring_forward_day_has_one_real_hour_less_and_a_nonexistent_start_is_shifted()
    {
        // Sunday 8 March 2026: 02:00 -> 03:00. Open 00:00-05:00 local = 4 real hours.
        var c = new ProviderCalendar { Zone = Ny };
        c.Overrides[new DateOnly(2026, 3, 8)] = [(0, 5 * 60)];
        var slots = Day(c, T(), 2026, 3, 8);
        Assert.Equal(4, slots.Count);
        Assert.Equal(U(Ny, 2026, 3, 8, 0), slots[0]);
        Assert.Equal(U(Ny, 2026, 3, 8, 3), slots[2]);                       // the hour after 01:00 is 03:00 EDT
        Assert.Equal(TimeSpan.FromHours(1), slots[2] - slots[1]);
        // an interval that starts in the gap (02:30) begins at 03:30
        var g = new ProviderCalendar { Zone = Ny };
        g.Overrides[new DateOnly(2026, 3, 8)] = [(2 * 60 + 30, 4 * 60)];
        var gs = Day(g, T(dur: 30, gran: 30), 2026, 3, 8, dur: 30);
        Assert.Equal(new DateTime(2026, 3, 8, 7, 30, 0, DateTimeKind.Utc), gs[0]);   // 03:30 EDT
        Assert.Single(gs);
    }

    [Fact]
    public void Fall_back_day_has_one_real_hour_more()
    {
        // Sunday 1 November 2026: 02:00 -> 01:00 (the 01:00 hour happens twice). Open 00:00-05:00 local = 6 real hours.
        var c = new ProviderCalendar { Zone = Ny };
        c.Overrides[new DateOnly(2026, 11, 1)] = [(0, 5 * 60)];
        var slots = Day(c, T(), 2026, 11, 1);
        Assert.Equal(6, slots.Count);
        Assert.Equal(new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc), slots[0]);    // 00:00 EDT
        Assert.Equal(new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc) - TimeSpan.FromHours(1), slots[^1]);
        // 9-5 on both sides of the change keep their local time
        var w = Cal();
        Assert.Equal(new DateTime(2026, 10, 30, 13, 0, 0, DateTimeKind.Utc), Day(w, T(), 2026, 10, 30)[0]);   // Fri EDT
        Assert.Equal(new DateTime(2026, 11, 2, 14, 0, 0, DateTimeKind.Utc), Day(w, T(), 2026, 11, 2)[0]);     // Mon EST
    }

    [Fact]
    public void A_booking_is_never_bookable_outside_the_interval_even_when_it_crosses_midnight_utc()
    {
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        var c = Cal(tokyo, 22 * 60, 24 * 60);          // 22:00-24:00 Tokyo = 13:00-15:00Z
        var slots = Day(c, T(), 2026, 1, 12);
        Assert.Equal(2, slots.Count);
        Assert.True(SlotEngine.IsBookable(c, T(), Now, new DateTime(2026, 1, 12, 13, 0, 0, DateTimeKind.Utc), 60, false));
        Assert.False(SlotEngine.IsBookable(c, T(), Now, new DateTime(2026, 1, 12, 14, 30, 0, DateTimeKind.Utc), 60, false));
    }

    [Fact]
    public void Footprint_includes_buffers_and_travel()
    {
        var f = SlotEngine.Footprint(T(before: 10, after: 20, travel: 30), new DateTime(2026, 1, 12, 14, 0, 0, DateTimeKind.Utc), 60, true, out var svc)[0];
        Assert.Equal(new DateTime(2026, 1, 12, 13, 20, 0, DateTimeKind.Utc), f.StartUtc);
        Assert.Equal(new DateTime(2026, 1, 12, 15, 50, 0, DateTimeKind.Utc), f.EndUtc);
        Assert.Equal(TimeSpan.FromHours(1), svc.EndUtc - svc.StartUtc);
    }
}
