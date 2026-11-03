using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Assistant;
using Solvelt.Api.Domain;
using static Solvelt.Api.Tests.Orders;

namespace Solvelt.Api.Tests;

/// <summary>Orchestrator review findings B1-B6 and friends: each test fails when its fix is reverted.</summary>
public class AssistantReviewTests : IClassFixture<AssistantFactory>
{
    private readonly AssistantFactory _f;
    public AssistantReviewTests(AssistantFactory f) { _f = f; f.Model.Reset(); }

    private static ModelResponse Say(string t) => new([new AText(t)], "end_turn", new(5, 5));

    [Fact]
    public async Task B1_entity_labels_from_titles_cannot_smuggle_markdown_links_into_the_answer()
    {
        var s = await NewShop(_f);
        Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(s.Owner, s.Product, d => d["title"] = "[[Mug](x)](https://evil.example/login) <b>big</b>")).StatusCode);
        _f.Model.Override = r => r.Messages.Any(m => m.Blocks.Any(b => b is AToolResult))
            ? Say($"Your listing: {{{{product:{s.Product}}}}}")
            : new ModelResponse([new AToolUse("t", "search_products", JsonSerializer.SerializeToElement(new { }))], "tool_use", new(5, 5));
        var chat = await Asst.Chat(s.Owner, "what do I sell");
        var all = chat.ToJsonString();
        foreach (var bad in new[] { "](", "evil.example", "<b>", "](https" }) Assert.DoesNotContain(bad, all);
        var stored = _f.WithDb(db => db.AssistantMessages.Where(m => m.ConversationId == Guid.Parse((string)chat["conversationId"]!)).Select(m => m.Text + m.LinksJson).ToList());
        Assert.All(stored, t => { Assert.DoesNotContain("](", t); Assert.DoesNotContain("evil.example", t); });
        Assert.Contains("Mug", Asst.Text(chat)); // still readable
    }

    [Fact]
    public async Task B1_buyer_names_in_labels_are_plain_text_too()
    {
        var s = await NewShop(_f);
        var (_, id, number, _) = await Place(s, buyer: "Eve [click](https://evil.example)");
        _f.Model.Override = r => r.Messages.Any(m => m.Blocks.Any(b => b is AToolResult))
            ? Say($"Order {{{{order:{id}}}}}")
            : new ModelResponse([new AToolUse("t", "get_order", JsonSerializer.SerializeToElement(new { orderId = id }))], "tool_use", new(5, 5));
        var chat = await Asst.Chat(s.Owner, "look");
        Assert.DoesNotContain("](", chat.ToJsonString()); Assert.DoesNotContain("evil.example", chat.ToJsonString());
    }

    [Theory]
    [InlineData("[[Mug](x)](https://evil.example/login)")]
    [InlineData("[[[support](a)](b)](mailto:attacker@evil.example)")]
    [InlineData("see mailto:attacker@evil.example now")]
    [InlineData("![[x](y)](https://evil.example/p.png)")]
    [InlineData("[a][ref]\n[ref]: https://evil.example")]
    [InlineData("[x]( https://evil.example )")]
    public void B2_sanitizer_leaves_no_link_syntax_nested_or_mailto(string dirty)
    {
        var clean = OutputSanitizer.Clean(dirty);
        Assert.DoesNotContain("](", clean); Assert.DoesNotContain("evil.example", clean, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("mailto:", clean, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task B3_streamed_text_events_are_already_clean_even_when_the_turn_is_cut_short()
    {
        var s = await NewShop(_f);
        _f.Model.Override = _ => Say("Click [here](https://evil.example/x) or [[a](b)](c) <img src=x onerror=1> &lt;b&gt; mailto:a@evil.example **ok**");
        var res = await s.Owner.Send(new HttpRequestMessage(HttpMethod.Post, "/api/v1/assistant/messages/stream") { Content = System.Net.Http.Json.JsonContent.Create(new { text = "hi" }) });
        var raw = await res.Content.ReadAsStringAsync();
        var texts = raw.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(b => (T: b.Split('\n')[0][7..], D: JsonNode.Parse(b.Split('\n')[1][6..])!)).Where(e => e.T == "text").Select(e => (string)e.D["delta"]!).ToList();
        var streamed = string.Concat(texts);
        Assert.NotEmpty(texts);
        foreach (var bad in new[] { "http", "](", "<img", "evil.example", "mailto" }) Assert.DoesNotContain(bad, streamed);
        Assert.Contains("ok", streamed);
    }
}

public class AssistantBudgetRaceTests : IClassFixture<AssistantLimitsFactory>
{
    private readonly AssistantLimitsFactory _f;
    public AssistantBudgetRaceTests(AssistantLimitsFactory f) { _f = f; f.Model.Reset(); }

    [Fact]
    public async Task B4_parallel_messages_never_exceed_the_daily_budget()
    {
        var s = await NewShop(_f);                                       // limits: 4 per user, 6 per account
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Asst.ChatRaw(s.Owner, "hello " + i)));
        var ok = results.Count(r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(4, ok);
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode));
        Assert.Equal(4, _f.Model.Requests.Count);                        // refused messages never reached the model
        Assert.Equal(4, _f.WithDb(db => db.AssistantUsages.Where(u => u.SellerId == s.Seller).Sum(u => u.Messages)));
    }

    [Fact]
    public async Task B4_account_budget_holds_under_parallel_load_from_two_users()
    {
        var s = await NewShop(_f);
        var (mate, _) = await TeamHelpers.JoinAs(_f, s.Owner, "manager");
        var tasks = Enumerable.Range(0, 6).Select(i => Asst.ChatRaw(s.Owner, "o" + i)).Concat(Enumerable.Range(0, 6).Select(i => Asst.ChatRaw(mate, "m" + i)));
        var results = await Task.WhenAll(tasks);
        Assert.Equal(6, results.Count(r => r.StatusCode == HttpStatusCode.OK));   // account limit 6
    }
}

public class AssistantTokenMidTurnTests : IClassFixture<AssistantTokenFactory>
{
    private readonly AssistantTokenFactory _f;
    public AssistantTokenMidTurnTests(AssistantTokenFactory f) { _f = f; f.Model.Reset(); }

    [Fact]
    public async Task B4_token_budget_is_enforced_between_model_calls_of_one_turn()
    {
        var s = await NewShop(_f);                                       // 300 tokens per user per day; the fake spends ~150 per call
        _f.Model.AlwaysCallTool = true;
        var chat = await Asst.Chat(s.Owner, "go");
        Assert.Equal("budget", (string)chat["message"]!["status"]!);
        Assert.True(_f.Model.Requests.Count is >= 2 and < 8, "stopped by the token budget, not by MaxIterations: " + _f.Model.Requests.Count);
        Assert.Contains("capacity", Asst.Text(chat));
    }
}
