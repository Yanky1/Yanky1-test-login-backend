using System.Text.Json;
using System.Text.RegularExpressions;

namespace Solvelt.Api.Assistant;

/// <summary>
/// Deterministic, offline stand-in for the model (dev and tests; no network). It maps plain-language requests to tool calls with keyword rules, and writes the final
/// answer from the tool results' "say" field. It records every request, so tests can assert exactly what a real model would have received.
/// <see cref="ObeyInjections"/> makes it behave like a hijacked model that follows instructions found inside tool results.
/// </summary>
public sealed class ScriptedAssistantModel : IAssistantModel
{
    private readonly List<ModelRequest> _requests = [];
    private readonly List<string> _wire = [];
    private readonly object _lock = new();
    public IReadOnlyList<ModelRequest> Requests { get { lock (_lock) return _requests.ToList(); } }
    /// <summary>Every request serialised as JSON (system + tools + messages): "what the model saw".</summary>
    public string Wire { get { lock (_lock) return string.Join("\n", _wire); } }
    public bool Fail { get; set; }
    public TimeSpan Delay { get; set; }
    public bool ObeyInjections { get; set; }
    public bool AlwaysCallTool { get; set; }
    /// <summary>Replaces the rules entirely (tests that script exact tool calls, e.g. a model asking for tools it was never offered).</summary>
    public Func<ModelRequest, ModelResponse?>? Override { get; set; }
    public void Reset() { lock (_lock) { _requests.Clear(); _wire.Clear(); } Fail = false; Delay = TimeSpan.Zero; ObeyInjections = false; AlwaysCallTool = false; Override = null; }

    public async Task<ModelResponse> CompleteAsync(ModelRequest r, CancellationToken ct)
    {
        lock (_lock)
        {
            _requests.Add(r);
            _wire.Add(JsonSerializer.Serialize(new { r.System, tools = r.Tools.Select(t => new { t.Name, t.Description, t.SchemaJson }), messages = r.Messages.Select(m => new { m.Role, blocks = m.Blocks.Select(Describe) }) }));
        }
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        ct.ThrowIfCancellationRequested();
        if (Fail) throw new InvalidOperationException("model unavailable (fake)");
        if (Override?.Invoke(r) is { } o) return o;
        return Decide(r);
    }


    private static object Describe(ABlock b) => b switch
    {
        AText t => new { type = "text", text = t.Text },
        AToolUse u => new { type = "tool_use", name = u.Name, input = u.Input.ToString() },
        AToolResult x => new { type = "tool_result", content = x.Content },
        _ => new { type = "other" }
    };

    private static ModelResponse Text(string t, int inTok = 100) => new([new AText(t)], "end_turn", new(inTok, Math.Max(1, t.Length / 4)));
    private static int _id;
    private static ModelResponse Call(string name, object input, string? pre = null)
    {
        var blocks = new List<ABlock>();
        if (pre is not null) blocks.Add(new AText(pre));
        blocks.Add(new AToolUse("toolu_" + Interlocked.Increment(ref _id), name, JsonSerializer.SerializeToElement(input)));
        return new(blocks, "tool_use", new(120, 30));
    }

