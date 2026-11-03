using System.Text.Json;

namespace Solvelt.Api.Assistant;

/// <summary>Provider-neutral conversation blocks. The engine speaks these; <see cref="IAssistantModel"/> implementations translate.</summary>
public abstract record ABlock;
public sealed record AText(string Text) : ABlock;
public sealed record AToolUse(string Id, string Name, JsonElement Input) : ABlock;
public sealed record AToolResult(string ToolUseId, string Content, bool IsError = false) : ABlock;

public sealed record AMessage(string Role, List<ABlock> Blocks)
{
    public static AMessage User(string text) => new("user", [new AText(text)]);
    public static AMessage Assistant(string text) => new("assistant", [new AText(text)]);
    public string PlainText => string.Concat(Blocks.OfType<AText>().Select(b => b.Text));
}

/// <summary>A tool as offered to the model: the schema is a JSON-schema object string (type, properties, required).</summary>
public sealed record ToolDef(string Name, string Description, string SchemaJson);

public sealed record ModelRequest(string System, IReadOnlyList<ToolDef> Tools, IReadOnlyList<AMessage> Messages, int MaxTokens, string Model);
public sealed record ModelUsage(int InputTokens, int OutputTokens, int CacheReadTokens = 0, int CacheWriteTokens = 0);
/// <summary>StopReason: end_turn | tool_use | max_tokens | refusal | other.</summary>
public sealed record ModelResponse(List<ABlock> Blocks, string StopReason, ModelUsage Usage);

public interface IAssistantModel
{
    /// <summary>One model call. Throws on transport/API failure or when ct fires (the engine turns both into a friendly error).</summary>
    Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct);
}
