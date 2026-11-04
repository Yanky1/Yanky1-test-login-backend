using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using static Solvelt.Api.Tests.Orders;

namespace Solvelt.Api.Tests;

public class JobFactory : OrdersFactory { public JobFactory() { Extra["Orders:AutoAcceptMinutes"] = "30"; } }

/// <summary>Time-travel tests: one fixture of their own so advancing the fake clock cannot disturb other tests.</summary>
public class OrderJobTests(JobFactory f) : IClassFixture<JobFactory>
{
    private async Task<OrderMaintenance.Summary> Run()
    {
        using var scope = f.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OrderMaintenance>().RunAsync();
    }

    private SellerOrder Row(Guid id) => f.WithDb(db => db.Orders.AsNoTracking().First(o => o.Id == id));

    [Fact]
    public async Task Auto_accept_accepts_new_orders_after_the_configured_delay_and_tells_the_marketplace()
    {
        var s = await NewShop(f);
        var (mid, id, _, _) = await Place(s);
        await Run();
        Assert.Equal(OrderStatus.New, Row(id).Status);            // too early
        f.Time.Advance(TimeSpan.FromMinutes(31));
        Assert.True((await Run()).AutoAccepted >= 1);
        Assert.Equal(OrderStatus.Accepted, Row(id).Status);
        Assert.Contains(OutboxFor(f, mid), m => m.EventType == "order.accepted" && m.PayloadJson.Contains("\"auto\":true"));
        Assert.Contains(f.WithDb(db => db.OrderEvents.Where(e => e.OrderId == id).ToList()), e => e.Type == "order.accepted" && e.Actor == Party.System);
        Assert.Equal(0, (await Run()).AutoAccepted);               // nothing left to accept
    }

    [Fact]
    public async Task Delivered_orders_complete_after_the_return_window_and_buyer_details_are_redacted_after_retention()
    {
        var s = await NewShop(f);
        var (mid, id, number, _) = await Place(s, buyer: "Priya Patel");
        await Ok(Act(s.Owner, id, "accept")); await Ok(Act(s.Owner, id, "ship", Ship())); await Ok(Act(s.Owner, id, "deliver"));
        var (_, _, conv, _) = await Msg.BuyerMessage(f, "order", mid, number, text: "My phone is 555-123-4567");
        var att = await Msg.Upload(s.Owner, conv, TestImages.Png());
        var attId = Guid.Parse((string)(await Node(att))["id"]!);
        await Msg.Reply(s.Owner, conv, "Thanks!", [attId]);
        var key = f.WithDb(db => db.MessageAttachments.First(a => a.Id == attId).StorageKey);
        Assert.True(File.Exists(Path.Combine(f.Dir, "uploads", key)));

        f.Time.Advance(TimeSpan.FromDays(29));
        await Run();
        Assert.Equal(OrderStatus.Delivered, Row(id).Status);       // still inside the 30-day return window
        f.Time.Advance(TimeSpan.FromDays(2));
        Assert.True((await Run()).Completed >= 1);
        var done = Row(id);
        Assert.Equal(OrderStatus.Completed, done.Status); Assert.NotNull(done.CompletedAt);
        Assert.Equal("Priya Patel", done.BuyerName);                // not yet redacted

        f.Time.Advance(TimeSpan.FromDays(88));                      // completed 88 days ago: still retained
        Assert.Equal(0, (await Run()).Redacted);
        f.Time.Advance(TimeSpan.FromDays(3));                       // > 90 days after completion
        var r = await Run();
        Assert.True(r.Redacted >= 1); Assert.True(r.ConversationsRedacted >= 1);
        var red = Row(id);
        Assert.NotNull(red.PiiRedactedAt);
        Assert.Equal("Redacted", red.BuyerName); Assert.Equal("Redacted", red.ShipLine1); Assert.Equal("Redacted", red.ShipCity); Assert.Equal("Redacted", red.ShipPostalCode);
        Assert.Null(red.BuyerPhone); Assert.Null(red.BuyerRelayEmail); Assert.Null(red.ShipLine2); Assert.Null(red.GiftMessage);
        Assert.Equal(1999, red.ItemsCents);                         // money and line snapshots stay (needed for accounting)
        Assert.Equal(s.Sku, f.WithDb(db => db.OrderLines.First(l => l.OrderId == id).Sku));
        // conversation: text and attachments are gone, files deleted
        Assert.All(f.WithDb(db => db.Messages.Where(m => m.ConversationId == conv).Select(m => m.Text).ToList()), t => Assert.Contains("removed", t));
        Assert.Empty(f.WithDb(db => db.MessageAttachments.Where(a => a.ConversationId == conv).ToList()));
        Assert.False(File.Exists(Path.Combine(f.Dir, "uploads", key)));
        Assert.Equal("Redacted", f.WithDb(db => db.Conversations.First(c => c.Id == conv).BuyerName));
        Assert.Equal(0, (await Run()).Redacted);                    // idempotent
    }

