using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Email;
using Solvelt.Api.Options;

namespace Solvelt.Api.Services;

public class KosherService(AppDbContext db, IFileStorage storage)
{
    public Task<List<SellerFieldConfig>> ConfigAsync() => db.SellerFieldConfigs.OrderBy(f => f.SortOrder).ToListAsync();

    public static Dictionary<string, string> Parse(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();

    public static HashSet<string> VisibleKeys(IEnumerable<SellerFieldConfig> cfg, IDictionary<string, string> answers)
    {
        var list = cfg.ToList();
        var visible = new HashSet<string>();
        foreach (var f in list.OrderBy(f => f.SortOrder))
        {
            if (f.ParentKey is null) { visible.Add(f.Key); continue; }
            if (visible.Contains(f.ParentKey) && answers.TryGetValue(f.ParentKey, out var v) && v == f.ParentValue) visible.Add(f.Key);
        }
        return visible;
    }

    /// <summary>Merges incoming answers, drops unknown keys, clears hidden answers (and deletes hidden uploads). Returns errors for invalid values.</summary>
    public async Task<(Dictionary<string, string> Answers, Dictionary<string, string> Errors)> ApplyAsync(
        SellerAccount seller, Dictionary<string, string?> incoming)
    {
        var cfg = await ConfigAsync();
        var byKey = cfg.ToDictionary(c => c.Key);
        var answers = Parse(seller.KosherAnswersJson);
        var errors = new Dictionary<string, string>();
        foreach (var (k, v) in incoming)
        {
            if (!byKey.TryGetValue(k, out var f) || f.Type == "file") continue;
            if (string.IsNullOrWhiteSpace(v)) { answers.Remove(k); continue; }
            var val = v.Trim();
            if (val.Length > 500) { errors[k] = "Too long."; continue; }
            if (f.Type == "yesno" && val is not ("yes" or "no")) { errors[k] = "Choose yes or no."; continue; }
            if (f.Type == "select" && f.OptionsJson is not null &&
                !(JsonSerializer.Deserialize<string[]>(f.OptionsJson) ?? []).Contains(val)) { errors[k] = "Choose one of the options."; continue; }
            answers[k] = val;
        }
        await ClearHiddenAsync(seller, cfg, answers);
        seller.KosherAnswersJson = JsonSerializer.Serialize(answers);
        return (answers, errors);
    }

    public async Task ClearHiddenAsync(SellerAccount seller, List<SellerFieldConfig> cfg, Dictionary<string, string> answers)
    {
        var visible = VisibleKeys(cfg, answers);
        foreach (var k in answers.Keys.ToList()) if (!visible.Contains(k)) answers.Remove(k);
        foreach (var f in cfg.Where(c => c.Type == "file" && !visible.Contains(c.Key)))
            await DeleteFilesAsync(seller.Id, f.Key);
    }

    public async Task DeleteFilesAsync(Guid sellerId, string fieldKey)
    {
        var files = await db.SellerFiles.Where(x => x.SellerId == sellerId && x.FieldKey == fieldKey).ToListAsync();
        foreach (var f in files) { await storage.DeleteAsync(f.StorageKey); db.SellerFiles.Remove(f); }
    }

    public async Task<Dictionary<string, string>> ValidateForSubmitAsync(SellerAccount seller)
    {
        var cfg = await ConfigAsync();
        var answers = Parse(seller.KosherAnswersJson);
        var visible = VisibleKeys(cfg, answers);
        var errors = new Dictionary<string, string>();
        foreach (var f in cfg.Where(c => visible.Contains(c.Key) && c.Required))
        {
            if (f.Type == "file")
            {
                if (!await db.SellerFiles.AnyAsync(x => x.SellerId == seller.Id && x.FieldKey == f.Key)) errors[f.Key] = "Required.";
            }
            else if (!answers.ContainsKey(f.Key)) errors[f.Key] = "Required.";
        }
        return errors;
    }
}

public static class FileSniffer
{
    /// <summary>Returns canonical content type if magic bytes match an allowed type; otherwise null.</summary>
    public static string? Sniff(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 5 && h[0] == 0x25 && h[1] == 0x50 && h[2] == 0x44 && h[3] == 0x46 && h[4] == 0x2D) return "application/pdf";
        if (h.Length >= 8 && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47 && h[4] == 0x0D && h[5] == 0x0A && h[6] == 0x1A && h[7] == 0x0A) return "image/png";
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return "image/jpeg";
        return null;
    }
}

public class SellerStatusService(AppDbContext db, TimeProvider time, Mailer mailer, IAuditLog audit,
    SessionService sessions, Microsoft.AspNetCore.Identity.UserManager<AppUser> users)
{
    private static readonly Dictionary<SellerStatus, SellerStatus[]> Allowed = new()
    {
        [SellerStatus.Draft] = [SellerStatus.PendingReview],
        [SellerStatus.PendingReview] = [SellerStatus.NeedsInfo, SellerStatus.Approved, SellerStatus.Rejected],
        [SellerStatus.NeedsInfo] = [SellerStatus.PendingReview, SellerStatus.Approved, SellerStatus.Rejected],
        [SellerStatus.Approved] = [SellerStatus.Live, SellerStatus.Suspended],
        [SellerStatus.Live] = [SellerStatus.Suspended],
        [SellerStatus.Suspended] = [SellerStatus.Live, SellerStatus.Approved],
        [SellerStatus.Rejected] = [SellerStatus.PendingReview]
    };

    public static bool CanTransition(SellerStatus from, SellerStatus to) => Allowed.TryGetValue(from, out var t) && t.Contains(to);

    public async Task<(bool Ok, string? Error)> ChangeAsync(SellerAccount seller, SellerStatus to, string? note, Guid? actor)
    {
        if (!CanTransition(seller.Status, to)) return (false, $"Cannot change status from {seller.Status} to {to}.");
        var from = seller.Status;
        seller.Status = to; seller.StatusNote = note; seller.UpdatedAt = time.UtcNow();
        if (to == SellerStatus.PendingReview) seller.SubmittedAt = time.UtcNow();
        await db.SaveChangesAsync();
        await audit.WriteAsync("seller.status_changed", seller.UserId, null, $"{from}->{to};actor={actor};note={(note is null ? "" : "yes")}");
        var user = await users.FindByIdAsync(seller.UserId.ToString());
        if (user?.Email is { } email)
        {
            var vendor = seller.VendorName ?? "your business";
            var msg = to switch
            {
                SellerStatus.PendingReview when from == SellerStatus.Draft => Templates.ApplicationReceived(email, vendor),
                SellerStatus.Approved => Templates.Approved(email, vendor),
                SellerStatus.NeedsInfo => Templates.NeedsInfo(email, vendor, note),
                SellerStatus.Rejected => Templates.Rejected(email, vendor),
                _ => null
            };
            if (msg is not null) await mailer.SendAsync(msg);
        }
        if (to is SellerStatus.Suspended or SellerStatus.Rejected) await sessions.RevokeAllAsync(seller.UserId, "status:" + to);
        return (true, null);
    }
}
