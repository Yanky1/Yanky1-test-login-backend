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

public class Mailer(IEmailSender sender, ILogger<Mailer> log)
{
    /// <summary>Never throws: failed email must not break the security flow; failures are logged without content.</summary>
    public async Task SendAsync(EmailMessage m)
    {
        try { await sender.SendAsync(m); }
        catch (Exception ex) { log.LogError(ex, "Failed to send {Kind} email", m.Kind); }
    }
}
