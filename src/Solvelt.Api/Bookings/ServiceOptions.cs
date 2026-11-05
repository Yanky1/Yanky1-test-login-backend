using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Bookings;

/// <summary>
/// The booking options of a visible service (C12, <c>GET /api/internal/v1/services/{serviceId}/options</c>): what a buyer can choose before a hold. Allow-list only:
/// no e-mail, phone, calendar data, address, meeting link or online instructions, and no ids but the opaque provider id.
/// </summary>
public static class ServiceOptions
{
    /// <summary>
    /// A person's public name derived from a sign-in address (the portal stores no other name): first name plus last initial ("John S."), and ONLY when the part before the @
    /// has at least two alphabetic tokens and the first is a real name (2+ letters). Otherwise null: a one-token local part ("jsmith", "johnsmith1985") is never published, not even partly (N14).
    /// </summary>
    public static string? PublicNameOrNull(string? email)
    {
        var local = (email ?? "").Split('@')[0];
        var tokens = System.Text.RegularExpressions.Regex.Split(local, "[^A-Za-z]+").Where(t => t.Length > 0).ToList();
        if (tokens.Count < 2 || tokens[0].Length < 2) return null;
        var first = char.ToUpperInvariant(tokens[0][0]) + tokens[0][1..].ToLowerInvariant();
        return first + " " + char.ToUpperInvariant(tokens[^1][0]) + ".";
    }

    /// <summary>Public provider label: the derived name when it is reliable, else a neutral "Provider N" (N = the provider's 1-based place in the service's provider list; never derived from the address).</summary>
    public static string PublicName(string? email, int ordinal) => PublicNameOrNull(email) ?? "Provider " + ordinal;

    public static string AssignmentOf(AssignmentRule r) => r switch { AssignmentRule.LeastBusy => "leastBusy", AssignmentRule.RoundRobin => "roundRobin", _ => "buyerChoice" };

    private static string KindOf(string type) => type switch { "yesno" => "yesNo", "choice" => "choice", _ => "text" };

    /// <summary>The options document, or null when the service is not visible in the catalog (unknown, draft, hidden, moderated, unlisted, or not a service).</summary>
    public static async Task<(string Json, long Version)?> BuildAsync(AppDbContext db, Guid serviceId, CancellationToken ct)
    {
        var p = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == serviceId && x.Type == ListingType.Service && x.CatalogListed, ct);
        var d = p is null ? null : await db.ServiceDetails.AsNoTracking().FirstOrDefaultAsync(x => x.ProductId == serviceId, ct);
        if (p is null || d is null) return null;

        var area = BJson.Area(d);
        var travel = new Dictionary<string, object?>();
        if (d.TravelFeeCents > 0) travel["feeCents"] = d.TravelFeeCents;
        if (area.PostalCodes is { Count: > 0 }) travel["serviceAreaPostalCodes"] = area.PostalCodes;
        if (area.Radius?.Distance is { } dist and > 0) travel["serviceRadiusKm"] = Math.Round(string.Equals(area.Radius.Unit, "mi", StringComparison.OrdinalIgnoreCase) ? dist * 1.609344 : dist, 2);   // the base address and coordinates are the seller's: never sent

        // eligible AND ready providers only (the same set ReadyProviders counts)
        var providers = (await (from l in db.ServiceProviders
                                join m in db.Memberships on l.MembershipId equals m.Id
                                join pp in db.ProviderProfiles on m.Id equals pp.MembershipId
                                join u in db.Users on m.UserId equals u.Id
                                where l.ProductId == serviceId && m.Status == MembershipStatus.Active && pp.IsProvider
                                    && (db.WorkingHours.Any(h => h.MembershipId == m.Id) || db.DateOverrides.Any(o => o.MembershipId == m.Id))
                                select new { m.Id, u.Email }).ToListAsync(ct))
            .OrderBy(x => x.Id).Select((x, i) => new Dictionary<string, object?> { ["providerId"] = x.Id, ["displayName"] = PublicName(x.Email, i + 1) }).ToList();   // no photo: the portal stores none

        var doc = new Dictionary<string, object?>
        {
            ["serviceId"] = serviceId, ["version"] = p.CatalogVersion,
            ["addOns"] = BJson.AddOns(d).Select(a => new { id = a.Id, name = a.Name, priceCents = a.PriceCents, extraMinutes = a.ExtraMinutes }).ToList(),
            ["intakeQuestions"] = BJson.Intake(d).Select(q =>
            {
                var o = new Dictionary<string, object?> { ["id"] = q.Id, ["label"] = q.Label, ["kind"] = KindOf(q.Type), ["required"] = q.Required };
                if (q.Type == "choice" && q.Options is { Count: > 0 }) o["choices"] = q.Options;
                return o;
            }).ToList(),
            ["providers"] = providers,
            ["assignment"] = AssignmentOf(d.AssignmentRule),
            ["policy"] = PolicyView.Of(d),
            ["travel"] = travel
        };
        return (JsonSerializer.Serialize(doc, BJson.Opts), p.CatalogVersion);
    }

    /// <summary>A strong ETag over the body: it follows what the buyer would see, even for a change that does not move the listing version.</summary>
    public static string ETagOf(string json) => "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32].ToLowerInvariant() + "\"";

    public static bool Matches(string? ifNoneMatch, string etag) =>
        !string.IsNullOrWhiteSpace(ifNoneMatch) && ifNoneMatch.Split(',').Select(x => x.Trim()).Any(x => x == "*" || x == etag || x == "W/" + etag);
}
