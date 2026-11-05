using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Email;

namespace Solvelt.Api.Demo;

public enum DemoMail { Normal, Sink, Suppress }

/// <summary>
/// Per-request switches of the demo tools. While a fill runs, outbound marketplace events and emails are suppressed; while a self-approval runs, its emails go to the
/// in-memory sink instead of a real provider. Outside those calls everything is <c>Normal</c>, and the registry lookups below only run when Demo:Enabled is on.
/// </summary>
public sealed class DemoContext(IOptions<DemoOptions> opt, EmailSink sink)
{
    public bool Enabled => opt.Value.Enabled;
    public DemoMail Mail { get; set; } = DemoMail.Normal;
    /// <summary>True during a fill: no marketplace outbox event of an order, booking, message or quote is written.</summary>
    public bool SuppressOutbox { get; set; }
    public EmailSink Sink => sink;

    /// <summary>Is this order/booking/conversation/quote id a demo record? Cheap no-op (false) when the feature is off.</summary>
    public bool IsDemo(AppDbContext db, Guid id) => Enabled && db.DemoRecords.Any(r => r.EntityId == id);

    /// <summary>Whether an outbox event is demo traffic that must not reach the marketplace. Listing events are NOT demo traffic: demo products appear in the catalog feed (flagged demo: true).</summary>
    public bool SuppressEvent(AppDbContext db, string eventType, string aggregateId, string? scope)
    {
        if (!Enabled || eventType.StartsWith("listing.", StringComparison.Ordinal)) return false;
        if (SuppressOutbox) return true;
        if (scope is { Length: > 3 } && Guid.TryParseExact(scope[3..], "N", out var sid) && IsDemo(db, sid)) return true;
        return Guid.TryParse(aggregateId, out var aid) && IsDemo(db, aid);
    }
}
