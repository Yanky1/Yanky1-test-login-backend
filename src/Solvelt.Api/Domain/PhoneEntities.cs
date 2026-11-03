using Microsoft.EntityFrameworkCore;

namespace Solvelt.Api.Domain;

/// <summary>The phone PIN (hashed with the Identity password hasher). The verified number itself lives in Identity's PhoneNumber/PhoneNumberConfirmed.</summary>
public class PhoneCredential
{
    public Guid UserId { get; set; }
    public string PinHash { get; set; } = "";
    public DateTime SetAt { get; set; }
}

/// <summary>Server-side conversation state for one call (key = voice:CallSid) or one SMS peer (key = sms:HMAC(number)). Short-lived.</summary>
public class PhoneState
{
    public string Key { get; set; } = "";
    public string Channel { get; set; } = ""; // voice | sms
    public string Phone { get; set; } = "";   // E.164 of the caller / texter
    public string Step { get; set; } = "";
    /// <summary>Voice only: the next expected request sequence number (replay and out-of-order protection).</summary>
    public int Seq { get; set; }
    public int Retries { get; set; }
    public string DataJson { get; set; } = "{}";
    public Guid? UserId { get; set; }          // set only once the phone/SMS session is authenticated
    public DateTime? AuthUntil { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public class PhoneOptOut
{
    public string Phone { get; set; } = "";
    public DateTime At { get; set; }
}

/// <summary>Opaque single-use link sent by SMS: password reset (purpose "reset") or finishing a phone sign-up on the web ("signup").</summary>
public class PhoneLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TokenHash { get; set; } = "";
    public string Purpose { get; set; } = "";
    public Guid? UserId { get; set; }
    public string? Phone { get; set; }
    /// <summary>Data-protected payload: the Identity reset token, or the (hashed) phone PIN chosen during an IVR sign-up.</summary>
    public string? Payload { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
}

/// <summary>Inbound webhook ids already processed (replay protection).</summary>
public class PhoneSeen
{
    public string Key { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
}

public static class PhoneModel
{
    public static void ConfigurePhone(this ModelBuilder b)
    {
        b.Entity<AppUser>().HasIndex(u => u.PhoneNumber).IsUnique(); // NULLs never collide
        b.Entity<PhoneCredential>(e => { e.ToTable("phone_credentials"); e.HasKey(x => x.UserId); });
        b.Entity<PhoneState>(e => { e.ToTable("phone_states"); e.HasKey(x => x.Key); e.HasIndex(x => x.ExpiresAt); });
        b.Entity<PhoneOptOut>(e => { e.ToTable("phone_opt_outs"); e.HasKey(x => x.Phone); });
        b.Entity<PhoneLink>(e => { e.ToTable("phone_links"); e.HasIndex(x => x.TokenHash).IsUnique(); });
        b.Entity<PhoneSeen>(e => { e.ToTable("phone_seen"); e.HasKey(x => x.Key); e.HasIndex(x => x.ExpiresAt); });
    }
}
