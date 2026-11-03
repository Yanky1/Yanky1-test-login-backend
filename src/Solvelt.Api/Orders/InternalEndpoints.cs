using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Services;

namespace Solvelt.Api.Orders;

public record ReportRequest(string? Reason);
public record ReceiptRequest(string? Status);

/// <summary>
/// The marketplace-facing API (/api/internal/v1). Never reachable with a seller token: no JWT is read here, every request must carry a valid
/// HMAC signature, a fresh timestamp and an unused nonce. See docs/ORDERS.md for the contract.
/// </summary>
public static class InternalEndpoints
{
    private static IResult Err(int status, string code, string message, object? extra = null) =>
        Results.Json(new { type = "about:blank", title = message, status, code, errors = extra }, statusCode: status);

    private static async Task<(T? Body, IResult? Error)> Signed<T>(HttpContext ctx, InboundVerifier verifier) where T : class
    {
        var v = await verifier.VerifyAsync(ctx.Request, ctx.RequestAborted);
        if (!v.Ok) return (null, Err(v.Status, v.Code, v.Message));
        try
        {
            var body = JsonSerializer.Deserialize<T>(v.Body!, OrderJson.Opts);
            return body is null ? (null, Err(400, "invalid_json", "The request body must be a JSON object.")) : (body, null);
        }
        catch (JsonException) { return (null, Err(400, "invalid_json", "The request body is not valid JSON of the expected shape.")); }
    }

    private static object OrderDto(SellerOrder o) => new { sellerOrderId = o.Id, number = o.Number, sellerId = o.SellerId, brandId = o.BrandId, status = o.Status.ToString(), oversold = o.Oversold, totalCents = o.TotalCents, feeCents = o.FeeCents, netCents = o.NetCents };

    public static void MapInternalEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/internal/v1").RequireRateLimiting("internal").AllowAnonymous();

        g.MapPost("/orders", async (HttpContext ctx, InboundVerifier verifier, OrderIngestService svc) =>
        {
            var (req, err) = await Signed<IngestOrder>(ctx, verifier);
            if (err is not null) return err;
            var r = await svc.CreateAsync(req!, ctx.RequestAborted);
            if (!r.Ok) return Err(422, "invalid_order", "The order was rejected; nothing was created.", r.Errors);
            return Results.Json(new { created = r.Created, orders = r.Orders.Select(OrderDto) }, OrderJson.Opts, statusCode: r.Created ? 201 : 200);
        });

        g.MapPost("/orders/{marketplaceOrderId}/cancel-requests", async (HttpContext ctx, InboundVerifier verifier, BuyerEventService svc, string marketplaceOrderId) =>
        {
            var (req, err) = await Signed<BuyerCancelRequest>(ctx, verifier);
            if (err is not null) return err;
            var r = await svc.CancelRequestAsync(marketplaceOrderId, req!);
            return r.Status != 200 ? Err(r.Status, r.Code, r.Message) : Results.Json(new { orders = r.Orders }, OrderJson.Opts);
        });

        g.MapPost("/orders/{marketplaceOrderId}/return-requests", async (HttpContext ctx, InboundVerifier verifier, BuyerEventService svc, string marketplaceOrderId) =>
        {
            var (req, err) = await Signed<BuyerReturnRequest>(ctx, verifier);
            if (err is not null) return err;
            var r = await svc.ReturnRequestAsync(marketplaceOrderId, req!);
            return r.Status != 200 ? Err(r.Status, r.Code, r.Message) : Results.Json(new { orders = r.Orders }, OrderJson.Opts);
        });

        g.MapPost("/orders/{marketplaceOrderId}/delivery-confirmations", async (HttpContext ctx, InboundVerifier verifier, BuyerEventService svc, string marketplaceOrderId) =>
        {
            var (req, err) = await Signed<BuyerDelivery>(ctx, verifier);
            if (err is not null) return err;
            var r = await svc.DeliveryAsync(marketplaceOrderId, req!);
            return r.Status != 200 ? Err(r.Status, r.Code, r.Message) : Results.Json(new { orders = r.Orders }, OrderJson.Opts);
        });

        g.MapPost("/messages", async (HttpContext ctx, InboundVerifier verifier, MessageService svc) =>
        {
            var (req, err) = await Signed<InboundMessage>(ctx, verifier);
            if (err is not null) return err;
            var r = await svc.IngestBuyerMessageAsync(req!, ctx.RequestAborted);
            if (r.Status >= 400) return Err(r.Status, r.Code, r.Message);
            return Results.Json(new { duplicate = r.Duplicate, conversationId = r.Conversation!.Id, messageId = r.Message2!.Id }, OrderJson.Opts, statusCode: r.Duplicate ? 200 : 201);
        });

        g.MapPost("/messages/{messageId}/reports", async (HttpContext ctx, InboundVerifier verifier, MessageService svc, string messageId) =>
        {
            var (req, err) = await Signed<ReportRequest>(ctx, verifier);
            if (err is not null) return err;
            var r = await svc.ReportAsync(messageId, req!.Reason);
            return r.Status >= 400 ? Err(r.Status, r.Code, r.Message) : Results.Ok(new { flagged = true });
        });

        g.MapPost("/messages/{messageId}/receipts", async (HttpContext ctx, InboundVerifier verifier, MessageService svc, string messageId) =>
        {
            var (req, err) = await Signed<ReceiptRequest>(ctx, verifier);
            if (err is not null) return err;
            var r = await svc.ReceiptAsync(messageId, req!.Status);
            return r.Status >= 400 ? Err(r.Status, r.Code, r.Message) : Results.Ok(new { status = r.Message2!.Delivery.ToString().ToLowerInvariant() });
        });

        // the marketplace downloads seller attachments with a signed GET (empty body)
        g.MapGet("/attachments/{id:guid}", async (HttpContext ctx, InboundVerifier verifier, AppDbContext db, IFileStorage storage, Guid id) =>
        {
            var v = await verifier.VerifyAsync(ctx.Request, ctx.RequestAborted);
            if (!v.Ok) return Err(v.Status, v.Code, v.Message);
            var a = await db.MessageAttachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.MessageId != null);
            if (a is null) return Results.NotFound();
            var s = await storage.OpenReadAsync(a.StorageKey);
            if (s is null) return Results.NotFound();
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{a.FileName}\"";
            return Results.Stream(s, a.ContentType);
        });
    }
}
