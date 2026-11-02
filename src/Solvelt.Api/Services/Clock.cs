namespace Solvelt.Api.Services;

public static class ClockExtensions
{
    public static DateTime UtcNow(this TimeProvider tp) => tp.GetUtcNow().UtcDateTime;
}
