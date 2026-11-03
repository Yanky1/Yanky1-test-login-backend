using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Solvelt.Api.Options;

namespace Solvelt.Api.Phone;

public interface IWebhookSignatureValidator
{
    /// <summary>Validates the provider signature of a form-encoded webhook. <paramref name="url"/> is the full URL the provider called.</summary>
    bool IsValid(string url, IEnumerable<KeyValuePair<string, string>> form, string? signature);
}

/// <summary>
/// Twilio / SignalWire (LaML) scheme: base64(HMAC-SHA1(key, url + each POST parameter sorted by name as name+value)).
/// Header: X-Twilio-Signature (SignalWire also sends X-SignalWire-Signature with the same value for LaML webhooks).
/// </summary>
public class LamlSignatureValidator(IOptions<PhoneOptions> opt) : IWebhookSignatureValidator
{
    public static string Sign(string key, string url, IEnumerable<KeyValuePair<string, string>> form)
    {
        var sb = new StringBuilder(url);
        foreach (var kv in form.OrderBy(k => k.Key, StringComparer.Ordinal).ThenBy(k => k.Value, StringComparer.Ordinal))
            sb.Append(kv.Key).Append(kv.Value);
        return Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public bool IsValid(string url, IEnumerable<KeyValuePair<string, string>> form, string? signature)
    {
        var o = opt.Value;
        var key = string.IsNullOrEmpty(o.SigningKey) ? o.AuthToken : o.SigningKey;
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(signature)) return false;
        var expected = Sign(key, url, form);
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature));
    }
}
