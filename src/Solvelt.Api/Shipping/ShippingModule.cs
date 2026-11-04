using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Solvelt.Api.Shipping;

public static class ShippingModule
{
    public static WebApplicationBuilder AddShipping(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration; var env = builder.Environment;
        builder.Services.Configure<ShippingOptions>(cfg.GetSection(ShippingOptions.Section));
        var o = cfg.GetSection(ShippingOptions.Section).Get<ShippingOptions>() ?? new ShippingOptions();
        // SHIPPO_API_KEY is accepted next to Shipping:ShippoApiKey
        var key = !string.IsNullOrWhiteSpace(o.ShippoApiKey) ? o.ShippoApiKey : Environment.GetEnvironmentVariable("SHIPPO_API_KEY") ?? "";
        var dev = env.IsDevelopment() || env.IsEnvironment("Testing");
        var hook = o.WebhookSecret;
        if (string.IsNullOrWhiteSpace(hook) && dev) hook = "dev-only-shipping-webhook-secret-0123456789";
        builder.Services.PostConfigure<ShippingOptions>(x => { x.ShippoApiKey = key; x.WebhookSecret = hook; });
        if (key.Length > 0)
        {
            ValidateKey(key, env.IsProduction(), o.AllowTestKeyInProduction, o.AllowLiveKeyOutsideProduction);
            if (env.IsProduction() && string.IsNullOrWhiteSpace(hook)) throw new InvalidOperationException("Shipping:WebhookSecret must be configured when a Shippo key is set in Production.");
            builder.Services.AddHttpClient<ShippoShippingProvider>(c => c.Timeout = TimeSpan.FromSeconds(30));
            builder.Services.AddSingleton<IShippingProvider>(sp => sp.GetRequiredService<ShippoShippingProvider>());
        }
        else if (dev) { builder.Services.AddSingleton<FakeShippingProvider>(); builder.Services.AddSingleton<IShippingProvider>(sp => sp.GetRequiredService<FakeShippingProvider>()); }
        else builder.Services.AddSingleton<IShippingProvider, UnconfiguredShippingProvider>();

        builder.Services.AddScoped<LabelLinks>();
        builder.Services.AddScoped<ShipmentTracker>();
        builder.Services.AddScoped<ShippingNotifier>();
        builder.Services.AddScoped<LabelService>();
        builder.Services.AddScoped<ReturnLabelService>();
        builder.Services.AddScoped<TrackingService>();
        builder.Services.AddScoped<ShippingWebhookService>();
        builder.Services.AddScoped<ShippingMaintenance>();
        if (!env.IsEnvironment("Testing") && o.RunBackgroundJobs) builder.Services.AddHostedService<ShippingWorker>();

        var perMinute = cfg.GetValue("RateLimit:ShippingPerMinute", 240);
        builder.Services.Configure<RateLimiterOptions>(r =>
        {
            r.AddPolicy("shipping", c => RateLimitPartition.GetFixedWindowLimiter("shipping:" + (c.User.FindFirst("sub")?.Value ?? c.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            r.AddPolicy("shipping-webhooks", c => RateLimitPartition.GetFixedWindowLimiter("shipping-hooks:" + (c.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = o.WebhooksPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        return builder;
    }

    /// <summary>Shippo keys look like shippo_test_... / shippo_live_...; production refuses a test key and other environments refuse a live key unless allowed on purpose.</summary>
    public static void ValidateKey(string key, bool production, bool allowTestInProduction, bool allowLiveOutsideProduction)
    {
        if (key.Length == 0) return;
        var test = key.StartsWith("shippo_test_"); var live = key.StartsWith("shippo_live_");
        if (!test && !live) throw new InvalidOperationException("Shipping:ShippoApiKey must be a Shippo API token (shippo_test_... or shippo_live_...).");
        if (production && test && !allowTestInProduction) throw new InvalidOperationException("Production refuses a Shippo TEST key. Use a live key, or set Shipping:AllowTestKeyInProduction=true on purpose.");
        if (!production && live && !allowLiveOutsideProduction) throw new InvalidOperationException("A Shippo LIVE key is only accepted in Production (set Shipping:AllowLiveKeyOutsideProduction=true to override).");
    }

    public static void MapShipping(this WebApplication app, RouteGroupBuilder v1)
    {
        v1.MapShippingSettings();
        v1.MapShippingLabels();
        v1.MapShippingAdmin();
        app.MapShippingWebhooks(v1);
        app.MapShippingInternal();
        if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing")) app.MapShippingDev();
    }
}
