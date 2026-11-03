using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Options;
using Solvelt.Api.Products;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Assistant;

public record MessageIn(Guid? ConversationId, string? Text, PageIn? Page);
public record PageIn(string? Kind, Guid? Id);
public record RenameIn(string? Title);

/// <summary>Retention + expiry job (also callable from tests).</summary>
public sealed class AssistantMaintenance(AppDbContext db, PendingActionService pending, TimeProvider time, IOptionsMonitor<AssistantOptions> opt)
{
    public record Summary(int ActionsExpired, int ConversationsDeleted);
    public async Task<Summary> RunAsync(CancellationToken ct = default)
    {
        var expired = await pending.ExpireDueAsync();
        var cutoff = time.GetUtcNow().UtcDateTime.AddDays(-Math.Max(1, opt.CurrentValue.RetentionDays));
        var ids = await db.AssistantConversations.Where(c => c.UpdatedAt < cutoff).Select(c => c.Id).Take(500).ToListAsync(ct);
        if (ids.Count > 0)
        {
            await db.AssistantActions.Where(x => ids.Contains(x.ConversationId)).ExecuteDeleteAsync(ct);
            await db.AssistantMessages.Where(x => ids.Contains(x.ConversationId)).ExecuteDeleteAsync(ct);
            await db.AssistantConversations.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(ct);
        }
        // old usage counters are not needed after a week
        var day = time.GetUtcNow().UtcDateTime.AddDays(-7).ToString("yyyy-MM-dd");
        await db.AssistantUsages.Where(u => string.Compare(u.Day, day) < 0).ExecuteDeleteAsync(ct);
        return new(expired, ids.Count);
    }
}

public sealed class AssistantWorker(IServiceScopeFactory scopes, ILogger<AssistantWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { using var s = scopes.CreateScope(); await s.ServiceProvider.GetRequiredService<AssistantMaintenance>().RunAsync(stop); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Assistant maintenance failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(30), stop); } catch (OperationCanceledException) { }
        }
    }
}

public static class AssistantModule
{
    private static readonly string[] PageKinds = ["product", "service", "order", "booking", "conversation", "quote", "none"];

