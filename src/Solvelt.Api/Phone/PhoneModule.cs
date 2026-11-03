using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Email;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Options;
using Solvelt.Api.Services;

namespace Solvelt.Api.Phone;

public static class PhoneModule
{
    /// <summary>Registers everything for the phone/SMS channels. The endpoints are only mapped when Phone:Enabled is true.</summary>
    public static WebApplicationBuilder AddPhoneChannels(this WebApplicationBuilder b)
    {
        var cfg = b.Configuration; var s = b.Services;
        s.Configure<PhoneOptions>(cfg.GetSection(PhoneOptions.Section));
        s.Configure<AiOptions>(cfg.GetSection(AiOptions.Section));
        var po = cfg.GetSection(PhoneOptions.Section).Get<PhoneOptions>() ?? new PhoneOptions();
        var ao = cfg.GetSection(AiOptions.Section).Get<AiOptions>() ?? new AiOptions();
        var dev = b.Environment.IsDevelopment() || b.Environment.IsEnvironment("Testing");

        s.AddSingleton<PhoneRateLimiter>();
        s.AddSingleton<SmsSink>();
        s.AddSingleton<FakeConversationAi>();
        s.AddScoped<PinService>();
        s.AddScoped<SmsService>();
        s.AddScoped<PhoneIdentityService>();
        s.AddScoped<PhoneLinkService>();
        s.AddScoped<PhoneEngine>();
        s.AddScoped<IWebhookSignatureValidator, LamlSignatureValidator>();
        s.AddSingleton<IVoiceResponseBuilder, VoiceResponseBuilder>();
        if (po.Provider.Equals("Sink", StringComparison.OrdinalIgnoreCase)) s.AddScoped<ISmsSender, SinkSmsSender>();
        else s.AddHttpClient<ISmsSender, LamlSmsSender>();
        if (ao.Provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)) s.AddSingleton<IConversationAi, AnthropicConversationAi>();
        else s.AddSingleton<IConversationAi>(sp => sp.GetRequiredService<FakeConversationAi>());

