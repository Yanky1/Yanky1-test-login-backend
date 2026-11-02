using Solvelt.Api.Data;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Services;

public interface IAuditLog
{
    Task WriteAsync(string type, Guid? userId = null, string? email = null, string? detail = null);
}

public class AuditLog(AppDbContext db, TimeProvider time, IHttpContextAccessor http) : IAuditLog
{
    public async Task WriteAsync(string type, Guid? userId = null, string? email = null, string? detail = null)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            Type = type, UserId = userId, Subject = email is null ? null : Mask(email),
            IpAddress = http.HttpContext?.Connection.RemoteIpAddress?.ToString(),
            Detail = detail, At = time.UtcNow()
        });
        await db.SaveChangesAsync();
    }

    public static string Mask(string email)
    {
        var at = email.IndexOf('@');
        return at <= 1 ? "***" : email[0] + "***" + email[at..];
    }
}
