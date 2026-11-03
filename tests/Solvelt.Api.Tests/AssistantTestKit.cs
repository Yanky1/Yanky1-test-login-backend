using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Assistant;
using Solvelt.Api.Domain;
using static Solvelt.Api.Tests.Orders;

namespace Solvelt.Api.Tests;

public class AssistantFactory : SvcFactory
{
    public const string Secret = "sk-ant-api03-TOP-SECRET-test-key-0123456789";
    public AssistantFactory()
    {
        Extra["Assistant:Enabled"] = "true";
        Extra["RateLimit:AssistantPerMinute"] = "100000";
        Extra["Ai:ApiKey"] = Secret;
        Extra["Orders:InboundSecret"] = InSecret;
    }
    public ScriptedAssistantModel Model => Services.GetRequiredService<ScriptedAssistantModel>();
}
public class AssistantLimitsFactory : AssistantFactory
{
    public AssistantLimitsFactory() { Extra["Assistant:MaxIterations"] = "3"; Extra["Assistant:CallTimeoutSeconds"] = "1"; Extra["Assistant:DailyMessagesPerUser"] = "4"; Extra["Assistant:DailyMessagesPerAccount"] = "6"; }
}
public class AssistantTokenFactory : AssistantFactory { public AssistantTokenFactory() { Extra["Assistant:DailyTokensPerUser"] = "300"; } }
public class AssistantStoppedFactory : AssistantFactory { public AssistantStoppedFactory() { Extra["Assistant:KillSwitch"] = "true"; } }
public class AssistantOffFactory : AssistantFactory { public AssistantOffFactory() { Extra["Assistant:Enabled"] = "false"; } }

public static class Asst
{
    public static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);

    public static Task<HttpResponseMessage> ChatRaw(Api api, string text, Guid? conv = null, object? page = null) =>
        api.Post("/api/v1/assistant/messages", new { conversationId = conv, text, page });

    public static async Task<JsonNode> Chat(Api api, string text, Guid? conv = null, object? page = null)
    {
        var r = await ChatRaw(api, text, conv, page);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return await Node(r);
    }

    public static Task<HttpResponseMessage> Confirm(Api api, string id, bool csrf = true) => Act(api, id, "confirm", csrf);
    public static Task<HttpResponseMessage> Cancel(Api api, string id, bool csrf = true) => Act(api, id, "cancel", csrf);
    private static Task<HttpResponseMessage> Act(Api api, string id, string verb, bool csrf)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/assistant/actions/{id}/{verb}") { Content = System.Net.Http.Json.JsonContent.Create(new { }) };
        if (csrf && api.Csrf is not null) req.Headers.Add("X-CSRF-Token", api.Csrf);
        return api.Send(req);
    }

    public static JsonNode Action(JsonNode chat, int i = 0) => chat["message"]!["actions"]![i]!;
    public static string ActionId(JsonNode chat, int i = 0) => (string)Action(chat, i)["id"]!;
    public static string Text(JsonNode chat) => (string)chat["message"]!["text"]!;

    /// <summary>Scripts exact tool calls: calls each (tool, input) in turn, then ends with a short text.</summary>
    public static void Script(ScriptedAssistantModel m, params (string Tool, object Input)[] calls)
    {
        m.Override = r =>
        {
            var done = r.Messages.SelectMany(x => x.Blocks).OfType<AToolResult>().Count();
            if (done >= calls.Length) return new ModelResponse([new AText("Done looking.")], "end_turn", new(10, 5));
            var (tool, input) = calls[done];
            return new ModelResponse([new AToolUse("toolu_s" + done, tool, JsonSerializer.SerializeToElement(input))], "tool_use", new(10, 5));
        };
    }

    /// <summary>The tool results the model saw in its LAST request (what the tools returned).</summary>
    public static string LastToolResults(ScriptedAssistantModel m) =>
        string.Join("\n", m.Requests.Last().Messages.SelectMany(x => x.Blocks).OfType<AToolResult>().Select(x => x.Content));

    public static string[] OfferedTools(ScriptedAssistantModel m) => m.Requests.First().Tools.Select(t => t.Name).ToArray();

    public static void SetRole(ApiFactory f, Guid seller, string email, AccountRole role) => f.WithDb(db =>
    {
        var uid = db.Users.First(u => u.Email == email).Id;
        db.Memberships.Where(x => x.SellerId == seller && x.UserId == uid).ExecuteUpdate(s => s.SetProperty(x => x.Role, role));
        return 0;
    });

    public static async Task<(Guid ConvId, Guid MsgId)> BuyerMessage(Shop s, Guid orderId, string number, string text)
    {
        var detail = await Orders.Detail(s.Owner, orderId);
        var m = await Msg.BuyerMessage(s.F, "order", (string)detail["marketplaceOrderId"]!, number, text: text);
        return (m.Conv, m.Message);
    }

    public static List<AuditEvent> Audit(ApiFactory f, string type) => f.WithDb(db => db.AuditEvents.AsNoTracking().Where(a => a.Type == type).ToList());
}
