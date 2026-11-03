using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Orders;

namespace Solvelt.Api.Admin;

public static class IntegrationEndpoints
{
    private static object Row(OutboxMessage m, bool detail) => new
    {
        m.Id, m.EventType, m.SellerId, m.AggregateId, status = m.Status == OutboxStatus.Pending && m.Attempts > 0 ? "Retrying" : m.Status.ToString(),
        m.Attempts, m.CreatedAt, m.NextAttemptAt, m.DeliveredAt, lastError = m.LastError is null ? null : Mask.Text(m.LastError.Length > 300 ? m.LastError[..300] : m.LastError),
        payload = detail ? PayloadRedactor.Redact(m.PayloadJson) : null
    };

    public static void MapIntegrationEndpoints(this RouteGroupBuilder g)
    {
        var i = g.MapGroup("/integrations");

        i.AGet("/outbox", async (string? status, string? type, int? page, int? pageSize, AppDbContext db) =>
        {
            var (p, size) = AdminHttp.Paging(page, pageSize);
            var q = db.Outbox.AsNoTracking().AsQueryable();
            switch ((status ?? "").ToLowerInvariant())
            {
                case "": break;
                case "pending": q = q.Where(m => m.Status == OutboxStatus.Pending && m.Attempts == 0); break;
                case "retrying": q = q.Where(m => m.Status == OutboxStatus.Pending && m.Attempts > 0); break;
                case "dead": case "deadlettered": q = q.Where(m => m.Status == OutboxStatus.Dead); break;
                case "delivered": q = q.Where(m => m.Status == OutboxStatus.Delivered); break;
                case "discarded": q = q.Where(m => m.Status == OutboxStatus.Discarded); break;
                default: return Validation.Invalid("status", "Use pending, retrying, dead, delivered or discarded.");
            }
            if (!string.IsNullOrWhiteSpace(type)) q = q.Where(m => m.EventType == type);
            var total = await q.CountAsync();
            var rows = await q.OrderByDescending(m => m.CreatedAt).Skip((p - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new
            {
                total, page = p, pageSize = size,
                counts = new
                {
                    pending = await db.Outbox.CountAsync(m => m.Status == OutboxStatus.Pending && m.Attempts == 0),
                    retrying = await db.Outbox.CountAsync(m => m.Status == OutboxStatus.Pending && m.Attempts > 0),
                    dead = await db.Outbox.CountAsync(m => m.Status == OutboxStatus.Dead),
                    discarded = await db.Outbox.CountAsync(m => m.Status == OutboxStatus.Discarded)
                },
                items = rows.Select(m => Row(m, false))
            });
        }, StaffPermission.IntegrationsView);

        i.AGet("/outbox/{id:guid}", async (Guid id, AppDbContext db) =>
            await db.Outbox.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id) is { } m ? Results.Ok(Row(m, true)) : Results.NotFound(), StaffPermission.IntegrationsView);

        i.APost("/outbox/{id:guid}/retry", async (Guid id, ReasonRequest r, HttpContext ctx, AppDbContext db, AdminAudit audit, TimeProvider time) =>
        {
            if (AdminHttp.NeedReason(r.Reason, out var reason) is { } bad) return bad;
            var now = time.GetUtcNow().UtcDateTime;
            var n = await db.Outbox.Where(m => m.Id == id && m.Status == OutboxStatus.Dead)
                .ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, OutboxStatus.Pending).SetProperty(m => m.Attempts, 0).SetProperty(m => m.NextAttemptAt, now).SetProperty(m => m.LockedUntil, (DateTime?)null));
            if (n == 0) return await db.Outbox.AnyAsync(m => m.Id == id) ? AdminHttp.Err(409, "not_dead", "Only dead-lettered events can be retried.") : Results.NotFound();
            await audit.WriteAsync(ctx.Staff(), "outbox.retried", "outbox", id, reason, new { status = "Dead" }, new { status = "Pending" });
            return Results.Ok(new { id, status = "Pending" });
        }, StaffPermission.IntegrationsAct);

        i.APost("/outbox/{id:guid}/discard", async (Guid id, ReasonRequest r, HttpContext ctx, AppDbContext db, AdminAudit audit) =>
        {
            if (AdminHttp.NeedReason(r.Reason, out var reason) is { } bad) return bad;
            var n = await db.Outbox.Where(m => m.Id == id && m.Status == OutboxStatus.Dead).ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, OutboxStatus.Discarded));
            if (n == 0) return await db.Outbox.AnyAsync(m => m.Id == id) ? AdminHttp.Err(409, "not_dead", "Only dead-lettered events can be discarded.") : Results.NotFound();
            await audit.WriteAsync(ctx.Staff(), "outbox.discarded", "outbox", id, reason, new { status = "Dead" }, new { status = "Discarded" });
            return Results.Ok(new { id, status = "Discarded" });
        }, StaffPermission.IntegrationsDiscard);

        i.AGet("/inbound", async (AppDbContext db, TimeProvider time) =>
        {
            var now = time.GetUtcNow().UtcDateTime; var d1 = now.AddHours(-24); var d7 = now.AddDays(-7);
            var events = await db.AuditEvents.AsNoTracking().Where(e => e.Type == "inbound.rejected" && e.At >= d7).OrderByDescending(e => e.At).Select(e => new { e.At, e.Detail, e.IpAddress }).Take(2000).ToListAsync();
            string Code(string? d) => d is { } x && x.StartsWith("code=") ? x[5..] : "other";
            object Counts(DateTime since) => events.Where(e => e.At >= since).GroupBy(e => Code(e.Detail)).ToDictionary(x => x.Key, x => x.Count());
            return Results.Ok(new { last24h = Counts(d1), last7d = Counts(d7), recent = events.Take(50).Select(e => new { e.At, code = Code(e.Detail), e.IpAddress }) });
        }, StaffPermission.IntegrationsView);

        i.AGet("/phone", async (AppDbContext db, TimeProvider time) =>
        {
            var now = time.GetUtcNow().UtcDateTime; var d1 = now.AddHours(-24); var d7 = now.AddDays(-7);
            string[] types = ["sms.failed", "sms.blocked", "sms.sent", "phone.signin_failed", "phone.blocked_locked"];
            var rows = await db.AuditEvents.AsNoTracking().Where(e => e.At >= d7 && types.Contains(e.Type)).Select(e => new { e.Type, e.At }).ToListAsync();
            object Counts(DateTime since) => types.ToDictionary(t => t, t => rows.Count(r => r.Type == t && r.At >= since));
            var recent = await db.AuditEvents.AsNoTracking().Where(e => e.Type == "sms.failed" || e.Type == "sms.blocked").OrderByDescending(e => e.At).Take(50).Select(e => new { e.Type, e.At, e.Detail }).ToListAsync();
            return Results.Ok(new { last24h = Counts(d1), last7d = Counts(d7), recentFailures = recent.Select(e => new { e.Type, e.At, detail = Mask.Text(e.Detail) }) });
        }, StaffPermission.IntegrationsView);

        i.AGet("/jobs", async (AppDbContext db, TimeProvider time) =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            var jobs = await db.JobRuns.AsNoTracking().OrderBy(j => j.Name).ToListAsync();
            return Results.Ok(new { items = jobs.Select(j => new { j.Name, j.LastStartedAt, j.LastFinishedAt, j.LastSucceeded, j.LastResult, j.Runs, j.Failures,
                minutesSinceLastRun = j.LastFinishedAt is { } f ? Math.Round((now - f).TotalMinutes, 1) : (double?)null }) });
        }, StaffPermission.IntegrationsView);
    }
}
