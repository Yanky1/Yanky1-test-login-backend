namespace Solvelt.Api.Domain;

public enum PendingStatus { Pending, Executing, Confirmed, Cancelled, Expired, Failed, Superseded }

/// <summary>One chat. Bound to a user AND an account: switching account or user never exposes it.</summary>
public class AssistantConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public Guid UserId { get; set; }
    /// <summary>Which product surface owns it (seller portal today; staff admin later).</summary>
    public string Surface { get; set; } = "seller";
    public string Title { get; set; } = "New chat";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Role: user | assistant | note (app-generated: an action was confirmed/cancelled). Tool results are NOT stored, only short summaries.</summary>
public class AssistantMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public Guid SellerId { get; set; }
    public Guid UserId { get; set; }
    public long Seq { get; set; }
    public string Role { get; set; } = "user";
    public string Text { get; set; } = "";
    /// <summary>[{name,label,ok}] what the assistant looked at (display only).</summary>
    public string ToolsJson { get; set; } = "[]";
    public string ActionIdsJson { get; set; } = "[]";
    /// <summary>[{kind,id,label}] entities mentioned; links are built from these by the client, never from model-provided URLs.</summary>
    public string LinksJson { get; set; } = "[]";
    public string Status { get; set; } = "ok";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>A prepared change. Nothing happens until the owning user confirms it with a separate authenticated request.</summary>
public class AssistantPendingAction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public Guid UserId { get; set; }
    public Guid ConversationId { get; set; }
    public string Type { get; set; } = "";
    public string TargetKind { get; set; } = "";
    public Guid? TargetId { get; set; }
    public string TargetLabel { get; set; } = "";
    public string Summary { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    /// <summary>[{field,before,after}]</summary>
    public string DiffJson { get; set; } = "[]";
    public string Risk { get; set; } = "low";
    public PendingStatus Status { get; set; } = PendingStatus.Pending;
    /// <summary>Fingerprint of the target's state when proposed (version/status/stock); a mismatch at confirm time yields a refreshed proposal.</summary>
    public string Stamp { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public Guid? SupersededBy { get; set; }
    public string? ResultCode { get; set; }
    public string? ResultMessage { get; set; }
    public string? ResultJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

/// <summary>Daily counters per user (Day = yyyy-MM-dd, UTC): messages, tokens and estimated cost in millionths of a dollar.</summary>
public class AssistantUsage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public Guid UserId { get; set; }
    public string Day { get; set; } = "";
    public int Messages { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CostMicros { get; set; }
}
