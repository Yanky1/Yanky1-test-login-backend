using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using static Solvelt.Api.Tests.Orders;

namespace Solvelt.Api.Tests;

public class BlockFactory : OrdersFactory { public BlockFactory() { Extra["Orders:BlockContactDetails"] = "true"; } }

public static class Msg
{
    public static string Id() => "M-" + Guid.NewGuid().ToString("N")[..12];

    public static async Task<(string ConvMid, string MsgId, Guid Conv, Guid Message)> BuyerMessage(ApiFactory f, string kind, string? marketplaceOrderId = null, string? number = null, Guid? product = null,
        string text = "Hi, when will this ship?", string? conv = null, string? msgId = null)
    {
        var c = conv ?? "C-" + Guid.NewGuid().ToString("N")[..10]; var m = msgId ?? Id();
        var r = await Inbound(f, "/api/internal/v1/messages", new { messageId = m, conversationId = c, kind, marketplaceOrderId, sellerOrderNumber = number, productId = product, buyerName = "Jane B.", text });
        Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        var n = await Node(r);
        return (c, m, Guid.Parse((string)n["conversationId"]!), Guid.Parse((string)n["messageId"]!));
    }

    public static async Task<HttpResponseMessage> Upload(Api api, Guid conv, byte[] data, string name = "photo.png", string type = "image/png")
    {
        var form = new MultipartFormDataContent();
        var fc = new ByteArrayContent(data); fc.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(fc, "file", name);
        return await api.Send(new HttpRequestMessage(HttpMethod.Post, $"/api/v1/seller/messages/{conv}/attachments") { Content = form });
    }

    public static Task<HttpResponseMessage> Reply(Api api, Guid conv, string text, Guid[]? atts = null, bool ack = false) =>
        api.Post($"/api/v1/seller/messages/{conv}/messages", new { text, attachmentIds = atts, acknowledgeWarning = ack });

    /// <summary>An open event stream drained by a background pump, so waiting for (or not seeing) an event never disturbs the connection.</summary>
    public sealed class Sse : IDisposable
    {
        private readonly List<string> _events = [];
        private readonly CancellationTokenSource _cts = new();
        public bool Ended { get; private set; }
        public Sse(StreamReader r)
        {
            _ = Task.Run(async () =>
            {
                try { while (await r.ReadLineAsync(_cts.Token) is { } line) if (line.StartsWith("event: ")) lock (_events) _events.Add(line[7..]); }
                catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
                Ended = true;
            });
        }
        public async Task<bool> Gets(string type, int ms = 3000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until)
            {
                lock (_events) { var i = _events.IndexOf(type); if (i >= 0) { _events.RemoveAt(i); return true; } }
                await Task.Delay(25);
            }
            return false;
        }
        public void Dispose() => _cts.Cancel();
    }

    public static async Task<Sse> Open(Api api, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/seller/events");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", api.Session!.AccessToken);
        var res = await api.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType!.MediaType);
        return new Sse(new StreamReader(await res.Content.ReadAsStreamAsync(ct)));
    }
}

