using System.Text.RegularExpressions;

namespace Solvelt.Api.Products;

/// <summary>
/// The seller's search keywords (one free-text field) as a list of terms: the single definition used when the field is WRITTEN (validation) and when it is PUBLISHED to the
/// marketplace (catalog feed). Terms are separated by commas, semicolons or line breaks (a space is part of a term: "coffee cup" is one term). Control characters are stripped,
/// terms are trimmed, empty ones dropped and duplicates (case-insensitive) removed.
/// </summary>
public static partial class SearchTerms
{
    public const int MaxTerms = 20, MaxTermLength = 50;

    // anywhere inside a term: "write to me@example.com" and "call me 555-123-4567" are contact data just like the bare address or number
    [GeneratedRegex(@"[^@\s]+@[^@\s]+\.[^@\s]+")] private static partial Regex EmailAnywhere();
    [GeneratedRegex(@"\+?\d[\d\s().\-]{5,}\d")] private static partial Regex PhoneRun();

    /// <summary>The terms of the raw field, normalised; no caps and no content rules applied.</summary>
    public static List<string> Split(string? raw) =>
        (raw ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string(t.Where(c => !char.IsControl(c)).ToArray()).Trim())
            .Where(t => t.Length > 0).DistinctBy(t => t.ToLowerInvariant()).ToList();

    /// <summary>
    /// Contains an e-mail address or a phone number (a run of digits with spaces, dots, dashes or brackets holding at least 7 digits): personal contact data that does not belong in
    /// public search keywords. A long plain number (a 7-digit part number) is treated the same way; short numbers such as "model 3000" are fine.
    /// </summary>
    public static bool IsContactData(string term) =>
        EmailAnywhere().IsMatch(term) || PhoneRun().Matches(term).Any(m => m.Value.Count(char.IsDigit) >= 7);

    /// <summary>The error to show the seller, or null when the field is acceptable.</summary>
    public static string? Validate(string? raw)
    {
        var terms = Split(raw);
        if (terms.Count > MaxTerms) return $"Use at most {MaxTerms} search terms (separate them with commas).";
        if (terms.Any(t => t.Length > MaxTermLength)) return $"Each search term can be at most {MaxTermLength} characters (separate terms with commas).";
        if (terms.Any(IsContactData)) return "Search terms can't contain e-mail addresses or phone numbers.";
        return null;
    }

    /// <summary>What is published: legacy values that would not pass <see cref="Validate"/> are cleaned here (contact data and over-long terms are dropped, the list is capped).</summary>
    public static List<string> ForFeed(string? raw) =>
        Split(raw).Where(t => t.Length <= MaxTermLength && !IsContactData(t)).Take(MaxTerms).ToList();
}
