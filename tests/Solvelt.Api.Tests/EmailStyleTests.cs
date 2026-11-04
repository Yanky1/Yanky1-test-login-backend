using Solvelt.Api.Email;
using Solvelt.Api.Phone;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Tests;

/// <summary>Figma restyle of the transactional emails: one shared shell, inline styles only, user data encoded.</summary>
public class EmailStyleTests
{
    private static IEnumerable<EmailMessage> All() => new[]
    {
        Templates.VerifyCode("a@b.co", "123456"), Templates.ResetPassword("a@b.co", "https://x/y?a=1&b=2"), Templates.Approved("a@b.co", "A"),
        Templates.Notification("a@b.co", "k", "S", "H", ["p"], "Open", "https://x/z"),
        TeamTemplates.Invitation("a@b.co", "Acct", "Bob", "Admin", null, "https://x/i", DateTime.UtcNow.AddDays(3)),
        PhoneTemplates.PhoneAdded("a@b.co", "+15551234567"), PhoneTemplates.StepUpEmail("a@b.co", "654321")
    };

    [Fact]
    public void Every_email_uses_the_shared_shell_with_brand_colours_and_support_footer()
    {
        Assert.All(All(), m =>
        {
            Assert.Contains("bgcolor=\"#f4f6f4\"", m.Html);          // page background
            Assert.Contains("border:1px solid #e2eae4;border-radius:16px", m.Html); // card
            Assert.Contains("bgcolor=\"#11362e\"", m.Html);          // Outlook-safe banner fallback
            Assert.Contains("linear-gradient(135deg,#18483e", m.Html);
            Assert.Contains("Playfair Display',Georgia", m.Html);
            Assert.Contains("Plus Jakarta Sans',Arial", m.Html);
            Assert.Contains("Enquiries: <a href=\"mailto:" + EmailLayout.SupportEmail, m.Html);
            Assert.Contains("Enquiries: " + EmailLayout.SupportEmail, m.Text);
            Assert.DoesNotContain("<style", m.Html);                  // inline styles only
            Assert.DoesNotContain("#0b5fff", m.Html);                 // old blue is gone
        });
    }

    [Fact]
    public void Button_is_a_bulletproof_dark_green_table_cell_and_urls_are_encoded()
    {
        var m = Templates.ResetPassword("a@b.co", "https://x/y?a=1&b=2");
        Assert.Contains("bgcolor=\"#1f4d40\"", m.Html);
        Assert.Contains("color:#ffffff", m.Html);
        Assert.Contains("href=\"https://x/y?a=1&amp;b=2\"", m.Html);
        Assert.Contains("https://x/y?a=1&b=2", m.Text);
    }

    [Fact]
    public void Codes_sit_in_a_highlighted_block()
    {
        var m = Templates.VerifyCode("a@b.co", "123456");
        Assert.Contains("background:#f4f7f5;border:1px solid #e2eae4;border-radius:12px", m.Html);
        Assert.Contains(">123456</td>", m.Html);
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
    public void Support_address_is_configurable_and_encoded()
    {
        var old = EmailLayout.SupportEmail;
        try
        {
            EmailLayout.SupportEmail = "help@solvelt.test";
            Assert.Contains("mailto:help@solvelt.test", Templates.PasswordChanged("a@b.co").Html);
            EmailLayout.SupportEmail = "a\"><script>@x";
            Assert.DoesNotContain("<script>", Templates.PasswordChanged("a@b.co").Html);
        }
        finally { EmailLayout.SupportEmail = old; }
    }
}
