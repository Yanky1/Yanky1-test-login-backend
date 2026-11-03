using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Email;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Options;
using Solvelt.Api.Services;

namespace Solvelt.Api.Phone;

public record PhoneStartRequest(string? PhoneNumber, string? CurrentPassword, string? StepUpCode);
public record PhoneVerifyRequest(string? Code);
public record PhoneStepUpRequest(string? CurrentPassword, string? StepUpCode);
public record PhonePinRequest(string? Pin, string? CurrentPassword, string? StepUpCode);

public static class PhoneAccountEndpoints
{
    private const string AddPurpose = "phone-add", StepUpPurpose = "phone-stepup";

    private static IResult Bad(string msg, string code) =>
        Results.Problem(msg, statusCode: 400, extensions: new Dictionary<string, object?> { ["code"] = code });

    /// <summary>
    /// Step-up for changing/removing the number or setting the PIN: the password on password accounts; otherwise a 6-digit code texted to the CURRENT verified number.
    /// Returns null when satisfied, otherwise the response to send (202 = code sent / 400 = wrong).
    /// </summary>
    private static async Task<IResult?> StepUpAsync(AppUser user, string? password, string? code, UserManager<AppUser> users, OneTimeCodeService codes, SmsService sms, IOptions<PhoneOptions> po)
    {
        if (user.HasPassword)
            return !string.IsNullOrEmpty(password) && await users.CheckPasswordAsync(user, password) ? null : Bad("Your current password isn't correct.", "wrong_password");
        if (string.IsNullOrWhiteSpace(code))
        {
            if (user.PhoneNumber is null || !user.PhoneNumberConfirmed) return Bad("Confirm your identity with a passkey or by adding a phone number first.", "stepup_unavailable");
            var issued = await codes.IssueAsync(user.Id, StepUpPurpose);
            if (issued.Code is not null) await sms.SendAsync(user.PhoneNumber, $"{po.Value.BrandName} confirmation code: {issued.Code}. Never share it.");
            return Results.Json(new { stepUpRequired = true, message = "We texted a confirmation code to your current number." }, statusCode: 202);
        }
        var (o, _) = await codes.VerifyAsync(user.Id, StepUpPurpose, code);
        return o == OneTimeCodeService.VerifyOutcome.Ok ? null : Bad("That confirmation code isn't right.", "stepup_failed");
    }

