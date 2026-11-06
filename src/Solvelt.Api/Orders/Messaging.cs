using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Products;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Orders;

/// <summary>Finds attempts to move the conversation off the marketplace: email addresses, phone numbers, external links, payment handles.</summary>
public static partial class ContactDetector
{
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+\s?(@|\(at\)|\[at\])\s?[A-Za-z0-9\-]+(\.|\s?\(dot\)\s?)[A-Za-z]{2,}", RegexOptions.IgnoreCase)] private static partial Regex Email();
    [GeneratedRegex(@"(\+\d[\d\s().\-]{8,}\d)|((?<!\d)\(?\d{3}\)?[\s.\-]\d{3}[\s.\-]\d{4}(?!\d))|(\b\d{3}[\s.\-]\d{4}\b)")] private static partial Regex Phone();
    [GeneratedRegex(@"(https?://|www\.)\S+|\b[a-z0-9\-]+\.(com|net|org|io|me|co|biz|info|shop|store)\b", RegexOptions.IgnoreCase)] private static partial Regex Link();
    [GeneratedRegex(@"\b(paypal|venmo|cash\s?app|zelle|western\s?union|bitcoin|crypto|wire\s?transfer|whatsapp|telegram|signal app|text me|call me|pay (me )?directly)\b|\$[a-z][a-z0-9]{2,}", RegexOptions.IgnoreCase)] private static partial Regex Payment();

    public static List<string> Detect(string text)
    {
        var found = new List<string>();
        if (Email().IsMatch(text)) found.Add("email");
        if (Phone().IsMatch(text)) found.Add("phone");
        if (Link().IsMatch(text)) found.Add("link");
        if (Payment().IsMatch(text)) found.Add("payment");
        return found;
    }
}

public static class AttachmentRules
{
    public static readonly string[] BadPdfTokens = ["/JavaScript", "/JS", "/Launch", "/EmbeddedFile", "/AA", "/RichMedia"];

    public record Checked(string ContentType, byte[] Data, string Extension);

    /// <summary>Content-sniffs the bytes (the declared name and content type are never trusted) and strips image metadata. Null: not an allowed file.</summary>
    public static Checked? Check(byte[] data)
    {
        if (data.Length < 8) return null;
        var type = ImageProcessing.Sniff(data);
        if (type is not null)
        {
            var img = ImageProcessing.Process(data);
            return img is null ? null : new(img.ContentType, img.Data, ImageProcessing.Extension(img.ContentType));
        }
        var head = System.Text.Encoding.ASCII.GetString(data, 0, Math.Min(1024, data.Length));
        if (!head.Contains("%PDF-") || head.IndexOf("%PDF-", StringComparison.Ordinal) > 8) return null;
        // PDF names may hide characters as #xx (/J#61vaScript): decode them before matching. Best effort only: files are always served as
        // attachments with nosniff, never rendered by the portal.
        var all = System.Text.RegularExpressions.Regex.Replace(System.Text.Encoding.Latin1.GetString(data), "#([0-9A-Fa-f]{2})", mm => ((char)Convert.ToInt32(mm.Groups[1].Value, 16)).ToString());
        if (BadPdfTokens.Any(t => all.Contains(t, StringComparison.OrdinalIgnoreCase))) return null;
        return new("application/pdf", data, ".pdf");
    }

    public static string SafeName(string? name, string ext)
    {
        var n = Path.GetFileNameWithoutExtension(name ?? "") is { Length: > 0 } s ? s : "attachment";
        n = Regex.Replace(n, @"[^A-Za-z0-9._ \-]", "_").Trim();
        if (n.Length > 80) n = n[..80];
        return (n.Length == 0 ? "attachment" : n) + ext;
    }
}

public record InboundMessage(string? MessageId, string? ConversationId, string? Kind, string? MarketplaceOrderId, string? SellerOrderNumber, Guid? ProductId,
    string? BuyerName, string? Text, DateTime? SentAt);

public record ReplyRequest(string? Text, List<Guid>? AttachmentIds, bool? AcknowledgeWarning);

