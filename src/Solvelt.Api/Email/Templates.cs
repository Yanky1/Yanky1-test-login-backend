using System.Net;
using System.Text;

namespace Solvelt.Api.Email;

public static class Templates
{
    private static string E(string s) => WebUtility.HtmlEncode(s);

    private static EmailMessage Build(string to, string kind, string subject, string heading, string[] paragraphs,
        (string Label, string Url)? button = null, string? highlight = null)
    {
        var html = new StringBuilder();
        html.Append("<!doctype html><html><body style=\"margin:0;background:#f4f5f7;font-family:Arial,Helvetica,sans-serif;color:#1f2933\">");
        html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr><td align=\"center\" style=\"padding:24px\">");
        html.Append("<table role=\"presentation\" width=\"560\" style=\"max-width:560px;background:#fff;border-radius:8px;padding:32px\"><tr><td>");
        html.Append("<p style=\"margin:0 0 16px;font-weight:bold;color:#0b5fff\">Solvelt</p>");
        html.Append($"<h1 style=\"font-size:20px;margin:0 0 16px\">{E(heading)}</h1>");
        foreach (var p in paragraphs) html.Append($"<p style=\"line-height:1.5\">{E(p)}</p>");
        if (highlight is not null)
            html.Append($"<p style=\"font-size:32px;letter-spacing:6px;font-weight:bold;margin:24px 0\">{E(highlight)}</p>");
        if (button is { } b)
            html.Append($"<p style=\"margin:24px 0\"><a href=\"{E(b.Url)}\" style=\"background:#0b5fff;color:#fff;padding:12px 20px;border-radius:6px;text-decoration:none\">{E(b.Label)}</a></p>");
        html.Append("<p style=\"color:#6b7280;font-size:12px;margin-top:32px\">You received this email because of activity on your Solvelt seller account.</p>");
        html.Append("</td></tr></table></td></tr></table></body></html>");

        var text = new StringBuilder();
        text.AppendLine("Solvelt").AppendLine().AppendLine(heading).AppendLine();
        foreach (var p in paragraphs) text.AppendLine(p).AppendLine();
        if (highlight is not null) text.AppendLine(highlight).AppendLine();
        if (button is { } bt) text.AppendLine($"{bt.Label}: {bt.Url}").AppendLine();
        return new EmailMessage(to, kind, subject, html.ToString(), text.ToString());
    }

    public static EmailMessage VerifyCode(string to, string code) => Build(to, "verify-code", "Your Solvelt verification code",
        "Verify your email", ["Enter this code to verify your email address. It expires in 10 minutes."], highlight: code);

    public static EmailMessage ChangeEmailCode(string to, string code) => Build(to, "change-email-code", "Confirm your new email address",
        "Confirm your new email", ["Enter this code to confirm this address for your Solvelt account. It expires in 10 minutes."], highlight: code);

    public static EmailMessage EmailChangedNotice(string to, string newEmail) => Build(to, "email-changed", "Your Solvelt email address was changed",
        "Email address changed", [$"The email on your account was changed to {newEmail}. If this wasn't you, reset your password immediately and contact support."]);

    public static EmailMessage ApplicationReceived(string to, string vendor) => Build(to, "application-received", "We received your application",
        "Application received", [$"Thanks, {vendor}. Your seller application is pending review. Management will reach out to you."]);

    public static EmailMessage Approved(string to, string vendor) => Build(to, "approved", "Your seller application was approved",
        "You're approved", [$"Good news, {vendor}: your seller application has been approved."]);

    public static EmailMessage NeedsInfo(string to, string vendor, string? note) => Build(to, "needs-info", "We need more information",
        "More information needed", [$"We need a little more information to review {vendor}.", note ?? "Please sign in and update your application."]);

    public static EmailMessage Rejected(string to, string vendor) => Build(to, "rejected", "Update on your seller application",
        "Application not approved", [$"Unfortunately we couldn't approve the application for {vendor} at this time."]);

    public static EmailMessage Locked(string to, int minutes) => Build(to, "locked", "Your Solvelt account was locked",
        "Account temporarily locked", [$"Too many failed sign-in attempts. Your account is locked for {minutes} minutes and you were signed out everywhere. If this wasn't you, reset your password."]);

    public static EmailMessage ResetPassword(string to, string link) => Build(to, "reset-password", "Reset your Solvelt password",
        "Reset your password", ["Use the button below to choose a new password. The link is valid for 30 minutes and works once."],
        ("Reset password", link));

    public static EmailMessage PasswordChanged(string to) => Build(to, "password-changed", "Your Solvelt password was changed",
        "Password changed", ["Your password was changed and you were signed out of all devices. If this wasn't you, reset your password immediately."]);
}