    public static void MapPhoneAccountEndpoints(this RouteGroupBuilder g)
    {
        var a = g.MapGroup("/account/phone").RequireAuthorization("VerifiedEmail");

        a.MapGet("", async (ClaimsPrincipal p, UserManager<AppUser> users, PinService pins) =>
        {
            var u = await users.FindByIdAsync(p.UserId().ToString());
            if (u is null) return Results.Unauthorized();
            var has = u.PhoneNumber is not null && u.PhoneNumberConfirmed;
            return Results.Ok(new { hasNumber = has, numberEnding = has ? u.PhoneNumber![^4..] : null, hasPin = has && await pins.HasPinAsync(u.Id) });
        });

        // Add (first number) or change (needs step-up). A code is texted to the NEW number.
        a.MapPost("/start", async (PhoneStartRequest r, ClaimsPrincipal p, UserManager<AppUser> users, OneTimeCodeService codes, SmsService sms, AppDbContext db,
            IOptions<PhoneOptions> po) =>
        {
            var u = await users.FindByIdAsync(p.UserId().ToString());
            if (u is null) return Results.Unauthorized();
            var number = PhoneFormat.Normalize(r.PhoneNumber, po.Value.DefaultRegion);
            if (number is null) return Validation.Invalid("phoneNumber", "Enter a valid mobile number, including the country code if it isn't local.");
            if (u.PhoneNumberConfirmed && u.PhoneNumber == number) return Validation.Invalid("phoneNumber", "That is already your number.");
            if (u.PhoneNumberConfirmed && await StepUpAsync(u, r.CurrentPassword, r.StepUpCode, users, codes, sms, po) is { } su) return su;
            // A number that belongs to another account is not revealed: same answer, nothing sent.
            var taken = await db.Users.AnyAsync(x => x.PhoneNumber == number && x.Id != u.Id);
            if (!taken)
            {
                var issued = await codes.IssueAsync(u.Id, AddPurpose, number);
                if (issued.Code is not null) await sms.SendAsync(number, $"{po.Value.BrandName} verification code: {issued.Code}. It expires in {OneTimeCodeService.ValidMinutes} minutes. Reply STOP to opt out.");
            }
            return Results.Accepted(value: new { message = "If this number can be used, we texted a 6-digit code to it.", resendAfterSeconds = OneTimeCodeService.ResendSeconds });
        }).RequireRateLimiting("sensitive");

        a.MapPost("/verify", async (PhoneVerifyRequest r, ClaimsPrincipal p, UserManager<AppUser> users, OneTimeCodeService codes, Mailer mailer, IAuditLog audit) =>
        {
            var u = await users.FindByIdAsync(p.UserId().ToString());
            if (u is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(r.Code) || r.Code.Trim().Length != 6 || !r.Code.Trim().All(char.IsAsciiDigit)) return Bad("That code isn't right or has expired.", "invalid_or_expired");
            var (o, row) = await codes.VerifyAsync(u.Id, AddPurpose, r.Code);
            if (o != OneTimeCodeService.VerifyOutcome.Ok || row?.Target is null) return Bad("That code isn't right or has expired.", "invalid_or_expired");
            var changed = u.PhoneNumberConfirmed;
            u.PhoneNumber = row.Target; u.PhoneNumberConfirmed = true;
            var res = await users.UpdateAsync(u);
            if (!res.Succeeded) return Bad("That number can't be used.", "phone_unavailable"); // unique index: somebody verified it first
            await mailer.SendAsync(changed ? PhoneTemplates.PhoneChanged(u.Email!, u.PhoneNumber) : PhoneTemplates.PhoneAdded(u.Email!, u.PhoneNumber));
            await audit.WriteAsync(changed ? "phone.changed" : "phone.added", u.Id, u.Email, $"phone={PhoneFormat.Mask(u.PhoneNumber)}");
            return Results.Ok(new { numberEnding = u.PhoneNumber[^4..] });
        }).RequireRateLimiting("sensitive");

        // Texts a step-up code for accounts without a password (the web card calls this before remove/PIN when there is no password).
        a.MapPost("/remove", async (PhoneStepUpRequest r, ClaimsPrincipal p, UserManager<AppUser> users, OneTimeCodeService codes, SmsService sms, PinService pins,
            Mailer mailer, IAuditLog audit, IOptions<PhoneOptions> po) =>
        {
            var u = await users.FindByIdAsync(p.UserId().ToString());
            if (u is null) return Results.Unauthorized();
            if (!u.PhoneNumberConfirmed) return Results.NoContent();
            if (await StepUpAsync(u, r.CurrentPassword, r.StepUpCode, users, codes, sms, po) is { } su) return su;
            u.PhoneNumber = null; u.PhoneNumberConfirmed = false;
            await users.UpdateAsync(u);
            await pins.RemoveAsync(u.Id);
            await mailer.SendAsync(PhoneTemplates.PhoneRemoved(u.Email!));
            await audit.WriteAsync("phone.removed", u.Id, u.Email);
            return Results.NoContent();
        }).RequireRateLimiting("sensitive");

        a.MapPut("/pin", async (PhonePinRequest r, ClaimsPrincipal p, UserManager<AppUser> users, OneTimeCodeService codes, SmsService sms, PinService pins,
            Mailer mailer, IAuditLog audit, IOptions<PhoneOptions> po) =>
        {
            var u = await users.FindByIdAsync(p.UserId().ToString());
            if (u is null) return Results.Unauthorized();
            if (!u.PhoneNumberConfirmed) return Bad("Add and verify a phone number first.", "no_phone");
            if (PinRules.Weakness(r.Pin) is { } weak) return Validation.Invalid("pin", weak);
            if (await StepUpAsync(u, r.CurrentPassword, r.StepUpCode, users, codes, sms, po) is { } su) return su;
            await pins.SetAsync(u.Id, r.Pin!);
            await mailer.SendAsync(PhoneTemplates.PinChanged(u.Email!));
            await audit.WriteAsync("phone.pin_changed", u.Id, u.Email, "channel=web");
            return Results.NoContent();
        }).RequireRateLimiting("sensitive");
    }
}
