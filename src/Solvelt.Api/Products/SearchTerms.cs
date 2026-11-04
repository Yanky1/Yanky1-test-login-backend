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
    [GeneratedRegex(@"\+\d[\d\s().\-]{5,}\d")] private static partial Regex InternationalPhone();                         // starts with +
    [GeneratedRegex(@"(?<!\d)\(?\d{3}\)?[\s.\-]?\d{3}[\s.\-]?\d{4}(?!\d)")] private static partial Regex UsPhone();           // (555) 123-4567, 555-123-4567, 555 123 4567, 5551234567
    [GeneratedRegex(@"(?<!\d)\d{10,}(?!\d)")] private static partial Regex LongDigits();                                          // a plain run of 10 or more digits (also a 12-digit UPC: rejected by documented intent)
    [GeneratedRegex(@"(?:19|20)\d\d\s*[-\u2013]\s*(?:19|20)\d\d")] private static partial Regex YearRange();                   // "fits 2019-2023 models" is not a phone number

    /// <summary>The terms of the raw field, normalised; no caps and no content rules applied.</summary>
    public static List<string> Split(string? raw) =>
        (raw ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string(t.Where(c => !char.IsControl(c)).ToArray()).Trim())
            .Where(t => t.Length > 0).DistinctBy(t => t.ToLowerInvariant()).ToList();

    /// <summary>
    /// Contains an e-mail address or a phone number: personal contact data that does not belong in public search keywords. A phone is a run that starts with <c>+</c> (at least 7 digits),
    /// a 10-digit US-style number (with or without brackets, spaces, dots or dashes), or a plain run of 10 or more digits (so a 12-digit UPC is rejected, by documented intent).
    /// Year ranges ("fits 2019-2023 models"), lists of short numbers ("100 200 300 ml set", "2019 2020 2021") and decimals ("1.5 2.5 3.5 4.5 mm") are not phones.
    /// </summary>
    public static bool IsContactData(string term)
    {
        if (EmailAnywhere().IsMatch(term)) return true;
        var t = YearRange().Replace(term, " ");
        return InternationalPhone().Matches(t).Any(m => m.Value.Count(char.IsDigit) >= 7) || UsPhone().IsMatch(t) || LongDigits().IsMatch(t);
    }

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
