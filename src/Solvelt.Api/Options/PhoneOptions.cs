namespace Solvelt.Api.Options;

public class PhoneOptions
{
    public const string Section = "Phone";
    /// <summary>Maps the IVR/SMS webhooks and the account phone endpoints. Off by default.</summary>
    public bool Enabled { get; set; }
    /// <summary>Sink (dev/test, messages kept in memory) | SignalWire | Twilio.</summary>
    public string Provider { get; set; } = "Sink";
    /// <summary>SignalWire Project ID / Twilio Account SID (REST API user name).</summary>
    public string AccountSid { get; set; } = "";
    /// <summary>SignalWire API token / Twilio auth token (REST API password).</summary>
    public string AuthToken { get; set; } = "";
    /// <summary>Webhook signing secret. SignalWire: the "Signing Key"; Twilio: the auth token. Falls back to AuthToken.</summary>
    public string SigningKey { get; set; } = "";
    /// <summary>SignalWire space host, e.g. example.signalwire.com.</summary>
    public string Space { get; set; } = "";
    /// <summary>Overrides the REST base URL (tests, proxies).</summary>
    public string ApiBaseUrl { get; set; } = "";
    public string FromNumber { get; set; } = "";
    /// <summary>Public origin the provider calls (https://api.example.com). Part of the signed URL, so it must match what is configured at the provider.</summary>
    public string PublicBaseUrl { get; set; } = "";
    public string Voice { get; set; } = "Polly.Joanna";
    public string Language { get; set; } = "en-US";
    public string DefaultRegion { get; set; } = "US";
    /// <summary>Number calls are transferred to from the signed-in menu. Empty = option hidden.</summary>
    public string SupportNumber { get; set; } = "";
    public string HelpContact { get; set; } = "support@example.com";
    public string BrandName { get; set; } = "Solvelt";
    public int CallStateMinutes { get; set; } = 10;
    public int PhoneSessionMinutes { get; set; } = 10;
    public int SmsSessionMinutes { get; set; } = 15;
    public int SignupLinkMinutes { get; set; } = 30;
    /// <summary>Webhook requests per caller/texter number per minute.</summary>
    public int NumberPerMinute { get; set; } = 30;
    /// <summary>Webhook requests per source IP per minute (providers share IPs, so keep this generous).</summary>
    public int IpPerMinute { get; set; } = 600;
    /// <summary>Send SMS off the request path so response time does not reveal whether a message was sent.</summary>
    public bool SendInBackground { get; set; } = true;
}