public class OrderMessagingTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    [Fact]
    public async Task Buyer_message_ingest_is_signed_idempotent_and_linked_to_the_order()
    {
        var s = await NewShop(f);
        var (mid, id, number, _) = await Place(s);
        var (_, msgId, conv, message) = await Msg.BuyerMessage(f, "order", mid, number);
        // same message id again -> same ids, still one message
        var again = await Inbound(f, "/api/internal/v1/messages", new { messageId = msgId, conversationId = "whatever", kind = "order", marketplaceOrderId = mid, sellerOrderNumber = number, buyerName = "Jane B.", text = "changed text" });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var an = await Node(again);
        Assert.True((bool)an["duplicate"]!); Assert.Equal(message.ToString(), (string)an["messageId"]!);
        Assert.Equal(1, f.WithDb(db => db.Messages.Count(m => m.ConversationId == conv)));
        // unsigned is rejected
        var unsigned = await f.NewClient().SendAsync(Signed(f, "POST", "/api/internal/v1/messages", new { messageId = "x" }, sign: false));
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        // a second message on the same marketplace thread appends
        await Msg.BuyerMessage(f, "order", mid, number, conv: (await Node(await s.Owner.Get($"/api/v1/seller/messages/{conv}")))["conversation"]!["id"] is null ? null : f.WithDb(db => db.Conversations.First(c => c.Id == conv).MarketplaceConversationId), text: "Any update?");
        var d = await Node(await s.Owner.Get($"/api/v1/seller/messages/{conv}"));
        Assert.Equal(2, d["messages"]!.AsArray().Count);
        Assert.Equal(number, (string)d["order"]!["number"]!); Assert.Equal("order", (string)d["conversation"]!["kind"]!);
        Assert.Equal(conv.ToString(), (string)(await Detail(s.Owner, id))["conversationId"]!);
    }

    [Fact]
    public async Task Ingest_validates_and_refuses_ambiguous_or_unknown_targets()
    {
        var s = await NewShop(f);
        var other = await NewShop(f);
        var mid = Mid();
        var split = await Inbound(f, "/api/internal/v1/orders", OrderBody(f, mid, [Line(s.Seller, s.Brand, s.Product), Line(other.Seller, other.Brand, other.Product)]));
        Assert.Equal(HttpStatusCode.Created, split.StatusCode);
        async Task<(HttpStatusCode, string?)> Try(object body) { var r = await Inbound(f, "/api/internal/v1/messages", body); return (r.StatusCode, await Code(r)); }
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "ambiguous_order"), await Try(new { messageId = Msg.Id(), conversationId = "c1" + Msg.Id(), kind = "order", marketplaceOrderId = mid, buyerName = "B", text = "hi" }));
        Assert.Equal((HttpStatusCode.NotFound, "not_found"), await Try(new { messageId = Msg.Id(), conversationId = "c2" + Msg.Id(), kind = "order", marketplaceOrderId = "NOPE", buyerName = "B", text = "hi" }));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "text"), await Try(new { messageId = Msg.Id(), conversationId = "c3", kind = "order", marketplaceOrderId = mid, buyerName = "B", text = " " }));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "kind"), await Try(new { messageId = Msg.Id(), conversationId = "c4", kind = "email", buyerName = "B", text = "hi" }));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "text"), await Try(new { messageId = Msg.Id(), conversationId = "c5", kind = "order", marketplaceOrderId = mid, buyerName = "B", text = new string('x', 4001) }));
        var draft = await ProdApi.ReadyProduct(s.Owner, "Not live", s.Brand, "ND-" + Guid.NewGuid().ToString("N")[..6]);
        Assert.Equal((HttpStatusCode.NotFound, "not_found"), await Try(new { messageId = Msg.Id(), conversationId = "c6" + Msg.Id(), kind = "product", productId = draft, buyerName = "B", text = "hi" }));
    }

    [Fact]
    public async Task Reply_goes_to_the_outbox_marks_the_conversation_answered_and_receipts_update_delivery()
    {
        var s = await NewShop(f);
        var (mid, _, number, _) = await Place(s);
        var (convMid, msgId, conv, buyerMsg) = await Msg.BuyerMessage(f, "order", mid, number);
        var counts = await Node(await s.Owner.Get("/api/v1/seller/messages/counts"));
        Assert.True((int)counts["needsReply"]! >= 1);
        var r = await Msg.Reply(s.Owner, conv, "Your parcel ships tomorrow!");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var mid2 = (string)(await Node(r))["message"]!["id"]!;
        var ev = JsonNode.Parse(OutboxFor(f, convMid).Single(m => m.EventType == "message.sent").PayloadJson)!;
        Assert.Equal("Your parcel ships tomorrow!", (string)ev["data"]!["text"]!);
        Assert.Equal(convMid, (string)ev["data"]!["marketplaceConversationId"]!); Assert.Equal(mid, (string)ev["data"]!["marketplaceOrderId"]!); Assert.Equal("order", (string)ev["data"]!["kind"]!);
        var d = await Node(await s.Owner.Get($"/api/v1/seller/messages/{conv}"));
        Assert.False((bool)d["conversation"]!["needsReply"]!);
        JsonNode By(JsonNode n, string dir) => n["messages"]!.AsArray().First(m => (string)m!["direction"]! == dir)!;
        Assert.NotNull(By(d, "buyer")["respondedAt"]); Assert.Equal("pending", (string)By(d, "seller")["delivery"]!);
        // marketplace reports delivery then read
        Assert.Equal("delivered", (string)(await Node(await Inbound(f, $"/api/internal/v1/messages/{mid2}/receipts", new { status = "delivered" })))["status"]!);
        Assert.Equal("read", (string)(await Node(await Inbound(f, $"/api/internal/v1/messages/{mid2}/receipts", new { status = "read" })))["status"]!);
        Assert.Equal("read", (string)(await Node(await Inbound(f, $"/api/internal/v1/messages/{mid2}/receipts", new { status = "delivered" })))["status"]!);   // never goes backwards
        Assert.Equal("read", (string)By(await Node(await s.Owner.Get($"/api/v1/seller/messages/{conv}")), "seller")["delivery"]!);
        // empty and oversized replies are rejected
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Msg.Reply(s.Owner, conv, " ")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Msg.Reply(s.Owner, conv, new string('a', 4001))).StatusCode);
        _ = msgId; _ = buyerMsg;
    }

    [Fact]
    public async Task Unread_counts_are_per_user_and_clear_when_read()
    {
        var s = await NewShop(f);
        var (admin, _) = await TeamHelpers.JoinAs(f, s.Owner, "Admin"); await admin.Switch(s.Seller);
        var (mid, _, number, _) = await Place(s);
        var (_, _, conv, _) = await Msg.BuyerMessage(f, "order", mid, number);
        Assert.Equal(1, (int)(await Node(await s.Owner.Get("/api/v1/seller/messages/counts")))["unread"]!);
        Assert.Equal(1, (int)(await Node(await admin.Get("/api/v1/seller/messages/counts")))["unread"]!);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.Post($"/api/v1/seller/messages/{conv}/read")).StatusCode);
        Assert.Equal(0, (int)(await Node(await s.Owner.Get("/api/v1/seller/messages/counts")))["unread"]!);
        Assert.Equal(1, (int)(await Node(await admin.Get("/api/v1/seller/messages/counts")))["unread"]!);      // someone else's read doesn't clear mine
        Assert.Contains(conv.ToString(), await (await admin.Get("/api/v1/seller/messages?filter=unread")).Content.ReadAsStringAsync());
        Assert.DoesNotContain(conv.ToString(), await (await s.Owner.Get("/api/v1/seller/messages?filter=unread")).Content.ReadAsStringAsync());
        await Msg.BuyerMessage(f, "order", mid, number, conv: f.WithDb(db => db.Conversations.First(c => c.Id == conv).MarketplaceConversationId), text: "Hello?");
        Assert.Equal(1, (int)(await Node(await s.Owner.Get("/api/v1/seller/messages/counts")))["unread"]!);   // a new buyer message is unread again
    }

    // ---- attachments ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Attachments_are_content_sniffed_stripped_limited_and_served_safely()
    {
        var s = await NewShop(f);
        var (mid, _, number, _) = await Place(s);
        var (_, _, conv, _) = await Msg.BuyerMessage(f, "order", mid, number);
        // real PNG with metadata: accepted, EXIF/text chunks removed from what is stored
        var up = await Msg.Upload(s.Owner, conv, TestImages.Png(), "my photo?.png");
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        var att = await Node(up);
        Assert.Equal("image/png", (string)att["contentType"]!); Assert.EndsWith(".png", (string)att["fileName"]!); Assert.DoesNotContain("?", (string)att["fileName"]!);
        // JPEG sent with a wrong declared name/type still works as JPEG (sniffed), extension corrected
        var jpg = await Node(await Msg.Upload(s.Owner, conv, TestImages.Jpeg(), "evil.exe", "application/x-msdownload"));
        Assert.Equal("image/jpeg", (string)jpg["contentType"]!); Assert.EndsWith(".jpg", (string)jpg["fileName"]!);
        Assert.Equal(HttpStatusCode.Created, (await Msg.Upload(s.Owner, conv, TestImages.WebP(), "w.webp", "image/webp")).StatusCode);
        var pdf = await Msg.Upload(s.Owner, conv, Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF"), "invoice.pdf", "application/pdf");
        Assert.Equal(HttpStatusCode.Created, pdf.StatusCode);

        // spoofed: text/script claiming to be an image, an HTML file named .pdf, an executable, a PDF with JavaScript, a truncated PNG header
        foreach (var (bytes, name, type) in new (byte[], string, string)[]
        {
            (Encoding.UTF8.GetBytes("<script>alert(1)</script>"), "x.png", "image/png"),
            (Encoding.UTF8.GetBytes("<html><body>hi</body></html>"), "x.pdf", "application/pdf"),
            ([0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04], "setup.png", "image/png"),
            (Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<</S/JavaScript/JS(app.alert(1))>>endobj"), "evil.pdf", "application/pdf"),
            ([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00], "trunc.png", "image/png"),
            (new byte[3], "tiny.png", "image/png"),
        })
        {
            var bad = await Msg.Upload(s.Owner, conv, bytes, name, type);
            Assert.True(bad.StatusCode is HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity, $"{name}: {bad.StatusCode}");
        }
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Msg.Upload(s.Owner, conv, new byte[6 * 1024 * 1024])).StatusCode);

        // stored bytes carry no metadata; served with nosniff, correct type and a Content-Disposition
        var key = f.WithDb(db => db.MessageAttachments.First(a => a.Id == Guid.Parse((string)att["id"]!)).StorageKey);
        Assert.Matches("^[0-9a-f]{32}$", key);
        var stored = await File.ReadAllBytesAsync(Path.Combine(f.Dir, "uploads", key));
        Assert.DoesNotContain("SECRET", Encoding.Latin1.GetString(stored));
        // send the message with the attachment, then download it
        var sent = await Msg.Reply(s.Owner, conv, "Here is a photo", [Guid.Parse((string)att["id"]!), Guid.Parse((string)(await Node(pdf))["id"]!)]);
        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        var dl = await s.Owner.Get($"/api/v1/seller/messages/{conv}/attachments/{(string)att["id"]!}");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Equal("image/png", dl.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", dl.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.StartsWith("inline; filename=", dl.Content.Headers.ContentDisposition!.ToString().Replace("\"", "").Replace("inline;", "inline;"));
        var dlPdf = await s.Owner.Get($"/api/v1/seller/messages/{conv}/attachments/{(string)(await Node(pdf))["id"]!}");
        Assert.StartsWith("attachment", dlPdf.Content.Headers.ContentDisposition!.DispositionType);
        // the outbox event lists the attachments for the marketplace to fetch
        var ev = JsonNode.Parse(f.WithDb(db => db.Outbox.AsNoTracking().OrderByDescending(m => m.CreatedAt).First(m => m.EventType == "message.sent" && m.PayloadJson.Contains("Here is a photo")).PayloadJson))!;
        Assert.Equal(2, ev["data"]!["attachments"]!.AsArray().Count);
        // the marketplace fetches it with a signed GET; an unsigned GET is refused
        var aPath = (string)ev["data"]!["attachments"]![0]!["path"]!;
        Assert.Equal(HttpStatusCode.OK, (await f.NewClient().SendAsync(Signed(f, "GET", aPath, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().SendAsync(Signed(f, "GET", aPath, null, sign: false))).StatusCode);
    }

    [Fact]
    public async Task Attachment_count_limit_drafts_and_reuse_are_enforced()
    {
        var s = await NewShop(f);
        var (mid, _, number, _) = await Place(s);
        var (_, _, conv, _) = await Msg.BuyerMessage(f, "order", mid, number);
        var (_, _, conv2, _) = await Msg.BuyerMessage(f, "order", mid, number);   // new thread (different conversation id)
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++) ids.Add(Guid.Parse((string)(await Node(await Msg.Upload(s.Owner, conv, TestImages.Png())))["id"]!));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Msg.Upload(s.Owner, conv, TestImages.Png())).StatusCode);      // 6th pending upload
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Msg.Reply(s.Owner, conv, "x", [.. ids, Guid.NewGuid()])).StatusCode);
        // a draft attachment is invisible to teammates and cannot be attached from another conversation
        var (admin, _) = await TeamHelpers.JoinAs(f, s.Owner, "Admin"); await admin.Switch(s.Seller);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Get($"/api/v1/seller/messages/{conv}/attachments/{ids[0]}")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Msg.Reply(admin, conv, "stolen", [ids[0]])).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Msg.Reply(s.Owner, conv2, "wrong thread", [ids[0]])).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Msg.Reply(s.Owner, conv, "ok", ids.Take(2).ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Msg.Reply(s.Owner, conv, "again", [ids[0]])).StatusCode);     // already sent
        Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.Delete($"/api/v1/seller/messages/{conv}/attachments/{ids[3]}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Owner.Delete($"/api/v1/seller/messages/{conv}/attachments/{ids[0]}")).StatusCode);   // sent: can't be removed
    }

    // ---- scope ---------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Conversations_and_attachments_are_invisible_across_accounts_and_out_of_scope_brands()
    {
        var s = await NewShop(f);
        var other = await NewShop(f);
        var brandY = await TeamHelpers.NewBrand(s.Owner, "Brand Y");
        var py = await ActiveProduct(s.Owner, "Y thing", brandY, "Y-" + Guid.NewGuid().ToString("N")[..6]);
        var (mid, _, number, _) = await Place(s);
        var (_, _, conv, _) = await Msg.BuyerMessage(f, "order", mid, number);
        var (_, _, convY, _) = await Msg.BuyerMessage(f, "product", product: py);
        var att = Guid.Parse((string)(await Node(await Msg.Upload(s.Owner, conv, TestImages.Png())))["id"]!);
        await Msg.Reply(s.Owner, conv, "hello", [att]);

        var o = other.Owner;
        foreach (var url in new[] { $"/api/v1/seller/messages/{conv}", $"/api/v1/seller/messages/{conv}/attachments/{att}" }) Assert.Equal(HttpStatusCode.NotFound, (await o.Get(url)).StatusCode);
        foreach (var act in new[] { "read", "messages", "attachments", "assign", "no-reply-needed", "notes" })
            Assert.Equal(HttpStatusCode.NotFound, (await o.Post($"/api/v1/seller/messages/{conv}/{act}", new { text = "x", userId = (Guid?)null })).StatusCode);
        Assert.DoesNotContain(conv.ToString(), await (await o.Get("/api/v1/seller/messages?pageSize=100")).Content.ReadAsStringAsync());

        var (mgr, _) = await TeamHelpers.JoinAs(f, s.Owner, "Manager", [s.Brand]); await mgr.Switch(s.Seller);
        Assert.Equal(HttpStatusCode.OK, (await mgr.Get($"/api/v1/seller/messages/{conv}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mgr.Get($"/api/v1/seller/messages/{convY}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Msg.Reply(mgr, convY, "x")).StatusCode);
        var list = await (await mgr.Get("/api/v1/seller/messages?pageSize=100")).Content.ReadAsStringAsync();
        Assert.Contains(conv.ToString(), list); Assert.DoesNotContain(convY.ToString(), list);
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Get($"/api/v1/seller/messages/{convY}")).StatusCode);   // owner sees all brands
        // a Viewer reads but cannot reply, upload, assign or add notes
        var (viewer, _) = await TeamHelpers.JoinAs(f, s.Owner, "Viewer"); await viewer.Switch(s.Seller);
        Assert.Equal(HttpStatusCode.OK, (await viewer.Get($"/api/v1/seller/messages/{conv}")).StatusCode);
        Assert.False((bool)(await Node(await viewer.Get($"/api/v1/seller/messages/{conv}")))["canWrite"]!);
        foreach (var act in new[] { "messages", "attachments", "assign", "no-reply-needed", "notes" })
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post($"/api/v1/seller/messages/{conv}/{act}", new { text = "x" })).StatusCode);
    }

    // ---- contact details -----------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData("call me on 555-123-4567", "phone")] [InlineData("my email is bob@example.com", "email")] [InlineData("bob (at) example (dot) com", "email")]
    [InlineData("pay via paypal.me/bob", "link")] [InlineData("see https://example.com/pay", "link")] [InlineData("send it on Venmo please", "payment")]
    [InlineData("cashapp $bobby", "payment")] [InlineData("text me", "payment")]
    public void Contact_details_and_payment_links_are_detected(string text, string kind) => Assert.Contains(kind, ContactDetector.Detect(text));

    [Theory]
    [InlineData("Your order ships tomorrow, tracking SO-2026-000123.")] [InlineData("Thanks! We have 3 in stock and 12 colours.")] [InlineData("It measures 12.5 x 8 cm.")]
    public void Normal_messages_are_not_flagged(string text) => Assert.Empty(ContactDetector.Detect(text));

    [Fact]
    public async Task Sharing_contact_details_warns_first_then_sends_once_acknowledged_and_is_logged()
    {
        var s = await NewShop(f);
        var (mid, _, number, _) = await Place(s);
        var (_, _, conv, _) = await Msg.BuyerMessage(f, "order", mid, number);
        var r = await Msg.Reply(s.Owner, conv, "Email me at bob@example.com and pay by Venmo");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var body = await Node(r);
        Assert.Equal("contact_details_warning", (string)body["code"]!); Assert.Contains("email", body["warnings"]!.AsArray().Select(x => (string)x!));
        Assert.Equal(0, f.WithDb(db => db.Messages.Count(m => m.ConversationId == conv && m.Direction == MessageDirection.Seller)));
        var ok = await Msg.Reply(s.Owner, conv, "Email me at bob@example.com and pay by Venmo", ack: true);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.True((bool)(await Node(ok))["message"]!["contactWarning"]!);
        Assert.Contains(f.WithDb(db => db.AuditEvents.Where(a => a.Type == "message.contact_details").Select(a => a.Detail).ToList()), d => d != null && d.Contains(conv.ToString()));
    }

    [Fact]
    public async Task Sharing_contact_details_can_be_blocked_by_configuration()
    {
        using var bf = new BlockFactory();
        var s = await NewShop(bf);
        var (mid, _, number, _) = await Place(s);
        var (_, _, conv, _) = await Msg.BuyerMessage(bf, "order", mid, number);
        var r = await Msg.Reply(s.Owner, conv, "call me 555-123-4567", ack: true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode); Assert.Equal("contact_details_blocked", await Code(r));
        Assert.Equal(HttpStatusCode.Created, (await Msg.Reply(s.Owner, conv, "Happy to help here.")).StatusCode);
    }

    // ---- team features, pre-sale, templates ---------------------------------------------------------------------------------
    [Fact]
    public async Task Presale_product_question_is_answered_through_the_outbox()
    {
        var s = await NewShop(f);
        var (cm, _, conv, _) = await Msg.BuyerMessage(f, "product", product: s.Product, text: "Does this come in blue?");
        var d = await Node(await s.Owner.Get($"/api/v1/seller/messages/{conv}"));
        Assert.Equal("product", (string)d["conversation"]!["kind"]!); Assert.Null(d["order"]); Assert.Equal(s.Product.ToString(), (string)d["product"]!["id"]!);
        Assert.Equal("Speckled stoneware coffee mug", (string)d["conversation"]!["subject"]!);
        Assert.Equal(HttpStatusCode.Created, (await Msg.Reply(s.Owner, conv, "Yes, in blue and green.")).StatusCode);
        var ev = JsonNode.Parse(OutboxFor(f, cm).Single().PayloadJson)!["data"]!;
        Assert.Equal("product", (string)ev["kind"]!); Assert.Equal(s.Product.ToString(), (string)ev["productId"]!);
        Assert.Contains(conv.ToString(), await (await s.Owner.Get("/api/v1/seller/messages?kind=product")).Content.ReadAsStringAsync());
        Assert.DoesNotContain(conv.ToString(), await (await s.Owner.Get("/api/v1/seller/messages?kind=order&pageSize=100")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Assign_no_reply_internal_notes_filters_and_search()
    {
        var s = await NewShop(f);
        var (mgr, mgrEmail) = await TeamHelpers.JoinAs(f, s.Owner, "Manager"); await mgr.Switch(s.Seller);
        var (viewer, _) = await TeamHelpers.JoinAs(f, s.Owner, "Viewer");
        var (mid, _, number, _) = await Place(s);
        var (_, _, conv, _) = await Msg.BuyerMessage(f, "order", mid, number, text: "Where is my teapot?");
        var mgrId = f.WithDb(db => db.Users.First(u => u.Email == mgrEmail).Id);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.Post($"/api/v1/seller/messages/{conv}/assign", new { userId = mgrId })).StatusCode);
        Assert.Contains(conv.ToString(), await (await mgr.Get("/api/v1/seller/messages?assignedToMe=true")).Content.ReadAsStringAsync());
        Assert.DoesNotContain(conv.ToString(), await (await s.Owner.Get("/api/v1/seller/messages?assignedToMe=true&pageSize=100")).Content.ReadAsStringAsync());
        // can't assign to a viewer, a stranger, or a member of another account
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.Post($"/api/v1/seller/messages/{conv}/assign", new { userId = f.WithDb(db => db.Users.First(u => u.Email == viewer.Session!.Email).Id) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.Post($"/api/v1/seller/messages/{conv}/assign", new { userId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.Post($"/api/v1/seller/messages/{conv}/assign", new { userId = f.WithDb(db => db.Users.First(u => u.Email == ApiFactory.AdminEmail).Id) })).StatusCode);
        // internal notes are team-only: not in the outbox, not in the message thread
        Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.Post($"/api/v1/seller/messages/{conv}/notes", new { text = "Buyer was rude last time" })).StatusCode);
        var d = await Node(await s.Owner.Get($"/api/v1/seller/messages/{conv}"));
        Assert.Equal("Buyer was rude last time", (string)d["notes"]![0]!["text"]!);
        Assert.DoesNotContain("rude", d["messages"]!.ToJsonString());
        Assert.Empty(f.WithDb(db => db.Outbox.Where(m => m.PayloadJson.Contains("rude")).ToList()));
        // no reply needed clears the needs-reply state, a new buyer message brings it back
        Assert.Contains(conv.ToString(), await (await s.Owner.Get("/api/v1/seller/messages?filter=needs_reply&pageSize=100")).Content.ReadAsStringAsync());
        await s.Owner.Post($"/api/v1/seller/messages/{conv}/no-reply-needed", new { value = true });
        Assert.DoesNotContain(conv.ToString(), await (await s.Owner.Get("/api/v1/seller/messages?filter=needs_reply&pageSize=100")).Content.ReadAsStringAsync());
        // search by buyer message text
        Assert.Contains(conv.ToString(), await (await s.Owner.Get("/api/v1/seller/messages?q=teapot")).Content.ReadAsStringAsync());
        Assert.DoesNotContain(conv.ToString(), await (await s.Owner.Get("/api/v1/seller/messages?q=zzzz")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.Get("/api/v1/seller/messages?filter=bogus")).StatusCode);
    }

    [Fact]
    public async Task Overdue_replies_are_flagged_counted_on_the_dashboard_and_notified_once()
    {
        var s = await NewShop(f);
        var (mid, _, number, _) = await Place(s);
        var (_, _, conv, _) = await Msg.BuyerMessage(f, "order", mid, number);
        Assert.False((bool)(await Node(await s.Owner.Get($"/api/v1/seller/messages/{conv}")))["conversation"]!["overdue"]!);
        f.WithDb(db => { var c = db.Conversations.First(x => x.Id == conv); c.AwaitingReplySince = f.Time.GetUtcNow().UtcDateTime.AddHours(-30); db.SaveChanges(); return 0; });
        var d = await Node(await s.Owner.Get($"/api/v1/seller/messages/{conv}"));
        Assert.True((bool)d["conversation"]!["overdue"]!);
        Assert.True((int)(await Node(await s.Owner.Get("/api/v1/seller/messages/counts")))["overdue"]! >= 1);
        Assert.True((int)(await Node(await s.Owner.Get("/api/v1/seller/orders/dashboard")))["messages"]!["overdue"]! >= 1);
        using (var scope = f.Services.CreateScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<OrderMaintenance>();
            Assert.True((await job.RunAsync()).OverdueNotified >= 1);
            Assert.Equal(0, (await scope.ServiceProvider.GetRequiredService<OrderMaintenance>().RunAsync()).OverdueNotified);   // only once
        }
        Assert.Single(f.Mails(s.Owner.Session!.Email, "message-overdue"));
        await Msg.Reply(s.Owner, conv, "Sorry for the wait!");
        Assert.False((bool)(await Node(await s.Owner.Get($"/api/v1/seller/messages/{conv}")))["conversation"]!["overdue"]!);
    }

    [Fact]
    public async Task Response_time_metrics_appear_on_the_dashboard()
    {
        var s = await NewShop(f);
        var (mid, _, number, _) = await Place(s);
        var (_, _, conv, _) = await Msg.BuyerMessage(f, "order", mid, number);
        await Msg.Reply(s.Owner, conv, "On it!");
        var m = (await Node(await s.Owner.Get("/api/v1/seller/orders/dashboard")))["messages"]!;
        Assert.NotNull(m["avgResponseHours"]); Assert.Equal(100, (double)m["repliedInTimePct"]!);
    }

    [Fact]
    public async Task Seller_can_start_an_order_conversation_and_buyer_reports_flag_messages_for_admin_review()
    {
        var s = await NewShop(f);
        var (mid, id, number, _) = await Place(s);
        var started = await s.Owner.Post($"/api/v1/seller/orders/{id}/messages", new { text = "Quick question about your address." });
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var conv = Guid.Parse((string)(await Node(started))["conversationId"]!);
        Assert.Equal(conv.ToString(), (string)(await Detail(s.Owner, id))["conversationId"]!);
        Assert.Contains(OutboxFor(f, conv.ToString()), m => m.EventType == "message.sent" && m.PayloadJson.Contains(mid));     // no marketplace thread yet: routed by order id
        // buyer abuse report
        var (_, msgId, convB, _) = await Msg.BuyerMessage(f, "order", mid, number, text: "You are scammers!!!");
        Assert.Equal(HttpStatusCode.OK, (await Inbound(f, $"/api/internal/v1/messages/{msgId}/reports", new { reason = "abusive" })).StatusCode);
        Assert.True((bool)(await Node(await s.Owner.Get($"/api/v1/seller/messages/{convB}")))["conversation"]!["flagged"]!);
        // staff area: a seller-kind Admin session (no staff session, no 2FA) is refused, a staff Moderator is not
        var legacyAdmin = new Api(f); await legacyAdmin.LoginAs(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await legacyAdmin.Get("/api/v1/admin/messages/reported")).StatusCode);
        var admin = await StaffKit.LoginNew(f, Solvelt.Api.Admin.StaffRole.Moderator);
        var rep = await Node(await admin.Get("/api/v1/admin/messages/reported"));
        var row = rep.AsArray().First(x => (string)x!["conversationId"]! == convB.ToString())!;
        Assert.Equal("abusive", (string)row["reason"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Owner.Get("/api/v1/admin/messages/reported")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Post($"/api/v1/admin/messages/{(string)row["id"]!}/dismiss-report", new { reason = "Heated but not abusive" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await legacyAdmin.Post($"/api/v1/admin/messages/{(string)row["id"]!}/dismiss-report", new { reason = "Heated but not abusive" })).StatusCode);
        Assert.False((bool)(await Node(await s.Owner.Get($"/api/v1/seller/messages/{convB}")))["conversation"]!["flagged"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await Inbound(f, "/api/internal/v1/messages/NOPE/reports", new { reason = "x" })).StatusCode);
    }

    [Fact]
    public async Task Saved_reply_templates_are_per_account_and_write_protected()
    {
        var s = await NewShop(f);
        var other = await NewShop(f);
        var (viewer, _) = await TeamHelpers.JoinAs(f, s.Owner, "Viewer"); await viewer.Switch(s.Seller);
        var c = await s.Owner.Post("/api/v1/seller/message-templates", new { title = "Shipping delay", body = "Your order is delayed by a day." });
        Assert.Equal(HttpStatusCode.Created, c.StatusCode);
        var id = (string)(await Node(c))["id"]!;
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.Post("/api/v1/seller/message-templates", new { title = "", body = "x" })).StatusCode);
        Assert.Contains("Shipping delay", await (await s.Owner.Get("/api/v1/seller/message-templates")).Content.ReadAsStringAsync());
        Assert.DoesNotContain("Shipping delay", await (await other.Owner.Get("/api/v1/seller/message-templates")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.Put($"/api/v1/seller/message-templates/{id}", new { title = "x", body = "y" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.Delete($"/api/v1/seller/message-templates/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.Get("/api/v1/seller/message-templates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post("/api/v1/seller/message-templates", new { title = "a", body = "b" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Put($"/api/v1/seller/message-templates/{id}", new { title = "Delay", body = "Updated" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.Delete($"/api/v1/seller/message-templates/{id}")).StatusCode);
    }

    // ---- real-time -----------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Server_sent_events_reach_authorised_members_only()
    {
        var s = await NewShop(f);
        var other = await NewShop(f);
        var brandY = await TeamHelpers.NewBrand(s.Owner, "Brand Y");
        var py = await ActiveProduct(s.Owner, "Y thing", brandY, "Y-" + Guid.NewGuid().ToString("N")[..6]);
        var (mgrX, _) = await TeamHelpers.JoinAs(f, s.Owner, "Manager", [s.Brand]); await mgrX.Switch(s.Seller);
        var (mgrY, _) = await TeamHelpers.JoinAs(f, s.Owner, "Manager", [brandY]); await mgrY.Switch(s.Seller);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var owner = await Msg.Open(s.Owner, cts.Token);
        using var strangers = await Msg.Open(other.Owner, cts.Token);
        using var x = await Msg.Open(mgrX, cts.Token);
        using var y = await Msg.Open(mgrY, cts.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Get("/api/v1/seller/events")).StatusCode);
        var (mid, _, number, _) = await Place(s);
        await Msg.BuyerMessage(f, "order", mid, number);                     // brand X conversation
        Assert.True(await owner.Gets("message.new")); Assert.True(await x.Gets("message.new"));
        Assert.False(await y.Gets("message.new", 1500));                // manager of brand Y: no
        Assert.False(await strangers.Gets("message.new", 1500));        // other account: no
        await Msg.BuyerMessage(f, "product", product: py);                   // brand Y conversation
        Assert.True(await y.Gets("message.new")); Assert.True(await owner.Gets("message.new"));
        Assert.False(await x.Gets("message.new", 1500));
    }

    [Fact]
    public async Task A_removed_member_stops_receiving_events_within_the_recheck_interval()
    {
        var s = await NewShop(f);
        var (admin, email) = await TeamHelpers.JoinAs(f, s.Owner, "Admin"); await admin.Switch(s.Seller);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var stream = await Msg.Open(admin, cts.Token);
        var uid = f.WithDb(db => db.Users.First(u => u.Email == email).Id);
        f.WithDb(db => { db.Memberships.Where(m => m.UserId == uid && m.SellerId == s.Seller).ExecuteUpdate(u => u.SetProperty(m => m.Status, MembershipStatus.Suspended)); return 0; });
        for (var i = 0; i < 60 && !stream.Ended; i++) await Task.Delay(100);
        Assert.True(stream.Ended);   // the server closed the stream after re-checking access
    }
}

public class OrderNotificationTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    [Fact]
    public async Task Owners_are_emailed_about_orders_cancel_requests_returns_and_messages_and_can_opt_out()
    {
        var s = await NewShop(f);
        var email = s.Owner.Session!.Email;
        var (mid, id, number, _) = await Place(s, lid: "RL");
        var m = f.Mails(email, "order-new").Last();
        Assert.Contains(number, m.Subject); Assert.NotEmpty(m.Html); Assert.NotEmpty(m.Text);
        Assert.Contains($"/orders/{id}", m.Text);
        Assert.DoesNotContain("Jane Buyer", m.Html + m.Text);             // no buyer PII in emails
        await Inbound(f, $"/api/internal/v1/orders/{mid}/cancel-requests", new { requestId = "C1", reason = "changed_mind" });
        Assert.Single(f.Mails(email, "order-cancel-request"));
        var cid = (string)(await Detail(s.Owner, id))["cancellations"]![0]!["id"]!;
        await Ok(Act(s.Owner, id, $"cancellation-requests/{cid}/reject", new { note = "packed" }));
        await Ok(Act(s.Owner, id, "accept")); await Ok(Act(s.Owner, id, "ship", Ship())); await Ok(Act(s.Owner, id, "deliver"));
        await Inbound(f, $"/api/internal/v1/orders/{mid}/return-requests", new { returnId = "R", reason = "damaged", lines = new[] { new { marketplaceLineId = "RL", quantity = 1 } } });
        Assert.Single(f.Mails(email, "order-return-request"));
        await Msg.BuyerMessage(f, "order", mid, number);
        Assert.Single(f.Mails(email, "buyer-message"));
        // opt out of buyer messages and new orders
        var put = await s.Owner.Put("/api/v1/seller/notification-preferences", new { buyerMessage = false, newOrder = false });
        Assert.False((bool)(await Node(put))["buyerMessage"]!);
        await Msg.BuyerMessage(f, "order", mid, number, text: "again");
        Assert.Single(f.Mails(email, "buyer-message"));
        var before = f.Mails(email, "order-new").Count;
        await Place(s);
        Assert.Equal(before, f.Mails(email, "order-new").Count);
        var prefs = await Node(await s.Owner.Get("/api/v1/seller/notification-preferences"));
        Assert.False((bool)prefs["newOrder"]!); Assert.True((bool)prefs["cancelRequest"]!);
    }

    [Fact]
    public async Task Managers_default_to_no_email_and_only_hear_about_their_brands_when_they_opt_in()
    {
        var s = await NewShop(f);
        var brandY = await TeamHelpers.NewBrand(s.Owner, "Brand Y");
        var py = await ActiveProduct(s.Owner, "Y thing", brandY, "Y-" + Guid.NewGuid().ToString("N")[..6]);
        var (mgr, mgrEmail) = await TeamHelpers.JoinAs(f, s.Owner, "Manager", [s.Brand]); await mgr.Switch(s.Seller);
        await Place(s);
        Assert.Empty(f.Mails(mgrEmail, "order-new"));                                          // default off for Managers
        Assert.Equal(HttpStatusCode.OK, (await mgr.Put("/api/v1/seller/notification-preferences", new { newOrder = true })).StatusCode);
        var n = f.Mails(mgrEmail, "order-new").Count;
        await Place(s);
        Assert.Equal(n + 1, f.Mails(mgrEmail, "order-new").Count);
        await Inbound(f, "/api/internal/v1/orders", OrderBody(f, Mid(), [Line(s.Seller, brandY, py)]));
        Assert.Equal(n + 1, f.Mails(mgrEmail, "order-new").Count);                              // brand Y order: not for this manager
    }

    [Fact]
    public async Task Order_and_message_counts_drive_the_sidebar_badges()
    {
        var s = await NewShop(f);
        var (mid, id, number, _) = await Place(s);
        var c = await Node(await s.Owner.Get("/api/v1/seller/orders/counts"));
        Assert.True((int)c["needsAction"]! >= 1);
        await Msg.BuyerMessage(f, "order", mid, number);
        Assert.True((int)(await Node(await s.Owner.Get("/api/v1/seller/messages/counts")))["unread"]! >= 1);
        _ = id;
    }
}
