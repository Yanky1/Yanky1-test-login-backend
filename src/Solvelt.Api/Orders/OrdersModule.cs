using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Solvelt.Api.Orders;

public static class OrdersModule
{
    public static WebApplicationBuilder AddOrders(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
        var env = builder.Environment;
        builder.Services.Configure<OrderOptions>(cfg.GetSection(OrderOptions.Section));
        var o = cfg.GetSection(OrderOptions.Section).Get<OrderOptions>() ?? new OrderOptions();
        var dev = env.IsDevelopment() || env.IsEnvironment("Testing");
        if (string.IsNullOrWhiteSpace(o.InboundSecret) || string.IsNullOrWhiteSpace(o.OutboundSecret))
        {
            if (!dev) throw new InvalidOperationException("Orders:InboundSecret and Orders:OutboundSecret must be configured outside Development/Testing.");
            builder.Services.PostConfigure<OrderOptions>(x =>
            {
                if (string.IsNullOrWhiteSpace(x.InboundSecret)) x.InboundSecret = "dev-only-inbound-secret-0123456789abcdef";
                if (string.IsNullOrWhiteSpace(x.OutboundSecret)) x.OutboundSecret = "dev-only-outbound-secret-fedcba9876543210";
            });
        }
        else if (o.InboundSecret == o.OutboundSecret) throw new InvalidOperationException("Orders:InboundSecret and Orders:OutboundSecret must differ.");

        builder.Services.AddSingleton<EventHub>();
        builder.Services.AddSingleton<OutboxSink>();
        builder.Services.AddSingleton<ReservationDrift>();
        builder.Services.AddSingleton<InflightHolds>();
        builder.Services.AddHttpClient("outbox", c => c.Timeout = TimeSpan.FromSeconds(10));
        builder.Services.AddSingleton<InboundRejectedSampler>();
        builder.Services.AddScoped<InboundVerifier>();
        builder.Services.AddScoped<Outbox>();
        builder.Services.AddScoped<OutboxDispatcher>();
        builder.Services.AddScoped<OrderNotifier>();
        builder.Services.AddScoped<OrderActions>();
        builder.Services.AddScoped<OrderIngestService>();
        builder.Services.AddScoped<BuyerEventService>();
        builder.Services.AddScoped<MessageService>();
        builder.Services.AddScoped<OrderMaintenance>();
        if (!env.IsEnvironment("Testing") && o.RunBackgroundJobs) builder.Services.AddHostedService<OutboxWorker>();

        var perMinute = cfg.GetValue("RateLimit:OrdersPerMinute", 300);
        var internalPerMinute = cfg.GetValue("RateLimit:InternalPerMinute", 1200);
        var orderReadPerMinute = cfg.GetValue("RateLimit:InternalOrderReadPerMinute", 600);
        builder.Services.Configure<RateLimiterOptions>(r =>
        {
            r.AddPolicy("orders", c => RateLimitPartition.GetFixedWindowLimiter("orders:" + (c.User.FindFirst("sub")?.Value ?? c.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            r.AddPolicy("internal-order-read", c => RateLimitPartition.GetFixedWindowLimiter("iorderread:" + (c.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = orderReadPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            r.AddPolicy("internal", c => RateLimitPartition.GetFixedWindowLimiter("internal:" + (c.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = internalPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        return builder;
    }

    public static void MapOrders(this WebApplication app, RouteGroupBuilder v1)
    {
        v1.MapOrderEndpoints();
        v1.MapMessageEndpoints();
        v1.MapEventStream();
        v1.MapOrderAdminEndpoints();
        app.MapInternalEndpoints();
        if (AdminDevEndpoints.SimulatorEnabled(app.Environment)) app.MapSimulator(v1);
    }
}
