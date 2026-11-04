namespace Solvelt.Api.Email;

public static class Templates
{
    /// <summary>Short title in the banner, by kind (security mails, application mails, everything else).</summary>
    private static string BannerFor(string kind) => kind switch
    {
        "verify-code" or "change-email-code" or "email-changed" or "stepup-code" or "locked" or "reset-password" or "password-changed" => "Account security",
        "application-received" or "approved" or "needs-info" or "rejected" => "Seller application",
        _ => "Seller portal"
    };

    private static EmailMessage Build(string to, string kind, string subject, string heading, string[] paragraphs,
        (string Label, string Url)? button = null, string? highlight = null, (string Label, string Url)? secondary = null)
    {
        var html = EmailLayout.Html(heading, heading, paragraphs, button, highlight, "You received this email because of activity on your Solvelt seller account.", BannerFor(kind), secondary);
        var text = EmailLayout.Text(heading, paragraphs, button, highlight, secondary);
        return new EmailMessage(to, kind, subject, html, text);
    }

    public static EmailMessage VerifyCode(string to, string code) => Build(to, "verify-code", "Your Solvelt verification code",
        "Verify your email", ["Enter this code to verify your email address. It expires in 10 minutes."], highlight: code);

    public static EmailMessage ChangeEmailCode(string to, string code) => Build(to, "change-email-code", "Confirm your new email address",
        "Confirm your new email", ["Enter this code to confirm this address for your Solvelt account. It expires in 10 minutes."], highlight: code);

    public static EmailMessage EmailChangedNotice(string to, string newEmail) => Build(to, "email-changed", "Your Solvelt email address was changed",
        "Email address changed", [$"The email on your account was changed to {newEmail}. If this wasn't you, reset your password immediately and contact support."]);

    public static EmailMessage StepUpCode(string to, string code) => Build(to, "stepup-code", "Confirm it's you",
        "Confirm it's you", ["Someone asked to change the email address on your Solvelt account. Enter this code to continue. It expires in 10 minutes. If this wasn't you, ignore this email and reset your password."], highlight: code);

    public static EmailMessage SignupExisting(string to, string signInLink, string resetLink) => Build(to, "signup-existing", "Someone tried to sign up with your email",
        "Sign-up attempt", ["Someone tried to create a Solvelt seller account with this email address, but you already have one. If it was you, sign in or reset your password with the links below. If not, you can ignore this email."],
        ("Sign in", signInLink), secondary: ("Reset your password", resetLink));

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

    /// <summary>Generic notification in the shared style (orders / messages): heading, short paragraphs and one button.</summary>
    public static EmailMessage Notification(string to, string kind, string subject, string heading, string[] paragraphs, string? buttonLabel = null, string? buttonUrl = null) =>
        Build(to, kind, subject, heading, paragraphs, buttonLabel is null || buttonUrl is null ? null : (buttonLabel, buttonUrl));
}
