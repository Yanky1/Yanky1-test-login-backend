using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Solvelt.Api.Options;

namespace Solvelt.Api.Security;

public interface ITurnstileVerifier
{
    Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct = default);
}

public class TurnstileVerifier(HttpClient http, IOptions<TurnstileOptions> opt, ILogger<TurnstileVerifier> log) : ITurnstileVerifier
{
    private record Result([property: JsonPropertyName("success")] bool Success);

    public async Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048) return false;
        var o = opt.Value;
        if (o.UseTestStub)
        {
            // Emulates Cloudflare's documented dummy secrets: 1x… always passes, 2x… always fails.
            return o.SecretKey.StartsWith("1x", StringComparison.Ordinal);
        }
        try
        {
            var form = new Dictionary<string, string> { ["secret"] = o.SecretKey, ["response"] = token };
            if (!string.IsNullOrEmpty(remoteIp)) form["remoteip"] = remoteIp;
            var res = await http.PostAsync("https://challenges.cloudflare.com/turnstile/v0/siteverify", new FormUrlEncodedContent(form), ct);
            var body = await res.Content.ReadFromJsonAsync<Result>(ct);
            return body?.Success == true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Turnstile verification unavailable");
            return false; // fail closed
        }
    }
}
