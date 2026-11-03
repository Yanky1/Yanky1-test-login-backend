namespace Solvelt.Api.Products;

/// <summary>Product identifier validation (UPC-A, EAN-8, EAN-13, GTIN-14, ISBN-13, ISBN-10) with check digits.</summary>
public static class Barcode
{
    public record Result(bool Valid, string Normalized, string? Type, string? Error);

    public static string Strip(string? s) => new((s ?? "").Where(c => c is not (' ' or '-')).Select(char.ToUpperInvariant).ToArray());

    /// <summary>GS1 check digit: digits right-to-left alternate weights 3,1 (excluding the check digit itself).</summary>
    public static bool Gs1Valid(string digits)
    {
        if (digits.Length < 8 || digits.Any(c => c is < '0' or > '9')) return false;
        var sum = 0;
        for (var i = 0; i < digits.Length - 1; i++)
        {
            var d = digits[digits.Length - 2 - i] - '0';
            sum += i % 2 == 0 ? d * 3 : d;
        }
        return (10 - sum % 10) % 10 == digits[^1] - '0';
    }

    public static bool Isbn10Valid(string s)
    {
        if (s.Length != 10) return false;
        var sum = 0;
        for (var i = 0; i < 10; i++)
        {
            int v;
            if (i == 9 && s[i] == 'X') v = 10;
            else if (s[i] is >= '0' and <= '9') v = s[i] - '0';
            else return false;
            sum += v * (10 - i);
        }
        return sum % 11 == 0;
    }

    public static Result Check(string? input)
    {
        var s = Strip(input);
        if (s.Length == 0) return new(false, "", null, "Enter a barcode.");
        if (s.Length == 10)
            return Isbn10Valid(s) ? new(true, s, "ISBN-10", null) : new(false, s, null, "This ISBN-10 has an invalid check digit.");
        if (s.Any(c => c is < '0' or > '9')) return new(false, s, null, "Barcodes contain digits only.");
        var type = s.Length switch { 8 => "EAN-8", 12 => "UPC-A", 13 => s.StartsWith("978") || s.StartsWith("979") ? "ISBN-13" : "EAN-13", 14 => "GTIN-14", _ => null };
        if (type is null) return new(false, s, null, "Use 8 (EAN-8), 10 (ISBN-10), 12 (UPC-A), 13 (EAN-13/ISBN-13) or 14 (GTIN-14) digits.");
        return Gs1Valid(s) ? new(true, s, type, null) : new(false, s, type, $"This {type} has an invalid check digit - please re-check the number.");
    }
}