    public static WebApplicationBuilder AddAssistant(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration; var s = builder.Services;
        s.Configure<AssistantOptions>(cfg.GetSection(AssistantOptions.Section));
        // The scripted fake must never be the model in production: there the assistant stays off until a real provider is configured.
        s.PostConfigure<AssistantOptions>(o => { if (builder.Environment.IsProduction() && o.Provider.Equals("Fake", StringComparison.OrdinalIgnoreCase)) o.Enabled = false; });
        s.AddSingleton<ScriptedAssistantModel>();
        s.AddSingleton<AnthropicAssistantModel>();
        s.AddSingleton<IAssistantModel>(sp => sp.GetRequiredService<IOptionsMonitor<AssistantOptions>>().CurrentValue.Provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
            ? sp.GetRequiredService<AnthropicAssistantModel>() : sp.GetRequiredService<ScriptedAssistantModel>());
        foreach (var t in ReadTools.Build()) s.AddSingleton<IAssistantTool>(t);
        foreach (var d in new ActionDef[] { new CreateProductAction(), new UpdateListingAction(), new SetPriceStockAction(), new SetListingStatusAction(), new AcceptOrderAction(), new MarkShippedAction(),
                                            new BuyerReplyAction(), new DecideBookingAction(), new RescheduleBookingAction(), new SendQuoteAction(), new CalendarBlockAction() })
            s.AddSingleton(d);
        s.AddSingleton<ActionRegistry>(sp => new ActionRegistry(sp.GetServices<ActionDef>()));
        s.AddScoped<PendingActionService>();
        s.AddScoped<ConversationStore>();
        s.AddScoped<UsageService>();
        s.AddScoped<AssistantEngine>();
        s.AddScoped<AssistantMaintenance>();
        // action tools need the (scoped) pending-action service: build the registry per request
        s.AddScoped<ToolRegistry>(sp => new ToolRegistry(sp.GetServices<IAssistantTool>().Concat(sp.GetServices<ActionDef>().Select(d => (IAssistantTool)new ActionTool(d)))));
        if (!builder.Environment.IsEnvironment("Testing") && (cfg.GetSection(AssistantOptions.Section).Get<AssistantOptions>() ?? new()).RunBackgroundJobs) s.AddHostedService<AssistantWorker>();
        var perMinute = cfg.GetValue("RateLimit:AssistantPerMinute", 30);
        s.Configure<RateLimiterOptions>(r => r.AddPolicy("assistant", c => RateLimitPartition.GetFixedWindowLimiter("assistant:" + (c.User.FindFirst("sub")?.Value ?? c.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
        return builder;
    }

    private static IResult Problem(int status, string code, string title) => Results.Json(new { type = "about:blank", title, status, code }, statusCode: status);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static async Task<object> MessageDto(AppDbContext db, AssistantMessage m, Dictionary<Guid, AssistantPendingAction>? actions = null)
    {
        var ids = JsonSerializer.Deserialize<List<Guid>>(m.ActionIdsJson) ?? [];
        var rows = actions is null ? await db.AssistantActions.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync() : ids.Where(actions.ContainsKey).Select(i => actions[i]).ToList();
        return new
        {
            id = m.Id, role = m.Role, text = m.Text, status = m.Status, createdAt = m.CreatedAt,
            tools = JsonSerializer.Deserialize<JsonElement>(m.ToolsJson), links = JsonSerializer.Deserialize<JsonElement>(m.LinksJson), actions = rows.Select(PendingActionService.Dto)
        };
    }

    public static void MapAssistant(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/assistant").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireActiveAccount().RequireRateLimiting("assistant");
        // feature flag off: everything except /config (which tells the UI to hide itself) answers 503, as documented
        g.AddEndpointFilter(async (ctx, next) =>
            !ctx.HttpContext.RequestServices.GetRequiredService<IOptionsMonitor<AssistantOptions>>().CurrentValue.Enabled && !ctx.HttpContext.Request.Path.Value!.EndsWith("/assistant/config", StringComparison.OrdinalIgnoreCase)
                ? Problem(503, "assistant_disabled", "The assistant isn't available.") : await next(ctx));

        g.MapGet("/config", async (HttpContext ctx, IOptionsMonitor<AssistantOptions> opt, UsageService usage) =>
        {
            var a = ctx.GetAccess()!; var o = opt.CurrentValue;
            var canAct = a.Has(Permission.BrandWorkWrite);
            return Results.Json(new
            {
                enabled = o.Enabled, stopped = o.KillSwitch, role = a.Role.ToString(), canPropose = canAct && o.Enabled && !o.KillSwitch,
                readOnlyReason = canAct ? null : "Your role (Viewer) can look things up but can't make changes. Ask an Owner, Admin or Manager.",
                maxMessageChars = o.MaxMessageChars, actionExpiryMinutes = o.ActionExpiryMinutes, retentionDays = o.RetentionDays, usage = await usage.SummaryAsync(a)
            }, Web);
        });

        g.MapGet("/conversations", async (HttpContext ctx, ConversationStore convs) =>
        {
            var rows = await convs.Mine(ctx.GetAccess()!).AsNoTracking().OrderByDescending(c => c.UpdatedAt).Take(100).ToListAsync();
            return Results.Json(new { items = rows.Select(c => new { id = c.Id, title = c.Title, createdAt = c.CreatedAt, updatedAt = c.UpdatedAt }) }, Web);
        });

        g.MapGet("/conversations/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, ConversationStore convs) =>
        {
            var c = await convs.Mine(ctx.GetAccess()!).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (c is null) return Results.NotFound();
            var msgs = await db.AssistantMessages.AsNoTracking().Where(m => m.ConversationId == id).OrderBy(m => m.Seq).ToListAsync();
            var acts = (await db.AssistantActions.AsNoTracking().Where(x => x.ConversationId == id).ToListAsync()).ToDictionary(x => x.Id);
            var now = ctx.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
            var list = new List<object>(); foreach (var m in msgs) list.Add(await MessageDto(db, m, acts));
            return Results.Json(new { id = c.Id, title = c.Title, messages = list }, Web);
        });

        g.MapPatch("/conversations/{id:guid}", async (Guid id, RenameIn r, HttpContext ctx, AppDbContext db, ConversationStore convs) =>
        {
            var t = r.Title?.Trim();
            if (string.IsNullOrEmpty(t) || t.Length > 120) return Results.Json(new { type = "about:blank", title = "Enter a title (1-120 characters).", status = 422, code = "invalid" }, statusCode: 422);
            var n = await convs.Mine(ctx.GetAccess()!).Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Title, t));
            return n == 0 ? Results.NotFound() : Results.Json(new { id, title = t }, Web);
        });

        g.MapDelete("/conversations/{id:guid}", async (Guid id, HttpContext ctx, ConversationStore convs, IAuditLog audit) =>
        {
            if (!await convs.DeleteAsync(ctx.GetAccess()!, id)) return Results.NotFound();
            await audit.WriteAsync("assistant.conversation.deleted", ctx.GetAccess()!.UserId, null, $"conversation={id}; seller={ctx.GetAccess()!.SellerId}");
            return Results.NoContent();
        });

        g.MapDelete("/conversations", async (HttpContext ctx, ConversationStore convs, IAuditLog audit) =>
        {
            var n = await convs.DeleteAllAsync(ctx.GetAccess()!);
            await audit.WriteAsync("assistant.history.deleted", ctx.GetAccess()!.UserId, null, $"count={n}; seller={ctx.GetAccess()!.SellerId}");
            return Results.Json(new { deleted = n }, Web);
        });

        g.MapGet("/usage", async (HttpContext ctx, UsageService usage) => Results.Json(await usage.SummaryAsync(ctx.GetAccess()!), Web));

        static TurnRequest ToTurn(HttpContext ctx, MessageIn r)
        {
            PageContext? page = r.Page?.Kind is { } k && PageKinds.Contains(k) && k != "none" ? new PageContext(k, r.Page.Id) : null;
            return new TurnRequest(ctx.GetAccess()!, r.ConversationId, r.Text ?? "", page);
        }

        // non-streaming fallback
        g.MapPost("/messages", async (MessageIn r, HttpContext ctx, AssistantEngine engine, AppDbContext db) =>
        {
            try
            {
                var res = await engine.RunAsync(ToTurn(ctx, r), _ => Task.CompletedTask, ctx.RequestAborted);
                var msg = await db.AssistantMessages.AsNoTracking().FirstAsync(m => m.Id == res.MessageId);
                return Results.Json(new { conversationId = res.ConversationId, message = await MessageDto(db, msg), status = res.Status }, Web);
            }
            catch (AssistantException e) { return Problem(e.Status, e.Code, e.Message); }
        });

        // Server-Sent Events: conversation, text (deltas), tool (running|done|error), action (a proposal card), error, done
        g.MapPost("/messages/stream", async (MessageIn r, HttpContext ctx, AssistantEngine engine) =>
        {
            var started = false;
            async Task Send(AssistantEvent e)
            {
                if (!started)
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "text/event-stream";
                    ctx.Response.Headers["Cache-Control"] = "no-store";
                    ctx.Response.Headers["X-Accel-Buffering"] = "no";
                    started = true;
                }
                await ctx.Response.WriteAsync($"event: {e.Type}\ndata: {JsonSerializer.Serialize(e.Data, Web)}\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
            try { await engine.RunAsync(ToTurn(ctx, r), Send, ctx.RequestAborted); }
            catch (AssistantException e) when (!started) { await Problem(e.Status, e.Code, e.Message).ExecuteAsync(ctx); }
            catch (AssistantException e) { await Send(new("error", new { code = e.Code, message = e.Message })); }
            catch (OperationCanceledException) { }
        });

        g.MapGet("/actions/{id:guid}", async (Guid id, HttpContext ctx, PendingActionService svc) =>
            await svc.GetAsync(ctx.GetAccess()!, id) is { } a ? Results.Json(PendingActionService.Dto(a), Web) : Results.NotFound());

        // Confirm / cancel need a SEPARATE authenticated request from the user's browser: bearer token + CSRF double-submit header. The model has neither.
        g.MapPost("/actions/{id:guid}/confirm", async (Guid id, HttpContext ctx, PendingActionService svc, AuthFlows flows) =>
        {
            if (!flows.CsrfOk(ctx)) return Problem(403, "csrf", "CSRF validation failed");
            return Out(await svc.ConfirmAsync(ctx.GetAccess()!, id));
        });
        g.MapPost("/actions/{id:guid}/cancel", async (Guid id, HttpContext ctx, PendingActionService svc, AuthFlows flows) =>
        {
            if (!flows.CsrfOk(ctx)) return Problem(403, "csrf", "CSRF validation failed");
            return Out(await svc.CancelAsync(ctx.GetAccess()!, id));
        });
    }

    private static IResult Out(ConfirmOutcome r)
    {
        if (r.Action is null) return Results.NotFound();
        var body = new
        {
            ok = r.Status is >= 200 and < 300, code = r.Code, message = r.Message, alreadyDone = r.AlreadyDone, action = PendingActionService.Dto(r.Action),
            refreshed = r.Refreshed is null ? null : PendingActionService.Dto(r.Refreshed), entity = r.Ref is null ? null : new { kind = r.Ref.Kind, id = r.Ref.Id, label = r.Ref.Label }
        };
        return Results.Json(body, Web, statusCode: r.Status);
    }
}
