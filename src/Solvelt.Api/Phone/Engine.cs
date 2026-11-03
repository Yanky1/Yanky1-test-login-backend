using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Email;
using Solvelt.Api.Options;
using Solvelt.Api.Services;

namespace Solvelt.Api.Phone;

public enum Chan { Voice, Sms }

/// <summary>What the channel should do next. Voice renders it as TwiML (Say/Gather/Dial/Hangup), SMS joins the lines into one reply.</summary>
public class Turn
{
    public List<string> Say { get; } = [];
    /// <summary>Null = no input expected (the call ends / nothing more to ask).</summary>
    public Ask? Ask { get; set; }
    public bool Hangup { get; set; }
    public string? Dial { get; set; }
    public Turn Add(params string[] lines) { Say.AddRange(lines.Where(l => !string.IsNullOrEmpty(l))); return this; }
}
public record Ask(bool Speech, int? Digits);

/// <summary>Structured conversation data kept in PhoneState.DataJson. Never holds a code, PIN or token in clear (only hashes / protected values).</summary>
public class ConvData
{
    public string? Target { get; set; }
    public bool CodeOk { get; set; }
    public int Attempts { get; set; }
    public string? CodeHash { get; set; }
    public DateTime? CodeAt { get; set; }
    public int CodeTries { get; set; }
    public string? PinHash { get; set; }
    public string? EmailProt { get; set; }
    public string? EmailMask { get; set; }
    public Guid? NewUser { get; set; }
    public int Field { get; set; }
    public Dictionary<string, string> Biz { get; set; } = [];
    public bool Greeted { get; set; }
    public bool AiFailed { get; set; }
}

public class Conv(PhoneState st, Chan chan)
{
    public PhoneState St { get; } = st;
    public Chan Chan { get; } = chan;
    public ConvData D { get; set; } = Load(st);
    public string Step { get => St.Step; set => St.Step = value; }
    public bool Voice => Chan == Chan.Voice;
    private static ConvData Load(PhoneState s) { try { return JsonSerializer.Deserialize<ConvData>(s.DataJson) ?? new(); } catch (JsonException) { return new(); } }
    public void Save() => St.DataJson = JsonSerializer.Serialize(D);
}

