namespace Solvelt.Api.Options;

public class AiOptions
{
    public const string Section = "Ai";
    /// <summary>Fake (deterministic keyword rules, no network; default for dev/tests) | Anthropic (Claude API).</summary>
    public string Provider { get; set; } = "Fake";
    /// <summary>Anthropic API key. Config/env only (Ai__ApiKey or ANTHROPIC_API_KEY), never committed.</summary>
    public string ApiKey { get; set; } = "";
    /// <summary>Model for SMS turns.</summary>
    public string Model { get; set; } = "claude-opus-5-5";
    /// <summary>Faster model for voice turns (latency matters). Default is the same as Model; claude-haiku-5-5 is the documented faster option.</summary>
    public string VoiceModel { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 8;
    public int VoiceTimeoutSeconds { get; set; } = 4;
    public int MaxTokens { get; set; } = 400;
}
