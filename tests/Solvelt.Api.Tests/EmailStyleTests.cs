using Microsoft.Extensions.Logging.Abstractions;
using Solvelt.Api.Email;
using Solvelt.Api.Options;
using Solvelt.Api.Phone;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Tests;

/// <summary>Figma restyle of the transactional emails: one shared shell, inline styles only, user data encoded. No shared mutable state: the support address is resolved by Mailer at send time.</summary>
public class EmailStyleTests
{
    private static IEnumerable<EmailMessage> All() => new[]
    {
        Templates.VerifyCode("a@b.co", "123456"), Templates.ResetPassword("a@b.co", "https://x/y?a=1&b=2"), Templates.Approved("a@b.co", "A"),
        Templates.SignupExisting("a@b.co", "https://x/login", "https://x/reset"),
        Templates.Notification("a@b.co", "k", "S", "H", ["p"], "Open", "https://x/z"),
        TeamTemplates.Invitation("a@b.co", "Acct", "Bob", "Admin", null, "https://x/i", DateTime.UtcNow.AddDays(3)), TeamTemplates.StepUp("a@b.co", "246810"),
        PhoneTemplates.PhoneAdded("a@b.co", "+15551234567"), PhoneTemplates.StepUpEmail("a@b.co", "654321")
    };

    private static async Task<EmailMessage> ThroughMailer(EmailMessage m, string? supportAddress = null, string helpContact = "support@example.com")
    {
        var sender = new Capture();
        var mailer = new Mailer(sender, null!, Microsoft.Extensions.Options.Options.Create(new EmailOptions { SupportAddress = supportAddress ?? "", SendInBackground = false }),
            Microsoft.Extensions.Options.Options.Create(new PhoneOptions { HelpContact = helpContact }), NullLogger<Mailer>.Instance);
        await mailer.SendAsync(m);
        return sender.Sent!;
    }
    private sealed class Capture : IEmailSender
    {
        public EmailMessage? Sent;
        public Task SendAsync(EmailMessage message, CancellationToken ct = default) { Sent = message; return Task.CompletedTask; }
    }

    [Fact]
    public void Every_email_uses_the_shared_shell_with_brand_colours_banner_title_and_support_footer()
    {
        Assert.All(All(), m =>
        {
            Assert.Contains("bgcolor=\"#f4f6f4\"", m.Html);          // page background
            Assert.Contains("border:1px solid #e2eae4;border-radius:16px", m.Html); // card
            Assert.Contains("bgcolor=\"#11362e\"", m.Html);          // Outlook-safe banner fallback
            Assert.Contains("linear-gradient(135deg,#18483e", m.Html);
            Assert.Contains("Playfair Display',Georgia", m.Html);
            Assert.Contains("Plus Jakarta Sans',Arial", m.Html);
            Assert.Contains("font-size:18px;line-height:24px;color:#f8ebb8", m.Html); // short banner title under the wordmark
            Assert.Contains("mailto:" + EmailLayout.SupportToken, m.Html);
            Assert.Contains("Enquiries: " + EmailLayout.SupportToken, m.Text);
            Assert.DoesNotContain("<style", m.Html);                  // inline styles only
            Assert.DoesNotContain("<img", m.Html);                    // no remote images
            Assert.DoesNotContain("#0b5fff", m.Html);                 // old blue is gone
        });
    }

    [Fact]
    public void Banner_titles_follow_the_kind()
    {
        Assert.Contains(">Account security</p>", Templates.VerifyCode("a@b.co", "1").Html);
        Assert.Contains(">Seller application</p>", Templates.Approved("a@b.co", "A").Html);
        Assert.Contains(">Team access</p>", TeamTemplates.Removed("a@b.co", "Acct").Html);
        Assert.Contains(">Account security</p>", PhoneTemplates.PinChanged("a@b.co").Html);
    }

    [Fact]
    public void Button_padding_is_on_the_table_cell_for_Outlook_and_urls_are_encoded()
    {
        var m = Templates.ResetPassword("a@b.co", "https://x/y?a=1&b=2");
        Assert.Contains("bgcolor=\"#1f4d40\"", m.Html);
        Assert.Contains("border-radius:12px;padding:14px 28px\"><a href=", m.Html); // padding on the <td>, which Outlook honours
        Assert.Contains("style=\"display:block;", m.Html);                         // the link fills the cell
        Assert.Contains("href=\"https://x/y?a=1&amp;b=2\"", m.Html);
        Assert.Contains("https://x/y?a=1&b=2", m.Text);
    }

