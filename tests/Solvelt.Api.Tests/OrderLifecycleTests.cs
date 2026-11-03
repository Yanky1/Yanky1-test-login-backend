using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using Solvelt.Api.Teams;
using static Solvelt.Api.Tests.Orders;

namespace Solvelt.Api.Tests;

public class OrderLifecycleTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    // ---- state machine -------------------------------------------------------------------------------------------------------
    private static readonly Dictionary<OrderStatus, OrderStatus[]> Expected = new()
    {
        [OrderStatus.New] = [OrderStatus.Accepted, OrderStatus.OnHold, OrderStatus.Cancelled],
        [OrderStatus.Accepted] = [OrderStatus.PartiallyShipped, OrderStatus.Shipped, OrderStatus.OnHold, OrderStatus.Cancelled],
        [OrderStatus.PartiallyShipped] = [OrderStatus.PartiallyShipped, OrderStatus.Shipped, OrderStatus.OnHold],
        [OrderStatus.Shipped] = [OrderStatus.Delivered, OrderStatus.ReturnRequested, OrderStatus.Refunded],
        [OrderStatus.Delivered] = [OrderStatus.Completed, OrderStatus.ReturnRequested, OrderStatus.Refunded],
        [OrderStatus.Completed] = [OrderStatus.Refunded],
        [OrderStatus.OnHold] = [OrderStatus.New, OrderStatus.Accepted, OrderStatus.PartiallyShipped, OrderStatus.Cancelled],
        [OrderStatus.Cancelled] = [],
        [OrderStatus.ReturnRequested] = [OrderStatus.ReturnApproved, OrderStatus.ReturnRejected],
        [OrderStatus.ReturnApproved] = [OrderStatus.ReturnReceived],
        [OrderStatus.ReturnRejected] = [OrderStatus.Completed, OrderStatus.Refunded],
        [OrderStatus.ReturnReceived] = [OrderStatus.Refunded],
        [OrderStatus.Refunded] = [],
    };

    [Fact]
    public void Every_allowed_and_forbidden_transition_is_enforced_by_the_one_state_machine()
    {
        foreach (var from in Enum.GetValues<OrderStatus>())
            foreach (var to in Enum.GetValues<OrderStatus>())
            {
                var o = new SellerOrder { Status = from };
                var err = OrderStateMachine.Move(o, to);
                if (Expected[from].Contains(to)) { Assert.Null(err); Assert.Equal(to, o.Status); }
                else { Assert.NotNull(err); Assert.Equal(from, o.Status); Assert.Contains("can't move", err!.Message); }
            }
    }

    [Fact]
    public async Task Invalid_transitions_over_the_api_answer_409_with_a_clear_message()
    {
        var s = await NewShop(f);
        var (_, id, _, _) = await Place(s);
        var r = await Act(s.Owner, id, "ship", Ship());          // New: must accept first
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("Accept this order", (string)(await Node(r))["title"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await Act(s.Owner, id, "deliver")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Act(s.Owner, id, "resume")).StatusCode);
        await Ok(Act(s.Owner, id, "accept"));
        Assert.Equal(HttpStatusCode.Conflict, (await Act(s.Owner, id, "accept")).StatusCode);   // already accepted
        await Ok(Act(s.Owner, id, "cancel", new { reason = "other" }));
        foreach (var a in new[] { "accept", "hold", "ship", "deliver", "cancel" })
            Assert.Equal(HttpStatusCode.Conflict, (await Act(s.Owner, id, a, a == "ship" ? Ship() : a == "cancel" ? new { reason = "other" } : null)).StatusCode);
    }

    // ---- accept / hold / ship / deliver --------------------------------------------------------------------------------------
    [Fact]
    public async Task Accept_hold_resume_flow_keeps_the_previous_status()
    {
        var s = await NewShop(f);
        var (mid, id, _, _) = await Place(s);
        Assert.Equal("OnHold", St(await Ok(Act(s.Owner, id, "hold", new { note = "Checking address" }))));
        Assert.Equal(HttpStatusCode.Conflict, (await Act(s.Owner, id, "ship", Ship())).StatusCode);
        Assert.Equal("New", St(await Ok(Act(s.Owner, id, "resume"))));
        Assert.Equal("Accepted", St(await Ok(Act(s.Owner, id, "accept"))));
        Assert.Equal("OnHold", St(await Ok(Act(s.Owner, id, "hold"))));
        Assert.Equal("Accepted", St(await Ok(Act(s.Owner, id, "resume"))));
        var types = OutboxFor(f, mid).Select(m => m.EventType).ToList();
        Assert.Equal(["order.held", "order.resumed", "order.accepted", "order.held", "order.resumed"], types);
    }

    [Fact]
    public async Task Partial_and_full_shipping_with_tracking_commits_stock_and_ends_in_delivered()
    {
        var s = await NewShop(f, stock: 10);
        var mid = Mid();
        var p2 = await ActiveProduct(s.Owner, "Linen napkin set", s.Brand, "NAP-" + Guid.NewGuid().ToString("N")[..6], 10);
        var r = await Inbound(f, "/api/internal/v1/orders", OrderBody(f, mid, [Line(s.Seller, s.Brand, s.Product, 3, lid: "A"), Line(s.Seller, s.Brand, p2, 1, lid: "B")]));
        var id = Guid.Parse((string)(await Node(r))["orders"]![0]!["sellerOrderId"]!);
        await Ok(Act(s.Owner, id, "accept"));
        var d = await Detail(s.Owner, id);
        var a = Guid.Parse((string)d["lines"]!.AsArray().First(l => (string)l!["title"]! == "Speckled stoneware coffee mug")!["id"]!); var b = Guid.Parse((string)d["lines"]!.AsArray().First(l => (string)l!["title"]! != "Speckled stoneware coffee mug")!["id"]!);

        // more than remaining -> 422; unknown line -> 422; bad tracking format -> 422
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Act(s.Owner, id, "ship", Ship(a, 4))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Act(s.Owner, id, "ship", Ship(Guid.NewGuid(), 1))).StatusCode);
        var badFmt = await Act(s.Owner, id, "ship", Ship(a, 1, "UPS", "12345"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badFmt.StatusCode); Assert.Equal("tracking_format", await Code(badFmt));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Act(s.Owner, id, "ship", new { carrier = "Pigeon", trackingNumber = "12345678" })).StatusCode);

        var first = await Ok(Act(s.Owner, id, "ship", new { carrier = "UPS", trackingNumber = "1z999aa10123456784", lines = new[] { new { lineId = a, quantity = 2 } } }));
        Assert.Equal("PartiallyShipped", St(first));
        Assert.Equal("1Z999AA10123456784", (string)first["shipments"]![0]!["trackingNumber"]!);
        Assert.Equal((8L, 1L), Stock(f, s.Product));                  // 2 units consumed, 1 still reserved
        Assert.Equal(HttpStatusCode.Conflict, (await Act(s.Owner, id, "deliver")).StatusCode);   // not fully shipped

        var rest = await Ok(Act(s.Owner, id, "ship", new { carrier = "FedEx", trackingNumber = "123456789012" })); // everything left
        Assert.Equal("Shipped", St(rest));
        Assert.Equal(2, rest["shipments"]!.AsArray().Count);
        Assert.Equal((7L, 0L), Stock(f, s.Product));
        Assert.Equal(HttpStatusCode.Conflict, (await Act(s.Owner, id, "ship", Ship())).StatusCode);   // nothing left

        // edit tracking, then deliver
        var sid = (string)rest["shipments"]![1]!["id"]!;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Owner.Put($"/api/v1/seller/orders/{id}/shipments/{sid}", new { carrier = "FedEx", trackingNumber = "x" })).StatusCode);
        var edited = await Ok(s.Owner.Put($"/api/v1/seller/orders/{id}/shipments/{sid}", new { carrier = "FedEx", trackingNumber = "999999999999" }));
        Assert.Equal("999999999999", (string)edited["shipments"]![1]!["trackingNumber"]!);
        var delivered = await Ok(Act(s.Owner, id, "deliver"));
        Assert.Equal("Delivered", St(delivered));
        Assert.Equal(["order.accepted", "order.shipped", "order.shipped", "order.tracking_updated", "order.delivered"], OutboxFor(f, mid).Select(m => m.EventType).ToArray());
        var shipPayload = JsonNode.Parse(OutboxFor(f, mid).First(m => m.EventType == "order.shipped").PayloadJson)!["data"]!;
        Assert.True((bool)shipPayload["partial"]!); Assert.Equal("1Z999AA10123456784", (string)shipPayload["trackingNumber"]!);
        Assert.Contains("ups.com", (string)shipPayload["trackingUrl"]!);
        _ = b;
    }

    [Theory]
    [InlineData("UPS", "1Z999AA10123456784", true)] [InlineData("UPS", "1Z999", false)]
    [InlineData("USPS", "9400111899223197428490", true)] [InlineData("USPS", "EA123456789US", true)] [InlineData("USPS", "12", false)]
    [InlineData("FedEx", "123456789012", true)] [InlineData("FedEx", "ABC", false)]
    [InlineData("DHL", "1234567890", true)] [InlineData("DHL", "12", false)]
    [InlineData("Other", "TRACK-123", true)] [InlineData("Other", "no way!", false)]
    public void Tracking_formats_per_carrier(string carrier, string number, bool valid) =>
        Assert.Equal(valid, Tracking.Valid(carrier, Tracking.Normalize(number)));

    [Fact]
    public async Task Ship_date_cannot_be_in_the_future_or_before_the_order()
    {
        var s = await NewShop(f);
        var (_, id, _, _) = await Place(s);
        await Ok(Act(s.Owner, id, "accept"));
        var tn = "1Z999AA10123456784";
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Act(s.Owner, id, "ship", new { carrier = "UPS", trackingNumber = tn, shippedAt = f.Time.GetUtcNow().UtcDateTime.AddDays(2) })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Act(s.Owner, id, "ship", new { carrier = "UPS", trackingNumber = tn, shippedAt = f.Time.GetUtcNow().UtcDateTime.AddDays(-3) })).StatusCode);
    }

    // ---- cancel --------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Seller_cancel_with_restock_releases_stock_and_without_restock_removes_it()
    {
        var s = await NewShop(f, stock: 10);
        var (mid, a, _, _) = await Place(s, qty: 3);
        Assert.Equal((10L, 3L), Stock(f, s.Product));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Act(s.Owner, a, "cancel", new { reason = "bogus" })).StatusCode);
        var c = await Ok(Act(s.Owner, a, "cancel", new { reason = "out_of_stock", note = "Sorry", restock = true }));
        Assert.Equal("Cancelled", St(c));
        Assert.Equal((10L, 0L), Stock(f, s.Product));
        var ev = JsonNode.Parse(OutboxFor(f, mid).Last().PayloadJson)!;
        Assert.Equal("order.cancelled", (string)ev["type"]!); Assert.Equal("out_of_stock", (string)ev["data"]!["reason"]!); Assert.True((bool)ev["data"]!["restocked"]!);

        var (_, b, _, _) = await Place(s, qty: 2);
        await Ok(Act(s.Owner, b, "cancel", new { reason = "other", restock = false }));
        Assert.Equal((8L, 0L), Stock(f, s.Product));       // damaged: the two units leave the on-hand count
    }

    [Fact]
    public async Task A_fully_shipped_order_cannot_be_cancelled()
    {
        var s = await NewShop(f);
        var (_, id, _, _) = await Place(s, qty: 2);
        await Ok(Act(s.Owner, id, "accept")); await Ok(Act(s.Owner, id, "ship", Ship()));
        var r = await Act(s.Owner, id, "cancel", new { reason = "other" });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task Buyer_cancel_request_can_be_approved_with_restock_or_rejected()
    {
        var s = await NewShop(f, stock: 10);
        var (mid, id, number, _) = await Place(s, qty: 2);
        var path = $"/api/internal/v1/orders/{mid}/cancel-requests";
        var rq = new { requestId = "CR-1", reason = "changed_mind", note = "Too slow" };
        var r = await Inbound(f, path, rq);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("requested", (string)(await Node(r))["orders"]![0]!["result"]!);
        Assert.Equal("duplicate", (string)(await Node(await Inbound(f, path, rq)))["orders"]![0]!["result"]!);   // same requestId -> idempotent
        var d = await Detail(s.Owner, id);
        Assert.True((bool)d["cancelRequested"]!);
        // cannot ship while the request is open
        await Ok(Act(s.Owner, id, "accept"));
        Assert.Equal("cancel_requested", await Code(await Act(s.Owner, id, "ship", Ship())));
        var cid = (string)d["cancellations"]![0]!["id"]!;
        // approve with restock -> stock restored
        var res = await Ok(Act(s.Owner, id, $"cancellation-requests/{cid}/approve", new { restock = true }));
        Assert.Equal("Cancelled", St(res)); Assert.False((bool)res["cancelRequested"]!);
        Assert.Equal((10L, 0L), Stock(f, s.Product));
        Assert.Equal("Approved", (string)res["cancellations"]![0]!["status"]!);
        Assert.Equal("buyer", ((string)res["cancelledBy"]!).ToLowerInvariant());

        // reject path needs a note
        var (mid2, id2, _, _) = await Place(s);
        await Inbound(f, $"/api/internal/v1/orders/{mid2}/cancel-requests", new { requestId = "CR-2", reason = "ordered_by_mistake" });
        var cid2 = (string)(await Detail(s.Owner, id2))["cancellations"]![0]!["id"]!;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Act(s.Owner, id2, $"cancellation-requests/{cid2}/reject", new { })).StatusCode);
        var rej = await Ok(Act(s.Owner, id2, $"cancellation-requests/{cid2}/reject", new { note = "Already packed" }));
        Assert.Equal("New", St(rej)); Assert.False((bool)rej["cancelRequested"]!);
        var evt = JsonNode.Parse(OutboxFor(f, mid2).Last().PayloadJson)!;
        Assert.Equal("cancellation.decided", (string)evt["type"]!); Assert.Equal("rejected", (string)evt["data"]!["decision"]!);
        _ = number;
    }

    [Fact]
    public async Task A_cancel_request_after_shipping_is_declined_automatically()
    {
        var s = await NewShop(f);
        var (mid, id, _, _) = await Place(s);
        await Ok(Act(s.Owner, id, "accept")); await Ok(Act(s.Owner, id, "ship", Ship()));
        var r = await Node(await Inbound(f, $"/api/internal/v1/orders/{mid}/cancel-requests", new { requestId = "CR-9", reason = "changed_mind" }));
        Assert.Equal("auto_rejected", (string)r["orders"]![0]!["result"]!);
        Assert.Contains(OutboxFor(f, mid), m => m.EventType == "cancellation.decided" && m.PayloadJson.Contains("rejected"));
        Assert.False((bool)(await Detail(s.Owner, id))["cancelRequested"]!);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Inbound(f, $"/api/internal/v1/orders/{mid}/cancel-requests", new { requestId = "CR-10", reason = "nonsense" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Inbound(f, "/api/internal/v1/orders/UNKNOWN/cancel-requests", new { requestId = "CR-11", reason = "changed_mind" })).StatusCode);
    }

    [Fact]
    public async Task Buyer_delivery_confirmation_marks_a_shipped_order_delivered_and_is_idempotent()
    {
        var s = await NewShop(f);
        var (mid, id, _, _) = await Place(s);
        await Ok(Act(s.Owner, id, "accept")); await Ok(Act(s.Owner, id, "ship", Ship()));
        var path = $"/api/internal/v1/orders/{mid}/delivery-confirmations";
        Assert.Equal("delivered", (string)(await Node(await Inbound(f, path, new { })))["orders"]![0]!["result"]!);
        Assert.Equal("already_delivered", (string)(await Node(await Inbound(f, path, new { })))["orders"]![0]!["result"]!);
        Assert.Equal("Delivered", St(await Detail(s.Owner, id)));
    }

    // ---- refunds -------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Refunds_are_limited_to_what_was_paid_and_partial_refunds_add_up()
    {
        var s = await NewShop(f);
        var (mid, id, _, _) = await Place(s, qty: 3, price: 1000, shipping: 500, tax: 240);   // total 3000 + 500 + 240 = 3740
        await Ok(Act(s.Owner, id, "accept")); await Ok(Act(s.Owner, id, "ship", Ship())); await Ok(Act(s.Owner, id, "deliver"));
        var line = await LineId(s.Owner, id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Act(s.Owner, id, "refunds", new { reason = "bogus", full = true })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Act(s.Owner, id, "refunds", new { reason = "goodwill" })).StatusCode);   // nothing selected
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Act(s.Owner, id, "refunds", new { reason = "goodwill", lines = new[] { new { lineId = line, quantity = 4 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Act(s.Owner, id, "refunds", new { reason = "goodwill", shippingCents = 501 })).StatusCode);

        var preview = await Ok(s.Owner.Post($"/api/v1/seller/orders/{id}/refunds/preview", new { reason = "goodwill", lines = new[] { new { lineId = line, quantity = 1 } }, shippingCents = 500 }));
        Assert.Equal(1080 + 500, (long)preview["amountCents"]!);         // one unit: 1000 + its third of tax 80
        var p1 = await Ok(Act(s.Owner, id, "refunds", new { reason = "damaged_in_transit", lines = new[] { new { lineId = line, quantity = 1 } }, shippingCents = 500 }));
        Assert.Equal(1580, (long)p1["money"]!["refundedCents"]!); Assert.Equal("Delivered", St(p1));
        var p2 = await Ok(Act(s.Owner, id, "refunds", new { reason = "goodwill", lines = new[] { new { lineId = line, quantity = 1 } } }));
        Assert.Equal(1580 + 1080, (long)p2["money"]!["refundedCents"]!);
        // the last unit takes the rounding remainder, so the total is exactly what was paid
        var p3 = await Ok(Act(s.Owner, id, "refunds", new { reason = "goodwill", lines = new[] { new { lineId = line, quantity = 1 } } }));
        Assert.Equal(3740, (long)p3["money"]!["refundedCents"]!);
        Assert.Equal("Refunded", St(p3));
        Assert.Equal(0, (long)p3["money"]!["refundableCents"]!);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Act(s.Owner, id, "refunds", new { reason = "goodwill", full = true })).StatusCode);
        Assert.Equal(3, OutboxFor(f, mid).Count(m => m.EventType == "refund.issued"));
        Assert.Equal(3740, f.WithDb(db => db.Refunds.Where(r => r.OrderId == id).Sum(r => r.AmountCents)));
    }

    [Fact]
    public async Task Full_refund_refunds_everything_remaining_in_one_step()
    {
        var s = await NewShop(f);
        var (_, id, _, _) = await Place(s, qty: 2, price: 1500, shipping: 300);
        await Ok(Act(s.Owner, id, "accept")); await Ok(Act(s.Owner, id, "ship", Ship())); await Ok(Act(s.Owner, id, "deliver"));
        var d = await Ok(Act(s.Owner, id, "refunds", new { reason = "goodwill", full = true, note = "Sorry about that" }));
        Assert.Equal(3300, (long)d["money"]!["refundedCents"]!); Assert.Equal("Refunded", St(d));
        Assert.Contains(d["timeline"]!.AsArray(), e => (string)e!["type"]! == "order.refunded");
    }

    [Fact]
    public async Task Cancelled_orders_cannot_be_refunded_by_the_seller()
    {
        var s = await NewShop(f);
        var (_, id, _, _) = await Place(s);
        await Ok(Act(s.Owner, id, "cancel", new { reason = "other" }));
        Assert.Equal(HttpStatusCode.Conflict, (await Act(s.Owner, id, "refunds", new { reason = "goodwill", full = true })).StatusCode);
    }

    // ---- returns -------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Return_flow_request_approve_receive_with_restock_then_partial_refund()
    {
        var s = await NewShop(f, stock: 10);
        var (mid, id, _, _) = await Place(s, qty: 3, price: 2000, shipping: 400, lid: "RL1");
        await Ok(Act(s.Owner, id, "accept")); await Ok(Act(s.Owner, id, "ship", Ship())); await Ok(Act(s.Owner, id, "deliver"));
        Assert.Equal((7L, 0L), Stock(f, s.Product));
        var path = $"/api/internal/v1/orders/{mid}/return-requests";
        // validation: unknown line, too many units
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Inbound(f, path, new { returnId = "R0", reason = "damaged", lines = new[] { new { marketplaceLineId = "NOPE", quantity = 1 } } })).StatusCode);
        Assert.Equal("rejected", (string)(await Node(await Inbound(f, path, new { returnId = "R0", reason = "damaged", lines = new[] { new { marketplaceLineId = "RL1", quantity = 9 } } })))["orders"]![0]!["result"]!);
        var rq = new { returnId = "R1", reason = "not_as_described", comment = "Colour is off", lines = new[] { new { marketplaceLineId = "RL1", quantity = 2 } } };
        Assert.Equal("requested", (string)(await Node(await Inbound(f, path, rq)))["orders"]![0]!["result"]!);
        Assert.Equal("duplicate", (string)(await Node(await Inbound(f, path, rq)))["orders"]![0]!["result"]!);
        var d = await Detail(s.Owner, id);
        Assert.Equal("ReturnRequested", St(d)); Assert.True((bool)d["returnOpen"]!);
        var rid = (string)d["returns"]![0]!["id"]!;
        // receive before approving is a conflict; refund before receiving is a conflict
        Assert.Equal(HttpStatusCode.Conflict, (await Act(s.Owner, id, $"returns/{rid}/receive")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Act(s.Owner, id, "refunds", new { reason = "returned_item", returnId = rid, full = true })).StatusCode);
        Assert.Equal("ReturnApproved", St(await Ok(Act(s.Owner, id, $"returns/{rid}/approve", new { note = "Please send it back" }))));
        var rec = await Ok(Act(s.Owner, id, $"returns/{rid}/receive", new { restock = true }));
        Assert.Equal("ReturnReceived", St(rec));
        Assert.Equal((9L, 0L), Stock(f, s.Product));            // two units back on the shelf
        var line = await LineId(s.Owner, id);
        var refund = await Ok(Act(s.Owner, id, "refunds", new { reason = "returned_item", returnId = rid, lines = new[] { new { lineId = line, quantity = 2 } } }));
        Assert.Equal("Refunded", St(refund));
        Assert.Equal(4000, (long)refund["money"]!["refundedCents"]!); Assert.True((long)refund["money"]!["refundedCents"]! < (long)refund["money"]!["totalCents"]!);   // partial
        Assert.Equal("Refunded", (string)refund["returns"]![0]!["status"]!);
        Assert.Equal(["return.decided", "return.received", "refund.issued"], OutboxFor(f, mid).Skip(OutboxFor(f, mid).FindIndex(m => m.EventType == "order.delivered") + 1).Select(m => m.EventType).ToArray());
    }

    [Fact]
    public async Task A_rejected_return_needs_an_allowed_reason_and_closes_the_flow()
    {
        var s = await NewShop(f);
        var (mid, id, _, _) = await Place(s, lid: "RL1");
        await Ok(Act(s.Owner, id, "accept")); await Ok(Act(s.Owner, id, "ship", Ship())); await Ok(Act(s.Owner, id, "deliver"));
        await Inbound(f, $"/api/internal/v1/orders/{mid}/return-requests", new { returnId = "R1", reason = "damaged", lines = new[] { new { marketplaceLineId = "RL1", quantity = 1 } } });
        var rid = (string)(await Detail(s.Owner, id))["returns"]![0]!["id"]!;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Act(s.Owner, id, $"returns/{rid}/reject", new { reason = "because" })).StatusCode);
        Assert.Equal("ReturnRejected", St(await Ok(Act(s.Owner, id, $"returns/{rid}/reject", new { reason = "item_used", note = "Visible wear" }))));
        Assert.Equal(HttpStatusCode.Conflict, (await Act(s.Owner, id, $"returns/{rid}/approve")).StatusCode);   // already decided
    }

    // ---- concurrency + misc ----------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_stale_version_is_a_409_and_concurrent_actions_apply_only_once()
    {
        var s = await NewShop(f, stock: 10);
        var (_, id, _, _) = await Place(s);
        var v = (int)(await Detail(s.Owner, id))["version"]!;
        Assert.Equal(HttpStatusCode.OK, (await Act(s.Owner, id, "accept", new { version = v })).StatusCode);
        var stale = await Act(s.Owner, id, "hold", new { version = v });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); Assert.Equal("version_conflict", await Code(stale));

        var (_, id2, _, _) = await Place(s, qty: 2);
        var rs = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Act(s.Owner, id2, "cancel", new { reason = "other", restock = true })));
        Assert.Equal(1, rs.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal((10L, 1L), Stock(f, s.Product));   // the first order is still New and holds 1; the cancelled one released its 2 exactly once
        Assert.Equal(1, f.WithDb(db => db.OrderEvents.Count(e => e.OrderId == id2 && e.Type == "order.cancelled")));   // side effects ran once
    }

    [Fact]
    public async Task Internal_notes_and_the_timeline_are_recorded()
    {
        var s = await NewShop(f);
        var (_, id, _, _) = await Place(s);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Act(s.Owner, id, "notes", new { text = " " })).StatusCode);
        var d = await Ok(Act(s.Owner, id, "notes", new { text = "Call the carrier" }));
        Assert.Equal("Call the carrier", (string)d["notes"]![0]!["text"]!);
        await Ok(Act(s.Owner, id, "accept"));
        var types = d["timeline"]!.AsArray().Select(e => (string)e!["type"]!).ToList();
        Assert.Contains("order.placed", types);
        Assert.Contains("order.accepted", (await Detail(s.Owner, id))["timeline"]!.AsArray().Select(e => (string)e!["type"]!));
    }

    [Fact]
    public async Task Listing_tabs_search_filters_sorting_and_counts_work()
    {
        var s = await NewShop(f, stock: 50);
        var (mid1, o1, n1, _) = await Place(s, buyer: "Zed Zebra");
        var (_, o2, _, _) = await Place(s, price: 5000);
        await Ok(Act(s.Owner, o2, "accept"));
        await Ok(Act(s.Owner, o2, "ship", Ship(tracking: "1Z999AA10123456784")));
        async Task<JsonNode> L(string q) => await Node(await s.Owner.Get("/api/v1/seller/orders" + q));
        Assert.Equal(1, (int)(await L("?tab=pending&q=" + mid1))["total"]!);
        Assert.Equal(0, (int)(await L("?tab=shipped&q=" + mid1))["total"]!);
        Assert.Equal(n1, (string)(await L("?q=zebra"))["items"]![0]!["number"]!);
        Assert.Equal(o2.ToString(), (string)(await L("?q=1z999aa10123456784"))["items"]![0]!["id"]!);          // tracking search
        Assert.Equal(o2.ToString(), (string)(await L($"?q={s.Sku.ToLower()}&tab=shipped"))["items"]![0]!["id"]!);   // SKU search
        Assert.Equal(o2.ToString(), (string)(await L("?sort=total_desc&q=" + s.Sku))["items"]![0]!["id"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.Get("/api/v1/seller/orders?status=bogus")).StatusCode);
        var counts = await Node(await s.Owner.Get("/api/v1/seller/orders/counts"));
        Assert.True((int)counts["pending"]! >= 1); Assert.True((int)counts["needsAction"]! >= 1); Assert.True((int)counts["shipped"]! >= 1);
        var page = await L("?pageSize=1&page=2");
        Assert.Equal(1, page["items"]!.AsArray().Count); Assert.True((int)page["total"]! >= 2);
    }

    [Fact]
    public async Task Late_orders_are_flagged_and_counted_after_the_ship_by_date()
    {
        var s = await NewShop(f);
        var (_, id, _, _) = await Place(s);
        Assert.False((bool)(await Detail(s.Owner, id))["late"]!);
        f.WithDb(db => { var o = db.Orders.First(x => x.Id == id); o.ShipByAt = f.Time.GetUtcNow().UtcDateTime.AddDays(-1); db.SaveChanges(); return 0; });
        Assert.True((bool)(await Detail(s.Owner, id))["late"]!);
        Assert.Contains(id.ToString(), await (await s.Owner.Get("/api/v1/seller/orders?late=true&pageSize=100")).Content.ReadAsStringAsync());
        Assert.True((int)(await Node(await s.Owner.Get("/api/v1/seller/orders/dashboard")))["late"]! >= 1);
        await Ok(Act(s.Owner, id, "accept")); await Ok(Act(s.Owner, id, "ship", Ship()));
        Assert.True(f.WithDb(db => db.Orders.AsNoTracking().First(o => o.Id == id).ShippedLate));
        Assert.False((bool)(await Detail(s.Owner, id))["late"]!);
    }
}
