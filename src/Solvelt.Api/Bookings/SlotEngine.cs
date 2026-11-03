namespace Solvelt.Api.Bookings;

public readonly record struct Span(DateTime StartUtc, DateTime EndUtc)
{
    public bool Overlaps(Span o) => StartUtc < o.EndUtc && o.StartUtc < EndUtc;
    public bool Contains(Span o) => StartUtc <= o.StartUtc && o.EndUtc <= EndUtc;
}

/// <summary>Everything the engine needs to know about one provider's calendar. Pure data, no database.</summary>
public sealed class ProviderCalendar
{
    public required TimeZoneInfo Zone { get; init; }
    /// <summary>Weekly template: DayOfWeek -> intervals in minutes after local midnight (end exclusive, up to 1440).</summary>
    public Dictionary<DayOfWeek, List<(int Start, int End)>> Weekly { get; init; } = [];
    /// <summary>Date specific hours; they REPLACE the weekly template of that local date (empty list = closed).</summary>
    public Dictionary<DateOnly, List<(int Start, int End)>> Overrides { get; init; } = [];
    /// <summary>Time off and manual blocks: the service time itself may not touch them.</summary>
    public List<Span> Off { get; init; } = [];
    /// <summary>Footprints of bookings and live holds (service time + buffers + travel).</summary>
    public List<Span> Busy { get; init; } = [];
}

public sealed record ServiceTiming(int DurationMinutes, int BufferBeforeMinutes, int BufferAfterMinutes, int GranularityMinutes, int MinNoticeMinutes,
    int MaxAdvanceDays, int TravelBufferMinutes = 0);

/// <summary>
/// The single source of truth for "can this service start at this instant?". Time is stored in UTC; working hours are expressed in the provider's
/// local time and converted per local date, so DST changes move the UTC instants (a 9-17 day is 8 real hours normally, 7 on spring-forward, 9 on
/// fall-back). Local times that do not exist (spring-forward gap) are shifted forward by the size of the gap; ambiguous local times (fall-back) use
/// the standard-time reading.
/// </summary>
public static class SlotEngine
{
    public static DateTime LocalToUtc(TimeZoneInfo zone, DateOnly date, int minuteOfDay)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue).AddMinutes(minuteOfDay), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // skip the gap: the wall clock jumps forward, so the first valid instant is the end of the gap
            var offsetBefore = zone.GetUtcOffset(local.AddDays(-1));
            var offsetAfter = zone.GetUtcOffset(local.AddDays(1));
            local = local.Add(offsetAfter - offsetBefore);
        }
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    public static IEnumerable<Span> WorkingSpans(ProviderCalendar cal, DateOnly date)
    {
        var intervals = cal.Overrides.TryGetValue(date, out var o) ? o : cal.Weekly.GetValueOrDefault(date.DayOfWeek) ?? [];
        foreach (var (s, e) in intervals.OrderBy(i => i.Start))
        {
            if (e <= s) continue;
            var a = LocalToUtc(cal.Zone, date, s);
            var b = LocalToUtc(cal.Zone, date, e);
            if (b > a) yield return new Span(a, b);
        }
    }

    public static DateOnly LocalDate(TimeZoneInfo zone, DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));

    public static List<Span> Footprint(ServiceTiming t, DateTime startUtc, int durationMinutes, bool atCustomer, out Span service)
    {
        service = new Span(startUtc, startUtc.AddMinutes(durationMinutes));
        var travel = atCustomer ? t.TravelBufferMinutes : 0;
        return [new Span(startUtc.AddMinutes(-(t.BufferBeforeMinutes + travel)), service.EndUtc.AddMinutes(t.BufferAfterMinutes + travel))];
    }

    /// <summary>Can a service of <paramref name="durationMinutes"/> start exactly at <paramref name="startUtc"/>? (Granularity is NOT checked here.)</summary>
    public static bool IsBookable(ProviderCalendar cal, ServiceTiming t, DateTime nowUtc, DateTime startUtc, int durationMinutes, bool atCustomer, bool ignoreNoticeAndWindow = false)
    {
        if (durationMinutes <= 0) return false;
        if (!ignoreNoticeAndWindow)
        {
            if (startUtc < nowUtc.AddMinutes(t.MinNoticeMinutes)) return false;
            if (startUtc > nowUtc.AddDays(t.MaxAdvanceDays)) return false;
        }
        var block = Footprint(t, startUtc, durationMinutes, atCustomer, out var service)[0];
        var date = LocalDate(cal.Zone, startUtc);
        // the service must sit inside ONE working interval (a day's interval can start on the previous/next UTC date, so look one local day around)
        var inside = false;
        foreach (var d in new[] { date.AddDays(-1), date, date.AddDays(1) })
            foreach (var w in WorkingSpans(cal, d))
                if (w.Contains(service)) inside = true;
        if (!inside) return false;
        foreach (var off in cal.Off) if (off.Overlaps(service)) return false;
        foreach (var busy in cal.Busy) if (busy.Overlaps(block)) return false;
        return true;
    }

    /// <summary>Bookable start instants in [fromUtc, toUtc), stepping through each working interval by the slot granularity.</summary>
    public static List<DateTime> Slots(ProviderCalendar cal, ServiceTiming t, DateTime nowUtc, DateTime fromUtc, DateTime toUtc, int durationMinutes, bool atCustomer)
    {
        var result = new SortedSet<DateTime>();
        var step = Math.Max(5, t.GranularityMinutes);
        var first = LocalDate(cal.Zone, fromUtc).AddDays(-1);
        var last = LocalDate(cal.Zone, toUtc).AddDays(1);
        for (var d = first; d <= last; d = d.AddDays(1))
            foreach (var w in WorkingSpans(cal, d))
                for (var s = w.StartUtc; s.AddMinutes(durationMinutes) <= w.EndUtc; s = s.AddMinutes(step))
                {
                    if (s < fromUtc || s >= toUtc) continue;
                    if (IsBookable(cal, t, nowUtc, s, durationMinutes, atCustomer)) result.Add(s);
                }
        return result.ToList();
    }
}