public class MessageService(AppDbContext db, TimeProvider time, IOptions<OrderOptions> opt, Outbox outbox, OrderNotifier notifier, IAuditLog audit,
    Services.IFileStorage storage)
{
    public record Outcome(int Status, string Code, string Message, Conversation? Conversation = null, OrderMessage? Message2 = null, bool Duplicate = false,
        List<string>? Warnings = null);

    // ---- inbound ------------------------------------------------------------------------------------------------------------
    public async Task<Outcome> IngestBuyerMessageAsync(InboundMessage m, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(m.MessageId) || m.MessageId.Length > 64) return new(422, "messageId", "messageId is required (max 64 characters).");
        if (string.IsNullOrWhiteSpace(m.ConversationId) || m.ConversationId.Length > 64) return new(422, "conversationId", "conversationId is required (max 64 characters).");
        var text = m.Text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 4000) return new(422, "text", "text is required (max 4000 characters).");
        if (m.Kind is not ("order" or "product")) return new(422, "kind", "kind must be order or product.");
        if (string.IsNullOrWhiteSpace(m.BuyerName) || m.BuyerName.Length > 120) return new(422, "buyerName", "buyerName is required (max 120 characters).");

        var now = time.UtcNow();
        // 1. resolve WHO the message is for from the payload (never from the thread id alone)
        Guid sellerId, brandId; Guid? orderId = null, productId = null; string subject;
        if (m.Kind == "order")
        {
            if (string.IsNullOrWhiteSpace(m.MarketplaceOrderId)) return new(422, "marketplaceOrderId", "marketplaceOrderId is required for order messages.");
            var orders = await db.Orders.AsNoTracking().Where(o => o.MarketplaceOrderId == m.MarketplaceOrderId && (m.SellerOrderNumber == null || o.Number == m.SellerOrderNumber)).ToListAsync(ct);
            if (orders.Count == 0) return new(404, "not_found", "Unknown order.");
            if (orders.Count > 1) return new(422, "ambiguous_order", "The marketplace order was split across several sellers: send sellerOrderNumber.");
            (sellerId, brandId, orderId, subject) = (orders[0].SellerId, orders[0].BrandId, orders[0].Id, $"Order {orders[0].Number}");
        }
        else
        {
            if (m.ProductId is null) return new(422, "productId", "productId is required for product questions.");
            var p = await db.Products.AsNoTracking().Where(x => x.Id == m.ProductId && x.Status == ProductStatus.Active).Select(x => new { x.Id, x.SellerId, x.BrandId, x.Title }).FirstOrDefaultAsync(ct);
            if (p is null)
            {
                // a retry of a question that was already stored must stay a duplicate after the product was deactivated: exactly one earlier message with this id, in a thread about THIS product
                // (an id used for another product / seller is another message and is never taken for a duplicate)
                var prior = await (from x in db.Messages.AsNoTracking() join c in db.Conversations.AsNoTracking() on x.ConversationId equals c.Id
                                   where x.MarketplaceMessageId == m.MessageId && c.ProductId == m.ProductId select new { Msg = x, Conv = c }).Take(2).ToListAsync(ct);
                if (prior.Count == 1) return new(200, "duplicate", "", prior[0].Conv, prior[0].Msg, true);
                return new(404, "not_found", "Unknown or inactive product.");
            }
            (sellerId, brandId, productId, subject) = (p.SellerId, p.BrandId, p.Id, p.Title);
        }
        // idempotency INSIDE the resolved seller account: the unique index is (SellerId, MarketplaceMessageId), so the same messageId for another seller is another message
        var dup = await db.Messages.AsNoTracking().FirstOrDefaultAsync(x => x.SellerId == sellerId && x.MarketplaceMessageId == m.MessageId, ct);
        if (dup is not null) return new(200, "duplicate", "", await db.Conversations.FirstAsync(c => c.Id == dup.ConversationId, ct), dup, true);
        // 2. find the thread INSIDE that seller account: the same marketplace thread id used for two sellers is two conversations
        var conv = await db.Conversations.FirstOrDefaultAsync(c => c.SellerId == sellerId && c.MarketplaceConversationId == m.ConversationId, ct);
        if (conv is not null && (conv.OrderId != orderId || conv.ProductId != productId))
            return new(422, "conversation_mismatch", "This conversationId already belongs to a different order or product.");
        // a thread the seller started has no marketplace id yet: the buyer's first reply joins it instead of opening a second one
        conv ??= orderId is null ? null : await db.Conversations.FirstOrDefaultAsync(c => c.OrderId == orderId && c.MarketplaceConversationId == null, ct);
        if (conv is not null && conv.MarketplaceConversationId is null) conv.MarketplaceConversationId = m.ConversationId;
        if (conv is null)
        {
            conv = new Conversation
            {
                MarketplaceConversationId = m.ConversationId, BuyerName = m.BuyerName.Trim(), CreatedAt = now, LastMessageAt = now, SellerId = sellerId, BrandId = brandId,
                Kind = orderId is null ? ConversationKind.Product : ConversationKind.Order, OrderId = orderId, ProductId = productId, Subject = subject
            };
            db.Conversations.Add(conv);
        }
        var sent = m.SentAt is { } s && s <= now.AddMinutes(5) && s > now.AddYears(-1) ? OrderSupport.Utc(s) : now;
        var msg = new OrderMessage { ConversationId = conv.Id, SellerId = conv.SellerId, Direction = MessageDirection.Buyer, MarketplaceMessageId = m.MessageId, Text = text, CreatedAt = sent, Delivery = DeliveryState.Delivered };
        db.Messages.Add(msg);
        conv.LastMessageAt = sent; conv.LastBuyerMessageAt = sent; conv.BuyerMessageCount++; conv.MessageCount++; conv.NoReplyNeeded = false; conv.OverdueNotifiedAt = null;
        conv.AwaitingReplySince ??= sent;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var again = await db.Messages.AsNoTracking().FirstOrDefaultAsync(x => x.SellerId == sellerId && x.MarketplaceMessageId == m.MessageId, ct);
            if (again is null) throw;
            return new(200, "duplicate", "", await db.Conversations.FirstAsync(c => c.Id == again.ConversationId, ct), again, true);
        }
        await notifier.BuyerMessageAsync(conv, ct);
        return new(201, "ok", "", conv, msg);
    }

    public async Task<Outcome> ReportAsync(string marketplaceMessageId, string? reason)
    {
        // the marketplace id is unique per SELLER only: it must name exactly one message, or the report could flag another seller's conversation
        var found = await db.Messages.Where(x => x.MarketplaceMessageId == marketplaceMessageId).Take(2).ToListAsync();
        if (found.Count > 1) return new(422, "ambiguous_message", "This message id exists for several sellers; the report cannot be matched to one message.");
        var msg = found.FirstOrDefault();
        if (msg is null) return new(404, "not_found", "Unknown message.");
        var conv = await db.Conversations.FirstAsync(c => c.Id == msg.ConversationId);
        msg.Reported = true; msg.ReportReason = (reason ?? "reported").Length > 200 ? reason![..200] : reason ?? "reported";
        conv.Flagged = true; conv.FlagReason = msg.ReportReason;
        await db.SaveChangesAsync();
        return new(200, "ok", "", conv, msg);
    }

    public async Task<Outcome> ReceiptAsync(string marketplaceMessageId, string? status)
    {
        var state = status?.ToLowerInvariant() switch { "delivered" => DeliveryState.Delivered, "read" => DeliveryState.Read, "failed" => DeliveryState.Failed, _ => (DeliveryState?)null };
        if (state is null) return new(422, "status", "status must be delivered, read or failed.");
        var gid = Guid.TryParse(marketplaceMessageId, out var g0) ? g0 : Guid.Empty;
        var found = await db.Messages.Where(x => x.MarketplaceMessageId == marketplaceMessageId || x.Id == gid).Take(2).ToListAsync();
        if (found.Count > 1) return new(422, "ambiguous_message", "This message id matches several messages; the receipt cannot be matched to one.");
        var msg = found.FirstOrDefault();
        if (msg is null || msg.Direction != MessageDirection.Seller) return new(404, "not_found", "Unknown message.");
        if (state > msg.Delivery || state == DeliveryState.Failed) msg.Delivery = state.Value;
        await db.SaveChangesAsync();
        var conv = await db.Conversations.AsNoTracking().FirstAsync(c => c.Id == msg.ConversationId);
        notifier.Live("message.status", conv.SellerId, conv.BrandId, new { conversationId = conv.Id, messageId = msg.Id, status = msg.Delivery.ToString() });
        return new(200, "ok", "", conv, msg);
    }

    // ---- seller side --------------------------------------------------------------------------------------------------------
    public async Task<Outcome> ReplyAsync(Conversation conv, Guid actor, ReplyRequest req, CancellationToken ct = default)
    {
        var o = opt.Value;
        var text = req.Text?.Trim() ?? "";
        var ids = (req.AttachmentIds ?? []).Distinct().ToList();
        if (text.Length == 0 && ids.Count == 0) return new(422, "text", "Write a message or attach a file.");
        if (text.Length > 4000) return new(422, "text", "The message is too long (4000 characters max).");
        if (ids.Count > o.MaxAttachmentsPerMessage) return new(422, "attachments", $"At most {o.MaxAttachmentsPerMessage} attachments per message.");
        var atts = await db.MessageAttachments.Where(a => ids.Contains(a.Id) && a.ConversationId == conv.Id && a.SellerId == conv.SellerId && a.MessageId == null && a.UploadedBy == actor).ToListAsync(ct);
        if (atts.Count != ids.Count) return new(422, "attachments", "An attachment is missing or was already sent.");
        var warn = ContactDetector.Detect(text);
        if (warn.Count > 0)
        {
            await audit.WriteAsync("message.contact_details", actor, null, $"conversation={conv.Id} kinds={string.Join(',', warn)} blocked={o.BlockContactDetails}");
            if (o.BlockContactDetails) return new(422, "contact_details_blocked", "Messages can't include contact details, links or payment instructions. Keep the conversation on Solvelt.", Warnings: warn);
            if (req.AcknowledgeWarning != true) return new(409, "contact_details_warning", "This message looks like it shares contact details or a payment link. Sharing them off-platform is against marketplace rules. Send anyway?", Warnings: warn);
        }
        var now = time.UtcNow();
        var msg = new OrderMessage { ConversationId = conv.Id, SellerId = conv.SellerId, Direction = MessageDirection.Seller, AuthorUserId = actor, Text = text, CreatedAt = now, ContactWarning = warn.Count > 0 };
        foreach (var a in atts) { a.MessageId = msg.Id; msg.Attachments.Add(a); }
        db.Messages.Add(msg);
        // everything the buyer wrote before this reply counts as answered (response-time metric)
        await db.Messages.Where(x => x.ConversationId == conv.Id && x.Direction == MessageDirection.Buyer && x.RespondedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RespondedAt, now), ct);
        conv.LastMessageAt = now; conv.MessageCount++; conv.AwaitingReplySince = null; conv.OverdueNotifiedAt = null;
        var order = conv.OrderId is { } oid ? await db.Orders.AsNoTracking().Where(x => x.Id == oid).Select(x => new { x.Number, x.MarketplaceOrderId }).FirstOrDefaultAsync(ct) : null;
        outbox.Enqueue("message.sent", conv.SellerId, conv.MarketplaceConversationId ?? conv.Id.ToString(), new
        {
            messageId = msg.Id, conversationId = conv.Id, marketplaceConversationId = conv.MarketplaceConversationId, kind = conv.Kind.ToString().ToLowerInvariant(),
            marketplaceOrderId = order?.MarketplaceOrderId, sellerOrderNumber = order?.Number, productId = conv.ProductId, text, sentAt = now,
            attachments = atts.Select(a => new { id = a.Id, fileName = a.FileName, contentType = a.ContentType, size = a.Size, path = $"/api/internal/v1/attachments/{a.Id}" })
        });
        await db.SaveChangesAsync(ct);
        await MarkReadAsync(conv.Id, actor, now, conv.BuyerMessageCount);   // the author has read the thread (separate, race-tolerant statement)
        notifier.Live("message.new", conv.SellerId, conv.BrandId, new { conversationId = conv.Id, byTeam = true });
        return new(201, "ok", "", conv, msg, Warnings: warn.Count > 0 ? warn : null);
    }

    /// <summary>
    /// Marks the conversation read for one user. Idempotent and race-free with a single atomic upsert (INSERT ... ON CONFLICT DO UPDATE, valid on PostgreSQL and SQLite):
    /// concurrent calls for the same (conversation, user) never raise (nor log) a unique-key violation, and the stored read position only ever moves forward.
    /// </summary>
    public async Task MarkReadAsync(Guid conversationId, Guid userId, DateTime at, int? buyerCount = null)
    {
        var count = buyerCount ?? await db.Conversations.Where(c => c.Id == conversationId).Select(c => c.BuyerMessageCount).FirstOrDefaultAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO conversation_reads ("ConversationId", "UserId", "LastReadAt", "ReadBuyerCount") VALUES ({conversationId}, {userId}, {at}, {count})
            ON CONFLICT ("ConversationId", "UserId") DO UPDATE SET
              "LastReadAt" = CASE WHEN conversation_reads."LastReadAt" < excluded."LastReadAt" THEN excluded."LastReadAt" ELSE conversation_reads."LastReadAt" END,
              "ReadBuyerCount" = CASE WHEN conversation_reads."ReadBuyerCount" < excluded."ReadBuyerCount" THEN excluded."ReadBuyerCount" ELSE conversation_reads."ReadBuyerCount" END
            """);
    }

    public record AttachmentOutcome(int Status, string Code, string Message, MessageAttachment? Attachment = null);

    public async Task<AttachmentOutcome> SaveAttachmentAsync(Conversation conv, Guid actor, byte[] data, string? fileName, CancellationToken ct = default)
    {
        var o = opt.Value;
        if (data.Length == 0) return new(422, "empty", "The file is empty.");
        if (data.Length > o.MaxAttachmentBytes) return new(413, "too_large", $"Files can be at most {o.MaxAttachmentBytes / (1024 * 1024)} MB.");
        var pending = await db.MessageAttachments.CountAsync(a => a.ConversationId == conv.Id && a.MessageId == null && a.UploadedBy == actor, ct);
        if (pending >= o.MaxAttachmentsPerMessage) return new(422, "too_many", $"At most {o.MaxAttachmentsPerMessage} attachments per message.");
        var ok = AttachmentRules.Check(data);
        if (ok is null) return new(415, "unsupported_type", "Only JPEG, PNG, WebP images and PDF files are allowed.");
        var key = Guid.NewGuid().ToString("N");
        using (var ms = new MemoryStream(ok.Data)) await storage.SaveAsync(key, ms, ct);
        var a = new MessageAttachment
        {
            SellerId = conv.SellerId, ConversationId = conv.Id, UploadedBy = actor, StorageKey = key, ContentType = ok.ContentType,
            FileName = AttachmentRules.SafeName(fileName, ok.Extension), Size = ok.Data.Length, CreatedAt = time.UtcNow()
        };
        db.MessageAttachments.Add(a);
        await db.SaveChangesAsync(ct);
        return new(201, "ok", "", a);
    }

    // ---- shared queries ------------------------------------------------------------------------------------------------------
    public IQueryable<Conversation> Unread(IQueryable<Conversation> q, Guid userId) =>
        q.Where(c => c.BuyerMessageCount > 0 && !db.ConversationReads.Any(r => r.ConversationId == c.Id && r.UserId == userId && r.ReadBuyerCount >= c.BuyerMessageCount));

    public IQueryable<Conversation> NeedsReply(IQueryable<Conversation> q) => q.Where(c => c.AwaitingReplySince != null && !c.NoReplyNeeded);

    public IQueryable<Conversation> Overdue(IQueryable<Conversation> q)
    {
        var cutoff = time.UtcNow().AddHours(-opt.Value.ReplyDueHours);
        return q.Where(c => c.AwaitingReplySince != null && !c.NoReplyNeeded && c.AwaitingReplySince < cutoff);
    }
}
