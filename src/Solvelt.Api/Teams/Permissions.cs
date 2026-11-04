using Solvelt.Api.Domain;

namespace Solvelt.Api.Teams;

public enum Permission
{
    AccountRead,            // see account status + business summary
    AccountEditBusiness,    // legal/business details, application wizard
    AgreementAccept,        // seller agreement / submit application
    OwnershipTransfer,
    AccountClose,
    MembersRead,
    MembersManage,          // invite, resend, revoke, change role/scope, remove (Admin: not Owners)
    BrandsRead,
    BrandsManage,           // create / edit / archive brands, logos
    BrandWorkRead,          // read brand content (products, questions: built later)
    BrandWorkWrite,         // write brand content (orders: accept/ship/cancel/returns, messages)
    ProvidersManage,        // mark team members as service providers, edit anyone's working hours/time off (Owner/Admin); providers always manage their own
    OrderRefund,            // issue refunds without a limit (Owner/Admin; Managers only up to Orders:ManagerRefundLimitCents, default 0)
    PaymentsRead,           // balances, ledger, transfers, payouts, statements, disputes (Owner/Admin; never Manager/Viewer)
    ShippingManage,         // shipping settings: ship-from addresses, packages, profiles, return address (Owner/Admin; Managers and Viewers read)
    PaymentsManage          // start/continue payout onboarding, open the Stripe Express dashboard (Owner only)
}

/// <summary>The central role -> permission matrix. Documented in docs/TEAMS.md; enforced by endpoint filters.</summary>
public static class Permissions
{
    private static readonly Dictionary<AccountRole, HashSet<Permission>> Matrix = new()
    {
        [AccountRole.Owner] = [.. Enum.GetValues<Permission>()],
        [AccountRole.Admin] = [.. Enum.GetValues<Permission>().Except([
            Permission.AccountEditBusiness, Permission.AgreementAccept, Permission.OwnershipTransfer, Permission.AccountClose, Permission.PaymentsManage])],
        [AccountRole.Manager] = [Permission.AccountRead, Permission.BrandsRead, Permission.BrandWorkRead, Permission.BrandWorkWrite],
        [AccountRole.Viewer] = [Permission.AccountRead, Permission.BrandsRead, Permission.BrandWorkRead],
    };

    public static bool Has(AccountRole role, Permission p) => Matrix[role].Contains(p);
    public static IReadOnlyCollection<Permission> For(AccountRole role) => Matrix[role];
}

/// <summary>Who is acting on which account right now. Re-resolved from the database on every authenticated request.</summary>
public sealed record AccountAccess(Guid UserId, Guid SellerId, Guid MembershipId, AccountRole Role, bool AllBrands,
    IReadOnlySet<Guid> BrandIds, SellerStatus AccountStatus)
{
    public bool Has(Permission p) => Permissions.Has(Role, p);
    public bool AtLeast(AccountRole r) => Role >= r;
    /// <summary>Owners/Admins always have all-brand scope.</summary>
    public bool CanSeeBrand(Guid brandId) => AllBrands || Role >= AccountRole.Admin || BrandIds.Contains(brandId);
    /// <summary>Read vs write access to one brand's content: scope AND permission.</summary>
    public bool CanWorkOnBrand(Guid brandId, bool write) =>
        CanSeeBrand(brandId) && Has(write ? Permission.BrandWorkWrite : Permission.BrandWorkRead);
}
