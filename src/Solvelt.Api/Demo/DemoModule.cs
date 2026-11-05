using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Admin;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Demo;

/// <summary>At most <c>Demo:PerHour</c> demo actions (fill, remove, approve) per account per hour, counted per seller account (not per IP: the account is what the data belongs to).</summary>
public sealed class DemoRateLimiter(TimeProvider time, IOptions<DemoOptions> opt)
{
    private readonly ConcurrentDictionary<Guid, Queue<DateTime>> hits = new();

    /// <summary>Records the call; false (and not recorded) when the account used up its hour.</summary>
    public bool TryAcquire(Guid sellerId, out TimeSpan retryAfter)
    {
        var q = hits.GetOrAdd(sellerId, _ => new Queue<DateTime>());
        var now = time.UtcNow();
        lock (q)
        {
            while (q.Count > 0 && q.Peek() <= now.AddHours(-1)) q.Dequeue();
            if (q.Count >= Math.Max(1, opt.Value.PerHour)) { retryAfter = q.Peek().AddHours(1) - now; return false; }
            q.Enqueue(now); retryAfter = TimeSpan.Zero; return true;
        }
    }
}

public static class DemoModule
{
    public const string ApproveActor = "demo-self-approve";

    public static WebApplicationBuilder AddDemo(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<DemoOptions>(builder.Configuration.GetSection(DemoOptions.Section));
        builder.Services.AddScoped<DemoContext>();
        builder.Services.AddSingleton<DemoRateLimiter>();
        builder.Services.AddScoped<DemoSeeder>();
        builder.Services.AddScoped<DemoRemover>();
        return builder;
    }

    private static IResult Problem(int status, string code, string title) => Results.Json(new { type = "about:blank", title, status, code }, statusCode: status);

