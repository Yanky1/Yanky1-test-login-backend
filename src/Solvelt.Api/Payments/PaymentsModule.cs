using Solvelt.Api.Services;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;

namespace Solvelt.Api.Payments;

public static class PaymentsModule
{
    public static WebApplicationBuilder AddPayments(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration; var env = builder.Environment;
        builder.Services.Configure<PaymentOptions>(cfg.GetSection(PaymentOptions.Section));
        builder.Services.Configure<StripeOptions>(cfg.GetSection(StripeOptions.Section));
        var p = cfg.GetSection(PaymentOptions.Section).Get<PaymentOptions>() ?? new PaymentOptions();
        var so = cfg.GetSection(StripeOptions.Section).Get<StripeOptions>() ?? new StripeOptions();
        // env vars STRIPE_SECRET_KEY / STRIPE_WEBHOOK_SECRET are accepted next to Stripe:SecretKey / Stripe:WebhookSecret
        var key = !string.IsNullOrWhiteSpace(so.SecretKey) ? so.SecretKey : Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY") ?? "";
        var hook = !string.IsNullOrWhiteSpace(so.WebhookSecret) ? so.WebhookSecret : Environment.GetEnvironmentVariable("STRIPE_WEBHOOK_SECRET") ?? "";
        builder.Services.PostConfigure<StripeOptions>(o => { o.SecretKey = key; o.WebhookSecret = hook; });
        var dev = env.IsDevelopment() || env.IsEnvironment("Testing");
        var prod = env.IsProduction();
        if (key.Length > 0)
        {
            ValidateKey(key, prod, p.AllowTestKeysInProduction, p.AllowLiveKeysOutsideProduction);
            builder.Services.AddSingleton<IPaymentProvider, StripePaymentProvider>();
        }
        else if (dev) { builder.Services.AddSingleton<FakePaymentProvider>(); builder.Services.AddSingleton<IPaymentProvider>(sp => sp.GetRequiredService<FakePaymentProvider>()); }
        else builder.Services.AddSingleton<IPaymentProvider, UnconfiguredPaymentProvider>();

        builder.Services.AddSingleton<PaymentTestHooks>();
        builder.Services.AddScoped<LedgerService>();
        builder.Services.AddScoped<PaymentNotifier>();
        builder.Services.AddScoped<PaymentIngestService>();
        builder.Services.AddScoped<StripeWebhookService>();
        builder.Services.AddScoped<OnboardingService>();
        builder.Services.AddScoped<JobLeaseService>();
        builder.Services.AddScoped<ReleaseService>();
        builder.Services.AddScoped<TransferService>();
        builder.Services.AddScoped<ReconciliationService>();
        if (!env.IsEnvironment("Testing") && p.RunBackgroundJobs) builder.Services.AddHostedService<PaymentsWorker>();

        var perMinute = cfg.GetValue("RateLimit:PaymentsPerMinute", 120);
        builder.Services.Configure<RateLimiterOptions>(r =>
        {
            r.AddPolicy("payments", c => RateLimitPartition.GetFixedWindowLimiter("payments:" + (c.User.FindFirst("sub")?.Value ?? c.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            r.AddPolicy("stripe-webhooks", c => RateLimitPartition.GetFixedWindowLimiter("stripe-hooks:" + (c.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = p.WebhooksPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        return builder;
    }

    /// <summary>Separate test/live key handling: a secret key must look like one; production refuses test keys and other environments refuse live keys unless allowed on purpose.</summary>
    public static void ValidateKey(string key, bool production, bool allowTestInProduction, bool allowLiveOutsideProduction)
    {
        if (key.Length == 0) return;
        var test = key.StartsWith("sk_test_") || key.StartsWith("rk_test_");
        var live = key.StartsWith("sk_live_") || key.StartsWith("rk_live_");
        if (!test && !live) throw new InvalidOperationException("Stripe:SecretKey must be a Stripe secret or restricted key (sk_test_..., sk_live_..., rk_...).");
        if (production && test && !allowTestInProduction) throw new InvalidOperationException("Production refuses a Stripe TEST key. Use a live key, or set Payments:AllowTestKeysInProduction=true on purpose.");
        if (!production && live && !allowLiveOutsideProduction) throw new InvalidOperationException("A Stripe LIVE key is only accepted in Production (set Payments:AllowLiveKeysOutsideProduction=true to override).");
    }

    public static void MapPayments(this WebApplication app, RouteGroupBuilder v1)
    {
        v1.MapSellerPayments();
        v1.MapPaymentAdmin();
        app.MapPaymentInternal();
        if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing")) app.MapPaymentDev();
    }

    private static IResult Err(int status, string code, string message, object? extra = null) =>
        Results.Json(new { type = "about:blank", title = message, status, code, errors = extra }, statusCode: status);

    private static async Task<string?> ReadBody(HttpRequest req, int max)
    {
        using var ms = new MemoryStream(); var buf = new byte[8192]; int n;
        while ((n = await req.Body.ReadAsync(buf)) > 0) { ms.Write(buf, 0, n); if (ms.Length > max) return null; }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Marketplace payment events (same HMAC scheme as the orders API) and the Stripe webhook endpoint.</summary>
    private static void MapPaymentInternal(this WebApplication app)
    {
        app.MapPost("/api/internal/v1/payment-events", async (HttpContext ctx, InboundVerifier verifier, PaymentIngestService svc) =>
        {
            var v = await verifier.VerifyAsync(ctx.Request, ctx.RequestAborted);
            if (!v.Ok) return Err(v.Status, v.Code, v.Message);
            MarketplacePaymentEvent? ev;
            try { ev = JsonSerializer.Deserialize<MarketplacePaymentEvent>(v.Body!, OrderJson.Opts); }
            catch (JsonException) { return Err(400, "invalid_json", "The request body is not valid JSON of the expected shape."); }
            if (ev is null) return Err(400, "invalid_json", "The request body must be a JSON object.");
            var r = await svc.HandleAsync(ev);
            return r.Status == 200 ? Results.Json(new { status = r.Code, result = r.Body }, OrderJson.Opts) : Err(r.Status, r.Code, r.Message);
        }).RequireRateLimiting("internal").AllowAnonymous();

        // Stripe -> portal. Authenticated by the endpoint signing secret only (never a bearer token); 2xx also for duplicates and ignored events.
        app.MapPost("/api/webhooks/stripe", async (HttpContext ctx, StripeWebhookService svc, IOptions<PaymentOptions> opt) =>
        {
            var body = await ReadBody(ctx.Request, 1024 * 1024);
            if (body is null) return Err(413, "too_large", "Request body too large.");
            var r = await svc.HandleAsync(body, ctx.Request.Headers["Stripe-Signature"].ToString());
            return r.Status == 200 ? Results.Json(new { received = true, status = r.Code, outcome = r.Outcome }) : Err(r.Status, r.Code, "Webhook rejected: " + r.Code);
        }).RequireRateLimiting("stripe-webhooks").AllowAnonymous();
    }

    /// <summary>Development/Testing helpers (never mapped elsewhere): drive the fake provider and the jobs from E2E tests and the simulator.</summary>
    private static void MapPaymentDev(this WebApplication app)
    {
        var g = app.MapGroup("/api/dev/payments").AllowAnonymous();

        g.MapPost("/run", async (ReleaseService rel, TransferService tr, ReconciliationService rec) =>
        {
            var r = await rel.RunAsync(); var t = await tr.RunAsync(); var c = await rec.RunAsync();
            return Results.Ok(new { release = r, transfers = t, reconcile = c });
        });

        // pretends the seller finished the hosted Stripe onboarding (fake provider only)
        g.MapPost("/complete-onboarding/{sellerId:guid}", async (Guid sellerId, AppDbContext db, IPaymentProvider provider, TimeProvider time) =>
        {
            if (provider is not FakePaymentProvider fake) return Err(409, "not_fake", "Only the fake provider can do this.");
            var acct = await db.PayoutAccounts.FirstOrDefaultAsync(a => a.SellerId == sellerId);
            if (acct is null) return Results.NotFound();
            var snap = fake.Complete(acct.StripeAccountId);
            StripeWebhookService.Apply(acct, snap, time.UtcNow()); acct.UpdatedAt = time.UtcNow();
            await db.SaveChangesAsync();
            return Results.Ok(OnboardingService.Map(acct, true));
        });

        // simulates the marketplace charging the buyer for a seller order (signed endpoints do this in production)
        g.MapPost("/capture/{orderId:guid}", async (Guid orderId, AppDbContext db, PaymentIngestService svc) =>
        {
            var o = await db.Orders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == orderId);
            if (o is null) return Results.NotFound();
            var data = JsonSerializer.SerializeToElement(new { kind = "order", marketplaceOrderId = o.MarketplaceOrderId, sellerOrderNumber = o.Number, paymentRef = "ch_dev_" + o.Number, currency = o.Currency }, OrderJson.Opts);
            var r = await svc.HandleAsync(new MarketplacePaymentEvent("dev-capture-" + orderId, "payment.captured", data));
            return Results.Json(new { r.Status, r.Code }, statusCode: r.Status);
        });

        g.MapPost("/refund/{orderId:guid}", async (Guid orderId, DevRefund body, AppDbContext db, PaymentIngestService svc) =>
        {
            var o = await db.Orders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == orderId);
            if (o is null) return Results.NotFound();
            var data = JsonSerializer.SerializeToElement(new { refundId = "dev-refund-" + Guid.NewGuid().ToString("N")[..10], marketplaceOrderId = o.MarketplaceOrderId, sellerOrderNumber = o.Number, amountCents = body.AmountCents, shippingCents = 0 }, OrderJson.Opts);
            var r = await svc.HandleAsync(new MarketplacePaymentEvent("dev-refund-ev-" + Guid.NewGuid().ToString("N"), "refund.processed", data));
            return Results.Json(new { r.Status, r.Code, r.Message }, statusCode: r.Status);
        });

        // moves an order's delivery back in time so release rules apply immediately
        g.MapPost("/age-order/{orderId:guid}", async (Guid orderId, AppDbContext db, IOptions<PaymentOptions> opt, IOptions<OrderOptions> orders) =>
        {
            var o = await db.Orders.FirstOrDefaultAsync(x => x.Id == orderId);
            if (o is null || o.DeliveredAt is null) return Results.NotFound();
            // past EVERY release rule: the release delay and, when required (the default), the return window
            var days = opt.Value.RequireReturnWindowClosed ? Math.Max(opt.Value.ReleaseDays, orders.Value.ReturnWindowDays) : opt.Value.ReleaseDays;
            o.DeliveredAt = o.DeliveredAt.Value.AddDays(-(days + 1));
            await db.SaveChangesAsync();
            return Results.Ok();
        });
    }
}

public record DevRefund(long AmountCents);
