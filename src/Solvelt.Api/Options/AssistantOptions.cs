namespace Solvelt.Api.Options;

/// <summary>Seller assistant settings (docs/ASSISTANT.md). Bound with IOptionsMonitor, so <see cref="KillSwitch"/> can be flipped without a restart (config reload / env + reload).</summary>
public class AssistantOptions
{
    public const string Section = "Assistant";
    /// <summary>Feature flag. When false every assistant endpoint answers 503 assistant_disabled and the UI hides the entry point.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Global emergency stop: no model call is made, no pending action can be confirmed. Reads stay available for history.</summary>
    public bool KillSwitch { get; set; }
    /// <summary>Fake (scripted, offline: dev and tests) | Anthropic (Claude API; key from Ai:ApiKey or ANTHROPIC_API_KEY).</summary>
    public string Provider { get; set; } = "Fake";
    /// <summary>Default claude-opus-5-5. claude-haiku-5-5 is the cheaper/faster option (less capable at multi-step tool use).</summary>
    public string Model { get; set; } = "claude-opus-5-5";
    /// <summary>Thinking depth for the Claude API: low | medium | high.</summary>
    public string Effort { get; set; } = "medium";
    /// <summary>Per model call. Generous on purpose: adaptive thinking spends output tokens before the answer, and a tool call cut off mid-JSON is useless.</summary>
    public int MaxTokens { get; set; } = 16000;
    /// <summary>Model calls per user message (each tool round trip is one).</summary>
    public int MaxIterations { get; set; } = 8;
    public int CallTimeoutSeconds { get; set; } = 60;
    public int ToolTimeoutSeconds { get; set; } = 20;
    public int MaxMessageChars { get; set; } = 4000;
    public int MaxToolResultChars { get; set; } = 12000;
    /// <summary>Earlier user/assistant messages replayed to the model (tool results are never replayed).</summary>
    public int HistoryMessages { get; set; } = 24;
    public int DailyMessagesPerUser { get; set; } = 150;
    public int DailyMessagesPerAccount { get; set; } = 1500;
    public long DailyTokensPerUser { get; set; } = 600_000;
    public long DailyTokensPerAccount { get; set; } = 6_000_000;
    public int ActionExpiryMinutes { get; set; } = 10;
    public int RetentionDays { get; set; } = 90;
    /// <summary>List prices per million tokens, for cost tracking only (default: Claude Opus 5.5).</summary>
    public decimal InputUsdPerMTok { get; set; } = 4m;
    public decimal OutputUsdPerMTok { get; set; } = 20m;
    public bool RunBackgroundJobs { get; set; } = true;
}
