using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Admin;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Tests;

public static class StaffKit
{
    public static string NewEmail() => $"staff-{Guid.NewGuid():N}@example.com";

    /// <summary>Creates an active staff member with a password and (by default) TOTP 2FA enabled.</summary>
    public static async Task<string> Create(ApiFactory f, StaffRole role, string? email = null, bool twoFactor = true)
    {
        email ??= NewEmail();
        using var scope = f.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            user = new AppUser { UserName = email, Email = email, EmailConfirmed = true, HasPassword = true, CreatedAt = DateTime.UtcNow };
            Assert.True((await users.CreateAsync(user, ApiFactory.Strong)).Succeeded);
        }
        if (twoFactor)
        {
            await users.ResetAuthenticatorKeyAsync(user);
            await users.SetTwoFactorEnabledAsync(user, true);
        }
        db.StaffMembers.Add(new StaffMember { UserId = user.Id, Role = role, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        if (role == StaffRole.SuperAdmin) await users.AddToRoleAsync(user, "Admin");
        return email;
    }

    public static async Task<string> Totp(ApiFactory f, string email)
    {
        using var scope = f.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = (await users.FindByEmailAsync(email))!;
        var key = (await users.GetAuthenticatorKeyAsync(user))!;
        return Rfc6238(key, DateTimeOffset.UtcNow);
    }

    /// <summary>RFC 6238 TOTP (SHA-1, 30 s, 6 digits) for a base32 key: Identity's authenticator provider can only verify, not generate.</summary>
    public static string Rfc6238(string base32, DateTimeOffset at)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0; var acc = 0; var bytes = new List<byte>();
        foreach (var ch in base32.Replace(" ", "").TrimEnd('=').ToUpperInvariant())
        {
            acc = (acc << 5) | alphabet.IndexOf(ch); bits += 5;
            if (bits >= 8) { bytes.Add((byte)(acc >> (bits - 8) & 0xFF)); bits -= 8; }
        }
        var counter = BitConverter.GetBytes((long)(at.ToUnixTimeSeconds() / 30)); if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        var h = System.Security.Cryptography.HMACSHA1.HashData(bytes.ToArray(), counter);
        var o = h[^1] & 0x0F;
        var bin = ((h[o] & 0x7F) << 24) | (h[o + 1] << 16) | (h[o + 2] << 8) | h[o + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    public static async Task<HttpResponseMessage> TryLogin(ApiFactory f, Api api, string email, string? password = null, string? code = "auto")
    {
        object body = code == "auto" ? new { email, password = password ?? ApiFactory.Strong, totpCode = await Totp(f, email) } : new { email, password = password ?? ApiFactory.Strong, totpCode = code };
        return await api.Post("/api/v1/admin/auth/login", body, false);
    }

    /// <summary>A signed-in staff session (password + TOTP).</summary>
    public static async Task<Api> Login(ApiFactory f, string email)
    {
        var api = new Api(f);
        var res = await TryLogin(f, api, email);
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"staff login failed: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        var j = await res.Content.ReadFromJsonAsync<JsonElement>();
        api.SetSession(new Login("Bearer", j.GetProperty("accessToken").GetString()!, j.GetProperty("expiresIn").GetInt32(), "", Guid.Empty, email, "", "Active", []));
        return api;
    }

    public static async Task<Api> LoginNew(ApiFactory f, StaffRole role) => await Login(f, await Create(f, role));

    public static async Task StepUp(ApiFactory f, Api api, string email)
    {
        var res = await api.Post("/api/v1/admin/auth/step-up", new { code = await Totp(f, email) });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    /// <summary>Staff signed in with step-up already done (for sensitive actions).</summary>
    public static async Task<(Api api, string email)> LoginSteppedUp(ApiFactory f, StaffRole role)
    {
        var email = await Create(f, role);
        var api = await Login(f, email);
        await StepUp(f, api, email);
        return (api, email);
    }

    public static Guid UserId(ApiFactory f, string email) => f.WithDb(db => db.Users.First(u => u.Email == email).Id);

    public static async Task<string?> Err(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("code", out var c) ? c.GetString() : null;

    public const string Why = "Customer ticket 4821";
}
