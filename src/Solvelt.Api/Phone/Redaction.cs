using System.Text.RegularExpressions;

namespace Solvelt.Api.Phone;

/// <summary>
/// Everything that goes to the AI model, an audit row or a log passes through here first. Removes emails, anything that looks like a code/PIN/phone number
/// (4+ digits, also when separated by spaces or dashes), URLs and long opaque tokens.
/// </summary>
public static partial class Redactor
{
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)+")] private static partial Regex EmailRx();
    [GeneratedRegex(@"https?://\S+|www\.\S+")] private static partial Regex UrlRx();
    [GeneratedRegex(@"\+?\d(?:[\s\-.()]?\d){3,}")] private static partial Regex NumberRx();
    [GeneratedRegex(@"\b[A-Za-z0-9_\-]{24,}\b")] private static partial Regex TokenRx();

    public static string Redact(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.Length > 500 ? text[..500] : text;
        t = UrlRx().Replace(t, "[link]");
        t = EmailRx().Replace(t, "[email]");
        t = NumberRx().Replace(t, "[number]");
        t = TokenRx().Replace(t, "[token]");
        return t;
    }

    /// <summary>Digits only, when the whole text is a number entered by keypad or typed (spaces/dashes allowed).</summary>
    [GeneratedRegex(@"^[\d\s\-.]+$")] private static partial Regex OnlyDigitsRx();
    public static string? AsDigits(string? text) =>
        text is not null && OnlyDigitsRx().IsMatch(text.Trim()) ? new string(text.Where(char.IsAsciiDigit).ToArray()) : null;

    [GeneratedRegex(@"(?<![\d])(\d{6})(?![\d])")] private static partial Regex SixRx();
    /// <summary>Six-digit groups in an SMS ("123456 654321" or "code 123456"), in order.</summary>
    public static List<string> SixDigitGroups(string? text) =>
        text is null ? [] : SixRx().Matches(text).Select(m => m.Value).Take(2).ToList();

    public static string? FindEmail(string? text)
    {
        if (text is null) return null;
        var m = EmailRx().Match(text);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }
}

/// <summary>Turns "john dot smith at gmail dot com" / "j o h n at gmail dot com" into an address. Deterministic: the model never sees emails.</summary>
public static class SpokenEmail
{
    public static string? Normalize(string? spoken)
    {
        if (string.IsNullOrWhiteSpace(spoken)) return null;
        if (Redactor.FindEmail(spoken) is { } direct) return direct;
        var words = spoken.ToLowerInvariant().Replace(",", " ").Replace("?", " ").Replace(".", " dot ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sb = new System.Text.StringBuilder();
        foreach (var w in words)
        {
            switch (w)
            {
                case "at": sb.Append('@'); break;
                case "dot" or "period" or "point": sb.Append('.'); break;
                case "dash" or "hyphen" or "minus": sb.Append('-'); break;
                case "underscore": sb.Append('_'); break;
                case "plus": sb.Append('+'); break;
                case "zero": sb.Append('0'); break;
                case "one": sb.Append('1'); break;
                case "two": sb.Append('2'); break;
                case "three": sb.Append('3'); break;
                case "four": sb.Append('4'); break;
                case "five": sb.Append('5'); break;
                case "six": sb.Append('6'); break;
                case "seven": sb.Append('7'); break;
                case "eight": sb.Append('8'); break;
                case "nine": sb.Append('9'); break;
                case "my" or "email" or "is" or "its" or "it's" or "address" or "the": break;
                default: sb.Append(w); break;
            }
        }
        var s = sb.ToString();
        return Endpoints.Validation.IsEmail(s) ? s : null;
    }

    public static string Mask(string email)
    {
        var at = email.IndexOf('@');
        return at <= 0 ? "***" : email[0] + "***" + email[at..];
    }

    /// <summary>For the spoken read-back: letters one by one, symbols named.</summary>
    public static string ReadBack(string email) =>
        string.Join(" ", email.Select(c => c switch { '@' => "at", '.' => "dot", '-' => "dash", '_' => "underscore", '+' => "plus", _ => c.ToString() }));
}
