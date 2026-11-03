using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Options;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Assistant;

public sealed record ConfirmOutcome(int Status, string Code, string Message, AssistantPendingAction? Action, AssistantPendingAction? Refreshed = null, EntityRef? Ref = null, bool AlreadyDone = false);

/// <summary>
/// Pending actions: the ONLY way the assistant changes anything, and only when the owning user confirms with a separate authenticated request.
/// Bound to user + active account + conversation; 404 for anyone else. Confirm re-checks permission, scope and the target's current state, executes at most once
/// (atomic claim), writes an audit row marked "via assistant" and appends the result to the conversation.
/// </summary>
public sealed class PendingActionService(AppDbContext db, ActionRegistry defs, IServiceProvider sp, TimeProvider time, IOptionsMonitor<AssistantOptions> opt, IAuditLog audit,
    ConversationStore convs, ILogger<PendingActionService> log)
{
    public static string Key(Guid user, Guid conv, string type, Guid? target, string payloadJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{user}|{conv}|{type}|{target}|{payloadJson}")))[..40];

    public static object Dto(AssistantPendingAction a) => new
    {
        id = a.Id, conversationId = a.ConversationId, type = a.Type, target = new { kind = a.TargetKind, id = a.TargetId, label = a.TargetLabel }, summary = a.Summary,
        diff = JsonSerializer.Deserialize<JsonElement>(a.DiffJson), risk = a.Risk, status = a.Status.ToString().ToLowerInvariant(), createdAt = a.CreatedAt, expiresAt = a.ExpiresAt,
        result = a.ResultCode is null ? null : new { code = a.ResultCode, message = a.ResultMessage }, supersededBy = a.SupersededBy
    };

    public async Task<AssistantPendingAction> ProposeAsync(ToolContext c, ActionDef def, Proposal p)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var payload = JsonSerializer.Serialize(p.Payload);
        var key = Key(c.Access.UserId, c.ConversationId, def.Type, p.TargetId, payload + "|" + p.Stamp);
        var existing = await db.AssistantActions.FirstOrDefaultAsync(x => x.UserId == c.Access.UserId && x.IdempotencyKey == key && x.Status == PendingStatus.Pending && x.ExpiresAt > now);
        if (existing is not null) return existing; // the model asked twice: one card
        var a = new AssistantPendingAction
        {
            SellerId = c.Access.SellerId, UserId = c.Access.UserId, ConversationId = c.ConversationId, Type = def.Type, TargetKind = p.TargetKind, TargetId = p.TargetId, TargetLabel = ReadTools.Clip(p.TargetLabel, 200),
            Summary = ReadTools.Clip(p.Summary, 500), PayloadJson = payload, DiffJson = JsonSerializer.Serialize(p.Diff, JsonSerializerOptions.Web), Risk = p.Risk, Stamp = p.Stamp, IdempotencyKey = key, CreatedAt = now,
            ExpiresAt = now.AddMinutes(opt.CurrentValue.ActionExpiryMinutes)
        };
        db.AssistantActions.Add(a);
        await db.SaveChangesAsync();
        return a;
    }

    private IQueryable<AssistantPendingAction> Mine(AccountAccess a, Guid id) => db.AssistantActions.Where(x => x.Id == id && x.SellerId == a.SellerId && x.UserId == a.UserId);

    public async Task<AssistantPendingAction?> GetAsync(AccountAccess a, Guid id) => await Mine(a, id).AsNoTracking().FirstOrDefaultAsync();

    public async Task<ConfirmOutcome> CancelAsync(AccountAccess a, Guid id)
    {
        var act = await Mine(a, id).AsNoTracking().FirstOrDefaultAsync();
        if (act is null) return new(404, "not_found", "Action not found.", null);
        var now = time.GetUtcNow().UtcDateTime;
        var n = await Mine(a, id).Where(x => x.Status == PendingStatus.Pending).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PendingStatus.Cancelled).SetProperty(x => x.ResolvedAt, now).SetProperty(x => x.ResultCode, "cancelled").SetProperty(x => x.ResultMessage, "Cancelled. Nothing was changed."));
        var fresh = await Mine(a, id).AsNoTracking().FirstAsync();
        if (n == 1)
        {
            await audit.WriteAsync("assistant.action.cancelled", a.UserId, null, $"via assistant; type={act.Type}; action={id}; seller={a.SellerId}");
            await convs.AppendAsync(act.ConversationId, a.SellerId, a.UserId, "note", $"Cancelled: {act.Summary} Nothing was changed.", [id]);
            return new(200, "cancelled", "Cancelled. Nothing was changed.", fresh);
        }
        return fresh.Status == PendingStatus.Cancelled ? new(200, "cancelled", "Already cancelled.", fresh, AlreadyDone: true) : new(409, "not_pending", $"This action is already {fresh.Status.ToString().ToLowerInvariant()}.", fresh);
    }

    public async Task<ConfirmOutcome> ConfirmAsync(AccountAccess a, Guid id, bool retried = false)
    {
        var act = await Mine(a, id).AsNoTracking().FirstOrDefaultAsync();
        if (act is null) return new(404, "not_found", "Action not found.", null);
        if (opt.CurrentValue.KillSwitch || !opt.CurrentValue.Enabled) return new(503, "assistant_stopped", "The assistant is switched off right now, so nothing was changed.", act);
        var now = time.GetUtcNow().UtcDateTime;
        switch (act.Status)
        {
            case PendingStatus.Confirmed: return new(200, "ok", act.ResultMessage ?? "Done.", act, AlreadyDone: true);
            case PendingStatus.Executing: return new(409, "in_progress", "This action is already being carried out.", act);
            case PendingStatus.Cancelled or PendingStatus.Superseded: return new(409, "not_pending", "This action was cancelled or replaced. Nothing was changed.", act);
            case PendingStatus.Failed: return new(409, act.ResultCode ?? "failed", act.ResultMessage ?? "This action failed earlier. Nothing further was changed.", act);
            case PendingStatus.Expired: return new(410, "action_expired", "This proposal expired. Ask me to prepare it again.", act);
        }
        if (now > act.ExpiresAt)
        {
            await Mine(a, id).Where(x => x.Status == PendingStatus.Pending).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PendingStatus.Expired).SetProperty(x => x.ResolvedAt, now));
            return new(410, "action_expired", "This proposal expired. Ask me to prepare it again.", act);
        }
        var def = defs.Find(act.Type);
        if (def is null) return await Finish(a, act, PendingStatus.Failed, 500, "unknown_action", "That kind of action isn't available.");
        // permission is re-checked against the membership as it is NOW (the request's AccountAccess is read from the database on every call)
        if (!a.Has(def.Permission)) { await Resolve(a, act, PendingStatus.Cancelled, "forbidden", "You no longer have permission to do this."); return new(403, "forbidden", "You don't have permission to do this.", await GetAsync(a, id)); }
        // atomic claim: only one concurrent confirm gets to execute
        var claimed = await Mine(a, id).Where(x => x.Status == PendingStatus.Pending && x.ExpiresAt > now).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PendingStatus.Executing));
        if (claimed == 0) return retried ? new(409, "in_progress", "This action is already being carried out.", await GetAsync(a, id)) : await ConfirmAsync(a, id, retried: true); // lost the race: report what the winner left behind
        db.ChangeTracker.Clear();
        var ctx = new ToolContext(db, a, sp, time, act.ConversationId, opt.CurrentValue);
        var payload = JsonDocument.Parse(act.PayloadJson).RootElement;
        try
        {
            var prepared = await def.PrepareAsync(ctx, payload); // scope + state, as of now
            if (prepared.Proposal is null) return await Finish(a, act, PendingStatus.Failed, 409, "target_unavailable", prepared.Error ?? "The target changed and this can't be done any more.");
            if (prepared.Proposal.Stamp != act.Stamp)
            {
                // the target changed since the proposal: do NOT execute; show a refreshed proposal instead
                await Resolve(a, act, PendingStatus.Superseded, "target_changed", "The target changed after this was proposed.");
                db.ChangeTracker.Clear();
                var fresh = await ProposeAsync(ctx, def, prepared.Proposal);
                await Mine(a, id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SupersededBy, fresh.Id));
                await convs.AppendAsync(act.ConversationId, a.SellerId, a.UserId, "note", $"Things changed since I prepared that, so I made a fresh proposal: {fresh.Summary}", [fresh.Id]);
                return new(409, "target_changed", "This changed since it was proposed. Review the refreshed proposal; nothing was changed.", await GetAsync(a, id), fresh);
            }
            db.ChangeTracker.Clear();
            var res = await def.ExecuteAsync(ctx, payload);
            if (!res.Ok) return await Finish(a, act, PendingStatus.Failed, res.Status, res.Code, res.Message);
            await Mine(a, id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PendingStatus.Confirmed).SetProperty(x => x.ResolvedAt, now).SetProperty(x => x.ResultCode, "ok").SetProperty(x => x.ResultMessage, res.Message));
            await audit.WriteAsync("assistant.action.confirmed", a.UserId, null, $"via assistant; type={act.Type}; target={act.TargetKind}:{act.TargetId}; action={id}; seller={a.SellerId}");
            await convs.AppendAsync(act.ConversationId, a.SellerId, a.UserId, "note", $"Done: {res.Message}", [id]);
            return new(200, "ok", res.Message, await GetAsync(a, id), Ref: res.Ref);
        }
        catch (Exception e)
        {
            log.LogError(e, "Assistant action {Type} failed", act.Type);
            return await Finish(a, act, PendingStatus.Failed, 500, "action_failed", "Something went wrong and the change may not have been made. Check the item before trying again.");
        }
    }

    private async Task Resolve(AccountAccess a, AssistantPendingAction act, PendingStatus st, string code, string msg)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await Mine(a, act.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, st).SetProperty(x => x.ResolvedAt, now).SetProperty(x => x.ResultCode, code).SetProperty(x => x.ResultMessage, msg));
    }

    private async Task<ConfirmOutcome> Finish(AccountAccess a, AssistantPendingAction act, PendingStatus st, int status, string code, string msg)
    {
        await Resolve(a, act, st, code, msg);
        await audit.WriteAsync("assistant.action.failed", a.UserId, null, $"via assistant; type={act.Type}; code={code}; action={act.Id}; seller={a.SellerId}");
        await convs.AppendAsync(act.ConversationId, a.SellerId, a.UserId, "note", $"I couldn't do that: {msg}", [act.Id]);
        return new(status, code, msg, await GetAsync(a, act.Id));
    }

    /// <summary>Maintenance: proposals nobody answered become Expired.</summary>
    public async Task<int> ExpireDueAsync()
    {
        var now = time.GetUtcNow().UtcDateTime;
        return await db.AssistantActions.Where(x => x.Status == PendingStatus.Pending && x.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PendingStatus.Expired).SetProperty(x => x.ResolvedAt, now));
    }
}

