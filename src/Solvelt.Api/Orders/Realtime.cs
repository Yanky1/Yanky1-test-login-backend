using System.Threading.Channels;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Orders;

public record LiveEvent(string Type, Guid SellerId, Guid BrandId, object? Data = null);

/// <summary>
/// In-process fan-out for Server-Sent Events. Each subscriber carries the caller's <see cref="AccountAccess"/>; an event reaches it only when the
/// subscriber belongs to the same account AND can see the event's brand. (Single-node; a multi-node deployment would put a broker behind this, see docs/ORDERS.md.)
/// </summary>
public class EventHub
{
    public sealed class Subscription(EventHub hub, AccountAccess access) : IDisposable
    {
        public AccountAccess Access { get; set; } = access;
        public Channel<LiveEvent> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<LiveEvent>(
            new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });
        public void Dispose() => hub.Remove(this);
    }

    private readonly List<Subscription> _subs = [];
    private readonly object _l = new();

    public Subscription Subscribe(AccountAccess a) { var s = new Subscription(this, a); lock (_l) _subs.Add(s); return s; }
    internal void Remove(Subscription s) { lock (_l) _subs.Remove(s); }
    public int Count { get { lock (_l) return _subs.Count; } }

    public void Publish(LiveEvent e)
    {
        List<Subscription> copy; lock (_l) copy = _subs.ToList();
        foreach (var s in copy)
            if (s.Access.SellerId == e.SellerId && s.Access.CanSeeBrand(e.BrandId) && s.Access.Has(Permission.BrandWorkRead)) s.Channel.Writer.TryWrite(e);
    }
}
