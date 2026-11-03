using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Domain;
using Solvelt.Api.Phone;
using Solvelt.Api.Services;

namespace Solvelt.Api.Tests;

public class PhoneApiFactory : ApiFactory
{
    public const string Key = "test-signing-key-0123456789";
    public const string Support = "+14155559999";
    public PhoneApiFactory()
    {
        Extra["Phone:Enabled"] = "true";
        Extra["Phone:Provider"] = "Sink";
        Extra["Phone:SigningKey"] = Key;
        Extra["Phone:SendInBackground"] = "false";
        Extra["Phone:SupportNumber"] = Support;
        Extra["Phone:NumberPerMinute"] = "1000";
        Extra["Phone:IpPerMinute"] = "100000";
        Extra["Ai:Provider"] = "Fake";
    }
    public SmsSink Sms => Services.GetRequiredService<SmsSink>();
    public FakeConversationAi Ai => Services.GetRequiredService<FakeConversationAi>();

    private static int _n = 1000;
    public static string NewNumber() => $"+1415555{Interlocked.Increment(ref _n):D4}";

    /// <summary>A verified seller that has a verified phone number and a PIN. Returns (email, phone).</summary>
    public async Task<(string Email, string Phone)> SellerWithPhone(string pin = "482915", string? phone = null, string? email = null, string? password = ApiFactory.Strong)
    {
        phone ??= NewNumber();
        var api = new Api(this);
        email = await api.SignupVerified(email, password ?? ApiFactory.Strong);
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var u = (await users.FindByEmailAsync(email))!;
        u.PhoneNumber = phone; u.PhoneNumberConfirmed = true;
        Assert.True((await users.UpdateAsync(u)).Succeeded);
        await scope.ServiceProvider.GetRequiredService<PinService>().SetAsync(u.Id, pin);
        return (email, phone);
    }

    public string LastSmsCode(string to) => Regex.Matches(Sms.To(to).Last().Text, @"\b(\d{6})\b").Last().Groups[1].Value;
    public string LastSmsLink(string to) => Regex.Match(Sms.To(to).Last().Text, @"https?://\S+").Value;
}

/// <summary>Posts provider-signed form webhooks and reads the TwiML back.</summary>
public class Phones(PhoneApiFactory f)
{
    private readonly HttpClient _http = f.NewClient();
    private readonly Dictionary<string, string> _next = new();
    private int _sms;
    public string LastXml = "";

    public async Task<HttpResponseMessage> Raw(string path, Dictionary<string, string> form, string? signature = null, bool sign = true)
    {
        var url = "https://localhost" + path;
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        if (sign) req.Headers.Add("X-Twilio-Signature", signature ?? LamlSignatureValidator.Sign(PhoneApiFactory.Key, url, form));
        return await _http.SendAsync(req);
    }

    public async Task<string> Call(string sid, string from, string? digits = null, string? speech = null, string? path = null)
    {
        var form = new Dictionary<string, string> { ["CallSid"] = sid, ["From"] = from, ["To"] = "+14155550000" };
        if (digits is not null) form["Digits"] = digits;
        if (speech is not null) form["SpeechResult"] = speech;
        path ??= _next.TryGetValue(sid, out var n) ? n : "/api/v1/channels/voice/incoming";
        var res = await Raw(path, form);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var xml = await res.Content.ReadAsStringAsync();
        var m = Regex.Match(xml, "action=\"([^\"]+)\"");
        if (m.Success) _next[sid] = WebUtility.HtmlDecode(m.Groups[1].Value);
        LastXml = xml;
        return xml;
    }

    public Task<string> Start(string sid, string from) => Call(sid, from);
    public Task<string> Press(string sid, string from, string digits) => Call(sid, from, digits: digits);
    public string NextAction(string sid) => _next[sid];

    public async Task<string> Sms(string from, string body, string? sid = null)
    {
        var res = await Raw("/api/v1/channels/sms/inbound", new() { ["MessageSid"] = sid ?? "SM" + Interlocked.Increment(ref _sms) + Guid.NewGuid().ToString("N")[..12], ["From"] = from, ["To"] = "+14155550000", ["Body"] = body });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return Reply(await res.Content.ReadAsStringAsync());
    }

    public static string Reply(string xml) => string.Concat(XDocument.Parse(xml).Root!.Elements("Message").Select(e => e.Value));
    public static string Says(string xml) => string.Join(" ", XDocument.Parse(xml).Descendants("Say").Select(e => e.Value));
    public static XElement? Gather(string xml) => XDocument.Parse(xml).Descendants("Gather").FirstOrDefault();
    public static bool HungUp(string xml) => XDocument.Parse(xml).Descendants("Hangup").Any();
}
