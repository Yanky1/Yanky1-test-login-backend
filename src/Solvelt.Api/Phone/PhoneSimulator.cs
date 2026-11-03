using System.Text;
using Microsoft.Extensions.Options;
using Solvelt.Api.Options;

namespace Solvelt.Api.Phone;

/// <summary>
/// Development-only phone simulator: POST /api/dev/phone/call and /api/dev/phone/sms sign a webhook exactly like the provider would, post it to this
/// backend and return the TwiML (or SMS reply) plus the texts that were "sent". Needs Phone:Enabled=true, Phone:Provider=Sink and Phone:SigningKey. See docs/PHONE-SMS.md.
/// </summary>
public static class PhoneSimulator
{
    public record CallRequest(string? From, string? CallSid, string? Digits, string? Speech, string? Action);
    public record SmsRequest(string? From, string? Body);

    public static void MapPhoneSimulator(this WebApplication app)
    {
        var g = app.MapGroup("/api/dev/phone");
        g.MapGet("/sms-outbox", (string? to, SmsSink sink) => Results.Ok(sink.All().Where(m => to is null || m.To == to)));

        g.MapPost("/call", async (CallRequest r, HttpContext ctx, IOptions<PhoneOptions> po, IHttpClientFactory hf, SmsSink sink) =>
        {
            var form = new Dictionary<string, string> { ["CallSid"] = r.CallSid ?? "CAdev0001", ["From"] = r.From ?? "+15551230001", ["To"] = po.Value.FromNumber };
            if (!string.IsNullOrEmpty(r.Digits)) form["Digits"] = r.Digits;
            if (!string.IsNullOrEmpty(r.Speech)) form["SpeechResult"] = r.Speech;
            var path = r.Action ?? "/api/v1/channels/voice/incoming";
            var xml = await PostSigned(ctx, po.Value, hf, path, form);
            return Results.Ok(new { twiml = xml, nextAction = System.Text.RegularExpressions.Regex.Match(xml, "action=\"([^\"]+)\"").Groups[1].Value.Replace("&amp;", "&"), smsOutbox = sink.All() });
        });

        g.MapPost("/sms", async (SmsRequest r, HttpContext ctx, IOptions<PhoneOptions> po, IHttpClientFactory hf, SmsSink sink) =>
        {
            var form = new Dictionary<string, string> { ["MessageSid"] = "SMdev" + Guid.NewGuid().ToString("N")[..16], ["From"] = r.From ?? "+15551230001", ["To"] = po.Value.FromNumber, ["Body"] = r.Body ?? "" };
            var xml = await PostSigned(ctx, po.Value, hf, "/api/v1/channels/sms/inbound", form);
            return Results.Ok(new { twiml = xml, smsOutbox = sink.All() });
        });
    }

    private static async Task<string> PostSigned(HttpContext ctx, PhoneOptions o, IHttpClientFactory hf, string path, Dictionary<string, string> form)
    {
        var origin = !string.IsNullOrEmpty(o.PublicBaseUrl) ? o.PublicBaseUrl.TrimEnd('/') : $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        var url = origin + path;
        var key = string.IsNullOrEmpty(o.SigningKey) ? o.AuthToken : o.SigningKey;
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        req.Headers.Add("X-Twilio-Signature", LamlSignatureValidator.Sign(key, url, form));
        using var http = hf.CreateClient();
        var res = await http.SendAsync(req);
        return $"<!-- HTTP {(int)res.StatusCode} -->\n" + await res.Content.ReadAsStringAsync();
    }
}
