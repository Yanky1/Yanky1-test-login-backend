using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Options;
using Solvelt.Api.Orders;
using Solvelt.Api.Services;

namespace Solvelt.Api.Admin;

public record FeeRequest(int? FeeBasisPoints, string? Reason);
public record LegalRequest(string? TermsVersion, string? SellerAgreementVersion, string? Reason);
public record FieldDto(string? Key, string? Label, string? Type, string[]? Options, string? ParentKey, string? ParentValue, bool Required, int SortOrder);
public record FieldsRequest(List<FieldDto>? Fields, string? Reason);
public record PreviewRequest(List<FieldDto>? Fields, Dictionary<string, string>? Answers);

public static class AuditEndpoints
{
    private static async Task<IQueryable<AuditEvent>> FilterAsync(AppDbContext db, string? actor, string? target, string? targetType, string? action, DateTime? from, DateTime? to, bool staffOnly)
    {
        var q = db.AuditEvents.AsNoTracking().AsQueryable();
        if (staffOnly) q = q.Where(e => e.Type.StartsWith("admin."));
        if (!string.IsNullOrWhiteSpace(actor))
        {
            if (Guid.TryParse(actor, out var gid)) q = q.Where(e => e.UserId == gid);
            else
            {
                var t = actor.Trim().ToLower();
                var ids = await db.Users.AsNoTracking().Where(u => u.Email!.ToLower().Contains(t)).Select(u => (Guid?)u.Id).Take(50).ToListAsync();
                q = q.Where(e => ids.Contains(e.UserId));
            }
        }
        if (!string.IsNullOrWhiteSpace(target)) { var t = target.Trim(); q = q.Where(e => e.TargetId == t); }
        if (!string.IsNullOrWhiteSpace(targetType)) q = q.Where(e => e.TargetType == targetType);
        if (!string.IsNullOrWhiteSpace(action)) { var a = action.Trim(); q = q.Where(e => e.Type == a || e.Type == "admin." + a || e.Type.StartsWith(a)); }
        if (from is { } f) q = q.Where(e => e.At >= f);
        if (to is { } tt) q = q.Where(e => e.At < tt);
        return q;
    }

    public static void MapAuditEndpoints(this RouteGroupBuilder g)
    {
        var a = g.MapGroup("/audit");

        a.AGet("", async (string? actor, string? target, string? targetType, string? action, DateTime? from, DateTime? to, bool? staffOnly, int? page, int? pageSize, AppDbContext db) =>
        {
            var (p, size) = AdminHttp.Paging(page, pageSize);
            var q = await FilterAsync(db, actor, target, targetType, action, from, to, staffOnly ?? false);
            var total = await q.CountAsync();
            var rows = await q.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Skip((p - 1) * size).Take(size)
                .Select(e => new { e.Id, e.Type, e.At, actorId = e.UserId, e.ActorRole, e.Subject, e.TargetType, e.TargetId, e.Reason, e.IpAddress, e.Detail }).ToListAsync();
            return Results.Ok(new { total, page = p, pageSize = size, items = rows });
        }, StaffPermission.AuditView);

        a.AGet("/export", async (string? actor, string? target, string? targetType, string? action, DateTime? from, DateTime? to, bool? staffOnly, HttpContext ctx,
            AppDbContext db, AdminAudit audit, IOptions<AdminOptions> opt, TimeProvider time) =>
        {
            var q = await FilterAsync(db, actor, target, targetType, action, from, to, staffOnly ?? false);
            var rows = await q.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Take(opt.Value.MaxExportRows)
                .Select(e => new { e.Id, e.Type, e.At, e.UserId, e.ActorRole, e.Subject, e.TargetType, e.TargetId, e.Reason, e.IpAddress, e.Detail }).ToListAsync();
            var sb = new StringBuilder();
            sb.Append(Csv.Row("id", "at", "type", "actor_id", "actor_role", "subject", "target_type", "target_id", "reason", "ip", "detail")).Append("\r\n");
            foreach (var e in rows)
                sb.Append(Csv.Row(e.Id.ToString(), e.At.ToString("O"), e.Type, e.UserId?.ToString(), e.ActorRole, e.Subject, e.TargetType, e.TargetId, e.Reason, e.IpAddress, e.Detail)).Append("\r\n");
            await audit.WriteAsync(ctx.Staff(), "audit.exported", "audit", null, "export", extra: $"rows={rows.Count}");
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=\"solvelt-audit-{time.GetUtcNow():yyyyMMdd-HHmmss}.csv\"";
            return Results.Text(sb.ToString(), "text/csv; charset=utf-8");
        }, StaffPermission.AuditExport);
    }
}

