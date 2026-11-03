namespace Solvelt.Api.Teams;

/// <summary>
/// Default-deny for the Driver role. A signed-in driver (the ACTIVE membership is a Driver) can only reach the driver app and their own
/// sign-in/security endpoints; every other seller endpoint answers 403 before it runs, so a Driver can never read products, orders, money or settings,
/// whichever endpoint filters a future module forgets. See docs/TEAMS.md and docs/LOCAL-DELIVERY.md.
/// </summary>
public static class DriverGuard
{
    private static readonly string[] Allowed =
    [
        "/api/v1/driver", "/api/v1/auth", "/api/v1/session", "/api/v1/public-config", "/api/v1/invitations", "/api/v1/team/leave",
        "/api/v1/account/me", "/api/v1/account/change-password", "/api/v1/account/2fa", "/api/v1/account/sessions", "/api/v1/account/passkeys",
        "/api/v1/account/email"
    ];

    public static bool PathAllowed(PathString path) => Allowed.Any(a => path.StartsWithSegments(a, StringComparison.OrdinalIgnoreCase));

    public static IApplicationBuilder UseDriverGuard(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        if (ctx.GetAccess() is { Role: Solvelt.Api.Domain.AccountRole.Driver } && !PathAllowed(ctx.Request.Path))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { type = "about:blank", title = "Drivers can only use the driver app", status = 403, code = "driver_forbidden" });
            return;
        }
        await next();
    });
}
