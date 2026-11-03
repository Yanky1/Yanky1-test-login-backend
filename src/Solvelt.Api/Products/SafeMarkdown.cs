using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Solvelt.Api.Products;

/// <summary>
/// Tiny, dependency-free Markdown subset renderer: headings, paragraphs, bold, italic, inline code, bullet/numbered lists, http(s) links.
/// All input is HTML-encoded FIRST, so raw HTML can never reach the output; link targets are limited to http/https/mailto and get rel="noopener nofollow ugc".
/// </summary>
public static partial class SafeMarkdown
{
    public static string Render(string? md)
    {
        if (string.IsNullOrWhiteSpace(md)) return "";
        var sb = new StringBuilder();
        var lines = md.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string? list = null; var para = new List<string>();
        void FlushPara() { if (para.Count > 0) { sb.Append("<p>").Append(Inline(string.Join(" ", para))).Append("</p>"); para.Clear(); } }
        void CloseList() { if (list is not null) { sb.Append("</").Append(list).Append('>'); list = null; } }
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) { FlushPara(); CloseList(); continue; }
            var h = HeadingRe().Match(line);
            if (h.Success) { FlushPara(); CloseList(); var n = Math.Min(h.Groups[1].Length + 2, 6); sb.Append($"<h{n}>").Append(Inline(h.Groups[2].Value)).Append($"</h{n}>"); continue; }
            var ul = BulletRe().Match(line);
            var ol = NumberRe().Match(line);
            if (ul.Success || ol.Success)
            {
                FlushPara();
                var tag = ul.Success ? "ul" : "ol";
                if (list != tag) { CloseList(); sb.Append('<').Append(tag).Append('>'); list = tag; }
                sb.Append("<li>").Append(Inline((ul.Success ? ul : ol).Groups[1].Value)).Append("</li>");
                continue;
            }
            CloseList();
            para.Add(line.Trim());
        }
        FlushPara(); CloseList();
        return sb.ToString();
    }

    private static string Inline(string text)
    {
        var s = WebUtility.HtmlEncode(text);
        s = CodeRe().Replace(s, "<code>$1</code>");
        s = BoldRe().Replace(s, "<strong>$1</strong>");
        s = ItalicRe().Replace(s, "<em>$1</em>");
        s = LinkRe().Replace(s, m =>
        {
            var url = WebUtility.HtmlDecode(m.Groups[2].Value);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https" or "mailto")) return m.Groups[1].Value;
            return $"<a href=\"{WebUtility.HtmlEncode(u.AbsoluteUri)}\" rel=\"noopener nofollow ugc\" target=\"_blank\">{m.Groups[1].Value}</a>";
        });
        return s;
    }

    [GeneratedRegex(@"^(#{1,3})\s+(.+)$")] private static partial Regex HeadingRe();
    [GeneratedRegex(@"^\s*[-*]\s+(.+)$")] private static partial Regex BulletRe();
    [GeneratedRegex(@"^\s*\d+[.)]\s+(.+)$")] private static partial Regex NumberRe();
    [GeneratedRegex(@"`([^`\n]+)`")] private static partial Regex CodeRe();
    [GeneratedRegex(@"\*\*([^*\n]+)\*\*")] private static partial Regex BoldRe();
    [GeneratedRegex(@"(?<![*\w])\*([^*\n]+)\*(?!\*)")] private static partial Regex ItalicRe();
    [GeneratedRegex(@"\[([^\]\n]+)\]\(([^)\s]+)\)")] private static partial Regex LinkRe();
}