public static class SettingsEndpoints
{
    private static readonly Regex KeyRx = new("^[a-z0-9_]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex VersionRx = new("^[0-9A-Za-z._-]{1,32}$", RegexOptions.Compiled);
    private static readonly string[] Types = ["yesno", "text", "select", "file"];
    private const string Protected = "kosher_certificate";

    private static SellerFieldConfig ToConfig(FieldDto f) => new()
    {
        Key = f.Key!, Label = f.Label!, Type = f.Type!, OptionsJson = f.Type == "select" ? JsonSerializer.Serialize(f.Options) : null,
        ParentKey = string.IsNullOrWhiteSpace(f.ParentKey) ? null : f.ParentKey, ParentValue = string.IsNullOrWhiteSpace(f.ParentKey) ? null : f.ParentValue, Required = f.Required, SortOrder = f.SortOrder
    };

    /// <summary>Validates the wizard configuration (keys, types, options, branching: a parent must exist, come earlier and be a yes/no or select field).</summary>
    public static Dictionary<string, string[]> Validate(List<FieldDto>? fields)
    {
        var errors = new Dictionary<string, string[]>();
        if (fields is null || fields.Count == 0) { errors["fields"] = ["Provide at least one field."]; return errors; }
        if (fields.Count > 50) { errors["fields"] = ["At most 50 fields."]; return errors; }
        var byKey = new Dictionary<string, FieldDto>();
        for (var i = 0; i < fields.Count; i++)
        {
            var f = fields[i]; var n = $"fields[{i}]";
            if (f.Key is null || !KeyRx.IsMatch(f.Key)) { errors[n + ".key"] = ["Use 1-64 lowercase letters, digits or underscores."]; continue; }
            if (!byKey.TryAdd(f.Key, f)) { errors[n + ".key"] = ["Duplicate key."]; continue; }
            if (string.IsNullOrWhiteSpace(f.Label) || f.Label.Length > 200) errors[n + ".label"] = ["A label of up to 200 characters is required."];
            if (f.Type is null || !Types.Contains(f.Type)) errors[n + ".type"] = ["Use yesno, text, select or file."];
            if (f.Type == "select" && (f.Options is null || f.Options.Length == 0 || f.Options.Any(o => string.IsNullOrWhiteSpace(o) || o.Length > 100) || f.Options.Distinct().Count() != f.Options.Length))
                errors[n + ".options"] = ["A select needs 1+ distinct options of up to 100 characters."];
        }
        foreach (var f in fields.Where(f => f.Key is not null && byKey.ContainsKey(f.Key) && !string.IsNullOrWhiteSpace(f.ParentKey)))
        {
            var n = $"fields[{fields.IndexOf(f)}].parentKey";
            if (!byKey.TryGetValue(f.ParentKey!, out var parent)) { errors[n] = ["Unknown parent field."]; continue; }
            if (parent.SortOrder >= f.SortOrder) errors[n] = ["The parent must come earlier (lower sort order)."];
            else if (parent.Type is not ("yesno" or "select")) errors[n] = ["Only yes/no and select fields can have branches."];
            else if (parent.Type == "yesno" && f.ParentValue is not ("yes" or "no")) errors[n] = ["For a yes/no parent the value is yes or no."];
            else if (parent.Type == "select" && (parent.Options is null || !parent.Options.Contains(f.ParentValue))) errors[n] = ["The value must be one of the parent's options."];
        }
        return errors;
    }

    public static void MapSettingsEndpoints(this RouteGroupBuilder g)
    {
        var s = g.MapGroup("/settings");

        s.AGet("", async (AppDbContext db, PlatformSettings ps, IConfiguration cfg, IWebHostEnvironment env) =>
        {
            var terms = await ps.TermsVersionAsync(); var agr = await ps.AgreementVersionAsync(); var fee = await ps.FeeBasisPointsAsync();
            static string? B(IConfiguration c, string k) => c[k];
            return Results.Ok(new
            {
                marketplace = new { feeBasisPoints = fee, feePercent = fee / 100m, overridden = await db.PlatformSettings.AnyAsync(x => x.Key == PlatformSettings.FeeKey) },
                legal = new
                {
                    termsVersion = terms, sellerAgreementVersion = agr,
                    sellersOnOlderTerms = await db.Sellers.CountAsync(x => x.TermsVersion != null && x.TermsVersion != terms),
                    sellersOnOlderAgreement = await db.Sellers.CountAsync(x => x.SellerAgreementVersion != null && x.SellerAgreementVersion != agr),
                    reacceptance = "New versions apply to every new acceptance. Existing sellers are not forced to re-accept automatically (see docs/ADMIN.md)."
                },
                // read-only view of operational switches that live in configuration (never secrets)
                featureFlags = new Dictionary<string, string?>
                {
                    ["Environment"] = env.EnvironmentName,
                    ["Database:Provider"] = B(cfg, "Database:Provider") ?? "Sqlite",
                    ["Email:Provider"] = B(cfg, "Email:Provider") ?? "Sink",
                    ["Orders:RunBackgroundJobs"] = B(cfg, "Orders:RunBackgroundJobs") ?? "true",
                    ["Bookings:RunBackgroundJobs"] = B(cfg, "Bookings:RunBackgroundJobs") ?? "true",
                    ["Phone:SendInBackground"] = B(cfg, "Phone:SendInBackground"),
                    ["Admin:IpAllowlistConfigured"] = (cfg.GetSection("Admin:IpAllowlist").GetChildren().Any()).ToString(),
                    ["Turnstile:Configured"] = (!string.IsNullOrEmpty(B(cfg, "Turnstile:SecretKey"))).ToString()
                }
            });
        }, StaffPermission.SettingsView);

        s.APut("/fee", async (FeeRequest r, HttpContext ctx, AppDbContext db, PlatformSettings ps, AdminAudit audit, TimeProvider time) =>
        {
            if (AdminHttp.NeedReason(r.Reason, out var reason) is { } bad) return bad;
            if (r.FeeBasisPoints is not (>= 0 and <= 5000)) return Validation.Invalid("feeBasisPoints", "Use 0-5000 basis points (0-50%).");
            var before = await ps.FeeBasisPointsAsync();
            await ps.SetAsync(PlatformSettings.FeeKey, r.FeeBasisPoints.Value.ToString(), ctx.Staff().UserId, time.GetUtcNow().UtcDateTime);
            await audit.WriteAsync(ctx.Staff(), "settings.fee_changed", "settings", PlatformSettings.FeeKey, reason, new { feeBasisPoints = before }, new { feeBasisPoints = r.FeeBasisPoints });
            return Results.Ok(new { feeBasisPoints = r.FeeBasisPoints, appliesTo = "orders received from now on" });
        }, StaffPermission.SettingsEdit);

        s.APut("/legal", async (LegalRequest r, HttpContext ctx, PlatformSettings ps, AdminAudit audit, TimeProvider time) =>
        {
            if (AdminHttp.NeedReason(r.Reason, out var reason) is { } bad) return bad;
            if (r.TermsVersion is null && r.SellerAgreementVersion is null) return Validation.Invalid("termsVersion", "Provide a new Terms and/or Seller Agreement version.");
            foreach (var (name, v) in new[] { ("termsVersion", r.TermsVersion), ("sellerAgreementVersion", r.SellerAgreementVersion) })
                if (v is not null && !VersionRx.IsMatch(v)) return Validation.Invalid(name, "Use up to 32 letters, digits, dots, dashes or underscores (e.g. 2026-07).");
            var t0 = await ps.TermsVersionAsync(); var a0 = await ps.AgreementVersionAsync();
            if ((r.TermsVersion is null || r.TermsVersion == t0) && (r.SellerAgreementVersion is null || r.SellerAgreementVersion == a0)) return AdminHttp.Err(409, "unchanged", "That is already the current version.");
            var now = time.GetUtcNow().UtcDateTime;
            if (r.TermsVersion is not null) await ps.SetAsync(PlatformSettings.TermsKey, r.TermsVersion, ctx.Staff().UserId, now);
            if (r.SellerAgreementVersion is not null) await ps.SetAsync(PlatformSettings.AgreementKey, r.SellerAgreementVersion, ctx.Staff().UserId, now);
            await audit.WriteAsync(ctx.Staff(), "settings.legal_version_bumped", "settings", "legal", reason,
                new { termsVersion = t0, sellerAgreementVersion = a0 }, new { termsVersion = r.TermsVersion ?? t0, sellerAgreementVersion = r.SellerAgreementVersion ?? a0 });
            return Results.Ok(new { termsVersion = r.TermsVersion ?? t0, sellerAgreementVersion = r.SellerAgreementVersion ?? a0 });
        }, StaffPermission.SettingsEdit);

        s.AGet("/seller-fields", async (AppDbContext db) =>
            Results.Ok(new { fields = (await db.SellerFieldConfigs.AsNoTracking().OrderBy(f => f.SortOrder).ToListAsync()).Select(f => new FieldDto(f.Key, f.Label, f.Type,
                f.OptionsJson is null ? null : JsonSerializer.Deserialize<string[]>(f.OptionsJson), f.ParentKey, f.ParentValue, f.Required, f.SortOrder)) }), StaffPermission.SettingsView);

        s.APost("/seller-fields/preview", (PreviewRequest r) =>
        {
            var errors = Validate(r.Fields);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var cfg = r.Fields!.Select(ToConfig).ToList();
            var visible = KosherService.VisibleKeys(cfg, r.Answers ?? new());
            return Results.Ok(new { fields = cfg.OrderBy(f => f.SortOrder).Select(f => new { f.Key, f.Label, f.Type, f.ParentKey, f.ParentValue, f.Required, visible = visible.Contains(f.Key) }) });
        }, StaffPermission.SettingsView);

        s.APut("/seller-fields", async (FieldsRequest r, HttpContext ctx, AppDbContext db, AdminAudit audit) =>
        {
            if (AdminHttp.NeedReason(r.Reason, out var reason) is { } bad) return bad;
            var errors = Validate(r.Fields);
            var existing = await db.SellerFieldConfigs.ToListAsync();
            if (errors.Count == 0 && existing.Any(e => e.Key == Protected) && r.Fields!.FirstOrDefault(f => f.Key == Protected) is not { Type: "file" })
                errors["fields"] = [$"The \"{Protected}\" file field is used by the certificate upload and must stay."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var before = existing.OrderBy(e => e.SortOrder).Select(e => new { e.Key, e.Type, e.ParentKey, e.ParentValue, e.Required, e.SortOrder }).ToList();
            var incoming = r.Fields!.Select(ToConfig).ToDictionary(c => c.Key);
            foreach (var e in existing.Where(e => !incoming.ContainsKey(e.Key))) db.SellerFieldConfigs.Remove(e);
            foreach (var (k, c) in incoming)
            {
                var row = existing.FirstOrDefault(e => e.Key == k);
                if (row is null) db.SellerFieldConfigs.Add(c);
                else { row.Label = c.Label; row.Type = c.Type; row.OptionsJson = c.OptionsJson; row.ParentKey = c.ParentKey; row.ParentValue = c.ParentValue; row.Required = c.Required; row.SortOrder = c.SortOrder; }
            }
            audit.Add(ctx.Staff(), "settings.seller_fields_changed", "settings", "seller_fields", reason, before, incoming.Values.OrderBy(c => c.SortOrder).Select(c => new { c.Key, c.Type, c.ParentKey, c.ParentValue, c.Required, c.SortOrder }));
            await db.SaveChangesAsync();
            return Results.Ok(new { fields = incoming.Count });
        }, StaffPermission.SettingsEdit);
    }
}
