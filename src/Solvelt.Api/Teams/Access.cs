using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Services;

namespace Solvelt.Api.Teams;

public static class AccessExtensions
{
    private const string Key = "solvelt.account-access";

    /// <summary>The validated active account for this request (null: no account selected, or the membership is gone/suspended).</summary>
    public static AccountAccess? GetAccess(this HttpContext ctx) => ctx.Items[Key] as AccountAccess;
    public static void SetAccess(this HttpContext ctx, AccountAccess a) => ctx.Items[Key] = a;

    /// <summary>Brands the caller may see in the active account (account + scope enforced). Use this for EVERY brand/product query.</summary>
    public static IQueryable<Brand> BrandsFor(this AppDbContext db, AccountAccess a)
    {
        var q = db.Brands.Where(b => b.SellerId == a.SellerId);
        if (a.AllBrands || a.Role >= AccountRole.Admin) return q;
        var ids = a.BrandIds.ToList();
        return q.Where(b => ids.Contains(b.Id));
    }

    // ---- Endpoint filters --------------------------------------------------------------------------------------------------
    public static TBuilder RequirePermission<TBuilder>(this TBuilder b, Permission p) where TBuilder : IEndpointConventionBuilder =>
        b.AddEndpointFilter(async (ctx, next) =>
        {
            var a = ctx.HttpContext.GetAccess();
            if (a is null) return NoAccount();
            if (!a.Has(p)) return Forbidden();
            return await next(ctx);
        });

    /// <summary>Requires at least the given role in the active account.</summary>
    public static TBuilder RequireAccountRole<TBuilder>(this TBuilder b, AccountRole minimum) where TBuilder : IEndpointConventionBuilder =>
        b.AddEndpointFilter(async (ctx, next) =>
        {
            var a = ctx.HttpContext.GetAccess();
            if (a is null) return NoAccount();
            if (a.Role < minimum) return Forbidden();
            return await next(ctx);
        });

    /// <summary>Requires any valid active account (membership checked against the database on this very request).</summary>
    public static TBuilder RequireActiveAccount<TBuilder>(this TBuilder b) where TBuilder : IEndpointConventionBuilder =>
        b.AddEndpointFilter(async (ctx, next) => ctx.HttpContext.GetAccess() is null ? NoAccount() : await next(ctx));

    /// <summary>
    /// Brand guard for handlers: loads the brand ONLY if it belongs to the active account and is in the caller's scope; otherwise null (answer 404, never 403,
    /// so other accounts' / out-of-scope brands are indistinguishable from missing ones). Pass write:true for mutations of brand content.
    /// </summary>
    public static async Task<Brand?> RequireBrandAccess(this AppDbContext db, AccountAccess a, Guid brandId, bool write = false)
    {
        var brand = await db.BrandsFor(a).FirstOrDefaultAsync(x => x.Id == brandId);
        if (brand is null) return null;
        return a.CanWorkOnBrand(brand.Id, write) ? brand : null;
    }

    public static IResult NoAccount() => Results.Json(new { type = "about:blank", title = "No active seller account", status = 403, code = "no_active_account" }, statusCode: 403);
    public static IResult Forbidden() => Results.Json(new { type = "about:blank", title = "You don't have permission to do this", status = 403, code = "forbidden" }, statusCode: 403);
}

/// <summary>Resolves memberships into <see cref="AccountAccess"/>, picks the active account, lists a user's accounts.</summary>
public class AccountContextService(AppDbContext db, TimeProvider time)
{
    public static bool Usable(SellerStatus s) => s is not (SellerStatus.Rejected or SellerStatus.Suspended or SellerStatus.Closed);

    public async Task<AccountAccess?> ResolveAsync(Guid userId, Guid sellerId)
    {
        var row = await (from m in db.Memberships.AsNoTracking()
                         join s in db.Sellers.AsNoTracking() on m.SellerId equals s.Id
                         where m.UserId == userId && m.SellerId == sellerId && m.Status == MembershipStatus.Active
                         select new { m, s.Status }).FirstOrDefaultAsync();
        if (row is null || !Usable(row.Status)) return null;
        var ids = row.m.AllBrands ? [] : await db.MembershipBrands.Where(x => x.MembershipId == row.m.Id).Select(x => x.BrandId).ToListAsync();
        return new AccountAccess(userId, sellerId, row.m.Id, row.m.Role, row.m.AllBrands || row.m.Role >= AccountRole.Admin, ids.ToHashSet(), row.Status);
    }

    public record AccountSummary(Guid SellerId, string VendorName, string Role, string AccountStatus, int BrandCount, string MembershipStatus);

    /// <summary>All of a user's accounts (active memberships only), most recently used first.</summary>
    public async Task<List<AccountSummary>> ListAsync(Guid userId, Guid? lastUsed = null)
    {
        var rows = await (from m in db.Memberships.AsNoTracking()
                          join s in db.Sellers.AsNoTracking() on m.SellerId equals s.Id
                          where m.UserId == userId && m.Status == MembershipStatus.Active && s.Status != SellerStatus.Closed // closed accounts are gone for members
                          select new { m.Id, m.SellerId, m.Role, m.AllBrands, m.LastActiveAt, s.VendorName, s.Status }).ToListAsync();
        var result = new List<(AccountSummary S, DateTime? At)>();
        foreach (var r in rows)
        {
            var q = db.Brands.AsNoTracking().Where(b => b.SellerId == r.SellerId && b.Status == BrandStatus.Active);
            var n = r.Role >= AccountRole.Admin || r.AllBrands
                ? await q.CountAsync()
                : await q.Where(b => db.MembershipBrands.Any(mb => mb.MembershipId == r.Id && mb.BrandId == b.Id)).CountAsync();
            result.Add((new AccountSummary(r.SellerId, r.VendorName ?? "", r.Role.ToString(), r.Status.ToString(), n, "Active"), r.LastActiveAt));
        }
        return result.OrderByDescending(x => x.S.SellerId == lastUsed).ThenByDescending(x => x.At ?? DateTime.MinValue)
            .ThenBy(x => x.S.VendorName).Select(x => x.S).ToList();
    }

    /// <summary>
    /// Keeps the session's active account if it is still valid; otherwise auto-selects: exactly one usable account, else the user's last-used one.
    /// Returns null when the user has to choose (several accounts) or has none.
    /// </summary>
    public async Task<Guid?> EnsureActiveAsync(AppUser user, UserSession session)
    {
        if (session.ActiveAccountId is { } cur && await ResolveAsync(user.Id, cur) is not null) return cur;
        var usable = new List<Guid>();
        foreach (var a in await ListAsync(user.Id, user.LastAccountId))
            if (Usable(Enum.Parse<SellerStatus>(a.AccountStatus))) usable.Add(a.SellerId);
        Guid? pick = null;
        if (usable.Count == 1) pick = usable[0];
        else if (user.LastAccountId is { } last && usable.Contains(last)) pick = last;
        if (session.ActiveAccountId != pick) { session.ActiveAccountId = pick; await db.SaveChangesAsync(); }
        return pick;
    }

    public async Task SetActiveAsync(AppUser user, UserSession session, Guid sellerId)
    {
        session.ActiveAccountId = sellerId;
        user.LastAccountId = sellerId;
        await db.Memberships.Where(m => m.UserId == user.Id && m.SellerId == sellerId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastActiveAt, time.UtcNow()));
        await db.SaveChangesAsync();
    }
}