/// <summary>Conversation storage: strictly per (account, user). Every lookup includes both ids, so somebody else's id is simply "not found".</summary>
public sealed class ConversationStore(AppDbContext db, TimeProvider time)
{
    public IQueryable<AssistantConversation> Mine(AccountAccess a) => db.AssistantConversations.Where(x => x.SellerId == a.SellerId && x.UserId == a.UserId);

    public async Task<AssistantMessage> AppendAsync(Guid conversationId, Guid sellerId, Guid userId, string role, string text, IEnumerable<Guid>? actionIds = null,
        string? toolsJson = null, string? linksJson = null, string status = "ok", int inTok = 0, int outTok = 0)
    {
        for (var attempt = 0; ; attempt++)
        {
            var seq = (await db.AssistantMessages.Where(m => m.ConversationId == conversationId).MaxAsync(m => (long?)m.Seq) ?? 0) + 1;
            var m = new AssistantMessage
            {
                ConversationId = conversationId, SellerId = sellerId, UserId = userId, Seq = seq, Role = role, Text = text, ToolsJson = toolsJson ?? "[]", LinksJson = linksJson ?? "[]", Status = status,
                ActionIdsJson = JsonSerializer.Serialize((actionIds ?? []).ToList()), InputTokens = inTok, OutputTokens = outTok, CreatedAt = time.GetUtcNow().UtcDateTime
            };
            db.AssistantMessages.Add(m);
            try
            {
                await db.SaveChangesAsync();
                var now = time.GetUtcNow().UtcDateTime;
                await db.AssistantConversations.Where(x => x.Id == conversationId).ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, now));
                return m;
            }
            catch (DbUpdateException) when (attempt < 3) { db.Entry(m).State = EntityState.Detached; }
        }
    }

    public async Task<bool> DeleteAsync(AccountAccess a, Guid id)
    {
        var conv = await Mine(a).FirstOrDefaultAsync(x => x.Id == id);
        if (conv is null) return false;
        await db.AssistantActions.Where(x => x.ConversationId == id).ExecuteDeleteAsync();
        await db.AssistantMessages.Where(x => x.ConversationId == id).ExecuteDeleteAsync();
        await db.AssistantConversations.Where(x => x.Id == id).ExecuteDeleteAsync();
        return true;
    }

    public async Task<int> DeleteAllAsync(AccountAccess a)
    {
        var ids = await Mine(a).Select(x => x.Id).ToListAsync();
        await db.AssistantActions.Where(x => ids.Contains(x.ConversationId)).ExecuteDeleteAsync();
        await db.AssistantMessages.Where(x => ids.Contains(x.ConversationId)).ExecuteDeleteAsync();
        return await db.AssistantConversations.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync();
    }
}