/// <summary>
/// THE deterministic state machine for both channels. It owns every decision: code/PIN validity, lockout, which account, and what is allowed in the current state.
/// The AI only proposes an action from a fixed allowlist (see <see cref="AiActions"/>); each proposal is validated against the current step here.
/// </summary>
public class PhoneEngine(PhoneIdentityService ident, PendingPhoneService pending, PinService pins, SmsService sms, PhoneLinkService links, UserManager<AppUser> users, AppDbContext db,
    OneTimeCodeService codes, SessionService sessions, AuthFlows flows, AccountSecurityService security, Mailer mailer, IAuditLog audit, Crypto crypto,
    IConversationAi ai, IOptions<PhoneOptions> po, IOptions<AppOptions> app, TimeProvider time, ILogger<PhoneEngine> log)
{
    private PhoneOptions O => po.Value;
    internal const string Internal = "change_pin";

    // ---- text helpers (voice / sms wording)
    private static string V(Conv c, string voice, string sms) => c.Voice ? voice : sms;
    private static readonly string[] SecretSteps = ["si_pin", "si_phone", "si_code", "si_pin2", "su_code", "su_pin1", "su_pin2", "su_emailcode", "pin_new", "pin_new2"];

    // ---- entry points -------------------------------------------------------------------------------------------------------------------

    /// <summary>First contact: voice greeting (with AI disclosure) + menu. Also what a replayed "incoming" webhook gets (the current prompt).</summary>
    public Turn Open(Conv c)
    {
        var t = new Turn();
        if (c.Voice && !c.D.Greeted)
        {
            c.D.Greeted = true;
            t.Add($"Welcome to {O.BrandName}.", "This call is handled by an automated AI assistant.");
        }
        return Prompt(c, t);
    }

    public async Task<Turn> HandleAsync(Conv c, string? rawText, bool dtmf, CancellationToken ct = default)
    {
        var text = (rawText ?? "").Trim();
        var t = new Turn();
        var now = time.UtcNow();
        if (c.Step is "auth_menu" or "pin_new" or "pin_new2" or "biz" && (c.St.UserId is null || c.St.AuthUntil is null || c.St.AuthUntil <= now)
            && !(c.Step == "biz" && c.D.NewUser is not null && c.St.UserId is not null && c.St.AuthUntil > now))
        {
            c.St.UserId = null; c.St.AuthUntil = null; c.Step = "menu";
            return Prompt(c, t.Add("Your session expired. Please sign in again."));
        }
        if (text.Length == 0)
        {
            if (!c.Voice) return Prompt(c, t);
            return Invalid(c, t, "I didn't hear anything.");
        }
        if (c.Voice && dtmf && text == "*") { c.St.Retries = 0; c.Step = c.St.UserId is not null && c.St.AuthUntil > now && c.Step != "biz" ? "auth_menu" : "menu"; return Prompt(c, t); }

        return c.Step switch
        {
            "menu" => await MenuAsync(c, t, text, dtmf, ct),
            "si_pin" => await SignInPinAsync(c, t, text, dtmf, caller: true),
            "si_phone" => await SignInPhoneAsync(c, t, text),
            "si_code" => await SignInCodeAsync(c, t, text),
            "si_pin2" => await SignInPinAsync(c, t, text, dtmf, caller: false),
            "su_code" => await SignUpCodeAsync(c, t, text),
            "su_pin1" => SignUpPin1(c, t, text),
            "su_pin2" => SignUpPin2(c, t, text),
            "su_choice" => await SignUpChoiceAsync(c, t, text, dtmf),
            "su_email" => await SignUpEmailAsync(c, t, text),
            "su_email_confirm" => await SignUpEmailConfirmAsync(c, t, text, dtmf),
            "su_terms" => await SignUpTermsAsync(c, t, text, dtmf),
            "su_emailcode" => await SignUpEmailCodeAsync(c, t, text),
            "biz" => await BizAsync(c, t, text, dtmf, ct),
            "auth_menu" => await AuthMenuAsync(c, t, text, dtmf, ct),
            "pin_new" => PinNew1(c, t, text),
            "pin_new2" => await PinNew2Async(c, t, text),
            _ => ResetToMenu(c, t)
        };
    }

    // ---- prompts ------------------------------------------------------------------------------------------------------------------------

    public Turn Prompt(Conv c, Turn? t = null)
    {
        t ??= new Turn();
        bool speech = !c.D.AiFailed;
        switch (c.Step)
        {
            case "menu":
                t.Add(V(c, $"Press 1 to sign in. Press 2 to create an account. Press 0 to reach a person. Press 9 to hear this again. Or just tell me what you need.",
                           $"{O.BrandName}: reply LOGIN to sign in or JOIN to create a seller account. HELP for help, STOP to opt out."));
                t.Ask = new(speech, 1); break;
            case "si_pin": t.Add(V(c, "Please enter your 6 digit PIN, then press pound.", "Reply with your 6-digit PIN.")); t.Ask = new(false, null); break;
            case "si_phone": t.Add("Enter the mobile number on your account, including the area code, then press pound."); t.Ask = new(false, null); break;
            case "si_code": t.Add(V(c, "If that number belongs to an account, we sent a 6 digit code by text. Enter it, then press pound.",
                                       "If this number has an account, we texted you a 6-digit code. Reply with it.")); t.Ask = new(false, null); break;
            case "si_pin2": t.Add(V(c, "Now enter your 6 digit PIN, then press pound.", "Thanks. Now reply with your 6-digit PIN.")); t.Ask = new(false, null); break;
            case "su_code": t.Add(V(c, "We sent a 6 digit code by text to the number you are calling from. Enter it, then press pound.",
                                       "We texted a 6-digit code to this number. Reply with it.")); t.Ask = new(false, null); break;
            case "su_pin1": t.Add(V(c, "Choose a 6 digit PIN for signing in by phone. Enter it, then press pound.", "Choose a 6-digit PIN for signing in by phone. Reply with it.")); t.Ask = new(false, null); break;
            case "su_pin2": t.Add(V(c, "Enter the same PIN again to confirm, then press pound.", "Reply with the same PIN again to confirm.")); t.Ask = new(false, null); break;
            case "su_choice":
                t.Add(V(c, "Press 1 to get a text with a link to finish on the web. Press 2 to continue by phone with your email address.",
                           "Reply LINK to finish on the web, or reply with your email address to continue here.")); t.Ask = new(speech, 1); break;
            case "su_email": t.Add(V(c, "Please say your email address, for example: john dot smith at gmail dot com.", "Reply with your email address.")); t.Ask = new(true, null); break;
            case "su_email_confirm":
            {
                var email = Unprotect(c.D.EmailProt);
                t.Add(V(c, $"I heard: {(email is null ? "" : SpokenEmail.ReadBack(email))}. Press 1 if that is right, or 2 to try again.", $"Is {c.D.EmailMask} right? Reply YES, or send the address again."));
                t.Ask = new(speech, 1); break;
            }
            case "su_terms":
                t.Add(V(c, $"To continue you must accept the {O.BrandName} Terms of Service, at {app.Value.FrontendBaseUrl.TrimEnd('/')} slash terms. Press 1 to accept, or 2 to cancel.",
                           $"Reply YES to accept the {O.BrandName} Terms of Service ({app.Value.FrontendBaseUrl.TrimEnd('/')}/terms). Reply NO to cancel.")); t.Ask = new(speech, 1); break;
            case "su_emailcode": t.Add(V(c, "We emailed you a 6 digit code. Enter it, then press pound.", "We emailed you a 6-digit code. Reply with it.")); t.Ask = new(false, null); break;
            case "biz":
                t.Add(c.D.Field < BizRules.Order.Length ? BizRules.Prompt(BizRules.Order[c.D.Field]) : "");
                t.Ask = new(true, null); break;
            case "auth_menu":
                t.Add(V(c, "Press 1 for your application status. Press 2 to get a password reset link by text. Press 3 to change your PIN. Press 4 to sign out of all devices. "
                         + (string.IsNullOrEmpty(O.SupportNumber) ? "" : "Press 5 to speak to support. ") + "Press 9 to repeat, or 0 to hang up.",
                           "Signed in. Reply STATUS, RESET (password link), LOGOUT or LOGOUT ALL. HELP for help."));
                t.Ask = new(speech, 1); break;
            case "pin_new": t.Add("Enter your new 6 digit PIN, then press pound."); t.Ask = new(false, null); break;
            case "pin_new2": t.Add("Enter it again to confirm, then press pound."); t.Ask = new(false, null); break;
            default: c.Step = "menu"; return Prompt(c, t);
        }
        return t;
    }

    // ---- shared helpers -----------------------------------------------------------------------------------------------------------------

    private Turn ResetToMenu(Conv c, Turn t) { c.Step = "menu"; c.St.Retries = 0; return Prompt(c, t); }

    /// <summary>Unrecognised / empty / rejected input: repeat the prompt; after 2 repeats say goodbye (voice) or fall back to the menu (SMS).</summary>
    private Turn Invalid(Conv c, Turn t, string message)
    {
        c.St.Retries++;
        t.Add(message);
        if (c.St.Retries > 2)
        {
            c.St.Retries = 0;
            if (c.Voice) { t.Add("Sorry, I couldn't understand. Goodbye."); t.Hangup = true; c.Step = "menu"; return t; }
            c.Step = c.St.UserId is not null && c.St.AuthUntil > time.UtcNow() ? "auth_menu" : "menu";
        }
        return Prompt(c, t);
    }

    private Turn Goodbye(Conv c, Turn t, string? line = null)
    {
        if (line is not null) t.Add(line);
        c.Step = "menu"; c.St.Retries = 0;
        if (c.Voice) { t.Add("Goodbye."); t.Hangup = true; t.Ask = null; return t; }
        return t;
    }

    private string? Unprotect(string? p) { try { return p is null ? null : links.StateProtector.Unprotect(p); } catch { return null; } }
    private string Protect(string v) => links.StateProtector.Protect(v);
    private string CodeHash(string phone, string code) => crypto.Hmac($"phonecode:{phone}:{code}");

    private static string? SixDigits(string text)
    {
        var d = Redactor.AsDigits(text);
        return d is { Length: 6 } ? d : null;
    }

    private async Task<bool> MayTextAsync(string phone)
    {
        // At most 5 texts per number per hour for codes/links (abuse and cost guard). Rows are keyed by a hash of the number.
        var now = time.UtcNow();
        var prefix = "cs:" + crypto.Hmac(phone) + ":";
        var n = await db.PhoneSeen.CountAsync(x => x.Key.StartsWith(prefix) && x.ExpiresAt > now);
        if (n >= 5) return false;
        db.PhoneSeen.Add(new PhoneSeen { Key = prefix + Guid.NewGuid().ToString("N"), ExpiresAt = now.AddHours(1) });
        await db.SaveChangesAsync();
        return true;
    }

    private async Task<(AppUser? User, Turn? Out)> AuthedUserAsync(Conv c, Turn t)
    {
        var user = c.St.UserId is { } id ? await users.FindByIdAsync(id.ToString()) : null;
        if (user is null || !await ident.CanSignInAsync(user))
        {
            c.St.UserId = null; c.St.AuthUntil = null; c.Step = "menu";
            return (null, Prompt(c, t.Add("Your session ended. Please sign in again.")));
        }
        return (user, null);
    }

    private async Task<Turn> LockedAsync(Conv c, Turn t, AttemptsHint hint)
    {
        c.Step = "menu"; c.St.Retries = 0;
        return Goodbye(c, t, ident.HintText(hint) is { Length: > 0 } s ? s : "Too many attempts. Please try again later.");
    }

    /// <summary>Asks the model for an intent. Secrets are redacted first; failures are logged distinctly and the caller falls back to the keypad menu / help text.</summary>
    private async Task<(AiResult? R, bool Failed)> AskAiAsync(Conv c, string text, string[] allowed, string[] fields, CancellationToken ct)
    {
        var red = Redactor.Redact(text);
        if (!red.Any(char.IsLetter)) return (null, false);
        try
        {
            var r = await ai.InterpretAsync(new AiRequest(c.Chan.ToString(), c.Step, allowed, red, fields), c.Voice, ct);
            if (r is null) return (null, false);
            await audit.WriteAsync("ai.proposal", c.St.UserId, null, $"channel={c.Chan};step={c.Step};action={(AiActions.All.Contains(r.Action) ? r.Action : "other")};allowed={allowed.Contains(r.Action)}");
            return (r, false);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "AI unavailable ({Kind}); falling back to menu", ex is OperationCanceledException ? "timeout" : "error");
            await audit.WriteAsync("ai.unavailable", c.St.UserId, null, $"channel={c.Chan};step={c.Step}");
            return (null, true);
        }
    }

    private Turn AiDown(Conv c, Turn t)
    {
        c.D.AiFailed = true;
        t.Add(V(c, "I'm having trouble understanding right now. Please use your keypad.", "Sorry, I couldn't process that. Reply HELP for the commands I understand."));
        return Prompt(c, t);
    }

    internal static string? Keyword(string text)
    {
        var k = System.Text.RegularExpressions.Regex.Replace(text.Trim().ToUpperInvariant(), @"[^A-Z ]", "").Trim();
        return k switch
        {
            "LOGIN" or "SIGNIN" or "SIGN IN" or "LOG IN" => AiActions.StartSignin,
            "JOIN" or "SIGNUP" or "SIGN UP" => AiActions.StartSignup,
            "STATUS" => AiActions.RequestStatus,
            "RESET" or "FORGOT" or "FORGOT PASSWORD" => AiActions.RequestResetLink,
            "LOGOUT ALL" or "SIGN OUT ALL" => AiActions.SignOutAll,
            "LOGOUT" or "SIGNOUT" or "SIGN OUT" or "BYE" or "GOODBYE" => AiActions.End,
            "AGENT" or "HUMAN" or "PERSON" or "OPERATOR" or "SUPPORT" or "REPRESENTATIVE" => AiActions.TalkToHuman,
            "REPEAT" or "MENU" => AiActions.Repeat,
            "CHANGE PIN" or "NEW PIN" => Internal,
            _ => null
        };
    }

    // ---- unauthenticated menu -----------------------------------------------------------------------------------------------------------

    private async Task<Turn> MenuAsync(Conv c, Turn t, string text, bool dtmf, CancellationToken ct)
    {
        string? action = null;
        if (c.Voice && dtmf)
            action = text switch { "1" => AiActions.StartSignin, "2" => AiActions.StartSignup, "9" => AiActions.Repeat, "0" => AiActions.TalkToHuman, _ => null };
        action ??= Keyword(text);
        string? say = null;
        if (action is null)
        {
            string[] allowed = c.Voice
                ? [AiActions.StartSignup, AiActions.StartSignin, AiActions.TalkToHuman, AiActions.Repeat, AiActions.End, AiActions.Chat]
                : [AiActions.StartSignup, AiActions.StartSignin, AiActions.RequestResetLink, AiActions.TalkToHuman, AiActions.Repeat, AiActions.End, AiActions.Chat];
            var (r, failed) = await AskAiAsync(c, text, allowed, [], ct);
            if (failed) return AiDown(c, t);
            if (r is not null && allowed.Contains(r.Action)) { action = r.Action; say = r.Action == AiActions.Chat ? SafeSay(r.Say) : null; }
            else if (r is not null && AiActions.All.Contains(r.Action) && r.Action != AiActions.Unclear)
                return Prompt(c, t.Add(V(c, "Please sign in first.", "Please sign in first: reply LOGIN."))); // real action, not allowed in this state: refused, never executed
        }
        switch (action)
        {
            case AiActions.StartSignin: return await StartSignInAsync(c, t);
            case AiActions.StartSignup: return await StartSignUpAsync(c, t);
            case AiActions.Repeat: c.St.Retries = 0; return Prompt(c, t);
            case AiActions.TalkToHuman: return Human(c, t);
            case AiActions.End: return Goodbye(c, t, V(c, "", "You're signed out."));
            case AiActions.Chat: return Prompt(c, t.Add(say ?? ""));
            case AiActions.RequestStatus or AiActions.RequestResetLink or AiActions.SignOutAll or Internal:
                if (action == AiActions.RequestResetLink && !c.Voice) return await SmsResetAsync(c, t);
                return Prompt(c, t.Add(V(c, "Please sign in first.", "Please sign in first: reply LOGIN.")));
            default: return Invalid(c, t, V(c, "Sorry, I didn't get that.", "Sorry, I didn't understand."));
        }
    }

    /// <summary>The model's optional friendly line: short, no digits, links or addresses, and no claims about the account.</summary>
    internal static string? SafeSay(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.Length > 140 || s.Any(char.IsDigit) || s.Contains("http", StringComparison.OrdinalIgnoreCase) || s.Contains('@') || s.Any(char.IsControl)) return null;
        var l = s.ToLowerInvariant();
        string[] banned = ["signed in", "logged in", "verified", "approved", "authenticated", "access granted", "password", "pin", "code"];
        return banned.Any(l.Contains) ? null : s;
    }

    private Turn Human(Conv c, Turn t)
    {
        if (c.Voice && !string.IsNullOrEmpty(O.SupportNumber)) { t.Add("Connecting you to support."); t.Dial = O.SupportNumber; return t; }
        t.Add($"You can reach a person at {O.HelpContact}.");
        return c.Voice ? Goodbye(c, t) : t;
    }

    // ---- sign-in ------------------------------------------------------------------------------------------------------------------------

    private async Task<Turn> StartSignInAsync(Conv c, Turn t)
    {
        c.St.Retries = 0; c.St.UserId = null; c.St.AuthUntil = null; // starting a sign-in always starts from signed-out
        c.D.Target = c.St.Phone;
        if (c.Voice)
        {
            // Every caller is asked for the account's number first (caller ID is spoofable: a different first prompt for registered numbers would reveal them).
            c.Step = "si_phone";
            return Prompt(c, t);
        }
        var user = await ident.FindByPhoneAsync(c.St.Phone);
        if (user is not null && await MayTextAsync(c.St.Phone)) await ident.SendSignInCodeAsync(user);
        c.Step = "si_code";
        return Prompt(c, t);
    }

    private async Task<Turn> SignInPhoneAsync(Conv c, Turn t, string text)
    {
        var number = PhoneFormat.FromKeypad(Redactor.AsDigits(text), O.DefaultRegion);
        if (number is null) return Invalid(c, t, "That doesn't look like a phone number.");
        c.D.Target = number; c.St.Retries = 0;
        var user = await ident.FindByPhoneAsync(number);
        if (user is not null && await MayTextAsync(number)) await ident.SendSignInCodeAsync(user);
        c.Step = "si_code";
        return Prompt(c, t);
    }

    private async Task<Turn> SignInCodeAsync(Conv c, Turn t, string text)
    {
        var groups = Redactor.SixDigitGroups(text);
        var code = groups.Count > 0 ? groups[0] : SixDigits(text);
        if (code is null) return Invalid(c, t, "Please enter the 6 digit code.");
        var phone = c.D.Target ?? c.St.Phone;
        var begin = await ident.BeginAttemptAsync(phone);
        if (begin.Blocked) return await LockedAsync(c, t, begin.Hint);
        c.D.CodeOk = await ident.CheckCodeAsync(begin.User, code);
        c.D.Attempts = begin.Attempts; c.St.Retries = 0;
        c.Step = "si_pin2";
        if (!c.Voice && groups.Count > 1) return await SignInPinAsync(c, t, groups[1], false, caller: false); // "123456 654321"
        return Prompt(c, t);
    }

    private async Task<Turn> SignInPinAsync(Conv c, Turn t, string text, bool dtmf, bool caller)
    {
        var pin = SixDigits(text);
        if (pin is null) return Invalid(c, t, "Please enter your 6 digit PIN.");
        var phone = c.D.Target ?? c.St.Phone;
        AppUser? user; int attempts; bool codeOk;
        if (caller)
        {
            var begin = await ident.BeginAttemptAsync(phone);
            if (begin.Blocked) return await LockedAsync(c, t, begin.Hint);
            user = begin.User; attempts = begin.Attempts; codeOk = true; // known-caller path: PIN only, as specified
        }
        else { user = await ident.FindByPhoneAsync(phone); attempts = c.D.Attempts; codeOk = c.D.CodeOk; }
        var pinOk = await ident.CheckPinAsync(user, pin);
        c.D.PinHash = null; c.D.CodeOk = false;
        if (pinOk && codeOk && user is not null)
        {
            await ident.SucceedAsync(phone, user, c.Chan.ToString());
            c.St.UserId = user.Id; c.St.AuthUntil = time.UtcNow().AddMinutes(c.Voice ? O.PhoneSessionMinutes : O.SmsSessionMinutes);
            c.Step = "auth_menu"; c.St.Retries = 0;
            return Prompt(c, t.Add("You're signed in."));
        }
        var outcome = await ident.FailAsync(phone, user, attempts);
        c.Step = "menu"; c.St.Retries = 0;
        if (outcome.Locked) return Goodbye(c, t, ident.HintText(outcome.Hint));
        t.Add("That didn't match.");
        if (ident.HintText(outcome.Hint) is { Length: > 0 } h) t.Add(h + ".");
        return Prompt(c, t);
    }

    // ---- sign-up (phone possession -> PIN -> link or email -> terms -> email code -> business details) ---------------------------------

    private async Task<Turn> StartSignUpAsync(Conv c, Turn t)
    {
        if (string.IsNullOrEmpty(c.St.Phone)) return Goodbye(c, t, "We can't see your phone number. Please sign up on the website.");
        c.D.Target = c.St.Phone; c.St.Retries = 0;
        var existing = await ident.FindByPhoneAsync(c.St.Phone);
        c.D.CodeHash = null; c.D.CodeTries = 0; c.D.CodeAt = time.UtcNow();
        // A number that already belongs to an account is not told so: the same prompt follows, but no code is sent and none can ever match.
        if (existing is null && await MayTextAsync(c.St.Phone))
        {
            var code = Crypto.SixDigits();
            c.D.CodeHash = CodeHash(c.St.Phone, code);
            await sms.SendAsync(c.St.Phone, $"{O.BrandName} code: {code}. It expires in 10 minutes. Never share it.");
        }
        c.Step = "su_code";
        return Prompt(c, t);
    }

    private async Task<Turn> SignUpCodeAsync(Conv c, Turn t, string text)
    {
        var code = SixDigits(text);
        if (code is null) return Invalid(c, t, "Please enter the 6 digit code.");
        var expired = c.D.CodeAt is null || time.UtcNow() - c.D.CodeAt > TimeSpan.FromMinutes(OneTimeCodeService.ValidMinutes);
        if (c.D.CodeTries >= OneTimeCodeService.MaxAttempts || expired)
            return Goodbye(c, t, "That code is no longer valid. Please start again.");
        c.D.CodeTries++;
        if (c.D.CodeHash is null || !Crypto.FixedEquals(c.D.CodeHash, CodeHash(c.St.Phone, code)))
            return Invalid(c, t, "That code didn't match.");
        c.D.CodeHash = null; c.St.Retries = 0; c.Step = "su_pin1";
        return Prompt(c, t);
    }

    private Turn SignUpPin1(Conv c, Turn t, string text)
    {
        var pin = SixDigits(text);
        if (pin is null) return Invalid(c, t, "Please enter exactly 6 digits.");
        if (PinRules.Weakness(pin) is { } weak) return Invalid(c, t, weak);
        c.D.PinHash = pins.Hash(pin); c.St.Retries = 0; c.Step = "su_pin2";
        return Prompt(c, t);
    }

    private Turn SignUpPin2(Conv c, Turn t, string text)
    {
        var pin = SixDigits(text);
        if (pin is null || c.D.PinHash is null || !pins.VerifyHash(c.D.PinHash, pin))
        {
            c.D.PinHash = null; c.Step = "su_pin1"; c.St.Retries = 0;
            return Prompt(c, t.Add("Those PINs didn't match. Let's try again."));
        }
        c.St.Retries = 0; c.Step = "su_choice";
        return Prompt(c, t);
    }

    private async Task<Turn> SignUpChoiceAsync(Conv c, Turn t, string text, bool dtmf)
    {
        var up = text.Trim().ToUpperInvariant();
        if (!c.Voice && Redactor.FindEmail(text) is not null) { c.Step = "su_email"; return await SignUpEmailAsync(c, t, text); }
        if ((c.Voice && dtmf && text == "1") || up is "LINK" or "WEB")
        {
            var url = await links.CreateAsync("signup", null, c.D.Target, c.D.PinHash);
            c.D.PinHash = null;
            await sms.SendAsync(c.D.Target!, $"Finish creating your {O.BrandName} seller account (link valid {O.SignupLinkMinutes} minutes, works once): {url}");
            await audit.WriteAsync("signup.phone_link_sent", null, null, $"phone={PhoneFormat.Mask(c.D.Target!)}");
            return Goodbye(c, t, "We texted you a link to finish on the web.");
        }
        if ((c.Voice && dtmf && text == "2") || up is "EMAIL" or "PHONE") { c.St.Retries = 0; c.Step = "su_email"; return Prompt(c, t); }
        if (c.Voice && !dtmf && SpokenEmail.Normalize(text) is not null) { c.Step = "su_email"; return await SignUpEmailAsync(c, t, text); }
        return Invalid(c, t, "Sorry, I didn't get that.");
    }

    private async Task<Turn> SignUpEmailAsync(Conv c, Turn t, string text)
    {
        var email = c.Voice ? SpokenEmail.Normalize(text) : Redactor.FindEmail(text);
        if (email is null || !Endpoints.Validation.IsEmail(email)) return Invalid(c, t, "Sorry, I didn't catch a valid email address.");
        c.D.EmailProt = Protect(email); c.D.EmailMask = SpokenEmail.Mask(email); c.St.Retries = 0; c.Step = "su_email_confirm";
        if (c.Voice && c.D.Target is { } to)
            await sms.SendAsync(to, $"{O.BrandName}: we heard the email address {c.D.EmailMask} for your new account. If that's wrong, tell us on the call."); // masked: no full emails by SMS
        return Prompt(c, t);
    }

    private static bool Yes(string text, bool dtmf, bool voice) =>
        (voice && dtmf && text == "1") || text.Trim().ToUpperInvariant() is "YES" or "Y" or "YEAH" or "YEP" or "CORRECT" or "RIGHT" or "THAT'S RIGHT" or "THATS RIGHT" or "I ACCEPT" or "ACCEPT";
    private static bool No(string text, bool dtmf, bool voice) =>
        (voice && dtmf && text == "2") || text.Trim().ToUpperInvariant() is "NO" or "N" or "NOPE" or "WRONG" or "CANCEL";

    private async Task<Turn> SignUpEmailConfirmAsync(Conv c, Turn t, string text, bool dtmf)
    {
        if (Yes(text, dtmf, c.Voice)) { c.St.Retries = 0; c.Step = "su_terms"; return Prompt(c, t); }
        if (!c.Voice && Redactor.FindEmail(text) is not null) return await SignUpEmailAsync(c, t, text);
        if (No(text, dtmf, c.Voice)) { c.D.EmailProt = null; c.St.Retries = 0; c.Step = "su_email"; return Prompt(c, t); }
        return Invalid(c, t, "Sorry, I didn't get that.");
    }

    private async Task<Turn> SignUpTermsAsync(Conv c, Turn t, string text, bool dtmf)
    {
        if (No(text, dtmf, c.Voice)) return Goodbye(c, t, "Okay, nothing was created.");
        if (!Yes(text, dtmf, c.Voice)) return Invalid(c, t, "Sorry, I didn't get that.");
        // Explicit "yes" to the Terms; the wording version (App:TermsVersion) and time are stored on the seller by CreateSellerUserAsync. The Seller Agreement is accepted on the web.
        var email = Unprotect(c.D.EmailProt);
        if (email is null) return Goodbye(c, t, "Something went wrong. Please start again.");
        var existing = await users.FindByEmailAsync(email);
        c.D.NewUser = null;
        if (existing is { EmailConfirmed: true })
        {
            // Same as the web: never reveal that the address is registered; the owner is told by email.
            var fe = app.Value.FrontendBaseUrl.TrimEnd('/');
            await mailer.SendDetachedAsync(Templates.SignupExisting(email, fe + "/login", fe + "/forgot-password"));
            await audit.WriteAsync("signup.existing_notified", existing.Id, email, "channel=phone");
        }
        else
        {
            try
            {
                if (existing is not null) await security.DeleteUnverifiedAsync(existing);
                var user = await flows.CreateSellerUserAsync(email, null, false, true);
                await pending.HoldAsync(user.Id, c.D.Target!, c.D.PinHash!); // attached only when the emailed code proves the mailbox
                var issued = await codes.IssueAsync(user.Id, "verify-email");
                if (issued.Code is not null) await mailer.SendAsync(Templates.VerifyCode(email, issued.Code));
                c.D.NewUser = user.Id;
                await audit.WriteAsync("signup.created", user.Id, email, "method=phone");
            }
            catch (Exception ex) when (ex is AccountCreationException or DbUpdateException)
            {
                log.LogWarning("Phone sign-up could not create the account");
                var stale = await users.FindByEmailAsync(email);
                if (stale is { EmailConfirmed: false }) await security.DeleteUnverifiedAsync(stale);
                return Goodbye(c, t, "We couldn't create the account. Please try again on the website.");
            }
        }
        c.D.PinHash = null; c.D.EmailProt = null; c.D.CodeTries = 0; c.St.Retries = 0; c.Step = "su_emailcode";
        return Prompt(c, t.Add(V(c, "Thank you.", "Thank you.")));
    }

    private async Task<Turn> SignUpEmailCodeAsync(Conv c, Turn t, string text)
    {
        var code = SixDigits(text);
        if (code is null) return Invalid(c, t, "Please enter the 6 digit code.");
        if (c.D.CodeTries >= OneTimeCodeService.MaxAttempts) return Goodbye(c, t, "That code is no longer valid. Please start again.");
        c.D.CodeTries++;
        var user = c.D.NewUser is { } id ? await users.FindByIdAsync(id.ToString()) : null;
        if (user is null || user.EmailConfirmed) return Invalid(c, t, "That code didn't match.");
        var (outcome, _) = await codes.VerifyAsync(user.Id, "verify-email", code);
        if (outcome != OneTimeCodeService.VerifyOutcome.Ok) return Invalid(c, t, "That code didn't match.");
        user.EmailConfirmed = true;
        await users.UpdateAsync(user);
        var seller = await db.Sellers.FirstOrDefaultAsync(s => s.UserId == user.Id);
        if (seller is not null && seller.DraftStep < 3) { seller.DraftStep = 3; await db.SaveChangesAsync(); }
        await audit.WriteAsync("email.verified", user.Id, user.Email, "channel=phone");
        await pending.AttachAsync(user);
        c.St.UserId = user.Id; c.St.AuthUntil = time.UtcNow().AddMinutes(c.Voice ? O.PhoneSessionMinutes : O.SmsSessionMinutes);
        c.D.Field = 0; c.D.Biz = []; c.St.Retries = 0; c.Step = "biz";
        return Prompt(c, t.Add("Your email is verified.", "Now a few details about your business."));
    }

    // ---- business details (AI-assisted extraction, deterministic validation) -----------------------------------------------------------

    private async Task<Turn> BizAsync(Conv c, Turn t, string text, bool dtmf, CancellationToken ct)
    {
        var (user, bad) = await AuthedUserAsync(c, t);
        if (bad is not null) return bad;
        var field = BizRules.Order[c.D.Field];
        var kw = Keyword(text);
        if (kw == AiActions.TalkToHuman) return Human(c, t);
        if (kw == AiActions.End) return await FinishBizAsync(c, t, user!, partial: true);
        var accepted = 0;
        var errors = new List<string>();
        var viaAi = false;
        if (field is not ("postalCode" or "country") && !dtmf && text.Any(char.IsLetter))
        {
            var (r, failed) = await AskAiAsync(c, text, [AiActions.ProvideBusinessField, AiActions.TalkToHuman, AiActions.Repeat, AiActions.End, AiActions.Chat, AiActions.Unclear], AiActions.BusinessFields, ct);
            if (failed) c.D.AiFailed = true;
            if (r is { Action: AiActions.TalkToHuman }) return Human(c, t);
            if (r is { Action: AiActions.Repeat }) return Prompt(c, t);
            if (r is { Action: AiActions.ProvideBusinessField })
            {
                viaAi = true;
                foreach (var (k, v) in r.Fields)
                {
                    if (!AiActions.BusinessFields.Contains(k)) continue;
                    var (val, err) = BizRules.Validate(k, v);
                    if (val is null) errors.Add(err!); else { c.D.Biz[k] = val; accepted++; }
                }
            }
        }
        if (!viaAi || accepted == 0 && errors.Count == 0)
        {
            // Plain answer to the question that was asked.
            var (val, err) = BizRules.Validate(field, text);
            if (val is null) return Invalid(c, t, err!);
            c.D.Biz[field] = val; accepted++;
        }
        else if (accepted == 0) return Invalid(c, t, errors[0]);
        c.St.Retries = 0;
        c.D.Field = Array.FindIndex(BizRules.Order, f => !c.D.Biz.ContainsKey(f));
        if (c.D.Field < 0) return await FinishBizAsync(c, t, user!, partial: false);
        return Prompt(c, t.Add(errors.Count > 0 ? errors[0] : ""));
    }

    private async Task<Turn> FinishBizAsync(Conv c, Turn t, AppUser user, bool partial)
    {
        var seller = await db.Sellers.FirstOrDefaultAsync(s => s.UserId == user.Id);
        if (seller is not null && seller.Status is SellerStatus.Draft or SellerStatus.NeedsInfo && c.D.Biz.Count > 0)
        {
            string? G(string k) => c.D.Biz.GetValueOrDefault(k);
            seller.VendorName = G("vendorName") ?? seller.VendorName; seller.ContactName = G("contactName") ?? seller.ContactName;
            seller.AddressLine1 = G("addressLine1") ?? seller.AddressLine1; seller.City = G("city") ?? seller.City;
            seller.PostalCode = G("postalCode") ?? seller.PostalCode; seller.Country = G("country") ?? seller.Country;
            seller.ContactPhone = user.PhoneNumber ?? seller.ContactPhone;
            if (!partial) seller.DraftStep = Math.Max(seller.DraftStep, 4);
            seller.UpdatedAt = time.UtcNow();
            await db.SaveChangesAsync();
            await audit.WriteAsync("seller.business_by_phone", user.Id, null, $"fields={c.D.Biz.Count}");
        }
        var url = await links.CreateResetAsync(users, user);
        var sent = user.PhoneNumber is not null && await sms.SendAsync(user.PhoneNumber, $"Finish your {O.BrandName} application (kosher certificate, Seller Agreement) and set your password. Link valid {O.SignupLinkMinutes} minutes, works once: {url}");
        c.D.Biz = []; c.D.Field = 0; c.St.UserId = null; c.St.AuthUntil = null;
        return Goodbye(c, t, sent ? "Thanks. We texted you a link to finish your application on the web." : "Thanks. Your details are saved. Please finish your application on the website.");
    }

    // ---- authenticated menu -------------------------------------------------------------------------------------------------------------

    private static string StatusText(SellerStatus s) => s switch
    {
        SellerStatus.Draft => "not yet submitted", SellerStatus.PendingReview => "pending review", SellerStatus.NeedsInfo => "waiting for more information from you",
        SellerStatus.Approved => "approved", SellerStatus.Live => "approved and live", SellerStatus.Rejected => "not approved", _ => "suspended"
    };

    private async Task<Turn> AuthMenuAsync(Conv c, Turn t, string text, bool dtmf, CancellationToken ct)
    {
        string? action = null;
        if (c.Voice && dtmf)
            action = text switch
            {
                "1" => AiActions.RequestStatus, "2" => AiActions.RequestResetLink, "3" => Internal, "4" => AiActions.SignOutAll,
                "5" when !string.IsNullOrEmpty(O.SupportNumber) => AiActions.TalkToHuman, "9" => AiActions.Repeat, "0" => AiActions.End, _ => null
            };
        action ??= Keyword(text);
        if (action is null)
        {
            string[] allowed = [AiActions.RequestStatus, AiActions.RequestResetLink, AiActions.SignOutAll, AiActions.TalkToHuman, AiActions.Repeat, AiActions.End, AiActions.Chat];
            var (r, failed) = await AskAiAsync(c, text, allowed, [], ct);
            if (failed) return AiDown(c, t);
            if (r is not null && allowed.Contains(r.Action))
            {
                action = r.Action;
                if (action == AiActions.Chat) return Prompt(c, t.Add(SafeSay(r.Say) ?? ""));
            }
        }
        if (action is null) return Invalid(c, t, V(c, "Sorry, I didn't get that.", "Sorry, I didn't understand."));
        c.St.Retries = 0;
        var (user, bad) = await AuthedUserAsync(c, t);
        if (bad is not null) return bad;
        switch (action)
        {
            case AiActions.Repeat: return Prompt(c, t);
            case AiActions.RequestStatus:
            {
                var s = await db.Sellers.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == user!.Id);
                t.Add(s is null ? "There is no seller application on this account." : $"Your application is {StatusText(s.Status)}.");
                return Prompt(c, t);
            }
            case AiActions.RequestResetLink:
            {
                if (user!.PhoneNumber is { } to && await MayTextAsync(to))
                {
                    var url = await links.CreateResetAsync(users, user);
                    var sent = await sms.SendAsync(to, $"{O.BrandName}: reset your password (valid {O.SignupLinkMinutes} minutes, works once): {url}");
                    if (sent) await mailer.SendAsync(PhoneTemplates.ResetByText(user.Email!));
                    await audit.WriteAsync("password.reset_requested", user.Id, user.Email, "channel=" + c.Chan);
                    t.Add(sent ? "We texted a reset link to the number on your account." : "We couldn't send the text.");
                }
                else t.Add("We couldn't send the text right now.");
                return Prompt(c, t);
            }
            case Internal:
                if (!c.Voice) return Prompt(c, t.Add("Change your PIN by phone call or on the website."));
                c.Step = "pin_new"; return Prompt(c, t);
            case AiActions.SignOutAll:
                await sessions.RevokeAllAsync(user!.Id, "phone-logout-all");
                await audit.WriteAsync("logout.all", user.Id, null, "channel=" + c.Chan);
                await mailer.SendAsync(PhoneTemplates.SignedOutAll(user.Email!));
                c.St.UserId = null; c.St.AuthUntil = null; // this phone/SMS session ends too
                return Goodbye(c, t, "You've been signed out of all devices.");
            case AiActions.TalkToHuman: return Human(c, t);
            case AiActions.End:
                c.St.UserId = null; c.St.AuthUntil = null;
                return Goodbye(c, t, V(c, "", "You're signed out."));
            default: return Prompt(c, t);
        }
    }

    private Turn PinNew1(Conv c, Turn t, string text)
    {
        var pin = SixDigits(text);
        if (pin is null) return Invalid(c, t, "Please enter exactly 6 digits.");
        if (PinRules.Weakness(pin) is { } weak) return Invalid(c, t, weak);
        c.D.PinHash = pins.Hash(pin); c.St.Retries = 0; c.Step = "pin_new2";
        return Prompt(c, t);
    }

    private async Task<Turn> PinNew2Async(Conv c, Turn t, string text)
    {
        var pin = SixDigits(text);
        var ok = pin is not null && c.D.PinHash is not null && pins.VerifyHash(c.D.PinHash, pin);
        var hash = c.D.PinHash; c.D.PinHash = null; c.St.Retries = 0;
        if (!ok) { c.Step = "pin_new"; return Prompt(c, t.Add("Those PINs didn't match. Let's try again.")); }
        var (user, bad) = await AuthedUserAsync(c, t);
        if (bad is not null) return bad;
        await pins.SetHashAsync(user!.Id, hash!);
        await mailer.SendAsync(PhoneTemplates.PinChanged(user.Email!));
        await audit.WriteAsync("phone.pin_changed", user.Id, null, "channel=" + c.Chan);
        c.Step = "auth_menu";
        return Prompt(c, t.Add("Your PIN was changed."));
    }

    // ---- SMS-only helpers ---------------------------------------------------------------------------------------------------------------

    /// <summary>RESET by text: the same reply for every number; a link goes only to a verified account number (and at most 5 texts per hour per number).</summary>
    public async Task<Turn> SmsResetAsync(Conv c, Turn t)
    {
        var user = await ident.FindByPhoneAsync(c.St.Phone);
        if (user is not null && await MayTextAsync(c.St.Phone))
        {
            var url = await links.CreateResetAsync(users, user);
            await sms.SendAsync(c.St.Phone, $"{O.BrandName}: reset your password (valid {O.SignupLinkMinutes} minutes, works once): {url}");
            await mailer.SendAsync(PhoneTemplates.ResetByText(user.Email!));
            await audit.WriteAsync("password.reset_requested", user.Id, user.Email, "channel=sms");
        }
        return t.Add("If this number belongs to an account, we texted a password reset link.");
    }
}
