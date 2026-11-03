using PhoneNumbers;

namespace Solvelt.Api.Phone;

public static class PhoneFormat
{
    /// <summary>Normalises to E.164 (+15551234567). Returns null for anything that is not a valid, SMS-capable number.</summary>
    public static string? Normalize(string? raw, string defaultRegion = "US")
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 40) return null;
        var util = PhoneNumberUtil.GetInstance();
        try
        {
            var n = util.Parse(raw.Trim(), defaultRegion);
            if (!util.IsValidNumber(n)) return null;
            if (util.GetNumberType(n) is PhoneNumberType.PREMIUM_RATE or PhoneNumberType.SHARED_COST or PhoneNumberType.UAN
                or PhoneNumberType.PAGER or PhoneNumberType.VOICEMAIL) return null;
            return util.Format(n, PhoneNumberFormat.E164);
        }
        catch (NumberParseException) { return null; }
    }

    /// <summary>Keypad entry has no "+": try the region first, then as an international number.</summary>
    public static string? FromKeypad(string? digits, string defaultRegion)
    {
        if (string.IsNullOrEmpty(digits) || !digits.All(char.IsAsciiDigit)) return null;
        return Normalize(digits, defaultRegion) ?? Normalize("+" + digits, defaultRegion);
    }

    /// <summary>Last four digits only; used in anything that may be logged or spoken.</summary>
    public static string Mask(string e164) => e164.Length <= 4 ? "****" : "***" + e164[^4..];
}
