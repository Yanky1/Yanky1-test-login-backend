namespace Solvelt.Api.Options;

public class AppOptions
{
    public const string Section = "App";
    public string[] CorsOrigins { get; set; } = ["http://localhost:5173"];
    public string FrontendBaseUrl { get; set; } = "http://localhost:5173";
    public string MarketplaceUrl { get; set; } = "https://example.com/marketplace";
    public string TermsVersion { get; set; } = "2026-01";
    public string SellerAgreementVersion { get; set; } = "2026-01";
    public string SmsWordingVersion { get; set; } = "2026-01";
    /// <summary>Secret for HMAC of codes / throttle keys. Required in production.</summary>
    public string HmacSecret { get; set; } = "";
    public bool UseHttpsRedirection { get; set; } = true;
    public bool UseHsts { get; set; } = true;
    public string[] TrustedProxies { get; set; } = [];
}

public class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "solvelt-api";
    public string Audience { get; set; } = "solvelt-portal";
    public int AccessTokenMinutes { get; set; } = 60;
    /// <summary>PKCS#8 PEM RSA private key. Ephemeral key generated when empty outside Production.</summary>
    public string PrivateKeyPem { get; set; } = "";
    public string KeyId { get; set; } = "k1";
    /// <summary>Old public keys (kid -> SPKI PEM) still accepted during rotation.</summary>
    public Dictionary<string, string> PreviousPublicKeys { get; set; } = new();
}

public class SessionSettings
{
    public const string Section = "Session";
    /// <summary>Server-side idle limit; should be a little larger than the frontend idle timeout.</summary>
    public int IdleTimeoutMinutes { get; set; } = 20;
    public int RefreshTokenDays { get; set; } = 30;
    public string RefreshCookiePath { get; set; } = "/api/v1/auth";
}

public class LockoutSettings
{
    public const string Section = "Lockout";
    public int MaxFailures { get; set; } = 5;
    public int LockMinutes { get; set; } = 15;
    public int FailureWindowMinutes { get; set; } = 15;
    public int CaptchaAtFailures { get; set; } = 4;
    public int WarnAtFailures { get; set; } = 3;
}

public class RateLimitOptions
{
    public const string Section = "RateLimit";
    public int AuthPerMinute { get; set; } = 20;
    public int SensitivePerMinute { get; set; } = 10;
}

public class TurnstileOptions
{
    public const string Section = "Turnstile";
    public string SecretKey { get; set; } = "";
    public string SiteKey { get; set; } = "";
    /// <summary>Testing environment only: emulate Cloudflare's documented test keys without network access.</summary>
    public bool UseTestStub { get; set; }
}

public class EmailOptions
{
    public const string Section = "Email";
    public string Provider { get; set; } = "Sink"; // Sink | Smtp | Resend
    public string FromAddress { get; set; } = "no-reply@example.com";
    public string FromName { get; set; } = "Solvelt";
    public string SmtpHost { get; set; } = "localhost";
    public int SmtpPort { get; set; } = 1025;
    public bool SmtpUseSsl { get; set; }
    public string? SmtpUser { get; set; }
    public string? SmtpPassword { get; set; }
    public string ResendApiKey { get; set; } = "";
}

public class PasskeyConfig
{
    public const string Section = "Passkeys";
    public string RelyingPartyId { get; set; } = "localhost";
    public string[] AllowedOrigins { get; set; } = ["http://localhost:5173"];
}

public class StorageOptions
{
    public const string Section = "Storage";
    public string LocalRoot { get; set; } = "App_Data/uploads";
}

public class ExternalAuthOptions
{
    public const string Section = "ExternalAuth";
    public string GoogleClientId { get; set; } = "";
    public string GoogleClientSecret { get; set; } = "";
    public bool AppleEnabled { get; set; }
    public string AppleClientId { get; set; } = "";
    public string AppleTeamId { get; set; } = "";
    public string AppleKeyId { get; set; } = "";
    public string ApplePrivateKeyPem { get; set; } = "";
}

public class AdminSeedOptions
{
    public const string Section = "AdminSeed";
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}

public class PasswordSettings
{
    public const string Section = "Passwords";
    /// <summary>Check HaveIBeenPwned k-anonymity range API (needs outbound HTTPS).</summary>
    public bool CheckPwned { get; set; }
}
