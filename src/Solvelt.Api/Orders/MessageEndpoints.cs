using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Orders;

public record AssignRequest(Guid? UserId);
public record NoReplyRequest(bool? Value);
public record TemplateRequest(string? Title, string? Body);
public record StartConversationRequest(string? Text);
public record PreferencesRequest(bool? NewOrder, bool? CancelRequest, bool? ReturnRequest, bool? BuyerMessage, bool? OverdueReply, bool? PaymentEmails = null);

public static class MessageEndpoints
{
    private static async Task<Conversation?> Load(AppDbContext db, AccountAccess a, Guid id, bool write)
    {
        var c = await db.ConversationsFor(a).FirstOrDefaultAsync(x => x.Id == id);
        return c is not null && (!write || a.CanWorkOnBrand(c.BrandId, true)) ? c : null;
    }

    private static object Dto(Conversation c, string brandName, bool unread, DateTime now, int dueHours, string? assignee, string? productTitle = null, string? orderNumber = null, string? lastText = null) => new
    {
        id = c.Id, kind = c.Kind.ToString().ToLowerInvariant(), subject = c.Subject, buyerName = c.BuyerName, brandId = c.BrandId, brandName, orderId = c.OrderId, orderNumber,
        productId = c.ProductId, productTitle, assignedUserId = c.AssignedUserId, assignedTo = assignee, noReplyNeeded = c.NoReplyNeeded, flagged = c.Flagged, flagReason = c.FlagReason,
        unread, needsReply = c.AwaitingReplySince != null && !c.NoReplyNeeded, awaitingReplySince = c.AwaitingReplySince,
        overdue = c.AwaitingReplySince != null && !c.NoReplyNeeded && c.AwaitingReplySince < now.AddHours(-dueHours), dueAt = c.AwaitingReplySince?.AddHours(dueHours),
        lastMessageAt = c.LastMessageAt, messageCount = c.MessageCount, redacted = c.PiiRedactedAt != null, preview = lastText
    };

    private static object MsgDto(OrderMessage m, Dictionary<Guid, string> authors) => new
    {
        id = m.Id, direction = m.Direction.ToString().ToLowerInvariant(), text = m.Text, createdAt = m.CreatedAt, delivery = m.Delivery.ToString().ToLowerInvariant(),
        author = m.AuthorUserId is { } u ? authors.GetValueOrDefault(u, "") : null, authorId = m.AuthorUserId, contactWarning = m.ContactWarning, reported = m.Reported, respondedAt = m.RespondedAt,
        attachments = m.Attachments.Select(a => new { id = a.Id, fileName = a.FileName, contentType = a.ContentType, size = a.Size })
    };