    [Fact]
    public async Task Cancelled_orders_and_old_presale_questions_are_redacted_too_and_replay_nonces_expire()
    {
        var s = await NewShop(f);
        var (_, id, _, _) = await Place(s);
        await Ok(Act(s.Owner, id, "cancel", new { reason = "other" }));
        var (_, _, pq, _) = await Msg.BuyerMessage(f, "product", product: s.Product, text: "Is it dishwasher safe? call 555-123-4567");
        Assert.True(f.WithDb(db => db.InboundNonces.Count()) > 0);
        f.Time.Advance(TimeSpan.FromDays(91));
        var r = await Run();
        Assert.Equal("Redacted", Row(id).BuyerName);
        Assert.Equal("Redacted", f.WithDb(db => db.Conversations.First(c => c.Id == pq).BuyerName));
        Assert.True(r.NoncesRemoved > 0);
        Assert.Empty(f.WithDb(db => db.InboundNonces.Where(n => n.ExpiresAt < f.Time.GetUtcNow().UtcDateTime).ToList()));
    }

    [Fact]
    public async Task A_return_in_progress_never_auto_completes()
    {
        var s = await NewShop(f);
        var (mid, id, _, _) = await Place(s, lid: "RL");
        await Ok(Act(s.Owner, id, "accept")); await Ok(Act(s.Owner, id, "ship", Ship())); await Ok(Act(s.Owner, id, "deliver"));
        await Inbound(f, $"/api/internal/v1/orders/{mid}/return-requests", new { returnId = "R", reason = "damaged", lines = new[] { new { marketplaceLineId = "RL", quantity = 1 } } });
        f.Time.Advance(TimeSpan.FromDays(60));
        await Run();
        Assert.Equal(OrderStatus.ReturnRequested, Row(id).Status);
    }
}

/// <summary>A host started in the Production environment: the simulator and the dev endpoints must not exist there.</summary>
public class ProductionFactory : WebApplicationFactory<Program>
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "solvelt-prod-" + Guid.NewGuid().ToString("N"));
    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        Directory.CreateDirectory(_dir);
        b.UseEnvironment("Production");
        var cfg = new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite", ["Database:ConnectionString"] = $"Data Source={Path.Combine(_dir, "p.db")}",
            ["App:HmacSecret"] = "prod-hmac-secret-0123456789abcdef", ["App:UseHttpsRedirection"] = "false", ["App:UseHsts"] = "false",
            ["App:CorsOrigins:0"] = "http://localhost:5173", ["App:FrontendBaseUrl"] = "http://localhost:5173",
            ["Jwt:PrivateKeyPem"] = RSA.Create(2048).ExportPkcs8PrivateKeyPem(), ["Email:Provider"] = "Sink", ["Storage:LocalRoot"] = Path.Combine(_dir, "up"),
            ["Orders:InboundSecret"] = "prod-inbound-secret-0123456789abcdef", ["Orders:OutboundSecret"] = "prod-outbound-secret-fedcba987654321",
            ["LocalDelivery:ProofUrlKey"] = "prod-proof-url-key-0123456789abcdef", ["Orders:RunBackgroundJobs"] = "false", ["Passkeys:RelyingPartyId"] = "localhost", ["Passkeys:AllowedOrigins:0"] = "http://localhost:5173",
            ["AdminSeed:Email"] = "admin@example.com", ["AdminSeed:Password"] = "Qu1et-Marble-Fox-Test!",
        };
        foreach (var (k, v) in cfg) b.UseSetting(k, v);
    }
}

