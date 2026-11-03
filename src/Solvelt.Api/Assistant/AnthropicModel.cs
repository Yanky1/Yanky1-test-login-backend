using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;
using Solvelt.Api.Options;

namespace Solvelt.Api.Assistant;

/// <summary>
/// Claude via the official Anthropic C# SDK. The agent loop itself lives in <see cref="AssistantEngine"/>; this class makes ONE Messages call per round trip.
/// Prompt caching: the last tool carries a cache breakpoint (tools render first, then system), and top-level auto-caching covers the growing conversation.
/// Adaptive thinking is on by default for the Opus 5.x models; effort comes from <c>Assistant:Effort</c>.
/// </summary>
public sealed class AnthropicAssistantModel(IOptions<AiOptions> ai, IOptionsMonitor<AssistantOptions> opt) : IAssistantModel
{
    private AnthropicClient? _client;
    /// <summary>Test seam: point the model at a fake transport.</summary>
    internal AnthropicAssistantModel WithClient(AnthropicClient c) { _client = c; return this; }
    private AnthropicClient Client => _client ??= new AnthropicClient { ApiKey = string.IsNullOrEmpty(ai.Value.ApiKey) ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") : ai.Value.ApiKey };

    public async Task<ModelResponse> CompleteAsync(ModelRequest r, CancellationToken ct)
    {
        var tools = new List<ToolUnion>();
        for (var i = 0; i < r.Tools.Count; i++)
        {
            var t = r.Tools[i];
            using var schema = JsonDocument.Parse(t.SchemaJson);
            var props = new Dictionary<string, JsonElement>();
            if (schema.RootElement.TryGetProperty("properties", out var p)) foreach (var x in p.EnumerateObject()) props[x.Name] = x.Value.Clone();
            var required = schema.RootElement.TryGetProperty("required", out var rq) ? rq.EnumerateArray().Select(e => e.GetString()!).ToList() : new List<string>();
            var tool = new Tool
            {
                Name = t.Name, Description = t.Description,
                InputSchema = new() { Properties = props, Required = required },
                CacheControl = i == r.Tools.Count - 1 ? new CacheControlEphemeral() : null
            };
            tools.Add(tool);
        }
        var messages = new List<MessageParam>();
        foreach (var m in r.Messages)
        {
            var content = new List<ContentBlockParam>();
            foreach (var b in m.Blocks)
                switch (b)
                {
                    case AText tx when tx.Text.Length > 0: content.Add(new TextBlockParam { Text = tx.Text }); break;
                    case AThinking th: content.Add(new ThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature }); break;
                    case ARedactedThinking rt: content.Add(new RedactedThinkingBlockParam { Data = rt.Data }); break;
                    case AToolUse tu: content.Add(new ToolUseBlockParam { ID = tu.Id, Name = tu.Name, Input = ToInput(tu.Input) }); break;
                    case AToolResult tr: content.Add(new ToolResultBlockParam { ToolUseID = tr.ToolUseId, Content = tr.Content, IsError = tr.IsError }); break;
                }
            if (content.Count == 0) continue;
            messages.Add(m.Role == "assistant" ? new AssistantMessageParam { Content = content } : new UserMessageParam { Content = content });
        }
        var effort = opt.CurrentValue.Effort.ToLowerInvariant() switch { "low" => Effort.Low, "high" => Effort.High, _ => Effort.Medium };
        var resp = await Client.Messages.Create(new MessageCreateParams
        {
            Model = r.Model, MaxTokens = r.MaxTokens, System = r.System, Tools = tools, Messages = messages,
            OutputConfig = new OutputConfig { Effort = effort },
            CacheControl = new CacheControlEphemeral()
        }, ct);

        var blocks = new List<ABlock>();
        foreach (var cb in resp.Content)
        {
            if (cb.TryPickThinking(out var th)) blocks.Add(new AThinking(th.Thinking, th.Signature));
            else if (cb.TryPickRedactedThinking(out var rt)) blocks.Add(new ARedactedThinking(rt.Data));
            else if (cb.TryPickText(out var text)) blocks.Add(new AText(text.Text));
            else if (cb.TryPickToolUse(out var tu)) blocks.Add(new AToolUse(tu.ID, tu.Name, JsonSerializer.SerializeToElement(tu.Input)));
        }
        var stop = resp.StopReason?.Raw()?.ToString()?.ToLowerInvariant() switch
        {
            "tool_use" or "tooluse" => "tool_use", "end_turn" or "endturn" => "end_turn", "max_tokens" or "maxtokens" => "max_tokens", "refusal" => "refusal", _ => blocks.Any(b => b is AToolUse) ? "tool_use" : "other"
        };
        var u = resp.Usage;
        return new ModelResponse(blocks, stop, new ModelUsage((int)u.InputTokens, (int)u.OutputTokens, (int)(u.CacheReadInputTokens ?? 0), (int)(u.CacheCreationInputTokens ?? 0)));
    }

    private static IReadOnlyDictionary<string, JsonElement> ToInput(JsonElement e)
    {
        var d = new Dictionary<string, JsonElement>();
        if (e.ValueKind == JsonValueKind.Object) foreach (var p in e.EnumerateObject()) d[p.Name] = p.Value.Clone();
        return d;
    }
}