        if (po.Enabled && !dev)
        {
            if (po.Provider.Equals("Sink", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Phone:Provider=Sink may only be used in Development/Testing.");
            if (string.IsNullOrEmpty(po.SigningKey) && string.IsNullOrEmpty(po.AuthToken)) throw new InvalidOperationException("Phone:SigningKey (or AuthToken) is required to validate provider webhooks.");
            if (string.IsNullOrEmpty(po.PublicBaseUrl)) throw new InvalidOperationException("Phone:PublicBaseUrl is required: webhook signatures cover the exact public URL.");
            if (ao.Provider.Equals("Fake", StringComparison.OrdinalIgnoreCase)) b.Logging.AddFilter("Solvelt.Phone", LogLevel.Warning); // keyword-only assistant: allowed, but not "AI"
        }
        if (po.Enabled && ao.Provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(ao.ApiKey) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
            throw new InvalidOperationException("Ai:Provider=Anthropic needs Ai:ApiKey or ANTHROPIC_API_KEY.");
        return b;
    }

    public static void MapPhoneChannels(this WebApplication app, RouteGroupBuilder v1)
    {
        if (!app.Services.GetRequiredService<IOptions<PhoneOptions>>().Value.Enabled) return;
        v1.MapPhoneWebhooks();
        v1.MapPhoneAccountEndpoints();
        if (app.Environment.IsEnvironment("Testing"))
        {
            var t = app.MapGroup("/api/test");
            t.MapGet("/sms", (string? to, SmsSink sink) => Results.Ok(sink.All().Where(m => to is null || m.To == to)));
            t.MapDelete("/sms", (SmsSink sink) => { sink.Clear(); return Results.NoContent(); });
        }
        if (app.Environment.IsDevelopment()) app.MapPhoneSimulator();
    }
}

public static class PhoneWebhooks
{
    private const string Base = "/api/v1/channels";
    private static readonly HashSet<string> Stop = ["STOP", "STOPALL", "UNSUBSCRIBE", "CANCEL", "END", "QUIT"];
    private static readonly HashSet<string> Start = ["START", "UNSTOP"];
    private static readonly HashSet<string> Help = ["HELP", "INFO"];

    private static string NormKeyword(string? body) => new string((body ?? "").Where(c => char.IsLetter(c) || c == ' ').ToArray()).Trim().ToUpperInvariant();

    /// <summary>The exact public URL the provider called (signed). PublicBaseUrl wins so a reverse proxy cannot change it.</summary>
    internal static string PublicUrl(HttpContext ctx, PhoneOptions o)
    {
        var path = ctx.Request.PathBase + ctx.Request.Path + ctx.Request.QueryString;
        return !string.IsNullOrEmpty(o.PublicBaseUrl) ? o.PublicBaseUrl.TrimEnd('/') + path : $"{ctx.Request.Scheme}://{ctx.Request.Host}{path}";
    }

    private record Hook(Dictionary<string, string>? Form, IResult? Error);

    /// <summary>Per-IP limit, form body, provider signature. 403 for anything unsigned or tampered with.</summary>
    private static async Task<Hook> ReadAsync(HttpContext ctx, IWebhookSignatureValidator sig, PhoneOptions o, PhoneRateLimiter rl)
    {
        if (!rl.Allow("ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "?"), o.IpPerMinute)) return new(null, Results.StatusCode(429));
        if (!ctx.Request.HasFormContentType) return new(null, Results.StatusCode(403));
        var f = await ctx.Request.ReadFormAsync();
        var form = f.ToDictionary(k => k.Key, k => k.Value.ToString());
        var signature = ctx.Request.Headers["X-Twilio-Signature"].ToString();
        if (signature.Length == 0) signature = ctx.Request.Headers["X-SignalWire-Signature"].ToString();
        if (!sig.IsValid(PublicUrl(ctx, o), form, signature)) return new(null, Results.StatusCode(403));
        return new(form, null);
    }

    private static IResult Xml(string xml) => Results.Content(xml, "text/xml", Encoding.UTF8);

    private static string F(Dictionary<string, string> f, string k) => f.TryGetValue(k, out var v) ? v : "";

    public static void MapPhoneWebhooks(this RouteGroupBuilder g)
    {
        var c = g.MapGroup("/channels").AllowAnonymous();

        // ---- voice -----------------------------------------------------------------------------------------------------------------
        c.MapPost("/voice/incoming", (HttpContext ctx, IWebhookSignatureValidator sig, IOptions<PhoneOptions> po, PhoneRateLimiter rl, AppDbContext db,
            PhoneEngine engine, IVoiceResponseBuilder vb, TimeProvider time) => VoiceAsync(ctx, sig, po.Value, rl, db, engine, vb, time, step: false));
        c.MapPost("/voice/step", (HttpContext ctx, IWebhookSignatureValidator sig, IOptions<PhoneOptions> po, PhoneRateLimiter rl, AppDbContext db,
            PhoneEngine engine, IVoiceResponseBuilder vb, TimeProvider time) => VoiceAsync(ctx, sig, po.Value, rl, db, engine, vb, time, step: true));
        c.MapPost("/voice/status", async (HttpContext ctx, IWebhookSignatureValidator sig, IOptions<PhoneOptions> po, PhoneRateLimiter rl, AppDbContext db) =>
        {
            var h = await ReadAsync(ctx, sig, po.Value, rl);
            if (h.Error is not null) return h.Error;
            if (F(h.Form!, "CallStatus") is "completed" or "failed" or "busy" or "no-answer" or "canceled" && F(h.Form!, "CallSid") is { Length: > 0 } sid)
                await db.PhoneStates.Where(s => s.Key == "voice:" + sid).ExecuteDeleteAsync(); // a finished call leaves nothing behind
            return Results.NoContent();
        });

        // ---- sms -------------------------------------------------------------------------------------------------------------------
        c.MapPost("/sms/inbound", (HttpContext ctx, IWebhookSignatureValidator sig, IOptions<PhoneOptions> po, PhoneRateLimiter rl, AppDbContext db,
            PhoneEngine engine, SmsService sms, Crypto crypto, TimeProvider time, IAuditLog audit) => SmsAsync(ctx, sig, po.Value, rl, db, engine, sms, crypto, time, audit));
        c.MapPost("/sms/status", async (HttpContext ctx, IWebhookSignatureValidator sig, IOptions<PhoneOptions> po, PhoneRateLimiter rl, AppDbContext db, TimeProvider time) =>
        {
            var h = await ReadAsync(ctx, sig, po.Value, rl);
            if (h.Error is not null) return h.Error;
            // 21610 = the recipient replied STOP to the carrier/provider directly: honour it as our own opt-out.
            if (F(h.Form!, "ErrorCode") == "21610" && PhoneFormat.Normalize(F(h.Form!, "To"), po.Value.DefaultRegion) is { } to && !await db.PhoneOptOuts.AnyAsync(x => x.Phone == to))
            { db.PhoneOptOuts.Add(new PhoneOptOut { Phone = to, At = time.UtcNow() }); await db.SaveChangesAsync(); }
            return Results.NoContent();
        });

        // ---- links (texted to the seller) --------------------------------------------------------------------------------------------
        c.MapPost("/links/redeem", async (LinkTokenRequest r, PhoneLinkService links, UserManager<AppUser> users, IAuditLog audit) =>
        {
            var generic = () => Results.Problem("This link is invalid or has expired.", statusCode: 400, extensions: new Dictionary<string, object?> { ["code"] = "invalid_link" });
            var l = await links.FindAsync(r.Token);
            if (l is null) return generic();
            if (l.Purpose == "signup") return Results.Ok(new { purpose = "signup", phoneEnding = l.Phone is { Length: > 4 } p ? p[^4..] : "" });
            var user = l.UserId is { } id ? await users.FindByIdAsync(id.ToString()) : null;
            var payload = links.Unprotect(l);
            if (user is null || payload is null || !await links.ConsumeAsync(l)) return generic();
            await audit.WriteAsync("phone.link_redeemed", user.Id, user.Email, "purpose=reset");
            return Results.Ok(new { purpose = "reset", email = user.Email, token = payload });
        }).RequireRateLimiting("sensitive");

        c.MapPost("/links/complete-signup", async (CompleteSignupRequest r, PhoneLinkService links, PinService pins, UserManager<AppUser> users, AuthFlows flows,
            OneTimeCodeService codes, Mailer mailer, AccountSecurityService security, IAuditLog audit, IOptions<AppOptions> app) =>
        {
            var l = await links.FindAsync(r.Token);
            if (l is null || l.Purpose != "signup") return Results.Problem("This link is invalid or has expired.", statusCode: 400, extensions: new Dictionary<string, object?> { ["code"] = "invalid_link" });
            if (!Validation.IsEmail(r.Email)) return Validation.Invalid("email", "Enter a valid email address.");
            if (!r.AcceptTerms) return Validation.Invalid("acceptTerms", "You must accept the Terms.");
            var email = Crypto.NormalizeEmail(r.Email!);
            var pw = PasswordRulesValidator.Describe(r.Password, email);
            if (pw.Count > 0) return Validation.Invalid("password", pw.ToArray());
            if (!await links.ConsumeAsync(l)) return Results.Problem("This link is invalid or has expired.", statusCode: 400, extensions: new Dictionary<string, object?> { ["code"] = "invalid_link" });
            var existing = await users.FindByEmailAsync(email);
            if (existing is { EmailConfirmed: true })
            {
                var fe = app.Value.FrontendBaseUrl.TrimEnd('/');
                await mailer.SendAsync(Templates.SignupExisting(email, fe + "/login", fe + "/forgot-password"));
                await audit.WriteAsync("signup.existing_notified", existing.Id, email, "channel=phone-link");
            }
            else
            {
                if (existing is not null) await security.DeleteUnverifiedAsync(existing);
                AppUser user;
                try
                {
                    user = await flows.CreateSellerUserAsync(email, r.Password, false, true);
                    user.PhoneNumber = l.Phone; user.PhoneNumberConfirmed = true;
                    var upd = await users.UpdateAsync(user);
                    if (!upd.Succeeded) { await security.DeleteUnverifiedAsync(user); return Results.Accepted(value: AuthEndpoints.SignupAccepted); } // number meanwhile taken: same generic answer
                }
                catch (AccountCreationException ex) { return Validation.Invalid("password", ex.Errors); }
                if (links.Unprotect(l) is { } pinHash) await pins.SetHashAsync(user.Id, pinHash);
                var issued = await codes.IssueAsync(user.Id, "verify-email");
                if (issued.Code is not null) await mailer.SendAsync(Templates.VerifyCode(email, issued.Code));
                await audit.WriteAsync("signup.created", user.Id, email, "method=phone-link");
            }
            return Results.Accepted(value: AuthEndpoints.SignupAccepted);
        }).RequireRateLimiting("auth");
    }

    private static async Task<IResult> VoiceAsync(HttpContext ctx, IWebhookSignatureValidator sig, PhoneOptions o, PhoneRateLimiter rl, AppDbContext db,
        PhoneEngine engine, IVoiceResponseBuilder vb, TimeProvider time, bool step)
    {
        var h = await ReadAsync(ctx, sig, o, rl);
        if (h.Error is not null) return h.Error;
        var f = h.Form!;
        var sid = F(f, "CallSid");
        if (sid.Length is 0 or > 64) return Results.StatusCode(403);
        var from = PhoneFormat.Normalize(F(f, "From"), o.DefaultRegion) ?? "";
        if (!rl.Allow("num:" + (from.Length > 0 ? from : sid), o.NumberPerMinute)) return Results.StatusCode(429);

        var now = time.UtcNow();
        var key = "voice:" + sid;
        var st = await db.PhoneStates.FirstOrDefaultAsync(s => s.Key == key);
        if (st is not null && st.ExpiresAt <= now) { db.PhoneStates.Remove(st); await db.SaveChangesAsync(); st = null; }
        var fresh = st is null;
        if (st is null)
        {
            st = new PhoneState { Key = key, Channel = "voice", Phone = from, Step = "menu", CreatedAt = now, ExpiresAt = now.AddMinutes(o.CallStateMinutes) };
            db.PhoneStates.Add(st);
        }
        var conv = new Conv(st, Chan.Voice);
        Turn turn;
        var seqOk = int.TryParse(ctx.Request.Query["seq"], out var seq) && seq == st.Seq;
        if (!step || fresh || !seqOk)
            turn = engine.Open(conv); // new call, replayed "incoming", or a stale/replayed step: just repeat the current prompt, nothing is processed
        else
        {
            var digits = F(f, "Digits"); var speech = F(f, "SpeechResult");
            turn = await engine.HandleAsync(conv, digits.Length > 0 ? digits : speech, dtmf: digits.Length > 0, ctx.RequestAborted);
            st.Seq++;
        }
        st.ExpiresAt = now.AddMinutes(o.CallStateMinutes);
        conv.Save();
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { return Results.StatusCode(409); } // two webhooks raced on the same call; the provider retries
        return Xml(RenderVoice(vb.Create(), turn, st.Seq).ToString());
    }

    internal static VoiceResponse RenderVoice(VoiceResponse r, Turn t, int seq)
    {
        var action = $"{Base}/voice/step?seq={seq}";
        if (t.Dial is not null) { foreach (var s in t.Say) r.Say(s); return r.Dial(t.Dial); }
        if (t.Hangup || t.Ask is null) { foreach (var s in t.Say) r.Say(s); return r.Hangup(); }
        return r.Gather(action, t.Say, t.Ask.Speech, t.Ask.Digits);
    }

    private static async Task<IResult> SmsAsync(HttpContext ctx, IWebhookSignatureValidator sig, PhoneOptions o, PhoneRateLimiter rl, AppDbContext db, PhoneEngine engine,
        SmsService sms, Crypto crypto, TimeProvider time, IAuditLog audit)
    {
        var h = await ReadAsync(ctx, sig, o, rl);
        if (h.Error is not null) return h.Error;
        var f = h.Form!;
        var from = PhoneFormat.Normalize(F(f, "From"), o.DefaultRegion);
        var msgSid = F(f, "MessageSid");
        if (from is null || msgSid.Length is 0 or > 64) return Results.StatusCode(403);
        if (!rl.Allow("num:" + from, o.NumberPerMinute)) return Results.StatusCode(429);
        var now = time.UtcNow();

        // Replay protection: a message id is processed once; a replay gets an empty reply.
        db.PhoneSeen.Add(new PhoneSeen { Key = "sms:" + msgSid, ExpiresAt = now.AddDays(1) });
        try { await db.SaveChangesAsync(); } catch (DbUpdateException) { db.ChangeTracker.Clear(); return Xml(VoiceResponse.Message(null)); }
        if (Random.Shared.Next(50) == 0) await db.PhoneSeen.Where(x => x.ExpiresAt < now).ExecuteDeleteAsync();

        var body = F(f, "Body");
        var kw = NormKeyword(body);
        var brand = o.BrandName;

        // 1. Compliance keywords: deterministic, BEFORE any AI, before any state.
        if (Stop.Contains(kw))
        {
            if (!await db.PhoneOptOuts.AnyAsync(x => x.Phone == from)) { db.PhoneOptOuts.Add(new PhoneOptOut { Phone = from, At = now }); }
            db.PhoneStates.RemoveRange(db.PhoneStates.Where(s => s.Phone == from && s.Channel == "sms"));
            await db.SaveChangesAsync();
            await audit.WriteAsync("sms.opt_out", null, null, $"phone={PhoneFormat.Mask(from)}");
            return Xml(VoiceResponse.Message($"{brand}: You have been unsubscribed and will receive no more messages. Reply START to resubscribe."));
        }
        var optedOut = await db.PhoneOptOuts.AnyAsync(x => x.Phone == from);
        if (Start.Contains(kw) || (optedOut && kw == "YES"))
        {
            await db.PhoneOptOuts.Where(x => x.Phone == from).ExecuteDeleteAsync();
            await audit.WriteAsync("sms.opt_in", null, null, $"phone={PhoneFormat.Mask(from)}");
            return Xml(VoiceResponse.Message($"{brand}: You are resubscribed. Msg&data rates may apply. Reply HELP for help, STOP to opt out."));
        }
        if (Help.Contains(kw))
            return Xml(VoiceResponse.Message($"{brand} seller account help: reply LOGIN to sign in, JOIN to sign up, STATUS, RESET (password link). Contact {o.HelpContact}. Msg&data rates may apply. Reply STOP to opt out."));
        if (optedOut) return Xml(VoiceResponse.Message(null)); // STOP blocks everything but START/HELP

        // 2. Conversation state (per number, expiring).
        var key = "sms:" + crypto.Hmac(from);
        var st = await db.PhoneStates.FirstOrDefaultAsync(s => s.Key == key);
        if (st is not null && st.ExpiresAt <= now) { db.PhoneStates.Remove(st); await db.SaveChangesAsync(); st = null; }
        if (st is null) { st = new PhoneState { Key = key, Channel = "sms", Phone = from, Step = "menu", CreatedAt = now }; db.PhoneStates.Add(st); }
        st.ExpiresAt = now.AddMinutes(o.SmsSessionMinutes);
        var conv = new Conv(st, Chan.Sms);
        // Commands win over whatever step the conversation is in.
        var cmd = PhoneEngine.Keyword(body);
        if (cmd is not null && cmd != PhoneEngine.Internal)
            conv.Step = st.UserId is not null && st.AuthUntil > now ? "auth_menu" : "menu";
        if (conv.Step == "menu" && cmd == AiActions.RequestResetLink)
        { var t0 = await engine.SmsResetAsync(conv, new Turn()); conv.Save(); await db.SaveChangesAsync(); return Xml(VoiceResponse.Message(string.Join(" ", t0.Say))); }

        var turn = await engine.HandleAsync(conv, body, dtmf: false, ctx.RequestAborted);
        conv.Save();
        await db.SaveChangesAsync();
        return Xml(VoiceResponse.Message(string.Join(" ", turn.Say)));
    }
}

public record LinkTokenRequest(string? Token);
public record CompleteSignupRequest(string? Token, string? Email, string? Password, bool AcceptTerms);