/// <summary>Daily budgets (per user and per account), enforced before the model is called and while a turn runs. Cost is an estimate from list prices.</summary>
public sealed class UsageService(AppDbContext db, TimeProvider time, IOptionsMonitor<AssistantOptions> opt)
{
    public string Today => time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd");

    public async Task<(string? Code, string? Message)> CheckAsync(AccountAccess a)
    {
        var o = opt.CurrentValue; var day = Today;
        var mine = await db.AssistantUsages.AsNoTracking().Where(u => u.SellerId == a.SellerId && u.UserId == a.UserId && u.Day == day).FirstOrDefaultAsync();
        var acc = await db.AssistantUsages.AsNoTracking().Where(u => u.SellerId == a.SellerId && u.Day == day).GroupBy(u => 1).Select(g => new { m = g.Sum(x => x.Messages), t = g.Sum(x => x.InputTokens + x.OutputTokens) }).FirstOrDefaultAsync();
        if ((mine?.Messages ?? 0) >= o.DailyMessagesPerUser) return ("budget_user_messages", "You've reached today's assistant message limit. It resets at midnight UTC.");
        if ((mine?.InputTokens ?? 0) + (mine?.OutputTokens ?? 0) >= o.DailyTokensPerUser) return ("budget_user_tokens", "You've used today's assistant capacity. It resets at midnight UTC.");
        if ((acc?.m ?? 0) >= o.DailyMessagesPerAccount) return ("budget_account_messages", "Your account reached today's assistant message limit. It resets at midnight UTC.");
        if ((acc?.t ?? 0) >= o.DailyTokensPerAccount) return ("budget_account_tokens", "Your account used today's assistant capacity. It resets at midnight UTC.");
        return (null, null);
    }

