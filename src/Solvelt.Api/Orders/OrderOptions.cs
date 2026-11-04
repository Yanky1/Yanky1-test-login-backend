namespace Solvelt.Api.Orders;

/// <summary>Configuration section "Orders". Secrets come only from configuration / environment (never from code outside Development/Testing).</summary>
public class OrderOptions
{
    public const string Section = "Orders";
    /// <summary>Shared secret the main website signs inbound requests with (HMAC-SHA256).</summary>
    public string InboundSecret { get; set; } = "";
    /// <summary>Separate secret this portal signs outbound webhooks with. Never the same as the inbound secret.</summary>
    public string OutboundSecret { get; set; } = "";
    /// <summary>Optional explicit key for image downloads (<c>GET /catalog/images/{id}</c>); empty: a subkey derived from <see cref="InboundSecret"/> (see <c>Signing.ImageKey</c>). Never accepted by any other endpoint.</summary>
    public string ImageKey { get; set; } = "";
    /// <summary>Whether image downloads signed with the inbound secret itself are still accepted (backward compatibility). Set false once the marketplace signs images with the image key.</summary>
    public bool ImageAcceptInboundKey { get; set; } = true;
    /// <summary>Marketplace webhook URL. Empty: Development/Testing deliver to the in-memory dev sink; Production keeps events pending.</summary>
    public string WebhookUrl { get; set; } = "";
    public int TimestampWindowSeconds { get; set; } = 300;
    public int MaxInboundBodyBytes { get; set; } = 256 * 1024;
    /// <summary>Marketplace fee in basis points (1000 = 10%).</summary>
    public int FeeBasisPoints { get; set; } = 1000;
    /// <summary>0 disables auto-accept; otherwise New orders are accepted after this many minutes.</summary>
    public int AutoAcceptMinutes { get; set; }
    public int DefaultHandlingDays { get; set; } = 2;
    public int ReturnWindowDays { get; set; } = 30;
    public int RedactAfterDays { get; set; } = 90;
    /// <summary>Decided marketplace content reports lose the reporter's reference, reason and note this many days after the decision (open reports are kept).</summary>
    public int ContentReportRedactAfterDays { get; set; } = 90;
    /// <summary>At most this many <c>inbound.rejected</c> audit rows are written per client IP per minute (a flood of unsigned requests costs one counter increment each beyond that, not a database insert).</summary>
    public int InboundRejectedAuditPerIpPerMinute { get; set; } = 20;
    /// <summary><c>inbound.rejected</c> audit rows are deleted by the maintenance job after this many days.</summary>
    public int InboundRejectedRetentionDays { get; set; } = 30;
    public int OutboxMaxAttempts { get; set; } = 8;
    public int OutboxBackoffSeconds { get; set; } = 30;
    /// <summary>Managers may refund up to this many cents per order; 0 (default) means only Owners/Admins can refund.</summary>
    public long ManagerRefundLimitCents { get; set; }
    public int ReplyDueHours { get; set; } = 24;
    public bool BlockContactDetails { get; set; }
    public int MaxAttachmentBytes { get; set; } = 5 * 1024 * 1024;
    public int MaxAttachmentsPerMessage { get; set; } = 5;
    public bool RunBackgroundJobs { get; set; } = true;
    /// <summary>How often an open event stream re-checks that its member still has access (seconds).</summary>
    public int StreamRecheckSeconds { get; set; } = 15;
}
