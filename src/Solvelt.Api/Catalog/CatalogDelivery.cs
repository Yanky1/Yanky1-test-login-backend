using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Catalog;

/// <summary>How a listing reaches the buyer, as published in the catalog (<c>delivery</c> of the listing document).</summary>
public sealed record DeliveryInfo(bool Ships, bool LocalDelivery, string[] ServiceLocations)
{
    /// <summary>Changes exactly when the published delivery options change.</summary>
    public string Signature => $"{(Ships ? 1 : 0)}{(LocalDelivery ? 1 : 0)}:{string.Join(",", ServiceLocations)}";
}

/// <summary>
/// The single definition of the delivery options of a listing. Used by the feed (always current), by the version tracker (the signature stored with each version) and by the refresh
/// job (which re-emits <c>listing.upserted</c> when a settings change flips a listing's options).
/// <list type="bullet">
/// <item><c>ships</c> (products): the account has an unarchived shipping profile with at least one zone that covers the listing's brand (account-wide, or that brand's own).</item>
/// <item><c>localDelivery</c> (products): the delivery setting that applies to the brand (the brand's own enabled setting, else the account-wide enabled one: the same rule as
/// <c>DeliveryService.SettingFor</c>) has at least one active zone.</item>
/// <item><c>serviceLocations</c> (services): the enabled location types of the service, in the fixed order online, customer, seller.</item>
/// </list>
/// </summary>
public sealed class DeliveryContext
{
    private readonly ILookup<Guid, Guid?> _profileBrands;                                   // seller -> brand scope of each usable shipping profile
    private readonly Dictionary<Guid, List<(Guid? Brand, bool ActiveZone)>> _settings;

    private DeliveryContext(ILookup<Guid, Guid?> profileBrands, Dictionary<Guid, List<(Guid?, bool)>> settings) { _profileBrands = profileBrands; _settings = settings; }

    public static async Task<DeliveryContext> LoadAsync(AppDbContext db, IReadOnlyCollection<Guid> sellerIds, CancellationToken ct)
    {
        var ids = sellerIds.ToList();
        var profiles = (await db.ShippingProfiles.AsNoTracking().Where(p => ids.Contains(p.SellerId) && !p.Archived).Select(p => new { p.SellerId, p.BrandId, p.ZonesJson }).ToListAsync(ct))
            .Where(p => HasZones(p.ZonesJson)).ToLookup(p => p.SellerId, p => p.BrandId);
        var settings = (await db.DeliverySettings.AsNoTracking().Where(s => ids.Contains(s.SellerId) && s.Enabled)
                .Select(s => new { s.SellerId, s.BrandId, Active = s.Zones.Any(z => z.Active) }).ToListAsync(ct))
            .GroupBy(s => s.SellerId).ToDictionary(g => g.Key, g => g.Select(x => (x.BrandId, x.Active)).ToList());
        return new(profiles, settings);
    }

    private static bool HasZones(string json)
    {
        try { using var d = JsonDocument.Parse(json); return d.RootElement.ValueKind == JsonValueKind.Array && d.RootElement.GetArrayLength() > 0; }
        catch (JsonException) { return false; }
    }

    public DeliveryInfo For(Product p, ServiceDetails? service)
    {
        if (p.Type == ListingType.Service)
        {
            var where = new List<string>();
            if (service?.Online == true) where.Add("online");
            if (service?.AtCustomer == true) where.Add("customer");
            if (service?.AtSeller == true) where.Add("seller");
            return new(false, false, where.ToArray());
        }
        var ships = _profileBrands[p.SellerId].Any(b => b is null || b == p.BrandId);
        var local = false;
        if (_settings.TryGetValue(p.SellerId, out var all))
        {
            var own = all.Where(s => s.Brand == p.BrandId).Select(s => ((bool, bool)?)(true, s.ActiveZone)).FirstOrDefault();
            var account = all.Where(s => s.Brand is null).Select(s => ((bool, bool)?)(true, s.ActiveZone)).FirstOrDefault();
            local = (own ?? account)?.Item2 ?? false;                                      // the brand's own enabled setting wins, even when it has no active zone
        }
        return new(ships, local, []);
    }

    /// <summary>Delivery signature per product from the CURRENT database state (service details: tracked edits win over stored ones).</summary>
    public static async Task<Dictionary<Guid, DeliveryInfo>> ForProductsAsync(AppDbContext db, IReadOnlyCollection<Product> products, CancellationToken ct)
    {
        var res = new Dictionary<Guid, DeliveryInfo>();
        if (products.Count == 0) return res;
        var ctx = await LoadAsync(db, products.Select(p => p.SellerId).Distinct().ToList(), ct);
        var serviceIds = products.Where(p => p.Type == ListingType.Service).Select(p => p.Id).ToList();
        var services = serviceIds.Count == 0 ? [] : (await db.ServiceDetails.AsNoTracking().Where(s => serviceIds.Contains(s.ProductId)).ToListAsync(ct)).ToDictionary(s => s.ProductId);
        foreach (var e in db.ChangeTracker.Entries<ServiceDetails>().Where(e => serviceIds.Contains(e.Entity.ProductId) && e.State is not (EntityState.Detached or EntityState.Deleted)))
            services[e.Entity.ProductId] = e.Entity;
        foreach (var p in products) res[p.Id] = ctx.For(p, services.GetValueOrDefault(p.Id));
        return res;
    }
}