    private async Task<AssistantUsage> RowAsync(AccountAccess a)
    {
        var day = Today;
        var row = await db.AssistantUsages.FirstOrDefaultAsync(u => u.SellerId == a.SellerId && u.UserId == a.UserId && u.Day == day);
        if (row is not null) return row;
        try { db.AssistantUsages.Add(row = new AssistantUsage { SellerId = a.SellerId, UserId = a.UserId, Day = day }); await db.SaveChangesAsync(); return row; }
        catch (DbUpdateException) { db.Entry(row!).State = EntityState.Detached; return await db.AssistantUsages.FirstAsync(u => u.SellerId == a.SellerId && u.UserId == a.UserId && u.Day == day); }
    }

    public async Task CountMessageAsync(AccountAccess a)
    {
        var day = Today;
        await RowAsync(a);
        await db.AssistantUsages.Where(u => u.SellerId == a.SellerId && u.UserId == a.UserId && u.Day == day).ExecuteUpdateAsync(s => s.SetProperty(u => u.Messages, u => u.Messages + 1));
    }

    public async Task AddTokensAsync(AccountAccess a, ModelUsage u)
    {
        var o = opt.CurrentValue; var day = Today;
        long input = u.InputTokens + u.CacheWriteTokens + u.CacheReadTokens; long output = u.OutputTokens;
        var micros = (long)Math.Round((input * o.InputUsdPerMTok + output * o.OutputUsdPerMTok) , 0); // $/MTok * tokens = micro-dollars
        await RowAsync(a);
        await db.AssistantUsages.Where(x => x.SellerId == a.SellerId && x.UserId == a.UserId && x.Day == day)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.InputTokens, x => x.InputTokens + input).SetProperty(x => x.OutputTokens, x => x.OutputTokens + output).SetProperty(x => x.CostMicros, x => x.CostMicros + micros));
    }

    public async Task<object> SummaryAsync(AccountAccess a)
    {
        var o = opt.CurrentValue; var day = Today;
        var mine = await db.AssistantUsages.AsNoTracking().FirstOrDefaultAsync(u => u.SellerId == a.SellerId && u.UserId == a.UserId && u.Day == day);
        return new { day, messages = mine?.Messages ?? 0, messageLimit = o.DailyMessagesPerUser, tokens = (mine?.InputTokens ?? 0) + (mine?.OutputTokens ?? 0), tokenLimit = o.DailyTokensPerUser, estimatedCostUsd = (mine?.CostMicros ?? 0) / 1_000_000m };
    }
}