    public static void MapMessageEndpoints(this RouteGroupBuilder g)
    {
        var grp = g.MapGroup("/seller/messages").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireRateLimiting("orders")
            .RequirePermission(Permission.BrandWorkRead);
        var write = (RouteHandlerBuilder b) => b.RequirePermission(Permission.BrandWorkWrite);

        grp.MapGet("", async (HttpContext ctx, AppDbContext db, MessageService svc, TimeProvider time, IOptions<OrderOptions> opt, string? filter, string? kind, Guid? brandId, bool? assignedToMe,
            string? q, int? page, int? pageSize) =>
        {
            var a = ctx.GetAccess()!;
            var rows = db.ConversationsFor(a).AsNoTracking();
            switch (filter?.ToLowerInvariant())
            {
                case "unread": rows = svc.Unread(rows, a.UserId); break;
                case "needs_reply": rows = svc.NeedsReply(rows); break;
                case "overdue": rows = svc.Overdue(rows); break;
                case "flagged": rows = rows.Where(c => c.Flagged); break;
                case null or "" or "all": break;
                default: return Validation.Invalid("filter", "Use all, unread, needs_reply, overdue or flagged.");
            }
            if (kind is "order") rows = rows.Where(c => c.Kind == ConversationKind.Order); else if (kind is "product") rows = rows.Where(c => c.Kind == ConversationKind.Product);
            else if (!string.IsNullOrEmpty(kind)) return Validation.Invalid("kind", "Use order or product.");
            if (brandId is { } b) rows = rows.Where(c => c.BrandId == b);
            if (assignedToMe == true) rows = rows.Where(c => c.AssignedUserId == a.UserId);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var s = q.Trim().ToLower();
                rows = rows.Where(c => c.Subject.ToLower().Contains(s) || c.BuyerName.ToLower().Contains(s) || db.Messages.Any(m => m.ConversationId == c.Id && m.Text.ToLower().Contains(s)));
            }
            var total = await rows.CountAsync();
            var size = Math.Clamp(pageSize ?? 25, 1, 100); var pg = Math.Max(page ?? 1, 1);
            var items = await rows.OrderByDescending(c => c.LastMessageAt).ThenBy(c => c.Id).Skip((pg - 1) * size).Take(size).ToListAsync();
            var ids = items.Select(c => c.Id).ToList();
            var reads = await db.ConversationReads.AsNoTracking().Where(r => r.UserId == a.UserId && ids.Contains(r.ConversationId)).ToDictionaryAsync(r => r.ConversationId, r => r.ReadBuyerCount);
            var brands = await db.Brands.AsNoTracking().Where(x => x.SellerId == a.SellerId).ToDictionaryAsync(x => x.Id, x => x.Name);
            var orderIds = items.Where(c => c.OrderId != null).Select(c => c.OrderId!.Value).ToList();
            var numbers = await db.Orders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Number);
            var assignIds = items.Where(c => c.AssignedUserId != null).Select(c => c.AssignedUserId!.Value).Distinct().ToList();
            var people = await db.Users.AsNoTracking().Where(u => assignIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Email ?? "");
            var last = new Dictionary<Guid, string>();
            foreach (var c in items)
                last[c.Id] = await db.Messages.AsNoTracking().Where(m => m.ConversationId == c.Id).OrderByDescending(m => m.CreatedAt).Select(m => m.Text).FirstOrDefaultAsync() ?? "";
            var now = time.UtcNow(); var due = opt.Value.ReplyDueHours;
            return Results.Json(new
            {
                items = items.Select(c => Dto(c, brands.GetValueOrDefault(c.BrandId, ""), c.BuyerMessageCount > 0 && (!reads.TryGetValue(c.Id, out var rd) || rd < c.BuyerMessageCount), now, due,
                    c.AssignedUserId is { } u ? people.GetValueOrDefault(u) : null, null, c.OrderId is { } oid ? numbers.GetValueOrDefault(oid) : null, Preview(last[c.Id]))),
                total, page = pg, pageSize = size
            }, OrderJson.Opts);
        });

        grp.MapGet("/counts", async (HttpContext ctx, AppDbContext db, MessageService svc) =>
        {
            var a = ctx.GetAccess()!;
            var rows = db.ConversationsFor(a).AsNoTracking();
            return Results.Json(new { unread = await svc.Unread(rows, a.UserId).CountAsync(), needsReply = await svc.NeedsReply(rows).CountAsync(), overdue = await svc.Overdue(rows).CountAsync(), all = await rows.CountAsync() }, OrderJson.Opts);
        });

        grp.MapGet("/{id:guid}", async (HttpContext ctx, AppDbContext db, TimeProvider time, IOptions<OrderOptions> opt, Guid id) =>
        {
            var a = ctx.GetAccess()!;
            var c = await Load(db, a, id, false);
            if (c is null) return Results.NotFound();
            var msgs = await db.Messages.AsNoTracking().Include(m => m.Attachments).Where(m => m.ConversationId == id).OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).ToListAsync();
            var authorIds = msgs.Where(m => m.AuthorUserId != null).Select(m => m.AuthorUserId!.Value).Distinct().ToList();
            var authors = await db.Users.AsNoTracking().Where(u => authorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Email ?? "");
            var notes = await db.ConversationNotes.AsNoTracking().Where(n => n.ConversationId == id).OrderBy(n => n.CreatedAt).ToListAsync();
            var noteAuthors = await db.Users.AsNoTracking().Where(u => notes.Select(n => n.AuthorId).Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Email ?? "");
            var brand = await db.Brands.AsNoTracking().Where(b => b.Id == c.BrandId).Select(b => b.Name).FirstOrDefaultAsync() ?? "";
            var readCount = await db.ConversationReads.AsNoTracking().Where(r => r.ConversationId == id && r.UserId == a.UserId).Select(r => r.ReadBuyerCount).FirstOrDefaultAsync();
            var order = c.OrderId is { } oid ? await db.Orders.AsNoTracking().Where(o => o.Id == oid).Select(o => new { o.Id, o.Number, o.Status, o.TotalCents, o.Currency, o.ShipByAt, o.PlacedAt }).FirstOrDefaultAsync() : null;
            var product = c.ProductId is { } pid ? await db.Products.AsNoTracking().Where(p => p.Id == pid).Select(p => new { p.Id, p.Title, p.Sku, p.PriceCents, Status = p.Status.ToString() }).FirstOrDefaultAsync() : null;
            var assignee = c.AssignedUserId is { } au ? await db.Users.AsNoTracking().Where(u => u.Id == au).Select(u => u.Email).FirstOrDefaultAsync() : null;
            // people this conversation can be assigned to: active members who can work on its brand (never Viewers); only offered to callers who can write
            var canAssign = a.CanWorkOnBrand(c.BrandId, true);
            var assignees = new List<object>();
            if (canAssign)
            {
                var ms = await db.Memberships.AsNoTracking().Where(m => m.SellerId == a.SellerId && m.Status == MembershipStatus.Active && m.Role != AccountRole.Viewer && m.Role != AccountRole.Driver).ToListAsync();   // Role is stored as text: compare for equality, never with < or >
                var scoped = await db.MembershipBrands.AsNoTracking().Where(b => b.BrandId == c.BrandId).Select(b => b.MembershipId).ToListAsync();
                var ok = ms.Where(m => m.AllBrands || m.Role >= AccountRole.Admin || scoped.Contains(m.Id)).ToList();
                var uids = ok.Select(m => m.UserId).ToList();
                var emails = await db.Users.AsNoTracking().Where(u => uids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Email ?? "");
                assignees = ok.Select(m => (object)new { userId = m.UserId, name = emails.GetValueOrDefault(m.UserId, ""), isMe = m.UserId == a.UserId }).ToList();
            }
            return Results.Json(new
            {
                conversation = Dto(c, brand, c.BuyerMessageCount > readCount, time.UtcNow(), opt.Value.ReplyDueHours, assignee, product?.Title, order?.Number),
                order = order is null ? null : new { id = order.Id, number = order.Number, status = order.Status.ToString(), totalCents = order.TotalCents, currency = order.Currency, shipByAt = order.ShipByAt, placedAt = order.PlacedAt },
                product,
                messages = msgs.Select(m => MsgDto(m, authors)),
                notes = notes.Select(n => new { id = n.Id, text = n.Text, author = noteAuthors.GetValueOrDefault(n.AuthorId, ""), createdAt = n.CreatedAt }),
                canWrite = a.CanWorkOnBrand(c.BrandId, true),
                me = a.UserId,
                assignees = canAssign ? assignees : []
            }, OrderJson.Opts);
        });

        grp.MapPost("/{id:guid}/read", async (HttpContext ctx, AppDbContext db, MessageService svc, TimeProvider time, Guid id) =>
        {
            var c = await Load(db, ctx.GetAccess()!, id, false);
            if (c is null) return Results.NotFound();
            await svc.MarkReadAsync(id, ctx.GetAccess()!.UserId, time.UtcNow());
            return Results.NoContent();
        });

        write(grp.MapPost("/{id:guid}/messages", async (HttpContext ctx, AppDbContext db, MessageService svc, Guid id, ReplyRequest req) =>
        {
            var a = ctx.GetAccess()!;
            var c = await Load(db, a, id, true);
            if (c is null) return Results.NotFound();
            if (c.PiiRedactedAt != null) return OrderEndpoints.Problem(409, "redacted", "This conversation was archived and can't be answered.");
            var r = await svc.ReplyAsync(c, a.UserId, req);
            if (r.Status >= 400) return Results.Json(new { type = "about:blank", title = r.Message, status = r.Status, code = r.Code, warnings = r.Warnings }, statusCode: r.Status);
            return Results.Json(new { message = MsgDto(await db.Messages.AsNoTracking().Include(m => m.Attachments).FirstAsync(m => m.Id == r.Message2!.Id), new() { [a.UserId] = "" }), warnings = r.Warnings }, OrderJson.Opts, statusCode: 201);
        }));

        write(grp.MapPost("/{id:guid}/attachments", async (HttpContext ctx, AppDbContext db, MessageService svc, Guid id) =>
        {
            var a = ctx.GetAccess()!;
            var c = await Load(db, a, id, true);
            if (c is null) return Results.NotFound();
            if (!ctx.Request.HasFormContentType) return Validation.Invalid("file", "Upload a file.");
            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null) return Validation.Invalid("file", "Upload a file.");
            var max = ctx.RequestServices.GetRequiredService<IOptions<OrderOptions>>().Value.MaxAttachmentBytes;
            if (file.Length > max) return OrderEndpoints.Problem(413, "too_large", $"Files can be at most {max / (1024 * 1024)} MB.");
            using var ms = new MemoryStream(); await file.CopyToAsync(ms);
            var r = await svc.SaveAttachmentAsync(c, a.UserId, ms.ToArray(), file.FileName);
            return r.Attachment is null ? OrderEndpoints.Problem(r.Status, r.Code, r.Message)
                : Results.Json(new { id = r.Attachment.Id, fileName = r.Attachment.FileName, contentType = r.Attachment.ContentType, size = r.Attachment.Size }, OrderJson.Opts, statusCode: 201);
        }));

        grp.MapGet("/{id:guid}/attachments/{attId:guid}", async (HttpContext ctx, AppDbContext db, IFileStorage storage, Guid id, Guid attId) =>
        {
            var c = await Load(db, ctx.GetAccess()!, id, false);
            if (c is null) return Results.NotFound();
            var att = await db.MessageAttachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == attId && x.ConversationId == id);
            if (att is null) return Results.NotFound();
            // a draft attachment (not sent yet) is only visible to the person who uploaded it
            if (att.MessageId is null && att.UploadedBy != ctx.GetAccess()!.UserId) return Results.NotFound();
            var s = await storage.OpenReadAsync(att.StorageKey);
            if (s is null) return Results.NotFound();
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var disp = att.ContentType == "application/pdf" ? "attachment" : "inline";
            ctx.Response.Headers.ContentDisposition = $"{disp}; filename=\"{att.FileName}\"";
            return Results.Stream(s, att.ContentType);
        });

        write(grp.MapDelete("/{id:guid}/attachments/{attId:guid}", async (HttpContext ctx, AppDbContext db, IFileStorage storage, Guid id, Guid attId) =>
        {
            var a = ctx.GetAccess()!;
            var c = await Load(db, a, id, true);
            if (c is null) return Results.NotFound();
            var att = await db.MessageAttachments.FirstOrDefaultAsync(x => x.Id == attId && x.ConversationId == id && x.MessageId == null && x.UploadedBy == a.UserId);
            if (att is null) return Results.NotFound();
            await storage.DeleteAsync(att.StorageKey);
            db.MessageAttachments.Remove(att);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }));

        write(grp.MapPost("/{id:guid}/assign", async (HttpContext ctx, AppDbContext db, Guid id, AssignRequest req) =>
        {
            var a = ctx.GetAccess()!;
            var c = await Load(db, a, id, true);
            if (c is null) return Results.NotFound();
            if (req.UserId is { } uid)
            {
                // the assignee must be an active member of THIS account who can see the brand
                var m = await db.Memberships.AsNoTracking().FirstOrDefaultAsync(x => x.SellerId == a.SellerId && x.UserId == uid && x.Status == MembershipStatus.Active);
                var scoped = m is not null && (m.AllBrands || m.Role >= AccountRole.Admin || await db.MembershipBrands.AnyAsync(b => b.MembershipId == m.Id && b.BrandId == c.BrandId));
                if (!scoped || m!.Role <= AccountRole.Viewer) return Validation.Invalid("userId", "Choose a team member who can work on this brand.");
            }
            c.AssignedUserId = req.UserId;
            await db.SaveChangesAsync();
            return Results.NoContent();
        }));

        write(grp.MapPost("/{id:guid}/no-reply-needed", async (HttpContext ctx, AppDbContext db, Guid id, NoReplyRequest req) =>
        {
            var c = await Load(db, ctx.GetAccess()!, id, true);
            if (c is null) return Results.NotFound();
            c.NoReplyNeeded = req.Value ?? true;
            await db.SaveChangesAsync();
            return Results.NoContent();
        }));

        write(grp.MapPost("/{id:guid}/notes", async (HttpContext ctx, AppDbContext db, TimeProvider time, Guid id, NoteRequest req) =>
        {
            var a = ctx.GetAccess()!;
            var c = await Load(db, a, id, true);
            if (c is null) return Results.NotFound();
            var t = req.Text?.Trim();
            if (string.IsNullOrEmpty(t) || t.Length > 2000) return Validation.Invalid("text", "A note needs 1 to 2000 characters.");
            db.ConversationNotes.Add(new ConversationNote { ConversationId = id, AuthorId = a.UserId, Text = t, CreatedAt = time.UtcNow() });
            await db.SaveChangesAsync();
            return Results.NoContent();
        }));

        // seller-initiated conversation about an order
        write(g.MapPost("/seller/orders/{orderId:guid}/messages", async (HttpContext ctx, AppDbContext db, MessageService svc, TimeProvider time, Guid orderId, ReplyRequest req) =>
        {
            var a = ctx.GetAccess()!;
            var o = await db.OrdersFor(a).FirstOrDefaultAsync(x => x.Id == orderId);
            if (o is null || !a.CanWorkOnBrand(o.BrandId, true)) return Results.NotFound();
            if (o.PiiRedactedAt != null) return OrderEndpoints.Problem(409, "redacted", "This order's buyer details were removed; messages can no longer be sent.");
            var c = await db.Conversations.FirstOrDefaultAsync(x => x.OrderId == orderId);
            if (c is null)
            {
                var now = time.UtcNow();
                c = new Conversation { SellerId = o.SellerId, BrandId = o.BrandId, Kind = ConversationKind.Order, OrderId = o.Id, BuyerName = o.BuyerName, Subject = $"Order {o.Number}", CreatedAt = now, LastMessageAt = now };
                db.Conversations.Add(c);
                await db.SaveChangesAsync();
            }
            var r = await svc.ReplyAsync(c, a.UserId, req);
            if (r.Status >= 400) return Results.Json(new { type = "about:blank", title = r.Message, status = r.Status, code = r.Code, warnings = r.Warnings }, statusCode: r.Status);
            return Results.Json(new { conversationId = c.Id, messageId = r.Message2!.Id, warnings = r.Warnings }, OrderJson.Opts, statusCode: 201);
        }).RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireRateLimiting("orders"));

        // ---- saved reply templates (per account)
        var tpl = g.MapGroup("/seller/message-templates").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireRateLimiting("orders").RequirePermission(Permission.BrandWorkRead);
        tpl.MapGet("", async (HttpContext ctx, AppDbContext db) =>
            Results.Json((await db.MessageTemplates.AsNoTracking().Where(t => t.SellerId == ctx.GetAccess()!.SellerId).OrderBy(t => t.Title).ToListAsync()).Select(t => new { id = t.Id, title = t.Title, body = t.Body }), OrderJson.Opts));
        write(tpl.MapPost("", async (HttpContext ctx, AppDbContext db, TimeProvider time, TemplateRequest r) =>
        {
            var a = ctx.GetAccess()!;
            var bad = CheckTemplate(r);
            if (bad is not null) return bad;
            if (await db.MessageTemplates.CountAsync(t => t.SellerId == a.SellerId) >= 50) return Validation.Invalid("title", "At most 50 saved replies.");
            var t = new MessageTemplate { SellerId = a.SellerId, Title = r.Title!.Trim(), Body = r.Body!.Trim(), CreatedAt = time.UtcNow() };
            db.MessageTemplates.Add(t); await db.SaveChangesAsync();
            return Results.Json(new { id = t.Id, title = t.Title, body = t.Body }, OrderJson.Opts, statusCode: 201);
        }));
        write(tpl.MapPut("/{id:guid}", async (HttpContext ctx, AppDbContext db, Guid id, TemplateRequest r) =>
        {
            var bad = CheckTemplate(r);
            if (bad is not null) return bad;
            var t = await db.MessageTemplates.FirstOrDefaultAsync(x => x.Id == id && x.SellerId == ctx.GetAccess()!.SellerId);
            if (t is null) return Results.NotFound();
            t.Title = r.Title!.Trim(); t.Body = r.Body!.Trim(); await db.SaveChangesAsync();
            return Results.Json(new { id = t.Id, title = t.Title, body = t.Body }, OrderJson.Opts);
        }));
        write(tpl.MapDelete("/{id:guid}", async (HttpContext ctx, AppDbContext db, Guid id) =>
        {
            var n = await db.MessageTemplates.Where(x => x.Id == id && x.SellerId == ctx.GetAccess()!.SellerId).ExecuteDeleteAsync();
            return n == 0 ? Results.NotFound() : Results.NoContent();
        }));

        // ---- notification preferences (per user and account; any active member)
        var pref = g.MapGroup("/seller/notification-preferences").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireActiveAccount();
        pref.MapGet("", async (HttpContext ctx, AppDbContext db) =>
        {
            var a = ctx.GetAccess()!;
            var p = await db.NotificationPreferences.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == a.UserId && x.SellerId == a.SellerId);
            return Results.Json(PrefDto(p, a.Role), OrderJson.Opts);
        });
        pref.MapPut("", async (HttpContext ctx, AppDbContext db, PreferencesRequest r) =>
        {
            var a = ctx.GetAccess()!;
            var p = await db.NotificationPreferences.FirstOrDefaultAsync(x => x.UserId == a.UserId && x.SellerId == a.SellerId);
            if (p is null) { p = new NotificationPreference { UserId = a.UserId, SellerId = a.SellerId }; db.NotificationPreferences.Add(p); }
            p.NewOrder = r.NewOrder ?? p.NewOrder; p.CancelRequest = r.CancelRequest ?? p.CancelRequest; p.ReturnRequest = r.ReturnRequest ?? p.ReturnRequest;
            p.BuyerMessage = r.BuyerMessage ?? p.BuyerMessage; p.OverdueReply = r.OverdueReply ?? p.OverdueReply;
            if (a.Role == AccountRole.Admin) p.PaymentEmails = r.PaymentEmails ?? p.PaymentEmails;   // payment emails: Owners always, Admins opt in, nobody else
            await db.SaveChangesAsync();
            return Results.Json(PrefDto(p, a.Role), OrderJson.Opts);
        });
    }

    private static object PrefDto(NotificationPreference? p, AccountRole role) => new
    {
        newOrder = OrderNotifier.Wants(p, role, NotifyKind.NewOrder), cancelRequest = OrderNotifier.Wants(p, role, NotifyKind.CancelRequest),
        returnRequest = OrderNotifier.Wants(p, role, NotifyKind.ReturnRequest), buyerMessage = OrderNotifier.Wants(p, role, NotifyKind.BuyerMessage),
        overdueReply = OrderNotifier.Wants(p, role, NotifyKind.OverdueReply),
        paymentEmails = role == AccountRole.Owner || (role == AccountRole.Admin && p?.PaymentEmails == true)
    };

    private static IResult? CheckTemplate(TemplateRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Title) || r.Title.Trim().Length > 80) return Validation.Invalid("title", "A title needs 1 to 80 characters.");
        if (string.IsNullOrWhiteSpace(r.Body) || r.Body.Trim().Length > 2000) return Validation.Invalid("body", "The reply needs 1 to 2000 characters.");
        return null;
    }

    private static string Preview(string s) => s.Length <= 140 ? s : s[..140] + "…";

    /// <summary>Server-Sent Events: GET /api/v1/seller/events. Authorised members only; access is re-checked periodically so removal ends the stream.</summary>
    public static void MapEventStream(this RouteGroupBuilder g)
    {
        g.MapGet("/seller/events", async (HttpContext ctx, EventHub hub, AccountContextService accounts, IOptions<OrderOptions> opt) =>
        {
            var a = ctx.GetAccess()!;
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            using var sub = hub.Subscribe(a);
            await ctx.Response.WriteAsync(": connected\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            var recheck = TimeSpan.FromSeconds(Math.Max(1, opt.Value.StreamRecheckSeconds));
            try
            {
                while (!ctx.RequestAborted.IsCancellationRequested)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                    cts.CancelAfter(recheck);
                    try
                    {
                        var e = await sub.Channel.Reader.ReadAsync(cts.Token);
                        await ctx.Response.WriteAsync($"event: {e.Type}\ndata: {JsonSerializer.Serialize(e.Data ?? new { }, OrderJson.Opts)}\n\n", ctx.RequestAborted);
                    }
                    catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
                    {
                        // heartbeat: also re-validate that the member still has access (removal / demotion / suspension ends the stream)
                        var now = await accounts.ResolveAsync(a.UserId, a.SellerId);
                        if (now is null || !now.Has(Permission.BrandWorkRead)) break;
                        sub.Access = now;   // role / brand scope changes apply to the open stream too
                        await ctx.Response.WriteAsync(": keep-alive\n\n", ctx.RequestAborted);
                    }
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) { }
        }).RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireActiveAccount().RequirePermission(Permission.BrandWorkRead);
    }
}
