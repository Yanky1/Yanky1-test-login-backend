using System.Text.RegularExpressions;

namespace Solvelt.Api.Orders;

/// <summary>Carrier list and tracking-number format checks for the common carriers ("Other" accepts any sane alphanumeric code).</summary>
public static class Tracking
{
    public static readonly string[] Carriers = ["UPS", "USPS", "FedEx", "DHL", "Other"];

    private static readonly Dictionary<string, Regex[]> Formats = new()
    {
        ["UPS"] = [new(@"^1Z[0-9A-Z]{16}$"), new(@"^T\d{10}$")],
        ["USPS"] = [new(@"^\d{20,22}$"), new(@"^[A-Z]{2}\d{9}US$"), new(@"^\d{26,34}$")],
        ["FedEx"] = [new(@"^\d{12}$"), new(@"^\d{15}$"), new(@"^\d{20,22}$")],
        ["DHL"] = [new(@"^\d{10,11}$"), new(@"^JD\d{18}$"), new(@"^[A-Z]{3}\d{7,}$")],
        ["Other"] = [new(@"^[A-Z0-9\-]{3,40}$")],
    };

    public static string Normalize(string? t) => Regex.Replace(t ?? "", @"[\s]", "").ToUpperInvariant();

    public static string? Carrier(string? c) => Carriers.FirstOrDefault(x => x.Equals(c?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool Valid(string carrier, string normalized) => Formats.TryGetValue(carrier, out var f) && f.Any(r => r.IsMatch(normalized));

    public static string? Url(string carrier, string tracking) => carrier switch
    {
        "UPS" => $"https://www.ups.com/track?tracknum={tracking}",
        "USPS" => $"https://tools.usps.com/go/TrackConfirmAction?tLabels={tracking}",
        "FedEx" => $"https://www.fedex.com/fedextrack/?trknbr={tracking}",
        "DHL" => $"https://www.dhl.com/en/express/tracking.html?AWB={tracking}",
        _ => null
    };
}
