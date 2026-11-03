using Solvelt.Api.Domain;

namespace Solvelt.Api.Orders;

/// <summary>
/// THE one place that knows which status changes are legal. Everything that changes <see cref="SellerOrder.Status"/> goes through
/// <see cref="Move"/>; an illegal change is a 409 with a plain-language message. Diagram: docs/ORDERS.md.
/// </summary>
public static class OrderStateMachine
{
    private static readonly Dictionary<OrderStatus, OrderStatus[]> Allowed = new()
    {
        [OrderStatus.New] = [OrderStatus.Accepted, OrderStatus.OnHold, OrderStatus.Cancelled],
        [OrderStatus.Accepted] = [OrderStatus.PartiallyShipped, OrderStatus.Shipped, OrderStatus.OnHold, OrderStatus.Cancelled],
        [OrderStatus.PartiallyShipped] = [OrderStatus.PartiallyShipped, OrderStatus.Shipped, OrderStatus.OnHold],
        [OrderStatus.Shipped] = [OrderStatus.Delivered, OrderStatus.ReturnRequested, OrderStatus.Refunded],
        [OrderStatus.Delivered] = [OrderStatus.Completed, OrderStatus.ReturnRequested, OrderStatus.Refunded],
        [OrderStatus.Completed] = [OrderStatus.Refunded],
        // resume goes back to HeldFrom (any non-terminal status the order could be held from)
        [OrderStatus.OnHold] = [OrderStatus.New, OrderStatus.Accepted, OrderStatus.PartiallyShipped, OrderStatus.Cancelled],
        [OrderStatus.Cancelled] = [],
        [OrderStatus.ReturnRequested] = [OrderStatus.ReturnApproved, OrderStatus.ReturnRejected],
        [OrderStatus.ReturnApproved] = [OrderStatus.ReturnReceived],
        [OrderStatus.ReturnRejected] = [OrderStatus.Completed, OrderStatus.Refunded],
        [OrderStatus.ReturnReceived] = [OrderStatus.Refunded],
        [OrderStatus.Refunded] = [],
    };

    public static bool Can(OrderStatus from, OrderStatus to) => Allowed[from].Contains(to);
    public static IReadOnlyList<OrderStatus> From(OrderStatus s) => Allowed[s];

    public static bool IsTerminal(OrderStatus s) => s is OrderStatus.Cancelled or OrderStatus.Refunded;
    /// <summary>Statuses in which the order still has goods to hand over (shipping is possible).</summary>
    public static bool CanShip(OrderStatus s) => s is OrderStatus.Accepted or OrderStatus.PartiallyShipped;

    public record Invalid(string Message);

    public static string Label(OrderStatus s) => s switch
    {
        OrderStatus.New => "pending acceptance", OrderStatus.PartiallyShipped => "partially shipped", OrderStatus.OnHold => "on hold",
        OrderStatus.ReturnRequested => "return requested", OrderStatus.ReturnApproved => "return approved", OrderStatus.ReturnRejected => "return rejected",
        OrderStatus.ReturnReceived => "return received", _ => s.ToString().ToLowerInvariant()
    };

    /// <summary>Applies the transition or returns an explanation (null when it succeeded).</summary>
    public static Invalid? Move(SellerOrder o, OrderStatus to)
    {
        if (!Can(o.Status, to)) return new($"This order is {Label(o.Status)}, so it can't move to {Label(to)}.");
        o.Status = to;
        return null;
    }
}