public class OrderSimulatorTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    [Fact]
    public async Task Simulator_creates_single_multi_item_out_of_stock_and_multi_brand_orders_for_the_signed_in_seller()
    {
        var s = await NewShop(f, stock: 20);
        await ActiveProduct(s.Owner, "Second item", s.Brand, "S2-" + Guid.NewGuid().ToString("N")[..6]);
        async Task<JsonNode2> Sim(string scenario) => new(await s.Owner.Post("/api/v1/seller/dev/orders", new { scenario }));
        var one = await Sim("single");
        Assert.Equal(HttpStatusCode.Created, one.R.StatusCode);
        var o1 = (await one.N)["orders"]![0]!;
        var d = await Detail(s.Owner, Guid.Parse((string)o1["id"]!));
        Assert.Equal("New", St(d)); Assert.NotEmpty((string)d["giftMessage"]!);
        var multi = await Sim("multi");
        Assert.Equal(HttpStatusCode.Created, multi.R.StatusCode);
        Assert.True((await Detail(s.Owner, Guid.Parse((string)(await multi.N)["orders"]![0]!["id"]!)))["lines"]!.AsArray().Count >= 2);
        var oos = await Sim("out_of_stock");
        Assert.True((bool)(await oos.N)["orders"]![0]!["oversold"]!);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Sim("multi_brand")).R.StatusCode);     // only one brand so far
        var brand2 = await TeamHelpers.NewBrand(s.Owner, "Brand 2");
        await ActiveProduct(s.Owner, "Brand two item", brand2, "B2-" + Guid.NewGuid().ToString("N")[..6]);
        var split = await Sim("multi_brand");
        Assert.Equal(HttpStatusCode.Created, split.R.StatusCode);
        Assert.Equal(2, (await split.N)["orders"]!.AsArray().Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await Sim("teleport")).R.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Sim("variants")).R.StatusCode);       // no variant product
    }

    private sealed record JsonNode2(HttpResponseMessage R) { public Task<System.Text.Json.Nodes.JsonNode> N => Node(R); }

    [Fact]
    public async Task Simulator_buyer_events_cancel_return_delivered_and_message()
    {
        var s = await NewShop(f);
        var sim = $"/api/v1/seller/dev/orders";
        var o1 = Guid.Parse((string)(await Node(await s.Owner.Post(sim, new { scenario = "single" })))["orders"]![0]!["id"]!);
        var ev = (string type) => s.Owner.Post($"{sim}/{o1}/events", new { type });
        Assert.Equal("requested", (string)(await Node(await ev("cancel_request")))["orders"]![0]!["result"]!);
        Assert.True((bool)(await Detail(s.Owner, o1))["cancelRequested"]!);
        var cid = (string)(await Detail(s.Owner, o1))["cancellations"]![0]!["id"]!;
        await Ok(Act(s.Owner, o1, $"cancellation-requests/{cid}/approve", new { restock = true }));
        var o2 = Guid.Parse((string)(await Node(await s.Owner.Post(sim, new { scenario = "single" })))["orders"]![0]!["id"]!);
        await Ok(Act(s.Owner, o2, "accept")); await Ok(Act(s.Owner, o2, "ship", Ship()));
        Assert.Equal("delivered", (string)(await Node(await s.Owner.Post($"{sim}/{o2}/events", new { type = "delivered" })))["orders"]![0]!["result"]!);
        Assert.Equal("requested", (string)(await Node(await s.Owner.Post($"{sim}/{o2}/events", new { type = "return_request" })))["orders"]![0]!["result"]!);
        var m = await Node(await s.Owner.Post($"{sim}/{o2}/events", new { type = "message", text = "Where is it?" }));
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Get($"/api/v1/seller/messages/{(string)m["conversationId"]!}")).StatusCode);
        var pq = await Node(await s.Owner.Post("/api/v1/seller/dev/messages", new { productId = s.Product, text = "Is it dishwasher safe?" }));
        Assert.Equal("product", (string)(await Node(await s.Owner.Get($"/api/v1/seller/messages/{(string)pq["conversationId"]!}")))["conversation"]!["kind"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.Post($"{sim}/{o2}/events", new { type = "nope" })).StatusCode);
        // a seller cannot drive someone else's order, and a Viewer cannot use the simulator
        var other = await NewShop(f);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.Post($"{sim}/{o2}/events", new { type = "cancel_request" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.Post("/api/v1/seller/dev/messages", new { productId = s.Product })).StatusCode);
        var (viewer, _) = await TeamHelpers.JoinAs(f, s.Owner, "Viewer"); await viewer.Switch(s.Seller);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post(sim, new { scenario = "single" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Post(sim, new { scenario = "single" })).StatusCode);
    }

    [Fact]
    public async Task Simulator_needs_active_products()
    {
        var (api, _) = await ProdApi.Approved(f);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await api.Post("/api/v1/seller/dev/orders", new { scenario = "single" })).StatusCode);
    }

    [Fact]
    public void Simulator_is_only_enabled_in_development_and_testing()
    {
        foreach (var (env, expected) in new[] { ("Development", true), ("Testing", true), ("Production", false), ("Staging", false) })
            Assert.Equal(expected, AdminDevEndpoints.SimulatorEnabled(new FakeEnv(env)));
    }

    private sealed class FakeEnv(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name; public string ApplicationName { get; set; } = "x"; public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

public class OrderProductionTests : IClassFixture<ProductionFactory>
{
    private readonly ProductionFactory _f;
    public OrderProductionTests(ProductionFactory f) => _f = f;

    [Fact]
    public async Task In_production_the_simulator_and_dev_endpoints_do_not_exist_but_the_signed_internal_api_does()
    {
        var c = _f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/api/v1/seller/dev/orders", JsonContent("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/dev/outbox")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/api/dev/maintenance/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/test/emails")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/internal/v1/orders", JsonContent("{}"))).StatusCode);   // present, but needs a signature
    }

    private static StringContent JsonContent(string s) => new(s, System.Text.Encoding.UTF8, "application/json");
}