    /// <summary>Maps the endpoints only when Demo:Enabled is true: when it is off they do not exist (404), exactly like any unknown route.</summary>
    public static void MapDemo(this WebApplication app, RouteGroupBuilder v1)
    {
        if (!app.Services.GetRequiredService<IOptions<DemoOptions>>().Value.Enabled) return;
        var g = v1.MapGroup("/seller").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireAccountRole(AccountRole.Owner);

        g.MapGet("/demo-data", async (HttpContext ctx, AppDbContext db, DemoRemover remover) =>
        {
            var a = ctx.GetAccess()!;
            var batch = await db.DemoBatches.AsNoTracking().FirstOrDefaultAsync(b => b.SellerId == a.SellerId);
            return Results.Ok(new { exists = batch is not null, batchId = batch?.Id, createdAt = batch?.CreatedAt, counts = await remover.CountsAsync(a.SellerId) });
        });

        g.MapPost("/demo-data", async (HttpContext ctx, DemoRateLimiter limit, AppDbContext db, DemoSeeder seeder, DemoRemover remover, IAuditLog audit) =>
        {
            var a = ctx.GetAccess()!;
            if (!limit.TryAcquire(a.SellerId, out var retry)) return TooMany(ctx, retry);
            if (await db.DemoBatches.AnyAsync(b => b.SellerId == a.SellerId))
                return Problem(409, "demo_data_exists", "Demo data already exists for this account. Remove it first.");
            await audit.WriteAsync("demo.fill_requested", a.UserId, null, $"seller={a.SellerId}");
            var r = await seeder.FillAsync(a, remover);
            if (!r.Created) return Problem(409, "demo_data_exists", "Demo data already exists for this account. Remove it first.");
            var batch = await db.DemoBatches.AsNoTracking().FirstAsync(b => b.SellerId == a.SellerId);
            return Results.Json(new { batchId = batch.Id, counts = r.Counts }, statusCode: 201);
        });

        g.MapDelete("/demo-data", async (HttpContext ctx, DemoRateLimiter limit, DemoRemover remover) =>
        {
            var a = ctx.GetAccess()!;
            if (!limit.TryAcquire(a.SellerId, out var retry)) return TooMany(ctx, retry);
            return Results.Ok(new { removed = await remover.RemoveAsync(a.SellerId, a.UserId) });
        });

        // Approves the caller's OWN account through the very code staff approval uses (state machine, status audit, admin audit, approval mail), mail routed to the sink.
        g.MapPost("/demo/approve", async (HttpContext ctx, DemoRateLimiter limit, AppDbContext db, DemoContext demo, SellerAdminService admin, IAuditLog audit) =>
        {
            var a = ctx.GetAccess()!;
            if (!limit.TryAcquire(a.SellerId, out var retry)) return TooMany(ctx, retry);
            var seller = await db.Sellers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == a.SellerId);   // the account comes from the session, never from the request: no IDOR
            if (seller is null) return Results.NotFound();
            if (seller.Status == SellerStatus.Approved) return Problem(409, "already_approved", "This account is already approved.");
            if (seller.Status is not (SellerStatus.PendingReview or SellerStatus.NeedsInfo)) return Problem(409, "not_pending", $"Only an account waiting for review can be approved this way (this one is {seller.Status}).");
            demo.Mail = DemoMail.Sink;
            try
            {
                var staff = new StaffContext(Guid.Empty, ApproveActor, StaffRole.ReadOnly, Guid.Empty, null);
                var res = await admin.ChangeStatusAsync(staff, seller.Id, SellerStatus.Approved, "demo.self_approve", ApproveActor);
                if (res is IStatusCodeHttpResult { StatusCode: >= 400 }) return Problem(409, "not_pending", "The account could not be approved from its current status.");
                await audit.WriteAsync("demo.self_approved", a.UserId, null, $"actor={ApproveActor};seller={a.SellerId}");
                return Results.Ok(new { status = SellerStatus.Approved.ToString() });
            }
            finally { demo.Mail = DemoMail.Normal; }
        });
    }

    private static IResult TooMany(HttpContext ctx, TimeSpan retry)
    {
        ctx.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)).ToString();
        return Problem(429, "rate_limited", "Too many demo actions for this account. Try again later.");
    }

    // ---- "demo": true on records in the seller's JSON responses -----------------------------------------------------------------
    private static readonly string[] FlaggedAreas = ["/api/v1/seller/products", "/api/v1/seller/services", "/api/v1/seller/orders", "/api/v1/seller/bookings", "/api/v1/seller/quote-requests", "/api/v1/seller/messages", "/api/v1/seller/questions", "/api/v1/seller/reviews"];

    /// <summary>Adds <c>"demo": true</c> to every object in a seller list/detail response whose id is a demo record of the caller's account (only when Demo:Enabled; one small query).</summary>
    public static void UseDemoFlags(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<IOptions<DemoOptions>>().Value.Enabled) return;
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";
            if (!HttpMethods.IsGet(ctx.Request.Method) || !FlaggedAreas.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase))) { await next(); return; }
            var original = ctx.Response.Body;
            await using var buffer = new MemoryStream();
            ctx.Response.Body = buffer;
            try { await next(); }
            finally { ctx.Response.Body = original; }
            buffer.Position = 0;
            var bytes = buffer.ToArray();
            if (ctx.Response.StatusCode == 200 && ctx.GetAccess() is { } access && (ctx.Response.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase) && bytes.Length > 2)
            {
                var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
                var ids = (await db.DemoRecords.AsNoTracking().Where(r => r.SellerId == access.SellerId && r.Kind != "payment_event" && r.Kind != "image_file").Select(r => r.EntityId).ToListAsync()).ToHashSet();
                if (ids.Count > 0)
                {
                    try
                    {
                        var node = JsonNode.Parse(bytes);
                        if (Flag(node, ids)) bytes = JsonSerializer.SerializeToUtf8Bytes(node);
                    }
                    catch (JsonException) { /* not JSON after all: pass through */ }
                }
            }
            ctx.Response.ContentLength = bytes.Length;
            await original.WriteAsync(bytes);
        });
    }

    private static bool Flag(JsonNode? n, HashSet<Guid> ids)
    {
        var changed = false;
        switch (n)
        {
            case JsonObject o:
                if (o["id"] is JsonValue v && v.TryGetValue<string>(out var s) && Guid.TryParse(s, out var g) && ids.Contains(g)) { o["demo"] = true; changed = true; }
                foreach (var kv in o.ToList()) changed |= Flag(kv.Value, ids);
                break;
            case JsonArray arr:
                foreach (var item in arr) changed |= Flag(item, ids);
                break;
        }
        return changed;
    }
}
