using System.Net.Http.Headers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Email;
using Solvelt.Api.Options;
using Solvelt.Api.Services;

namespace Solvelt.Api.Phone;

public record SinkSms(string To, string Text, DateTime At);

/// <summary>Dev/test: outgoing SMS are kept in memory, like the email sink.</summary>
public class SmsSink(TimeProvider time)
{
    private readonly List<SinkSms> _items = [];
    private readonly object _lock = new();
    public void Add(string to, string text) { lock (_lock) _items.Add(new(to, text, time.GetUtcNow().UtcDateTime)); }
    public IReadOnlyList<SinkSms> All() { lock (_lock) return _items.ToList(); }
    public IReadOnlyList<SinkSms> To(string phone) { lock (_lock) return _items.Where(m => m.To == phone).ToList(); }
    public void Clear() { lock (_lock) _items.Clear(); }
}

public class SinkSmsSender(SmsSink sink) : ISmsSender
{
    public Task SendAsync(string phoneNumber, string message, CancellationToken ct = default) { sink.Add(phoneNumber, message); return Task.CompletedTask; }
}

/// <summary>SignalWire (LaML compatibility API) and Twilio share the same REST shape: POST .../2010-04-01/Accounts/{sid}/Messages.json.</summary>
public class LamlSmsSender(HttpClient http, IOptions<PhoneOptions> opt, ILogger<LamlSmsSender> log) : ISmsSender
{
    public async Task SendAsync(string phoneNumber, string message, CancellationToken ct = default)
    {
        var o = opt.Value;
        var baseUrl = !string.IsNullOrEmpty(o.ApiBaseUrl) ? o.ApiBaseUrl.TrimEnd('/')
            : o.Provider.Equals("Twilio", StringComparison.OrdinalIgnoreCase) ? "https://api.twilio.com"
            : $"https://{o.Space}/api/laml";
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/2010-04-01/Accounts/{Uri.EscapeDataString(o.AccountSid)}/Messages.json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{o.AccountSid}:{o.AuthToken}")));
        var form = new Dictionary<string, string> { ["To"] = phoneNumber, ["From"] = o.FromNumber, ["Body"] = message };
        if (!string.IsNullOrEmpty(o.PublicBaseUrl)) form["StatusCallback"] = o.PublicBaseUrl.TrimEnd('/') + "/api/v1/channels/sms/status";
        req.Content = new FormUrlEncodedContent(form);
        var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"SMS provider returned {(int)res.StatusCode}");
        log.LogInformation("SMS sent via {Provider}", o.Provider);
    }
}

/// <summary>Why an SMS is being sent; decides which consent rules apply.</summary>
public enum SmsKind
{
    /// <summary>A code/link the person just asked for (IVR menu choice, keyword, web button). Allowed unless the number sent STOP.</summary>
    Requested,
    /// <summary>Anything not requested right now: needs the seller's stored SMS opt-in.</summary>
    Notification
}

/// <summary>Every outbound SMS goes through here: honours STOP and the stored opt-in, never logs content, never throws.</summary>
public class SmsService(AppDbContext db, ISmsSender sender, IServiceScopeFactory scopes, IOptions<PhoneOptions> opt, ILogger<SmsService> log, IAuditLog audit)
{
    public async Task<bool> IsOptedOutAsync(string phone) => await db.PhoneOptOuts.AnyAsync(o => o.Phone == phone);

    public async Task<bool> MaySendAsync(string phone, SmsKind kind)
    {
        if (await IsOptedOutAsync(phone)) return false;
        if (kind == SmsKind.Notification)
        {
            var optedIn = await (from u in db.Users join s in db.Sellers on u.Id equals s.UserId
                                 where u.PhoneNumber == phone && u.PhoneNumberConfirmed && s.SmsOptIn select s.Id).AnyAsync();
            if (!optedIn) return false;
        }
        return true;
    }

    /// <summary>Returns false when the message was blocked (STOP / no consent) or could not be queued.</summary>
    public async Task<bool> SendAsync(string phone, string text, SmsKind kind = SmsKind.Requested, bool detached = false)
    {
        if (!await MaySendAsync(phone, kind))
        {
            await audit.WriteAsync("sms.blocked", null, null, $"to={PhoneFormat.Mask(phone)};kind={kind}");
            return false;
        }
        if (detached && opt.Value.SendInBackground)
        {
            _ = Task.Run(async () =>
            {
                try { using var scope = scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<ISmsSender>().SendAsync(phone, text); }
                catch (Exception ex) { log.LogError(ex, "Failed to send SMS"); }
            });
            return true;
        }
        try { await sender.SendAsync(phone, text); return true; }
        catch (Exception ex) { log.LogError(ex, "Failed to send SMS"); return false; }
    }
}
