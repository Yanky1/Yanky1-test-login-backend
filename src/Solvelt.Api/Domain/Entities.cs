using Microsoft.AspNetCore.Identity;

namespace Solvelt.Api.Domain;

public class AppUser : IdentityUser<Guid>
{
    public DateTime CreatedAt { get; set; }
    public bool HasPassword { get; set; }
}

public enum SellerStatus { Draft, PendingReview, NeedsInfo, Approved, Live, Rejected, Suspended }

public class SellerAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public SellerStatus Status { get; set; } = SellerStatus.Draft;
    public string? StatusNote { get; set; }
    public int DraftStep { get; set; } = 1; // 1 Account, 2 Verify, 3 Business, 4 Kosher, 5 Review
    // business
    public string? VendorName { get; set; }
    public string? ContactName { get; set; }
    public string? ContactPhone { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public bool SmsOptIn { get; set; }
    public DateTime? SmsConsentAt { get; set; }
    public string? SmsConsentWordingVersion { get; set; }
    // kosher (driven by seller_field_config)
    public string KosherAnswersJson { get; set; } = "{}";
    // legal
    public string? TermsVersion { get; set; }
    public DateTime? TermsAcceptedAt { get; set; }
    public string? SellerAgreementVersion { get; set; }
    public DateTime? SellerAgreementAcceptedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
}

public class SellerFieldConfig
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "text"; // yesno | text | select | file
    public string? OptionsJson { get; set; }
    public string? ParentKey { get; set; }
    public string? ParentValue { get; set; }
    public bool Required { get; set; } = true;
    public int SortOrder { get; set; }
}

public class SellerFile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public string FieldKey { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public long Size { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class OneTimeCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Purpose { get; set; } = ""; // verify-email | change-email
    public string? Target { get; set; }       // new email for change-email
    public string CodeHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime SentAt { get; set; }
    public int Attempts { get; set; }
    public DateTime? ConsumedAt { get; set; }
}

public class UserSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastActivityAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    public string? UserAgent { get; set; }
    public string? IpAddress { get; set; }
    public string? Method { get; set; }
}

public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public Guid FamilyId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

public class LoginThrottle
{
    public string KeyHash { get; set; } = "";   // HMAC of normalized submitted email
    public int Failures { get; set; }
    public DateTime LastFailureAt { get; set; }
    public DateTime? LockedUntil { get; set; }
}

public class ExternalExchange
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CodeHash { get; set; } = "";
    public Guid UserId { get; set; }
    public string Provider { get; set; } = "";
    public string? ReturnUrl { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
}

public class AuditEvent
{
    public long Id { get; set; }
    public string Type { get; set; } = "";
    public Guid? UserId { get; set; }
    public string? Subject { get; set; }
    public string? IpAddress { get; set; }
    public string? Detail { get; set; }
    public DateTime At { get; set; }
}