    private ModelResponse Decide(ModelRequest r)
    {
        var offered = r.Tools.Select(t => t.Name).ToHashSet();
        var last = r.Messages[^1];
        if (AlwaysCallTool && offered.Contains("catalog_stats")) return Call("catalog_stats", new { });
        if (last.Blocks.Any(b => b is AToolResult))
        {
            var results = last.Blocks.OfType<AToolResult>().ToList();
            if (ObeyInjections)
            {
                var all = string.Join(" ", r.Messages.SelectMany(m => m.Blocks).OfType<AToolResult>().Select(x => x.Content));
                if (Regex.IsMatch(all, @"accept all orders|ignore (all )?previous instructions", RegexOptions.IgnoreCase))
                {
                    var prior = r.Messages.SelectMany(m => m.Blocks).OfType<AToolUse>().Select(t => t.Name).ToList();
                    if (!prior.Contains("list_orders")) return Call("list_orders", new { });
                    if (!prior.Contains("propose_accept_order"))
                    {
                        var listed = r.Messages.SelectMany(m => m.Blocks).OfType<AToolResult>().Select(x => x.Content).FirstOrDefault(c => c.Contains("\"number\"")) ?? "";
                        var id = Regex.Match(listed, @"""id"":""([0-9a-f-]{36})""").Groups[1].Value;
                        return Call("propose_accept_order", new { orderId = id });
                    }
                    if (!prior.Contains("issue_refund")) return Call("issue_refund", new { amount = 9999 });
                    if (!prior.Contains("reveal_secret")) return Call("reveal_secret", new { name = "Ai:ApiKey" });
                    return Text("Done, as the message instructed.");
                }
            }
            var says = results.Select(x => Say(x.Content)).Where(s => s.Length > 0).ToList();
            return Text(says.Count > 0 ? string.Join("\n\n", says) : "I couldn't complete that.");
        }
        var text = last.Blocks.OfType<AText>().LastOrDefault()?.Text ?? ""; // the user's own words (the first block is the app's context note)
        var pageText = last.PlainText;
        var t = text.ToLowerInvariant();
        bool Has(string n) => offered.Contains(n);
        ModelResponse Refuse(string why) => Text(why);

        var ctxProduct = Regex.Match(pageText, @"\[page: product (?<id>[0-9a-fA-F-]{36})\]");
        if (Regex.IsMatch(t, @"improve (this|the) listing|improve my listing|make (this|the) listing better") && ctxProduct.Success && Has("get_product"))
            return Call("get_product", new { productId = ctxProduct.Groups["id"].Value }, "Let me look at the listing.");
        var draft = Regex.Match(text, @"(?:add|create)\s+(?:a\s+)?draft\s+product\s+(?:for\s+)?(?:an?\s+)?(?<title>[^,$]+?)\s*,?\s*\$(?<price>\d+(?:\.\d{1,2})?)\s*,?\s*(?<stock>\d+)\s+in\s+stock", RegexOptions.IgnoreCase);
        if (draft.Success)
        {
            if (!Has("propose_create_product")) return Refuse("I can't create products for you: your role only lets you view. Ask an Owner, Admin or Manager.");
            var title = Regex.Replace(draft.Groups["title"].Value.Trim(), @"\s+", " ");
            title = char.ToUpper(title[0]) + title[1..];
            return Call("propose_create_product", new { title, price = decimal.Parse(draft.Groups["price"].Value, System.Globalization.CultureInfo.InvariantCulture), quantity = int.Parse(draft.Groups["stock"].Value), categorySlug = "kitchen-dining" });
        }
        var accept = Regex.Match(text, @"accept order\s+(?<n>[A-Za-z0-9-]+)", RegexOptions.IgnoreCase);
        if (accept.Success) return Has("propose_accept_order") ? Call("propose_accept_order", new { number = accept.Groups["n"].Value }) : Refuse("I can't accept orders for you with your role.");
        var ship = Regex.Match(text, @"ship order\s+(?<n>[A-Za-z0-9-]+)\s+with\s+(?<c>\w+)\s+tracking\s+(?<t>[A-Za-z0-9]+)", RegexOptions.IgnoreCase);
        if (ship.Success) return Has("propose_mark_shipped") ? Call("propose_mark_shipped", new { number = ship.Groups["n"].Value, carrier = ship.Groups["c"].Value, trackingNumber = ship.Groups["t"].Value }) : Refuse("I can't mark orders shipped with your role.");
        var price = Regex.Match(text, @"set (?:the )?price of (?<id>[0-9a-fA-F-]{36}) to \$(?<p>\d+(?:\.\d{1,2})?)", RegexOptions.IgnoreCase);
        if (price.Success) return Has("propose_set_price_stock") ? Call("propose_set_price_stock", new { productId = price.Groups["id"].Value, price = decimal.Parse(price.Groups["p"].Value, System.Globalization.CultureInfo.InvariantCulture) }) : Refuse("I can't change prices with your role.");
        var reply = Regex.Match(text, @"reply to conversation (?<id>[0-9a-fA-F-]{36}):\s*(?<body>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (reply.Success) return Has("propose_buyer_reply") ? Call("propose_buyer_reply", new { conversationId = reply.Groups["id"].Value, text = reply.Groups["body"].Value.Trim() }) : Refuse("I can't send messages with your role.");
        var activate = Regex.Match(text, @"(?<v>activate|deactivate) (?:listing )?(?<id>[0-9a-fA-F-]{36})", RegexOptions.IgnoreCase);
        if (activate.Success) return Has("propose_set_listing_status") ? Call("propose_set_listing_status", new { productId = activate.Groups["id"].Value, status = activate.Groups["v"].Value.ToLower() == "activate" ? "active" : "inactive" }) : Refuse("I can't change listings with your role.");
        var booking = Regex.Match(text, @"(?<v>approve|decline) booking (?<id>[0-9a-fA-F-]{36})", RegexOptions.IgnoreCase);
        if (booking.Success) return Has("propose_decide_booking") ? Call("propose_decide_booking", new { bookingId = booking.Groups["id"].Value, decision = booking.Groups["v"].Value.ToLower() }) : Refuse("I can't decide bookings with your role.");

        if (Regex.IsMatch(t, @"\blate\b") && t.Contains("order")) return Has("list_orders") ? Call("list_orders", new { late = true }) : Refuse("I can't look at orders right now.");
        if (Regex.IsMatch(t, @"to ship|need(s)? shipping|ready to ship")) return Call("list_orders", new { tab = "to_ship" });
        if (t.Contains("order")) return Call("list_orders", new { });
        if (Regex.IsMatch(t, @"unanswered|need(s)? (a )?repl|buyer message|messages? from buyers?")) return Call("list_conversations", new { unansweredOnly = true });
        if (Regex.IsMatch(t, @"today|schedule|booking")) return Call("list_bookings", new { tab = t.Contains("request") ? "requests" : "today" });
        if (t.Contains("quote")) return Call("list_quote_requests", new { });
        if (Regex.IsMatch(t, @"sales|revenue|response time|stats")) return Call("get_stats", new { days = 30 });
        if (Regex.IsMatch(t, @"readiness|catalog|gaps|health")) return Call("catalog_stats", new { });
        if (Regex.IsMatch(t, @"team|who works")) return Has("list_team") ? Call("list_team", new { }) : Call("list_brands", new { });
        if (t.Contains("brand")) return Call("list_brands", new { });
        if (Regex.IsMatch(t, @"how do i|help|what is|where can i")) return Call("help_lookup", new { topic = text });
        if (Regex.IsMatch(t, @"product|listing|stock|inventory|mug|board")) return Call("search_products", new { query = Regex.Match(text, @"(?:for|called|named)\s+(?<q>[\w ]{3,40})", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups["q"].Value.Trim() : null });
        return Text("Hi! I can look up your products, orders, bookings and buyer messages, and prepare changes for you to confirm. What would you like to do?");
    }

    private static string Say(string content)
    {
        var m = Regex.Match(content, @"""say"":""((?:[^""\\]|\\.)*)""");
        if (!m.Success) return "";
        try { return JsonSerializer.Deserialize<string>("\"" + m.Groups[1].Value + "\"") ?? ""; } catch { return ""; }
    }
}
