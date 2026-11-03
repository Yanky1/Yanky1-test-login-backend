using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Solvelt.Api.Products;

public static class ProductModule
{
    public static WebApplicationBuilder AddProducts(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<CategoryService>();
        builder.Services.AddScoped<IInventoryService, InventoryService>();
        builder.Services.AddSingleton<ICsvImportHooks, NoCsvImportHooks>();
        var perMinute = builder.Configuration.GetValue("RateLimit:CatalogPerMinute", 240);
        builder.Services.Configure<RateLimiterOptions>(o => o.AddPolicy("catalog", c =>
            RateLimitPartition.GetFixedWindowLimiter("catalog:" + (c.User.FindFirst("sub")?.Value ?? c.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
        return builder;
    }

    public static void MapProducts(this WebApplication app, RouteGroupBuilder v1)
    {
        v1.MapCategoryEndpoints();
        v1.MapCsvEndpoints();      // before /{id:guid} routes is irrelevant (guid constraint) but keeps related routes together
        v1.MapProductEndpoints();
        v1.MapQuestionEndpoints();
        if (app.Environment.IsEnvironment("Testing") || app.Environment.IsDevelopment()) app.MapDevQuestionEndpoint();
    }
}
