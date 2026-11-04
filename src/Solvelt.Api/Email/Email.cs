using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using Solvelt.Api.Options;

namespace Solvelt.Api.Email;

public record EmailMessage(string To, string Kind, string Subject, string Html, string Text);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>Defined only; no implementation (SMS is out of scope, see docs/ROADMAP.md).</summary>
public interface ISmsSender
{
    Task SendAsync(string phoneNumber, string message, CancellationToken ct = default);
}

public class EmailSink
{
    private readonly List<EmailMessage> _items = [];
    private readonly object _lock = new();
    public void Add(EmailMessage m) { lock (_lock) _items.Add(m); }
    public IReadOnlyList<EmailMessage> All() { lock (_lock) return _items.ToList(); }
    public void Clear() { lock (_lock) _items.Clear(); }
}

public class SinkEmailSender(EmailSink sink) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct = default) { sink.Add(message); return Task.CompletedTask; }
}

public class SmtpEmailSender(IOptions<EmailOptions> opt, ILogger<SmtpEmailSender> log) : IEmailSender
{
    public async Task SendAsync(EmailMessage m, CancellationToken ct = default)
    {
        var o = opt.Value;
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(o.FromName, o.FromAddress));
        mime.To.Add(MailboxAddress.Parse(m.To));
        mime.Subject = m.Subject;
        mime.Body = new BodyBuilder { HtmlBody = m.Html, TextBody = m.Text }.ToMessageBody();
        using var client = new SmtpClient();
        await client.ConnectAsync(o.SmtpHost, o.SmtpPort, o.SmtpUseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, ct);
        if (!string.IsNullOrEmpty(o.SmtpUser)) await client.AuthenticateAsync(o.SmtpUser, o.SmtpPassword ?? "", ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
        log.LogInformation("Email {Kind} sent via SMTP", m.Kind);
    }
}

public class ResendEmailSender(HttpClient http, IOptions<EmailOptions> opt, ILogger<ResendEmailSender> log) : IEmailSender
{
    public async Task SendAsync(EmailMessage m, CancellationToken ct = default)
    {
        var o = opt.Value;
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ResendApiKey);
        req.Content = JsonContent.Create(new
        {
            from = $"{o.FromName} <{o.FromAddress}>", to = new[] { m.To }, subject = m.Subject, html = m.Html, text = m.Text
        });
        var res = await http.SendAsync(req, ct);
        if (res.StatusCode != HttpStatusCode.OK && res.StatusCode != HttpStatusCode.Accepted)
            throw new InvalidOperationException($"Resend API returned {(int)res.StatusCode}");
        log.LogInformation("Email {Kind} sent via Resend", m.Kind);
    }
}

public class Mailer(IEmailSender sender, IServiceScopeFactory scopes, IOptions<EmailOptions> opt, IOptions<PhoneOptions> phone, ILogger<Mailer> log)
{
    private static int _warnedDefaultSupport;

    /// <summary>Fills the footer's support address from configuration (see <see cref="EmailLayout.ResolveSupport"/>); warns once when neither setting holds a usable address.</summary>
    private EmailMessage Finish(EmailMessage m)
    {
        var support = EmailLayout.ResolveSupport(opt.Value.SupportAddress, phone.Value.HelpContact, out var usedDefault);
        if (usedDefault && Interlocked.Exchange(ref _warnedDefaultSupport, 1) == 0)
            log.LogWarning("No usable support address: set Email:SupportAddress (a plain address); email footers show {Default}", EmailLayout.DefaultSupport);
        return EmailLayout.Resolve(m, support);
    }

    /// <summary>Never throws: failed email must not break the security flow; failures are logged without content.</summary>
    public async Task SendAsync(EmailMessage m)
    {
        try { await sender.SendAsync(Finish(m)); }
        catch (Exception ex) { log.LogError(ex, "Failed to send {Kind} email", m.Kind); }
    }

    /// <summary>For anti-enumeration flows: returns immediately (when enabled) so timing is identical whether or not a mail is sent.</summary>
    public Task SendDetachedAsync(EmailMessage m)
    {
        if (!opt.Value.SendInBackground) return SendAsync(m);
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IEmailSender>().SendAsync(Finish(m));
            }
            catch (Exception ex) { log.LogError(ex, "Failed to send {Kind} email", m.Kind); }
        });
        return Task.CompletedTask;
    }
}
