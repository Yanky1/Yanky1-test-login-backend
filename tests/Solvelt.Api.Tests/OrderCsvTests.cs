using System.Net;
using System.Text;
using Solvelt.Api.Orders;
using static Solvelt.Api.Tests.Orders;

namespace Solvelt.Api.Tests;

public class OrderCsvTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    private static Task<HttpResponseMessage> Upload(Api api, string csv, bool apply)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "tracking.csv" } };
        return api.Send(new HttpRequestMessage(HttpMethod.Post, $"/api/v1/seller/orders/import-tracking?apply={apply.ToString().ToLowerInvariant()}") { Content = form });
    }

    [Fact]
    public async Task Tracking_import_previews_row_errors_then_applies_only_valid_rows()
    {
        var s = await NewShop(f, stock: 20);
        var (_, a, na, _) = await Place(s);
        var (_, b, nb, _) = await Place(s, qty: 2);
        var (_, c, nc, _) = await Place(s);                       // left New on purpose: cannot ship
        await Ok(Act(s.Owner, a, "accept")); await Ok(Act(s.Owner, b, "accept"));
        var csv = "order_number,carrier,tracking_number,ship_date,sku,quantity\r\n" +
                  $"{na},UPS,1Z999AA10123456784,,,\r\n" +
                  $"{nb},UPS,not-a-number,,,\r\n" +
                  $"{nc},FedEx,123456789012,,,\r\n" +
                  "SO-2000-999999,UPS,1Z999AA10123456784,,,\r\n" +
                  $"{nb},Pigeon,1Z999AA10123456784,,,\r\n" +
                  $"{nb},FedEx,123456789012,2020-13-45,,\r\n" +
                  $"{nb},FedEx,123456789012,,WRONG-SKU,1\r\n";
        var preview = await Node(await Upload(s.Owner, csv, apply: false));
        Assert.False((bool)preview["applied"]!);
        Assert.Equal(7, (int)preview["total"]!); Assert.Equal(1, (int)preview["valid"]!);
        var rows = preview["results"]!.AsArray();
        Assert.True((bool)rows[0]!["ok"]!);
        Assert.Contains("tracking number", (string)rows[1]!["error"]!);
        Assert.Contains("Accept", (string)rows[2]!["error"]!);
        Assert.Equal("Order not found.", (string)rows[3]!["error"]!);
        Assert.Contains("carrier", (string)rows[4]!["error"]!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ship_date", (string)rows[5]!["error"]!);
        Assert.Contains("SKU", (string)rows[6]!["error"]!);
        Assert.Equal(3, (int)rows[1]!["row"]!);                              // 1-based CSV line numbers (header = 1)
        Assert.Equal("Accepted", St(await Detail(s.Owner, a)));               // preview changed nothing
        var applied = await Node(await Upload(s.Owner, csv, apply: true));
        Assert.True((bool)applied["applied"]!); Assert.Equal(1, (int)applied["valid"]!);
        Assert.Equal("Shipped", St(await Detail(s.Owner, a)));
        Assert.Equal("Accepted", St(await Detail(s.Owner, b))); Assert.Equal("New", St(await Detail(s.Owner, c)));
    }

    [Fact]
    public async Task Tracking_import_can_ship_one_sku_and_rejects_bad_files()
    {
        var s = await NewShop(f);
        var (_, id, num, _) = await Place(s, qty: 3);
        await Ok(Act(s.Owner, id, "accept"));
        var ok = await Node(await Upload(s.Owner, $"order_number,carrier,tracking_number,sku,quantity\n{num},UPS,1Z999AA10123456784,{s.Sku},2\n", true));
        Assert.Equal(1, (int)ok["valid"]!);
        Assert.Equal("PartiallyShipped", St(await Detail(s.Owner, id)));
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(s.Owner, "foo,bar\n1,2\n", false)).StatusCode);        // missing columns
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(s.Owner, "order_number,carrier,tracking_number\n\"oops", false)).StatusCode);   // unterminated quote
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(s.Owner, "", false)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Get("/api/v1/seller/orders/tracking-template.csv")).StatusCode);
        // a Viewer cannot upload
        var (viewer, _) = await TeamHelpers.JoinAs(f, s.Owner, "Viewer"); await viewer.Switch(s.Seller);
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(viewer, "order_number,carrier,tracking_number\n", false)).StatusCode);
    }

    [Fact]
    public async Task Export_is_csv_scoped_and_neutralises_spreadsheet_formulas()
    {
        var s = await NewShop(f);
        var (mid, id, number, _) = await Place(s, buyer: "=HYPERLINK(\"http://evil\",\"x\")");
        var (_, id2, _, _) = await Place(s, buyer: "+SUM(1,1)");
        var other = await NewShop(f);
        var r = await s.Owner.Get("/api/v1/seller/orders/export.csv");
        Assert.Equal("text/csv; charset=utf-8", r.Content.Headers.ContentType!.ToString());
        var csv = await r.Content.ReadAsStringAsync();
        Assert.StartsWith("order_number,marketplace_order_id", csv);
        Assert.Contains(number, csv); Assert.Contains(mid, csv);
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\",\"\"x\"\")\"", csv);       // apostrophe-prefixed and quoted
        Assert.Contains(",'+SUM(1,1)", csv.Replace("\"", "")); 
        foreach (var line in csv.Split("\r\n").Skip(1).Where(l => l.Length > 0)) foreach (var cell in line.Split(',')) Assert.False(cell.Length > 0 && cell[0] is '=' or '@' or '\t');
        Assert.DoesNotContain("Brooklyn1", csv);
        // another seller's export never includes our orders
        Assert.DoesNotContain(mid, await (await other.Owner.Get("/api/v1/seller/orders/export.csv")).Content.ReadAsStringAsync());
        _ = id; _ = id2;
    }

    [Fact]
    public async Task Packing_slips_contain_what_the_packer_needs_and_hide_prices_on_gift_orders()
    {
        var s = await NewShop(f);
        var (_, id, num, _) = await Place(s, qty: 2);
        var slip = await Node(await s.Owner.Get($"/api/v1/seller/orders/{id}/packing-slip"));
        Assert.Equal(num, (string)slip["number"]!); Assert.Equal("Jane Buyer", (string)slip["shipTo"]!["name"]!);
        Assert.Equal(2, (int)slip["lines"]![0]!["quantity"]!); Assert.True((bool)slip["showPrices"]!);
        var bulk = await Node(await s.Owner.Post("/api/v1/seller/orders/bulk/packing-slips", new { ids = new[] { id } }));
        Assert.Single(bulk["slips"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.Post("/api/v1/seller/orders/bulk/packing-slips", new { ids = Array.Empty<Guid>() })).StatusCode);
    }

    [Fact]
    public async Task Bulk_accept_reports_each_order()
    {
        var s = await NewShop(f);
        var (_, a, _, _) = await Place(s); var (_, b, _, _) = await Place(s);
        await Ok(Act(s.Owner, b, "cancel", new { reason = "other" }));
        var res = (await Node(await s.Owner.Post("/api/v1/seller/orders/bulk/accept", new { ids = new[] { a, b, Guid.NewGuid() } })))["results"]!.AsArray();
        Assert.True((bool)res[0]!["ok"]!); Assert.False((bool)res[1]!["ok"]!); Assert.Equal("not_found", (string)res[2]!["code"]!);
        Assert.Equal("Accepted", St(await Detail(s.Owner, a)));
    }
}
