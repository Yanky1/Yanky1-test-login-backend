using System.Globalization;
using System.Text.Json;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Options;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Assistant;

/// <summary>A thing the assistant may mention. The client builds the link from kind + id; the model never supplies URLs.</summary>
public sealed record EntityRef(string Kind, Guid Id, string Label);
public sealed record DiffRow(string Field, string? Before, string? After);

/// <summary>Everything a tool may touch. Authorization data (Access) comes from the authenticated request, never from the model.</summary>
public sealed class ToolContext(AppDbContext db, AccountAccess access, IServiceProvider sp, TimeProvider time, Guid conversationId, AssistantOptions options)
{
    public AppDbContext Db { get; } = db;
    public AccountAccess Access { get; } = access;
    public IServiceProvider Services { get; } = sp;
    public TimeProvider Time { get; } = time;
    public Guid ConversationId { get; } = conversationId;
    public AssistantOptions Options { get; } = options;
    public DateTime Now => Time.GetUtcNow().UtcDateTime;
    public T Get<T>() where T : notnull => (T)Services.GetService(typeof(T))!;
    public List<EntityRef> Refs { get; } = [];
    public List<AssistantPendingAction> Proposals { get; } = [];
    public EntityRef Ref(string kind, Guid id, string label) { var r = new EntityRef(kind, id, OutputSanitizer.Label(label)); Refs.Add(r); return r; }
}

public sealed class ToolOutput
{
    public object? Data { get; init; }
    /// <summary>One plain sentence about the outcome. The scripted fake uses it as its answer; real models read it as a hint.</summary>
    public string Say { get; init; } = "";
    public bool IsError { get; init; }
    public static ToolOutput Ok(string say, object? data = null) => new() { Say = say, Data = data };
    public static ToolOutput Error(string message) => new() { Say = message, IsError = true, Data = new { error = message } };
}

public interface IAssistantTool
{
    string Name { get; }
    string Description { get; }
    string SchemaJson { get; }
    /// <summary>Shown in the UI while it runs ("Looking up your orders…").</summary>
    string Label { get; }
    /// <summary>Checked when the tool list is built AND again on every call.</summary>
    Permission Permission { get; }
    /// <summary>True: only PREPARES a change (returns a pending action). No tool in this assistant changes data directly.</summary>
    bool IsAction { get; }
    Task<ToolOutput> RunAsync(ToolContext ctx, JsonElement input);
}

public sealed class DelegateTool(string name, string description, string schema, string label, Permission permission, bool isAction, Func<ToolContext, JsonElement, Task<ToolOutput>> run) : IAssistantTool
{
    public string Name => name; public string Description => description; public string SchemaJson => schema; public string Label => label;
    public Permission Permission => permission; public bool IsAction => isAction;
    public Task<ToolOutput> RunAsync(ToolContext ctx, JsonElement input) => run(ctx, input);
}

/// <summary>Defensive readers for model-supplied tool input (the model's JSON is untrusted: wrong types and missing fields are normal).</summary>
public static class In
{
    public static string? Str(JsonElement e, string k, int max = 4000) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? Clip(v.GetString(), max) : null;
    private static string? Clip(string? s, int max) { s = s?.Trim(); return string.IsNullOrEmpty(s) ? null : s.Length > max ? s[..max] : s; }
    public static Guid? Id(JsonElement e, string k) => Str(e, k, 64) is { } s && Guid.TryParse(s, out var g) ? g : null;
    public static int? Int(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    public static decimal? Dec(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;
    public static bool? Bool(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    public static DateTime? Time(JsonElement e, string k) =>
        Str(e, k, 40) is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    public static List<string>? Strs(JsonElement e, string k, int maxItems = 10, int maxLen = 600) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).Take(maxItems).Select(x => x.Length > maxLen ? x[..maxLen] : x).ToList() : null;
    public static List<DateTime>? Times(JsonElement e, string k) =>
        Strs(e, k, 10, 40)?.Select(s => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? (DateTime?)d : null).Where(d => d is not null).Select(d => d!.Value).ToList();
}

/// <summary>Builds the per-user tool list. The model is only ever offered what this user may do, and every call is re-checked (see <see cref="AssistantEngine"/>).</summary>
public sealed class ToolRegistry
{
    private readonly List<IAssistantTool> _tools;
    public ToolRegistry(IEnumerable<IAssistantTool> tools) => _tools = tools.ToList();
    public IReadOnlyList<IAssistantTool> All => _tools;
    public IAssistantTool? Find(string name) => _tools.FirstOrDefault(t => t.Name == name);

    /// <summary>Role AND brand scope: someone with no brand in scope only gets the account-level tools (brands list, help).</summary>
    public IReadOnlyList<IAssistantTool> For(AccountAccess a) => _tools.Where(t => Offered(t, a)).ToList();

    public static bool Offered(IAssistantTool t, AccountAccess a)
    {
        if (!a.Has(t.Permission)) return false;
        var hasBrands = a.AllBrands || a.Role >= AccountRole.Admin || a.BrandIds.Count > 0;
        var brandWork = t.Permission is Permission.BrandWorkRead or Permission.BrandWorkWrite;
        return !brandWork || hasBrands;
    }
}
