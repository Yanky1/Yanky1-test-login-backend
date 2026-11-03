using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Products;

public record AnswerRequest(string? Answer, bool? Publish);
public record DevQuestionRequest(Guid ProductId, string? AskerName, string? Text);

public static class QuestionEndpoints
{
    private static object Dto(ProductQuestion q, string productTitle, Guid productId) => new
    {
        id = q.Id, productId, productTitle, askerName = q.AskerName, text = q.Text, answer = q.Answer, state = q.State.ToString(),
        answered = q.Answer != null, askedAt = q.AskedAt, answeredAt = q.AnsweredAt
    };

    /// <summary>Loads a question only if its product is inside the caller's account + brand scope; null means 404.</summary>
    private static async Task<(ProductQuestion? Q, string Title)> Load(AppDbContext db, AccountAccess a, Guid id, bool write)
    {
        var q = await db.Questions.FirstOrDefaultAsync(x => x.Id == id && x.SellerId == a.SellerId);
        if (q is null) return (null, "");
        var pr = await db.Products.AsNoTracking().Where(x => x.Id == q.ProductId && x.SellerId == a.SellerId).Select(x => new { x.BrandId, x.Title }).FirstOrDefaultAsync();
        return pr is not null && a.CanWorkOnBrand(pr.BrandId, write) ? (q, pr.Title) : (null, "");
    }

    public static void MapQuestionEndpoints(this RouteGroupBuilder g)
    {
        var q = g.MapGroup("/seller/questions").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireRateLimiting("catalog");

        q.MapGet("", async (HttpContext ctx, AppDbContext db, string? state, Guid? productId, Guid? brandId, int? page, int? pageSize) =>
        {
            var a = ctx.GetAccess()!;
            var rows = from x in db.Questions.AsNoTracking()
                       join p in db.ProductsFor(a) on x.ProductId equals p.Id
                       where x.SellerId == a.SellerId
                       select new { Q = x, p.Title, p.BrandId };
            if (productId is { } pid) rows = rows.Where(r => r.Q.ProductId == pid);
            if (brandId is { } bid) rows = rows.Where(r => r.BrandId == bid);
            var counts = new
            {
                unanswered = await rows.CountAsync(r => r.Q.Answer == null),
                answered = await rows.CountAsync(r => r.Q.Answer != null),
                hidden = await rows.CountAsync(r => r.Q.State == QuestionState.Hidden)
            };
            switch (state?.ToLowerInvariant())
            {
                case "unanswered": rows = rows.Where(r => r.Q.Answer == null); break;
                case "answered": rows = rows.Where(r => r.Q.Answer != null); break;
                case "hidden": rows = rows.Where(r => r.Q.State == QuestionState.Hidden); break;
                case null or "" or "all": break;
                default: return Validation.Invalid("state", "Use unanswered, answered, hidden or all.");
            }
            var total = await rows.CountAsync();
            var size = Math.Clamp(pageSize ?? 25, 1, 100); var pg = Math.Max(page ?? 1, 1);
            var items = await rows.OrderByDescending(r => r.Q.AskedAt).ThenBy(r => r.Q.Id).Skip((pg - 1) * size).Take(size).ToListAsync();
            return Results.Json(new { items = items.Select(r => Dto(r.Q, r.Title, r.Q.ProductId)), total, page = pg, pageSize = size, counts }, ProductJson.Opts);
        }).RequirePermission(Permission.BrandWorkRead);

        q.MapPut("/{id:guid}/answer", async (Guid id, AnswerRequest r, HttpContext ctx, AppDbContext db, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            var (qu, title) = await Load(db, a, id, write: true);
            if (qu is null) return Results.NotFound();
            var ans = r.Answer?.Trim() ?? "";
            if (ans.Length is 0 or > 2000) return Validation.Invalid("answer", "Enter an answer (1-2000 characters).");
            qu.Answer = ans; qu.AnsweredAt = time.UtcNow(); qu.AnsweredBy = a.UserId;
            qu.State = r.Publish == false ? QuestionState.Pending : QuestionState.Published;
            await db.SaveChangesAsync();
            return Results.Json(Dto(qu, title, qu.ProductId), ProductJson.Opts);
        }).RequirePermission(Permission.BrandWorkWrite);

        q.MapPost("/{id:guid}/publish", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var (qu, title) = await Load(db, ctx.GetAccess()!, id, write: true);
            if (qu is null) return Results.NotFound();
            if (qu.Answer is null) return Validation.Invalid("answer", "Answer the question before publishing it.");
            qu.State = QuestionState.Published; await db.SaveChangesAsync();
            return Results.Json(Dto(qu, title, qu.ProductId), ProductJson.Opts);
        }).RequirePermission(Permission.BrandWorkWrite);

        q.MapPost("/{id:guid}/hide", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var (qu, title) = await Load(db, ctx.GetAccess()!, id, write: true);
            if (qu is null) return Results.NotFound();
            qu.State = QuestionState.Hidden; await db.SaveChangesAsync();
            return Results.Json(Dto(qu, title, qu.ProductId), ProductJson.Opts);
        }).RequirePermission(Permission.BrandWorkWrite);
    }

    /// <summary>Stand-in for the (future) buyer site. Mapped ONLY in the Testing and Development environments.</summary>
    public static void MapDevQuestionEndpoint(this WebApplication app)
    {
        app.MapPost("/api/dev/questions", async (DevQuestionRequest r, AppDbContext db, TimeProvider time) =>
        {
            var p = await db.Products.AsNoTracking().Where(x => x.Id == r.ProductId).Select(x => new { x.Id, x.SellerId }).FirstOrDefaultAsync();
            if (p is null) return Results.NotFound();
            var text = r.Text?.Trim() ?? "";
            if (text.Length is 0 or > 1000) return Validation.Invalid("text", "Enter a question (1-1000 characters).");
            var name = string.IsNullOrWhiteSpace(r.AskerName) ? "A shopper" : r.AskerName.Trim();
            if (name.Length > 80) name = name[..80];
            var q = new ProductQuestion { ProductId = p.Id, SellerId = p.SellerId, AskerName = name, Text = text, AskedAt = time.UtcNow() };
            db.Questions.Add(q); await db.SaveChangesAsync();
            return Results.Created($"/api/dev/questions/{q.Id}", new { id = q.Id });
        }).AllowAnonymous();
    }
}
