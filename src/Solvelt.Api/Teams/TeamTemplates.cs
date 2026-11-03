using System.Net;
using System.Text;
using Solvelt.Api.Email;

namespace Solvelt.Api.Teams;

/// <summary>Team emails (own file so it doesn't collide with the redesign of Email/Templates.cs). HTML + text, with a preheader. Restyle to the design system later.</summary>
public static class TeamTemplates
{
    private static string E(string s) => WebUtility.HtmlEncode(s);

    private static EmailMessage Build(string to, string kind, string subject, string preheader, string heading, string[] paragraphs,
        (string Label, string Url)? button = null)
    {
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"en\"><body style=\"margin:0;background:#f4f5f7;font-family:Arial,Helvetica,sans-serif;color:#1f2933\">");
        html.Append($"<div style=\"display:none;max-height:0;overflow:hidden;opacity:0\">{E(preheader)}</div>");
        html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr><td align=\"center\" style=\"padding:24px\">");
        html.Append("<table role=\"presentation\" width=\"560\" style=\"max-width:560px;background:#fff;border-radius:8px;padding:32px\"><tr><td>");
        html.Append("<p style=\"margin:0 0 16px;font-weight:bold;color:#0b5fff\">Solvelt</p>");
        html.Append($"<h1 style=\"font-size:20px;margin:0 0 16px\">{E(heading)}</h1>");
        foreach (var p in paragraphs) html.Append($"<p style=\"line-height:1.5\">{E(p)}</p>");
        if (button is { } b)
            html.Append($"<p style=\"margin:24px 0\"><a href=\"{E(b.Url)}\" style=\"background:#0b5fff;color:#fff;padding:12px 20px;border-radius:6px;text-decoration:none\">{E(b.Label)}</a></p>");
        html.Append("<p style=\"color:#6b7280;font-size:12px;margin-top:32px\">You received this email because of activity on a Solvelt seller account.</p>");
        html.Append("</td></tr></table></td></tr></table></body></html>");
        var text = new StringBuilder();
        text.AppendLine("Solvelt").AppendLine().AppendLine(heading).AppendLine();
        foreach (var p in paragraphs) text.AppendLine(p).AppendLine();
        if (button is { } bt) text.AppendLine($"{bt.Label}: {bt.Url}").AppendLine();
        return new EmailMessage(to, kind, subject, html.ToString(), text.ToString());
    }

    public static EmailMessage Invitation(string to, string account, string inviter, string role, string[]? brands, string link, DateTime expires) =>
        Build(to, "team-invitation", $"{inviter} invited you to {account} on Solvelt",
            $"Join {account} as {role}. The invitation expires on {expires:yyyy-MM-dd}.",
            $"You're invited to {account}",
            [$"{inviter} invited you to join the seller account \"{account}\" on Solvelt as {role}.",
             brands is { Length: > 0 } ? $"Brand access: {string.Join(", ", brands)}." : "Brand access: all brands.",
             $"This invitation expires on {expires:yyyy-MM-dd}. If you weren't expecting it, you can ignore this email."],
            ("View invitation", link));

    public static EmailMessage InviteAccepted(string to, string account, string who, string role) =>
        Build(to, "team-invite-accepted", $"{who} joined {account}", $"{who} accepted your invitation.",
            "Invitation accepted", [$"{who} accepted your invitation and joined \"{account}\" as {role}."]);

    public static EmailMessage RoleChanged(string to, string account, string role, string[]? brands) =>
        Build(to, "team-role-changed", $"Your role in {account} changed", $"You are now {role} in {account}.",
            "Your role changed", [$"Your role in \"{account}\" is now {role}.",
                brands is { Length: > 0 } ? $"Brand access: {string.Join(", ", brands)}." : "Brand access: all brands."]);

    public static EmailMessage Removed(string to, string account) =>
        Build(to, "team-removed", $"You were removed from {account}", $"You no longer have access to {account}.",
            "Access removed", [$"You were removed from the seller account \"{account}\" and can no longer access it. Your own Solvelt login and any other accounts are not affected."]);

    public static EmailMessage OwnershipTransferred(string to, string account, string newOwner, bool toNewOwner) =>
        Build(to, "team-ownership", $"Ownership of {account} was transferred", toNewOwner ? $"You are now the owner of {account}." : $"{newOwner} is now the owner of {account}.",
            "Ownership transferred", [toNewOwner
                ? $"You are now an Owner of \"{account}\"."
                : $"Ownership of \"{account}\" was transferred to {newOwner}. Your role is now Admin."]);

    public static EmailMessage StepUp(string to, string code) =>
        Build(to, "team-stepup", "Confirm the ownership transfer", "Your confirmation code for the ownership transfer.",
            "Confirm it's you", [$"Someone asked to transfer ownership of a seller account you own. Your confirmation code is {code}. It expires in 10 minutes. If this wasn't you, ignore this email and reset your password."]);
}
