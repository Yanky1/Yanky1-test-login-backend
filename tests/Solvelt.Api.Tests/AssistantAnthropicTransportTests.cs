using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using Microsoft.Extensions.Options;
using Solvelt.Api.Assistant;
using Solvelt.Api.Options;

namespace Solvelt.Api.Tests;

/// <summary>The real Anthropic model class against a fake local HTTP endpoint (no network, no key).</summary>
public class AssistantAnthropicTransportTests
{
    private sealed class FakeApi : IDisposable
    {
        private readonly HttpListener _l = new(); public string Url { get; }
        public List<string> Bodies { get; } = []; public Func<int, string> Reply { get; set; } = _ => "{}";
        public FakeApi()
        {
            var port = new Random().Next(20000, 60000); Url = $"http://127.0.0.1:{port}"; _l.Prefixes.Add(Url + "/"); _l.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var c = await _l.GetContextAsync(); using var r = new StreamReader(c.Request.InputStream); var body = await r.ReadToEndAsync();
                        lock (Bodies) Bodies.Add(body); var n = Bodies.Count; var bytes = Encoding.UTF8.GetBytes(Reply(n));
                        c.Response.ContentType = "application/json"; await c.Response.OutputStream.WriteAsync(bytes); c.Response.Close();
                    }
                }
                catch { /* closed */ }
            });
        }
        public void Dispose() => _l.Close();
    }

    private static AnthropicAssistantModel Model(FakeApi api) =>
        new AnthropicAssistantModel(Microsoft.Extensions.Options.Options.Create(new AiOptions()), new TestMonitor(new AssistantOptions())).WithClient(new AnthropicClient { ApiKey = "test-key", BaseUrl = api.Url });

    private sealed class TestMonitor(AssistantOptions v) : IOptionsMonitor<AssistantOptions>
    {
        public AssistantOptions CurrentValue => v; public AssistantOptions Get(string? name) => v; public IDisposable? OnChange(Action<AssistantOptions, string?> l) => null;
    }

    private const string ToolTurn = """{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5","content":[{"type":"thinking","thinking":"","signature":"SIG-ABC123"},{"type":"tool_use","id":"toolu_1","name":"list_orders","input":{"late":true}}],"stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":5}}""";
    private const string FinalTurn = """{"id":"msg_2","type":"message","role":"assistant","model":"claude-opus-5-5","content":[{"type":"text","text":"Done."}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":20,"output_tokens":3}}""";

    [Fact]
    public async Task Thinking_blocks_of_a_tool_turn_are_replayed_unchanged_before_the_tool_result()
    {
        using var api = new FakeApi { Reply = n => n == 1 ? ToolTurn : FinalTurn };
        var model = Model(api);
        var tools = new[] { new ToolDef("list_orders", "d", """{"type":"object","properties":{"late":{"type":"boolean"}}}""") };
        var first = await model.CompleteAsync(new ModelRequest("sys", tools, [AMessage.User("late orders?")], 16000, "claude-opus-5-5"), CancellationToken.None);
        Assert.Equal("tool_use", first.StopReason);
        Assert.Contains(first.Blocks, b => b is AThinking { Signature: "SIG-ABC123" });
        var use = first.Blocks.OfType<AToolUse>().Single();

        // the engine appends the assistant turn exactly as returned, then the tool result
        var second = await model.CompleteAsync(new ModelRequest("sys", tools, [AMessage.User("late orders?"), new("assistant", first.Blocks), new("user", [new AToolResult(use.Id, "{\"ok\":true}")])], 16000, "claude-opus-5-5"), CancellationToken.None);
        Assert.Equal("end_turn", second.StopReason);

        using var doc = JsonDocument.Parse(api.Bodies[1]);
        var msgs = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
        var assistant = msgs[1].GetProperty("content").EnumerateArray().Select(c => c.GetProperty("type").GetString()).ToList();
        Assert.Equal(["thinking", "tool_use"], assistant);                       // order preserved: thinking first
        Assert.Equal("SIG-ABC123", msgs[1].GetProperty("content")[0].GetProperty("signature").GetString());
        Assert.Equal("tool_result", msgs[2].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal(16000, doc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("medium", doc.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        // caching: a breakpoint on the last tool
        Assert.Equal("ephemeral", doc.RootElement.GetProperty("tools")[0].GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Refusal_and_max_tokens_stop_reasons_are_mapped()
    {
        using var api = new FakeApi { Reply = n => (n == 1 ? FinalTurn.Replace("end_turn", "refusal") : FinalTurn.Replace("end_turn", "max_tokens")) };
        var model = Model(api);
        var req = new ModelRequest("sys", [], [AMessage.User("x")], 1000, "claude-opus-5-5");
        Assert.Equal("refusal", (await model.CompleteAsync(req, CancellationToken.None)).StopReason);
        Assert.Equal("max_tokens", (await model.CompleteAsync(req, CancellationToken.None)).StopReason);
    }
}