    [Theory]
    [InlineData("javascript:alert(1)")] [InlineData("JaVaScRiPt:alert(1)")] [InlineData("data:text/html,<b>x</b>")] [InlineData("/relative/path")] [InlineData("//evil.example/x")] [InlineData("")]
    public void Links_with_other_schemes_are_dropped_in_both_parts(string url)
    {
        var n = Templates.Notification("a@b.co", "k", "S", "H", ["p"], "Open", url);
        if (url.Length > 0) Assert.DoesNotContain("href=\"" + url, n.Html);
        Assert.DoesNotContain("Open", n.Html);
        Assert.DoesNotContain("Open:", n.Text);
        var s = Templates.SignupExisting("a@b.co", "https://x/login", url);
        Assert.Contains("href=\"https://x/login\"", s.Html);
        Assert.DoesNotContain("Reset your password", s.Html);
        Assert.DoesNotContain("Reset your password", s.Text);
    }

    [Theory]
    [InlineData("https://x/y")] [InlineData("http://x/y")] [InlineData("mailto:a@b.co")]
    public void Http_https_and_mailto_links_are_kept(string url) =>
        Assert.Contains("href=\"" + url + "\"", Templates.Notification("a@b.co", "k", "S", "H", ["p"], "Open", url).Html);

    [Fact]
    public void Signup_existing_shows_both_the_sign_in_and_the_reset_link()
    {
        var m = Templates.SignupExisting("a@b.co", "https://x/login?a=1&b=2", "https://x/reset");
        Assert.Contains("href=\"https://x/login?a=1&amp;b=2\"", m.Html);
        Assert.Contains("Reset your password: <a href=\"https://x/reset\"", m.Html);
        Assert.Contains("Sign in: https://x/login?a=1&b=2", m.Text);
        Assert.Contains("Reset your password: https://x/reset", m.Text);
    }

    [Fact]
    public void Codes_sit_in_a_highlighted_block_including_the_team_ownership_step_up()
    {
        Assert.Contains("background:#f4f7f5;border:1px solid #e2eae4;border-radius:12px", Templates.VerifyCode("a@b.co", "123456").Html);
        var t = TeamTemplates.StepUp("a@b.co", "246810");
        Assert.Contains(">246810</td>", t.Html);
        Assert.Contains("246810", t.Text);
        Assert.DoesNotContain("code is 246810", t.Html);
    }

    [Fact]
    public void User_data_is_html_encoded_everywhere_in_the_shell()
    {
        var m = TeamTemplates.Invitation("a@b.co", "<img src=x onerror=1>", "<b>Bob</b>", "Admin", ["<i>x</i>"], "https://x/i?a=\"1\"", DateTime.UtcNow);
        Assert.DoesNotContain("<img", m.Html);
        Assert.DoesNotContain("<b>Bob</b>", m.Html);
        Assert.DoesNotContain("<i>x</i>", m.Html);
        Assert.Contains("&lt;img", m.Html);
        Assert.Contains("href=\"https://x/i?a=&quot;1&quot;\"", m.Html);
    }

    [Fact]
    public async Task Mailer_fills_the_support_address_from_Email_SupportAddress_then_Phone_HelpContact_then_the_default()
    {
        var a = await ThroughMailer(Templates.PasswordChanged("a@b.co"), supportAddress: "help@solvelt.test", helpContact: "other@solvelt.test");
        Assert.Contains("mailto:help@solvelt.test", a.Html);
        Assert.Contains("Enquiries: help@solvelt.test", a.Text);
        var b = await ThroughMailer(Templates.PasswordChanged("a@b.co"), helpContact: "other@solvelt.test");
        Assert.Contains("mailto:other@solvelt.test", b.Html);
        var c = await ThroughMailer(Templates.PasswordChanged("a@b.co"), supportAddress: "not an address", helpContact: "call us on 555");
        Assert.Contains("mailto:support@example.com", c.Html);
        Assert.DoesNotContain(EmailLayout.SupportToken, c.Html + c.Text);
    }

    [Theory]
    [InlineData("a\"><script>@x.co")] [InlineData("a b@x.co")] [InlineData("<a@x.co>")] [InlineData("a@x")] [InlineData("a@x.co, b@x.co")] [InlineData("javascript:alert(1)")]
    public async Task Unusable_support_addresses_never_reach_the_email(string bad)
    {
        var m = await ThroughMailer(Templates.PasswordChanged("a@b.co"), supportAddress: bad, helpContact: bad);
        Assert.Contains("mailto:support@example.com", m.Html);
        Assert.DoesNotContain("<script>", m.Html);
        Assert.DoesNotContain(bad, m.Html + m.Text);
    }
}
