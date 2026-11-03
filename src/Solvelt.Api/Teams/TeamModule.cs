using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Solvelt.Api.Options;

namespace Solvelt.Api.Teams;

public static class TeamModule
{
    /// <summary>Registers team/brand/invitation services and the "team" rate-limit policy (account switching, team management).</summary>
    public static WebApplicationBuilder AddTeams(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<TeamService>();
        builder.Services.AddScoped<AccountContextService>();
        var perMinute = builder.Configuration.GetValue("RateLimit:TeamPerMinute", 60);
        builder.Services.Configure<RateLimiterOptions>(o => o.AddPolicy("team", c =>
            RateLimitPartition.GetFixedWindowLimiter("team:" + (c.User.FindFirst("sub")?.Value ?? c.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
        return builder;
    }

    public static void MapTeams(this RouteGroupBuilder v1)
    {
        v1.MapSessionEndpoints();
        v1.MapTeamEndpoints();
        v1.MapBrandEndpoints();
        v1.MapInviteEndpoints();
    }
}
