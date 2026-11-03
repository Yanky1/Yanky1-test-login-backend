using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Email;
using Solvelt.Api.Options;
using Solvelt.Api.Services;

namespace Solvelt.Api.Teams;

public record TeamResult(bool Ok, int Status = 200, string? Code = null, string? Message = null, object? Data = null)
{
    public static TeamResult Success(object? data = null) => new(true, 200, null, null, data);
    public static TeamResult Fail(int status, string code, string message) => new(false, status, code, message);
    public IResult ToHttp() => Ok ? (Data is null ? Results.NoContent() : Results.Ok(Data))
        : Results.Json(new { type = "about:blank", title = Message, status = Status, code = Code }, statusCode: Status);
}

public static partial class TeamRules
{
    public const int InviteDays = 7;
    public const int InvitesPerAccountPerHour = 30;
    public const int InvitesPerInviterPerHour = 20;
    public const int ResendCooldownSeconds = 60;
    public const int MaxActiveInvites = 100;

    [GeneratedRegex("[^a-z0-9]+")] private static partial Regex NonSlug();
    public static string Slugify(string name)
    {
        var s = NonSlug().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        return s.Length == 0 ? "brand" : (s.Length > 60 ? s[..60].Trim('-') : s);
    }
}

public class TeamService(AppDbContext db, TimeProvider time, IAuditLog audit, Mailer mailer, UserManager<AppUser> users,
    IOptions<AppOptions> app)
{
    // ------------------------------------------------------------------ accounts / brands

    /// <summary>Creates the seller account, the Owner membership and the default brand (the user is added to the portal "Seller" role by the caller).</summary>
    public SellerAccount NewAccount(Guid ownerUserId, int draftStep, string? termsVersion, DateTime? termsAt, string? vendorName = null)
    {
        var now = time.UtcNow();
        var acc = new SellerAccount
        {
            UserId = ownerUserId, DraftStep = draftStep, CreatedAt = now, UpdatedAt = now,
            TermsVersion = termsVersion, TermsAcceptedAt = termsAt, VendorName = vendorName
        };
        db.Sellers.Add(acc);
        db.Memberships.Add(new AccountMembership
        {
            SellerId = acc.Id, UserId = ownerUserId, Role = AccountRole.Owner, AllBrands = true, CreatedAt = now, UpdatedAt = now
        });
        db.Brands.Add(new Brand
        {
            SellerId = acc.Id, Name = DefaultBrandName(vendorName), Slug = TeamRules.Slugify(DefaultBrandName(vendorName)),
            IsDefault = true, CreatedAt = now, UpdatedAt = now
        });
        return acc;
    }

    public static string DefaultBrandName(string? vendor) => string.IsNullOrWhiteSpace(vendor) ? "Main brand" : (vendor.Trim().Length > 80 ? vendor.Trim()[..80] : vendor.Trim());

    /// <summary>The wizard saved a vendor name: rename the auto-created default brand (if still untouched).</summary>
    public async Task SyncDefaultBrandAsync(SellerAccount acc)
    {
        var brand = await db.Brands.FirstOrDefaultAsync(b => b.SellerId == acc.Id && b.IsDefault);
        if (brand is null || string.IsNullOrWhiteSpace(acc.VendorName)) return;
        var name = DefaultBrandName(acc.VendorName);
        if (brand.Name == name || await db.Brands.AnyAsync(b => b.SellerId == acc.Id && b.Name == name && b.Id != brand.Id)) return;
        brand.Name = name; brand.Slug = await UniqueSlugAsync(acc.Id, name, brand.Id); brand.UpdatedAt = time.UtcNow();
    }

    public async Task<string> UniqueSlugAsync(Guid sellerId, string name, Guid? except = null)
    {
        var baseSlug = TeamRules.Slugify(name);
        var slug = baseSlug;
        for (var i = 2; await db.Brands.AnyAsync(b => b.SellerId == sellerId && b.Slug == slug && b.Id != except); i++) slug = $"{baseSlug}-{i}";
        return slug;
    }

    /// <summary>Idempotent data backfill (runs at startup): every seller account gets an Owner membership for its UserId and a default brand.</summary>
    public static async Task BackfillAsync(AppDbContext db, TimeProvider time)
    {
        var now = time.UtcNow();
        var withoutOwner = await db.Sellers.Where(s => !db.Memberships.Any(m => m.SellerId == s.Id && m.UserId == s.UserId)).ToListAsync();
        foreach (var s in withoutOwner)
            db.Memberships.Add(new AccountMembership { SellerId = s.Id, UserId = s.UserId, Role = AccountRole.Owner, AllBrands = true, CreatedAt = now, UpdatedAt = now });
        var withoutBrand = await db.Sellers.Where(s => !db.Brands.Any(b => b.SellerId == s.Id)).ToListAsync();
        foreach (var s in withoutBrand)
        {
            var n = DefaultBrandName(s.VendorName);
            db.Brands.Add(new Brand { SellerId = s.Id, Name = n, Slug = TeamRules.Slugify(n), IsDefault = true, CreatedAt = now, UpdatedAt = now });
        }
        if (withoutOwner.Count + withoutBrand.Count > 0) await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ helpers

    private async Task<SellerAccount?> AccountAsync(Guid id) => await db.Sellers.FirstOrDefaultAsync(s => s.Id == id);
    private static string AccountName(SellerAccount s) => string.IsNullOrWhiteSpace(s.VendorName) ? "a seller account" : s.VendorName!;
    private string Link(string token) => $"{app.Value.FrontendBaseUrl.TrimEnd('/')}/invite/{token}";

    public async Task<string> DisplayNameAsync(Guid userId) => (await users.FindByIdAsync(userId.ToString()))?.Email ?? "A team member";

    private async Task<int> ActiveOwnersAsync(Guid sellerId) =>
        await db.Memberships.CountAsync(m => m.SellerId == sellerId && m.Role == AccountRole.Owner && m.Status == MembershipStatus.Active);

    /// <summary>Validates a role + scope request. Returns an error result or null; fills the normalized scope.</summary>
    private async Task<(TeamResult? Error, bool AllBrands, List<Guid> Brands)> NormalizeScopeAsync(Guid sellerId, AccountRole role, bool? allBrands, IEnumerable<Guid>? brandIds)
    {
        if (role >= AccountRole.Admin) return (null, true, []);
        var ids = (brandIds ?? []).Distinct().ToList();
        var all = allBrands ?? ids.Count == 0;
        if (all) return (null, true, []);
        if (ids.Count == 0) return (TeamResult.Fail(400, "brand_scope_required", "Choose at least one brand, or give access to all brands."), false, []);
        var valid = await db.Brands.CountAsync(b => b.SellerId == sellerId && ids.Contains(b.Id));
        if (valid != ids.Count) return (TeamResult.Fail(400, "invalid_brand", "One of the brands doesn't belong to this account."), false, []);
        return (null, false, ids);
    }

    public static bool TryRole(string? s, out AccountRole r) => Enum.TryParse(s, true, out r) && Enum.IsDefined(r);

    private async Task<string[]?> BrandNamesAsync(bool all, IEnumerable<Guid> ids)
    {
        if (all) return null;
        var l = ids.ToList();
        return await db.Brands.Where(b => l.Contains(b.Id)).Select(b => b.Name).ToArrayAsync();
    }

    // ------------------------------------------------------------------ members

    public async Task<object> ListMembersAsync(AccountAccess a)
    {
        var rows = await (from m in db.Memberships.AsNoTracking()
                          join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                          where m.SellerId == a.SellerId
                          orderby m.Role descending, u.Email
                          select new { m, u.Email }).ToListAsync();
        var scopes = await db.MembershipBrands.AsNoTracking().Where(x => db.Memberships.Any(m => m.Id == x.MembershipId && m.SellerId == a.SellerId)).ToListAsync();
        var brandNames = await db.Brands.AsNoTracking().Where(b => b.SellerId == a.SellerId).ToDictionaryAsync(b => b.Id, b => b.Name);
        return rows.Select(r => new
        {
            id = r.m.Id, userId = r.m.UserId, email = r.Email, role = r.m.Role.ToString(), status = r.m.Status.ToString(),
            allBrands = r.m.AllBrands || r.m.Role >= AccountRole.Admin,
            brands = scopes.Where(s => s.MembershipId == r.m.Id).Select(s => new { id = s.BrandId, name = brandNames.GetValueOrDefault(s.BrandId, "") }),
            createdAt = r.m.CreatedAt, lastActiveAt = r.m.LastActiveAt, isMe = r.m.UserId == a.UserId
        });
    }

    public async Task<TeamResult> ChangeMemberAsync(AccountAccess actor, Guid memberId, AccountRole? newRole, bool? allBrands, IEnumerable<Guid>? brandIds, MembershipStatus? status)
    {
        var m = await db.Memberships.Include(x => x.Brands).FirstOrDefaultAsync(x => x.Id == memberId && x.SellerId == actor.SellerId);
        if (m is null) return TeamResult.Fail(404, "not_found", "Member not found.");
        if (m.UserId == actor.UserId) return TeamResult.Fail(403, "self_edit", "You can't change your own role or access.");
        if (m.Role > actor.Role) return TeamResult.Fail(403, "forbidden", "You can't change someone with a higher role than yours.");
        if (m.Role == AccountRole.Owner && actor.Role != AccountRole.Owner) return TeamResult.Fail(403, "forbidden", "Only an Owner can change an Owner.");
        var role = newRole ?? m.Role;
        if (role > actor.Role || (role == AccountRole.Owner && actor.Role != AccountRole.Owner))
            return TeamResult.Fail(403, "forbidden", "You can't grant a role higher than your own.");
        var newStatus = status ?? m.Status;
        var wasActiveOwner = m.Role == AccountRole.Owner && m.Status == MembershipStatus.Active;
        var staysActiveOwner = role == AccountRole.Owner && newStatus == MembershipStatus.Active;
        if (wasActiveOwner && !staysActiveOwner && await ActiveOwnersAsync(actor.SellerId) <= 1)
            return TeamResult.Fail(409, "last_owner", "An account must always have at least one Owner.");
        if (allBrands is null && brandIds is null) { allBrands = m.AllBrands; brandIds = m.Brands.Select(x => x.BrandId).ToList(); }
        var (err, all, ids) = await NormalizeScopeAsync(actor.SellerId, role, allBrands, brandIds);
        if (err is not null) return err;
        var changedRole = role != m.Role;
        var oldScope = (m.AllBrands, m.Brands.Select(b => b.BrandId).OrderBy(x => x).ToList());
        m.Role = role; m.Status = newStatus; m.AllBrands = all; m.UpdatedAt = time.UtcNow();
        db.MembershipBrands.RemoveRange(m.Brands);
        m.Brands = ids.Select(i => new MembershipBrand { MembershipId = m.Id, BrandId = i }).ToList();
        await db.SaveChangesAsync();
        var scopeChanged = oldScope.AllBrands != all || !oldScope.Item2.SequenceEqual(ids.OrderBy(x => x));
        await audit.WriteAsync(changedRole ? "team.role_changed" : scopeChanged ? "team.scope_changed" : "team.member_updated", actor.UserId, null,
            $"account={actor.SellerId};member={m.UserId};role={role};all={all};brands={string.Join(',', ids)};status={newStatus}");
        if (changedRole || scopeChanged)
            if (await users.FindByIdAsync(m.UserId.ToString()) is { Email: { } email } && (await AccountAsync(actor.SellerId)) is { } acc)
                await mailer.SendAsync(TeamTemplates.RoleChanged(email, AccountName(acc), role.ToString(), await BrandNamesAsync(all, ids)));
        return TeamResult.Success(new { id = m.Id, role = role.ToString(), status = newStatus.ToString(), allBrands = all, brandIds = ids });
    }

    public async Task<TeamResult> RemoveMemberAsync(AccountAccess actor, Guid memberId)
    {
        var m = await db.Memberships.FirstOrDefaultAsync(x => x.Id == memberId && x.SellerId == actor.SellerId);
        if (m is null) return TeamResult.Fail(404, "not_found", "Member not found.");
        if (m.UserId == actor.UserId) return TeamResult.Fail(403, "self_edit", "Use \"Leave account\" to remove yourself.");
        if (m.Role > actor.Role || (m.Role == AccountRole.Owner && actor.Role != AccountRole.Owner))
            return TeamResult.Fail(403, "forbidden", "You can't remove someone with a higher role than yours.");
        return await DeleteMembershipAsync(m, actor.UserId, "team.member_removed", notify: true);
    }

    public async Task<TeamResult> LeaveAsync(AccountAccess me)
    {
        var m = await db.Memberships.FirstOrDefaultAsync(x => x.Id == me.MembershipId);
        if (m is null) return TeamResult.Fail(404, "not_found", "Membership not found.");
        return await DeleteMembershipAsync(m, me.UserId, "team.member_left", notify: false);
    }

    private async Task<TeamResult> DeleteMembershipAsync(AccountMembership m, Guid actorId, string auditType, bool notify)
    {
        if (m.Role == AccountRole.Owner && m.Status == MembershipStatus.Active && await ActiveOwnersAsync(m.SellerId) <= 1)
            return TeamResult.Fail(409, "last_owner", "An account must always have at least one Owner. Transfer ownership first.");
        db.Memberships.Remove(m);
        // Access to THIS account ends immediately: sessions acting on it lose the active account (checked again on every request anyway).
        await db.Sessions.Where(s => s.UserId == m.UserId && s.ActiveAccountId == m.SellerId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ActiveAccountId, (Guid?)null));
        await db.Users.Where(u => u.Id == m.UserId && u.LastAccountId == m.SellerId).ExecuteUpdateAsync(u => u.SetProperty(x => x.LastAccountId, (Guid?)null));
        await db.SaveChangesAsync();
        await audit.WriteAsync(auditType, actorId, null, $"account={m.SellerId};member={m.UserId};role={m.Role}");
        if (notify && await users.FindByIdAsync(m.UserId.ToString()) is { Email: { } email } && await AccountAsync(m.SellerId) is { } acc)
            await mailer.SendAsync(TeamTemplates.Removed(email, AccountName(acc)));
        return TeamResult.Success();
    }

    public async Task<TeamResult> TransferOwnershipAsync(AccountAccess actor, Guid memberId)
    {
        var target = await db.Memberships.FirstOrDefaultAsync(x => x.Id == memberId && x.SellerId == actor.SellerId && x.Status == MembershipStatus.Active);
        if (target is null) return TeamResult.Fail(404, "not_found", "Member not found.");
        if (target.UserId == actor.UserId) return TeamResult.Fail(400, "self", "Choose another member.");
        var me = await db.Memberships.FirstAsync(x => x.Id == actor.MembershipId);
        target.Role = AccountRole.Owner; target.AllBrands = true; target.UpdatedAt = time.UtcNow();
        db.MembershipBrands.RemoveRange(db.MembershipBrands.Where(x => x.MembershipId == target.Id));
        me.Role = AccountRole.Admin; me.UpdatedAt = time.UtcNow();
        var acc = await AccountAsync(actor.SellerId);
        if (acc is not null) acc.UserId = target.UserId; // SellerAccount.UserId = the primary owner (kept for legacy code paths)
        await db.SaveChangesAsync();
        await audit.WriteAsync("team.ownership_transferred", actor.UserId, null, $"account={actor.SellerId};to={target.UserId}");
        if (acc is not null)
        {
            var newOwner = await DisplayNameAsync(target.UserId);
            if ((await users.FindByIdAsync(target.UserId.ToString()))?.Email is { } te) await mailer.SendAsync(TeamTemplates.OwnershipTransferred(te, AccountName(acc), newOwner, true));
            if ((await users.FindByIdAsync(actor.UserId.ToString()))?.Email is { } oe) await mailer.SendAsync(TeamTemplates.OwnershipTransferred(oe, AccountName(acc), newOwner, false));
        }
        return TeamResult.Success(new { ownerMemberId = target.Id, yourRole = "Admin" });
    }

    // ------------------------------------------------------------------ invitations

    public object InviteDto(AccountInvitation i, Dictionary<Guid, string> brandNames, DateTime now) => new
    {
        id = i.Id, email = i.Email, role = i.Role.ToString(), allBrands = i.AllBrands,
        brands = JsonSerializer.Deserialize<List<Guid>>(i.BrandIdsJson)!.Select(b => new { id = b, name = brandNames.GetValueOrDefault(b, "") }),
        status = i.Status == InviteStatus.Pending && i.ExpiresAt <= now ? "Expired" : i.Status.ToString(),
        createdAt = i.CreatedAt, expiresAt = i.ExpiresAt, lastSentAt = i.LastSentAt, sendCount = i.SendCount
    };

    public async Task<object> ListInvitesAsync(AccountAccess a)
    {
        var now = time.UtcNow();
        var list = await db.Invitations.AsNoTracking().Where(i => i.SellerId == a.SellerId && i.Status == InviteStatus.Pending).OrderByDescending(i => i.CreatedAt).ToListAsync();
        var names = await db.Brands.AsNoTracking().Where(b => b.SellerId == a.SellerId).ToDictionaryAsync(b => b.Id, b => b.Name);
        return list.Select(i => InviteDto(i, names, now));
    }

    private async Task<TeamResult?> ThrottleAsync(Guid sellerId, Guid inviter)
    {
        var since = time.UtcNow().AddHours(-1);
        var tag = $"account={sellerId}";
        var perAccount = await db.AuditEvents.CountAsync(e => e.Type == "invite.sent" && e.At >= since && e.Detail!.StartsWith(tag));
        var perInviter = await db.AuditEvents.CountAsync(e => e.Type == "invite.sent" && e.At >= since && e.UserId == inviter);
        return perAccount >= TeamRules.InvitesPerAccountPerHour || perInviter >= TeamRules.InvitesPerInviterPerHour
            ? TeamResult.Fail(429, "invite_rate_limited", "Too many invitations sent recently. Try again later.") : null;
    }

    /// <summary>Creates or refreshes the pending invite for (account, email). The response never depends on whether the email has a Solvelt login.</summary>
    public async Task<TeamResult> InviteAsync(AccountAccess actor, string email, AccountRole role, bool? allBrands, IEnumerable<Guid>? brandIds)
    {
        if (role == AccountRole.Owner) return TeamResult.Fail(403, "forbidden", "Owners can't be invited. Transfer ownership instead.");
        if (role > actor.Role) return TeamResult.Fail(403, "forbidden", "You can't invite someone to a higher role than yours.");
        email = Crypto.NormalizeEmail(email);
        var (err, all, ids) = await NormalizeScopeAsync(actor.SellerId, role, allBrands, brandIds);
        if (err is not null) return err;
        if (await ThrottleAsync(actor.SellerId, actor.UserId) is { } t) return t;
        var existingUser = await users.FindByEmailAsync(email);
        if (existingUser is not null && await db.Memberships.AnyAsync(m => m.SellerId == actor.SellerId && m.UserId == existingUser.Id))
            return TeamResult.Fail(409, "already_member", "This person is already a member of the account.");
        var now = time.UtcNow();
        var inv = await db.Invitations.FirstOrDefaultAsync(i => i.SellerId == actor.SellerId && i.Email == email && i.Status == InviteStatus.Pending);
        if (inv is null)
        {
            if (await db.Invitations.CountAsync(i => i.SellerId == actor.SellerId && i.Status == InviteStatus.Pending) >= TeamRules.MaxActiveInvites)
                return TeamResult.Fail(409, "too_many_pending", "Too many pending invitations. Revoke some first.");
            inv = new AccountInvitation { SellerId = actor.SellerId, Email = email, InvitedBy = actor.UserId, CreatedAt = now };
            db.Invitations.Add(inv);
        }
        else if ((now - inv.LastSentAt).TotalSeconds < TeamRules.ResendCooldownSeconds)
            return TeamResult.Fail(429, "resend_too_soon", "This person was invited a moment ago. Try again shortly.");
        else inv.SendCount++;
        inv.Role = role; inv.AllBrands = all; inv.BrandIdsJson = JsonSerializer.Serialize(ids);
        inv.InvitedBy = actor.UserId;
        await SendInviteAsync(inv, actor.UserId, "invite.created");
        return TeamResult.Success(new { id = inv.Id, email, message = "Invitation sent." });
    }

    public async Task<TeamResult> ResendAsync(AccountAccess actor, Guid inviteId)
    {
        var inv = await db.Invitations.FirstOrDefaultAsync(i => i.Id == inviteId && i.SellerId == actor.SellerId && i.Status == InviteStatus.Pending);
        if (inv is null) return TeamResult.Fail(404, "not_found", "Invitation not found.");
        if (inv.Role > actor.Role) return TeamResult.Fail(403, "forbidden", "You can't manage this invitation.");
        if ((time.UtcNow() - inv.LastSentAt).TotalSeconds < TeamRules.ResendCooldownSeconds)
            return TeamResult.Fail(429, "resend_too_soon", "This invitation was sent a moment ago. Try again shortly.");
        if (await ThrottleAsync(actor.SellerId, actor.UserId) is { } t) return t;
        inv.SendCount++;
        await SendInviteAsync(inv, actor.UserId, "invite.resent");
        return TeamResult.Success(new { id = inv.Id, message = "Invitation sent again." });
    }

    /// <summary>(Re)issues a fresh random token (only its hash is stored, so a resend rotates the link), extends the expiry, emails it.</summary>
    private async Task SendInviteAsync(AccountInvitation inv, Guid actorId, string auditType)
    {
        var now = time.UtcNow();
        var token = Crypto.RandomToken();
        inv.TokenHash = Crypto.Sha256(token);
        inv.ExpiresAt = now.AddDays(TeamRules.InviteDays); inv.LastSentAt = now;
        await db.SaveChangesAsync();
        var acc = await AccountAsync(inv.SellerId);
        var ids = JsonSerializer.Deserialize<List<Guid>>(inv.BrandIdsJson)!;
        await mailer.SendAsync(TeamTemplates.Invitation(inv.Email, acc is null ? "a seller account" : AccountName(acc), await DisplayNameAsync(actorId),
            inv.Role.ToString(), await BrandNamesAsync(inv.AllBrands, ids), Link(token), inv.ExpiresAt));
        await audit.WriteAsync(auditType, actorId, null, $"account={inv.SellerId};invite={inv.Id};role={inv.Role};to={AuditLog.Mask(inv.Email)}");
        await audit.WriteAsync("invite.sent", actorId, null, $"account={inv.SellerId};invite={inv.Id}");
    }

    public async Task<TeamResult> RevokeAsync(AccountAccess actor, Guid inviteId)
    {
        var inv = await db.Invitations.FirstOrDefaultAsync(i => i.Id == inviteId && i.SellerId == actor.SellerId && i.Status == InviteStatus.Pending);
        if (inv is null) return TeamResult.Fail(404, "not_found", "Invitation not found.");
        if (inv.Role > actor.Role) return TeamResult.Fail(403, "forbidden", "You can't manage this invitation.");
        inv.Status = InviteStatus.Revoked; inv.ResolvedAt = time.UtcNow();
        await db.SaveChangesAsync();
        await audit.WriteAsync("invite.revoked", actor.UserId, null, $"account={actor.SellerId};invite={inv.Id}");
        return TeamResult.Success();
    }

    public Task<AccountInvitation?> FindByTokenAsync(string token) =>
        string.IsNullOrWhiteSpace(token) || token.Length > 100 ? Task.FromResult<AccountInvitation?>(null)
            : db.Invitations.FirstOrDefaultAsync(i => i.TokenHash == Crypto.Sha256(token));

    public string StateOf(AccountInvitation i) => i.Status switch
    {
        InviteStatus.Pending => i.ExpiresAt <= time.UtcNow() ? "expired" : "pending",
        InviteStatus.Accepted => "accepted", InviteStatus.Declined => "declined", _ => "revoked"
    };

    public async Task<object> PreviewAsync(AccountInvitation i)
    {
        var acc = await AccountAsync(i.SellerId);
        var ids = JsonSerializer.Deserialize<List<Guid>>(i.BrandIdsJson)!;
        return new
        {
            state = StateOf(i), email = i.Email, accountName = acc is null ? "" : AccountName(acc), role = i.Role.ToString(),
            allBrands = i.AllBrands, brands = await BrandNamesAsync(i.AllBrands, ids) ?? [], expiresAt = i.ExpiresAt
        };
    }

    public async Task<IEnumerable<object>> PendingForAsync(AppUser user)
    {
        var email = Crypto.NormalizeEmail(user.Email ?? "");
        var now = time.UtcNow();
        if (!user.EmailConfirmed) return [];
        var list = await (from i in db.Invitations.AsNoTracking() join s in db.Sellers.AsNoTracking() on i.SellerId equals s.Id
                          where i.Email == email && i.Status == InviteStatus.Pending && i.ExpiresAt > now
                          orderby i.CreatedAt descending select new { i, s.VendorName }).ToListAsync();
        return list.Select(x => new { id = x.i.Id, accountName = x.VendorName ?? "", role = x.i.Role.ToString(), expiresAt = x.i.ExpiresAt });
    }

    /// <summary>Accepts for an authenticated user: their VERIFIED email must equal the invited one.</summary>
    public async Task<TeamResult> AcceptAsync(AccountInvitation inv, AppUser user)
    {
        var state = StateOf(inv);
        if (state != "pending") return TeamResult.Fail(410, "invite_" + state, state switch
        { "expired" => "This invitation has expired. Ask for a new one.", "revoked" => "This invitation was withdrawn.", _ => "This invitation was already used." });
        if (!user.EmailConfirmed || Crypto.NormalizeEmail(user.Email ?? "") != inv.Email)
        {
            await audit.WriteAsync("invite.accept_refused", user.Id, null, $"invite={inv.Id};reason=email_mismatch");
            return TeamResult.Fail(403, "email_mismatch", "This invitation was sent to a different email address. Sign in with the invited address to accept it.");
        }
        var now = time.UtcNow();
        var ids = JsonSerializer.Deserialize<List<Guid>>(inv.BrandIdsJson)!;
        var valid = await db.Brands.Where(b => b.SellerId == inv.SellerId && ids.Contains(b.Id)).Select(b => b.Id).ToListAsync();
        var all = inv.AllBrands || inv.Role >= AccountRole.Admin || valid.Count == 0;
        var m = await db.Memberships.Include(x => x.Brands).FirstOrDefaultAsync(x => x.SellerId == inv.SellerId && x.UserId == user.Id);
        if (m is null)
        {
            m = new AccountMembership { SellerId = inv.SellerId, UserId = user.Id, CreatedAt = now };
            db.Memberships.Add(m);
        }
        else db.MembershipBrands.RemoveRange(m.Brands);
        m.Role = inv.Role; m.Status = MembershipStatus.Active; m.AllBrands = all; m.InvitedBy = inv.InvitedBy; m.UpdatedAt = now;
        m.Brands = all ? [] : valid.Select(b => new MembershipBrand { MembershipId = m.Id, BrandId = b }).ToList();
        inv.Status = InviteStatus.Accepted; inv.ResolvedAt = now; inv.AcceptedUserId = user.Id;
        if (!await users.IsInRoleAsync(user, "Seller")) await users.AddToRoleAsync(user, "Seller");
        await db.SaveChangesAsync();
        await audit.WriteAsync("invite.accepted", user.Id, user.Email, $"account={inv.SellerId};invite={inv.Id};role={inv.Role}");
        var acc = await AccountAsync(inv.SellerId);
        if (acc is not null && (await users.FindByIdAsync(inv.InvitedBy.ToString()))?.Email is { } inviterEmail)
            await mailer.SendAsync(TeamTemplates.InviteAccepted(inviterEmail, AccountName(acc), user.Email!, inv.Role.ToString()));
        return TeamResult.Success(new { sellerId = inv.SellerId, role = inv.Role.ToString() });
    }

    public async Task<TeamResult> DeclineAsync(AccountInvitation inv)
    {
        if (inv.Status != InviteStatus.Pending) return TeamResult.Success();
        inv.Status = InviteStatus.Declined; inv.ResolvedAt = time.UtcNow();
        await db.SaveChangesAsync();
        await audit.WriteAsync("invite.declined", null, null, $"account={inv.SellerId};invite={inv.Id}");
        return TeamResult.Success();
    }
}
