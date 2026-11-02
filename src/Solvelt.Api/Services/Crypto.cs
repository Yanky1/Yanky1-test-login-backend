using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Solvelt.Api.Options;

namespace Solvelt.Api.Services;

public class Crypto(IOptions<AppOptions> opt)
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(opt.Value.HmacSecret);

    public string Hmac(string value) => Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value)));

    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string RandomToken(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    public static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string SixDigits() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
