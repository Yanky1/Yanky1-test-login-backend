using System.Net;
using System.Text;

namespace Solvelt.Api.Email;

/// <summary>
/// The one email shell for every transactional message (Figma restyle): table-based, inline styles only, email-client safe.
/// Fonts are 'Plus Jakarta Sans' with Arial fallback for body; headings use Georgia (Playfair Display fallback), so no web font is required.
/// All dynamic text is HTML-encoded here. Colours: docs/DESIGN.md in the frontend repo (tokens).
/// </summary>
public static class EmailLayout
{
    /// <summary>Support address shown in the footer. Set at start-up from the configured help contact (PhoneOptions.HelpContact).</summary>
    public static string SupportEmail { get; set; } = "support@example.com";

    private const string Sans = "'Plus Jakarta Sans',Arial,Helvetica,sans-serif";
    private const string Serif = "'Playfair Display',Georgia,'Times New Roman',serif";
    private static string E(string s) => WebUtility.HtmlEncode(s);

    public static string Html(string preheader, string heading, IEnumerable<string> paragraphs, (string Label, string Url)? button, string? highlight, string note)
    {
        var h = new StringBuilder();
        h.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta name=\"color-scheme\" content=\"light\"><title>").Append(E(heading)).Append("</title></head>");
        h.Append($"<body style=\"margin:0;padding:0;background:#f4f6f4;font-family:{Sans};color:#0d231e\">");
        if (!string.IsNullOrEmpty(preheader))
            h.Append($"<div style=\"display:none;max-height:0;overflow:hidden;opacity:0;mso-hide:all\">{E(preheader)}</div>");
        h.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" bgcolor=\"#f4f6f4\" style=\"background:#f4f6f4\"><tr><td align=\"center\" style=\"padding:32px 16px\">");
        h.Append("<table role=\"presentation\" width=\"560\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" bgcolor=\"#ffffff\" style=\"width:100%;max-width:560px;background:#ffffff;border:1px solid #e2eae4;border-radius:16px;overflow:hidden\">");
        // header banner: solid colour fallback (bgcolor) for Outlook, gradient for everyone else
        h.Append("<tr><td bgcolor=\"#11362e\" style=\"background:#11362e;background-image:linear-gradient(135deg,#18483e 0%,#11362e 55%,#0a241e 100%);padding:28px 32px\">");
        h.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\"><tr>");
        h.Append($"<td width=\"44\" height=\"44\" align=\"center\" valign=\"middle\" bgcolor=\"#d4af37\" style=\"width:44px;height:44px;background:#d4af37;border-radius:12px;font-family:{Serif};font-size:24px;font-weight:bold;color:#071612\">S</td>");
        h.Append($"<td style=\"padding-left:12px;font-family:{Sans}\"><span style=\"font-size:20px;font-weight:bold;color:#ffffff;letter-spacing:-0.3px\">Solvelt</span> <span style=\"color:#e7c655;font-size:14px\">&#10022;</span><br>");
        h.Append("<span style=\"font-size:10px;font-weight:bold;letter-spacing:3px;color:#f2dc87;text-transform:uppercase\">Market</span></td>");
        h.Append("</tr></table></td></tr>");
        h.Append("<tr><td style=\"padding:32px 32px 8px 32px\">");
        h.Append($"<h1 style=\"font-family:{Serif};font-size:26px;line-height:34px;font-weight:bold;letter-spacing:-0.5px;color:#0d231e;margin:0 0 16px\">{E(heading)}</h1>");
        foreach (var p in paragraphs)
            h.Append($"<p style=\"font-family:{Sans};font-size:16px;line-height:24px;color:#40564e;margin:0 0 16px\">{E(p)}</p>");
        if (highlight is not null)
            h.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"margin:8px 0 24px\"><tr><td align=\"center\" bgcolor=\"#f4f7f5\" style=\"background:#f4f7f5;border:1px solid #e2eae4;border-radius:12px;padding:20px 16px;font-family:'Courier New',Courier,monospace;font-size:32px;line-height:40px;font-weight:bold;letter-spacing:8px;color:#0d231e\">{E(highlight)}</td></tr></table>");
        if (button is { } b)
            h.Append($"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"margin:8px 0 24px\"><tr><td align=\"center\" bgcolor=\"#1f4d40\" style=\"background:#1f4d40;border-radius:12px\"><a href=\"{E(b.Url)}\" style=\"display:inline-block;padding:14px 28px;font-family:{Sans};font-size:16px;font-weight:bold;line-height:20px;color:#ffffff;text-decoration:none;border-radius:12px\">{E(b.Label)}</a></td></tr></table>");
        h.Append("</td></tr>");
        h.Append("<tr><td style=\"padding:0 32px\"><table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\"><tr><td style=\"border-top:1px solid #e9efea;font-size:0;line-height:0\">&nbsp;</td></tr></table></td></tr>");
        h.Append($"<tr><td style=\"padding:16px 32px 28px 32px;font-family:{Sans};font-size:12px;line-height:18px;color:#5b7369\">");
        h.Append($"<p style=\"margin:0 0 8px\">{E(note)}</p>");
        h.Append($"<p style=\"margin:0\">Enquiries: <a href=\"mailto:{E(SupportEmail)}\" style=\"color:#22634d;text-decoration:underline\">{E(SupportEmail)}</a> &nbsp;&middot;&nbsp; <b style=\"color:#0d231e\">Solvelt</b> <span style=\"font-size:9px;font-weight:bold;letter-spacing:2px;text-transform:uppercase\">Market</span></p>");
        h.Append("</td></tr></table></td></tr></table></body></html>");
        return h.ToString();
    }

    public static string Text(string heading, IEnumerable<string> paragraphs, (string Label, string Url)? button, string? highlight)
    {
        var t = new StringBuilder();
        t.AppendLine("Solvelt Market").AppendLine().AppendLine(heading).AppendLine();
        foreach (var p in paragraphs) t.AppendLine(p).AppendLine();
        if (highlight is not null) t.AppendLine(highlight).AppendLine();
        if (button is { } b) t.AppendLine($"{b.Label}: {b.Url}").AppendLine();
        t.AppendLine($"Enquiries: {SupportEmail}");
        return t.ToString();
    }
}
