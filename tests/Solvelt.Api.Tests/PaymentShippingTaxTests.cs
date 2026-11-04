using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;
using static Solvelt.Api.Tests.Orders;

namespace Solvelt.Api.Tests;

/// <summary>Tax on shipping and in refunds never enters the payments ledger: the marketplace remits it, <c>refund.processed</c> carries amounts without it.</summary>
public class PaymentShippingTaxTests(PayFactory f) : IClassFixture<PayFactory>
{
    private static async Task<(string Mid, Guid Id, string Number)> PaidTaxed(Shop s, int qty, long price, long lineTax, long shipping, long shipTax)
    {
        await Pay.Fresh(s);
        var mid = Mid();
        var r = await Inbound(s.F, "/api/internal/v1/orders", OrderBody(s.F, mid, [Line(s.Seller, s.Brand, s.Product, qty, price, "L1", tax: lineTax)],
            [new { sellerId = s.Seller, brandId = s.Brand, amountCents = shipping, taxCents = shipTax }]));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var o = (await Node(r))["orders"]![0]!;
        var id = Guid.Parse((string)o["sellerOrderId"]!); var number = (string)o["number"]!;
        await Ok(Act(s.Owner, id, "accept")); await Ok(Act(s.Owner, id, "ship", Ship())); await Ok(Act(s.Owner, id, "deliver"));
        Assert.Equal(HttpStatusCode.OK, (await Pay.Capture(s.F, mid, number)).StatusCode);
        return (mid, id, number);
    }

    private static List<(LedgerAccount Account, string Kind, long Amount)> Entries(ApiFactory f, Guid orderId) =>
        f.WithDb(db => db.LedgerJournals.AsNoTracking().Include(j => j.Entries).Where(j => j.OrderId == orderId).ToList())
            .SelectMany(j => j.Entries).Select(e => (e.Account, e.Kind, e.AmountCents)).OrderBy(e => e.Account).ThenBy(e => e.Kind).ToList();

    [Fact]
    public async Task Shipping_tax_and_line_tax_do_not_change_a_single_ledger_entry()
    {
        var s = await NewShop(f);
        var plain = await PaidTaxed(s, 1, 1999, 0, 500, 0);
        var taxed = await PaidTaxed(s, 1, 1999, 160, 500, 45);          // same items and shipping, plus 160 line tax and 45 shipping tax
        var a = Entries(f, plain.Id); var b = Entries(f, taxed.Id);
        Assert.NotEmpty(a);
        Assert.Equal(a, b);                                              // sale = items + shipping, fee on the items, nothing for any tax
        Assert.Equal(0, b.Sum(e => e.Amount));                           // and the journal still balances
    }

    [Fact]
    public async Task A_refund_processed_built_from_the_event_fields_without_tax_is_accepted_at_every_step_and_the_ledger_settles()
    {
        var s = await NewShop(f);
        var (mid, id, number) = await PaidTaxed(s, 3, 1000, 100, 500, 45);       // 3000 + 100 + 500 + 45
        var line = await LineId(s.Owner, id);
        for (var i = 0; i < 3; i++)
        {
            var body = i < 2 ? (object)new { reason = "goodwill", lines = new[] { new { lineId = line, quantity = 1 } }, shippingCents = i == 0 ? 200 : 300 }
                             : new { reason = "goodwill", lines = new[] { new { lineId = line, quantity = 1 } } };
            await Ok(Act(s.Owner, id, "refunds", body));
            var ev = JsonNode.Parse(OutboxFor(f, mid).Where(m => m.EventType == "refund.issued").ElementAt(i).PayloadJson)!["data"]!;
            var amount = (long)ev["amountCents"]!; var tax = (long)ev["taxCents"]!; var ship = (long)ev["shippingCents"]!; var shipTax = (long)ev["shippingTaxCents"]!;
            Assert.Equal(tax - shipTax, (long)ev["lines"]![0]!["taxCents"]!);
            // the marketplace backs the tax out: items without tax, shipping without tax
            var r = await Pay.Refund(f, mid, number, amount - tax - ship, ship);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }
        Assert.Equal(3645, f.WithDb(db => db.Refunds.Where(r => r.OrderId == id).Sum(r => r.AmountCents)));
        var ledger = f.WithDb(db => db.LedgerJournals.AsNoTracking().Include(j => j.Entries).Where(j => j.OrderId == id).ToList());
        Assert.All(ledger, j => Assert.Equal(0, j.Entries.Sum(e => e.AmountCents)));
    }
}
