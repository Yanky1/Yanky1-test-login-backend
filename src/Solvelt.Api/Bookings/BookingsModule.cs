using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Email;
using Solvelt.Api.Options;
using Solvelt.Api.Orders;
using Solvelt.Api.Teams;
using Solvelt.Api.Products;
using Solvelt.Api.Services;

namespace Solvelt.Api.Bookings;

public record RescheduleReqIn(string? RequestId, DateTime? NewStart);
public record BuyerCancelIn(string? RequestId, string? Reason);
public record ProposalAnswerIn(bool Accept);
public record QuoteDeclineIn(string? Reason);
public record BookingMessageIn(string? MessageId, string? Text);
public record BookingMessageOut(string? Text);

/// <summary>Background work: expire holds, auto-decline unanswered requests, expire quotes, remind providers, redact customer data.</summary>
public class BookingMaintenance(AppDbContext db, BookingService svc, Mailer mailer, CalendarService cal, Outbox outbox, IOptions<BookingOptions> opt, IOptions<AppOptions> app,
    TimeProvider time, ILogger<BookingMaintenance> log, Solvelt.Api.Demo.DemoContext? demo = null)
{
    public record Summary(int HoldsExpired, int AutoDeclined, int QuotesExpired, int RemindersSent, int Redacted);

    public async Task<Summary> RunAsync(CancellationToken ct = default)
    {
        var now = time.UtcNow();
        int holds = 0, declined = 0, quotes = 0, reminders = 0, redacted = 0;
        foreach (var id in await db.Bookings.Where(b => b.Status == BookingStatus.Hold && b.HoldExpiresAt <= now).Select(b => b.Id).Take(500).ToListAsync(ct))
        {
            var b = await db.Bookings.FirstAsync(x => x.Id == id, ct);
            if ((await svc.ExpireHoldAsync(b)).Ok) holds++;
            db.ChangeTracker.Clear();
        }
        foreach (var id in await db.Bookings.Where(b => b.Status == BookingStatus.Requested && b.ApprovalDeadline <= now).Select(b => b.Id).Take(500).ToListAsync(ct))
        {
            var b = await db.Bookings.FirstAsync(x => x.Id == id, ct);
            if ((await svc.AutoDeclineAsync(b)).Ok) declined++;
            db.ChangeTracker.Clear();
        }
        foreach (var q in await db.QuoteRequests.Where(x => x.Status == QuoteStatus.Quoted && x.QuoteExpiresAt <= now).Take(500).ToListAsync(ct))
        {
            q.Status = QuoteStatus.Expired; q.UpdatedAt = now; q.Version++;
            outbox.Enqueue("quote.expired", q.SellerId, q.Id.ToString(), new { quote = new { quoteId = q.Id, marketplaceQuoteId = q.MarketplaceQuoteId } });
            quotes++;
        }
        await db.SaveChangesAsync(ct);
        reminders += await RemindAsync(now, ct);

        var cutoff = now.AddDays(-opt.Value.RedactAfterDays);
        var terminal = new[] { BookingStatus.Completed, BookingStatus.NoShow, BookingStatus.CancelledByBuyer, BookingStatus.CancelledBySeller, BookingStatus.Declined, BookingStatus.Expired };
        foreach (var b in await db.Bookings.Where(b => b.PiiRedactedAt == null && terminal.Contains(b.Status) && (b.CompletedAt ?? b.UpdatedAt) <= cutoff).Take(500).ToListAsync(ct))
        {
            b.BuyerName = "Redacted"; b.BuyerEmail = null; b.BuyerPhone = null; b.AddressJson = null; b.PostalCode = null; b.IntakeAnswersJson = "[]"; b.PiiRedactedAt = now;
            db.BookingEvents.Add(new BookingEvent { BookingId = b.Id, At = now, Type = "booking.redacted", Actor = "system", Text = "Customer details removed" });
            foreach (var m in await db.BookingMessages.Where(x => x.BookingId == b.Id).ToListAsync(ct)) m.Text = "[message removed]";
            // free text that can carry buyer data: reasons, timeline texts, delivered outbound payloads (messages, cancellation reasons)
            b.CancelReason = null; b.DeclineReason = null;
            var bid = b.Id; var bidText = b.Id.ToString();
            await db.BookingEvents.Where(e => e.BookingId == bid && e.Type != "booking.redacted").ExecuteUpdateAsync(u => u.SetProperty(e => e.Text, (string?)null), ct);
            await db.Outbox.Where(ob => ob.AggregateId == bidText && ob.Status != OutboxStatus.Pending).ExecuteUpdateAsync(u => u.SetProperty(ob => ob.PayloadJson, "{\"redacted\":true}"), ct);
            redacted++;
        }
        var qTerminal = new[] { QuoteStatus.Accepted, QuoteStatus.Declined, QuoteStatus.Expired, QuoteStatus.Withdrawn };
        foreach (var q in await db.QuoteRequests.Where(x => x.PiiRedactedAt == null && qTerminal.Contains(x.Status) && x.UpdatedAt <= cutoff).Take(500).ToListAsync(ct))
        {
            q.QuoteNote = null; q.DeclineReason = null;
            var qidText = q.Id.ToString();
            await db.Outbox.Where(ob => ob.AggregateId == qidText && ob.Status != OutboxStatus.Pending).ExecuteUpdateAsync(u => u.SetProperty(ob => ob.PayloadJson, "{\"redacted\":true}"), ct);
            q.BuyerName = "Redacted"; q.BuyerEmail = null; q.BuyerPhone = null; q.AddressJson = null; q.PostalCode = null; q.Details = "Redacted"; q.PreferredTimesJson = "[]"; q.PiiRedactedAt = now;
            redacted++;
        }
        await db.SaveChangesAsync(ct);
        return new Summary(holds, declined, quotes, reminders, redacted);
    }

    private async Task<int> RemindAsync(DateTime now, CancellationToken ct)
    {
        var sent = 0;
        var horizon = now.AddHours(24);
        var due = await db.Bookings.Where(b => (b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.Rescheduled) && b.StartUtc > now && b.StartUtc <= horizon
            && (b.ReminderDaySentAt == null || (b.ReminderHourSentAt == null && b.StartUtc <= now.AddHours(1)))).Take(500).ToListAsync(ct);
        foreach (var b in due)
        {
            var hour = (BJson.Utc(b.StartUtc) - now).TotalHours <= 1;
            var kind = hour ? "hour_before" : "day_before";
            if (hour ? b.ReminderHourSentAt != null : b.ReminderDaySentAt != null) { if (hour && b.ReminderDaySentAt == null) b.ReminderDaySentAt = now; continue; }
            if (hour) { b.ReminderHourSentAt = now; b.ReminderDaySentAt ??= now; } else b.ReminderDaySentAt = now;
            outbox.Enqueue("booking.reminder", b.SellerId, b.Id.ToString(), new { booking = new { bookingId = b.Id, number = b.Number, marketplaceBookingId = b.MarketplaceBookingId }, kind, startAt = BJson.Utc(b.StartUtc) });
            var prof = await db.ProviderProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.MembershipId == b.ProviderId, ct);
            // demo bookings (test tool) never send mail: the outbox event above is already dropped for them
            if (prof is not null && (hour ? prof.ReminderHourBefore : prof.ReminderDayBefore) && demo?.IsDemo(db, b.Id) != true)
            {
                try
                {
                    var email = await (from m in db.Memberships join u in db.Users on m.UserId equals u.Id where m.Id == b.ProviderId select u.Email).FirstOrDefaultAsync(ct);
                    if (email is not null)
                    {
                        var zone = await cal.ZoneAsync(b.SellerId, b.ProviderId);
                        var local = TimeZoneInfo.ConvertTimeFromUtc(BJson.Utc(b.StartUtc), zone);
                        await mailer.SendAsync(Templates.Notification(email, "booking-reminder", hour ? "Your appointment starts within the hour" : "Appointment tomorrow",
                            hour ? "Your next appointment is about to start" : "You have an appointment in the next 24 hours",
                            [$"{b.ServiceTitle} ({b.Number}) starts {local:ddd d MMM, HH:mm} ({zone.Id})."], "Open booking", app.Value.FrontendBaseUrl.TrimEnd((char)47) + $"/bookings/{b.Id}"));
                        sent++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Booking reminder failed"); }
            }
        }
        await db.SaveChangesAsync(ct);
        return sent;
    }
}

public class BookingWorker(IServiceScopeFactory scopes, ILogger<BookingWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { using var scope = scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<Solvelt.Api.Admin.JobRecorder>().RunAsync("booking-maintenance", () => scope.ServiceProvider.GetRequiredService<BookingMaintenance>().RunAsync(stop)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Booking background job failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stop); } catch (OperationCanceledException) { }
        }
    }
}

public static class BookingsModule
{
    public static WebApplicationBuilder AddBookings(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
        builder.Services.Configure<BookingOptions>(cfg.GetSection(BookingOptions.Section));
        builder.Services.AddScoped<CalendarService>();
        builder.Services.AddSingleton<BookingTestHooks>();
        builder.Services.AddScoped<BookingService>();
        builder.Services.AddScoped<QuoteService>();
        builder.Services.AddScoped<BookingMaintenance>();
        builder.Services.AddScoped<IMemberRemovalGuard, BookingRemovalGuard>();
        var o = cfg.GetSection(BookingOptions.Section).Get<BookingOptions>() ?? new BookingOptions();
        if (!builder.Environment.IsEnvironment("Testing") && o.RunBackgroundJobs) builder.Services.AddHostedService<BookingWorker>();
        var writes = cfg.GetValue("RateLimit:BookingsPerMinute", 300);
        builder.Services.AddSingleton(new AvailabilityLimits(cfg.GetValue("RateLimit:AvailabilityPerMinute", 600), cfg.GetValue("RateLimit:AvailabilityFailedPerMinute", 60)));
        builder.Services.Configure<RateLimiterOptions>(r =>
        {
            r.AddPolicy("bookings", c => RateLimitPartition.GetFixedWindowLimiter("bookings:" + (c.User.FindFirst("sub")?.Value ?? c.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = writes, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        return builder;
    }

    public static void MapBookings(this WebApplication app, RouteGroupBuilder v1)
    {
        v1.MapServiceEndpoints();
        v1.MapProviderEndpoints();
        v1.MapBookingEndpoints();
        app.MapIcsFeed();
        app.MapInternalBookingEndpoints();
        if (AdminDevEndpoints.SimulatorEnabled(app.Environment)) app.MapBookingSimulator(v1);
    }

    private static IResult Out(BRes r) => r.Ok
        ? Results.Json(r.Data ?? new { ok = true }, BJson.Opts, statusCode: r.Status is 201 ? 201 : 200)
        : Results.Json(new { type = "about:blank", title = r.Message, status = r.Status, code = r.Code }, statusCode: r.Status);

    private static async Task<(T? Body, IResult? Error)> Signed<T>(HttpContext ctx, InboundVerifier verifier) where T : class
    {
        var v = await verifier.VerifyAsync(ctx.Request, ctx.RequestAborted);
        if (!v.Ok) return (null, Results.Json(new { type = "about:blank", title = v.Message, status = v.Status, code = v.Code }, statusCode: v.Status));
        try
        {
            var body = JsonSerializer.Deserialize<T>(v.Body!, BJson.Opts);
            return body is null ? (null, Err(400, "invalid_json", "The request body must be a JSON object.")) : (body, null);
        }
        catch (JsonException) { return (null, Err(400, "invalid_json", "The request body is not valid JSON of the expected shape.")); }
    }

    private static IResult Err(int status, string code, string title) => Results.Json(new { type = "about:blank", title, status, code }, statusCode: status);

    /// <summary>The marketplace-facing booking API: same HMAC signing, replay protection and body cap as the orders API (see docs/SERVICES.md).</summary>
    public static void MapInternalBookingEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/internal/v1").AllowAnonymous();
        var read = g.MapGroup("");
        var write = g.MapGroup("").RequireRateLimiting("internal");

        // C12: what a buyer can choose before a hold. Same signing, stale and replay rules as the other internal GETs; only services visible in the catalog; ETag + 304.
        read.MapGet("/services/{serviceId:guid}/options", async (HttpContext ctx, InboundVerifier verifier, AppDbContext db, AvailabilityLimits limits, Guid serviceId) =>
        {
            var v = await verifier.VerifyAsync(ctx.Request, ctx.RequestAborted);
            if (!v.Ok) return limits.TryFailed(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown") ? Err(v.Status, v.Code, v.Message) : Err(429, "rate_limited", "Too many failed requests.");
            if (!limits.TryService(serviceId)) return Err(429, "rate_limited", "Too many requests for this service.");
            var built = await ServiceOptions.BuildAsync(db, serviceId, ctx.RequestAborted);
            if (built is null) return Err(404, "not_found", "This service is not available.");      // unknown, not a service, draft, hidden, moderated and unlisted all look the same
            var etag = ServiceOptions.ETagOf(built.Value.Json);
            ctx.Response.Headers.ETag = etag;
            ctx.Response.Headers.CacheControl = "private, max-age=300";
            if (ServiceOptions.Matches(ctx.Request.Headers.IfNoneMatch.ToString(), etag)) return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Text(built.Value.Json, "application/json");
        });

        read.MapGet("/services/{serviceId:guid}/availability", async (HttpContext ctx, InboundVerifier verifier, BookingService svc, AvailabilityLimits limits, Guid serviceId, DateTime? from, DateTime? to,
            Guid? providerId, string? location, string? postalCode, double? lat, double? lng, int? minutes, string? addOns) =>
        {
            var v = await verifier.VerifyAsync(ctx.Request, ctx.RequestAborted);
            // unsigned / failed requests only ever touch a small PER-IP bucket; the per-service bucket is spent by VERIFIED requests alone,
            // so a flood of forged requests can never make the marketplace's own signed requests 429
            if (!v.Ok) return limits.TryFailed(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown") ? Err(v.Status, v.Code, v.Message) : Err(429, "rate_limited", "Too many failed requests.");
            if (!limits.TryService(serviceId)) return Err(429, "rate_limited", "Too many availability requests for this service.");
            if (from is null || to is null) return Err(422, "range", "from and to are required.");
            return Out(await svc.AvailabilityAsync(serviceId, from.Value, to.Value, providerId, location, postalCode, lat, lng, minutes,
                addOns?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()));
        });

        write.MapPost("/bookings/holds", async (HttpContext ctx, InboundVerifier verifier, BookingService svc) =>
        {
            var (req, err) = await Signed<BookingIn>(ctx, verifier);
            return err ?? Out(await svc.CreateAsync(req!, hold: true));
        });
        write.MapPost("/bookings", async (HttpContext ctx, InboundVerifier verifier, BookingService svc) =>
        {
            var (req, err) = await Signed<BookingIn>(ctx, verifier);
            return err ?? Out(await svc.CreateAsync(req!, hold: false));
        });
        write.MapPost("/bookings/{mid}/confirm", async (HttpContext ctx, InboundVerifier verifier, BookingService svc, string mid) =>
        {
            var (req, err) = await Signed<BookingIn>(ctx, verifier);
            return err ?? Out(await svc.ConfirmHoldAsync(mid, req!));
        });
        write.MapGet("/bookings/{mid}", async (HttpContext ctx, InboundVerifier verifier, AppDbContext db, string mid) =>
        {
            var v = await verifier.VerifyAsync(ctx.Request, ctx.RequestAborted);
            if (!v.Ok) return Err(v.Status, v.Code, v.Message);
            var b = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.MarketplaceBookingId == mid);
            if (b is null) return Err(404, "booking_not_found", "Booking not found.");
            return Results.Json(BookingService.Summary(b, await db.ServiceDetails.Where(d => d.ProductId == b.ProductId).Select(d => d.OnlineInstructions).FirstOrDefaultAsync()), BJson.Opts);
        });
        write.MapPost("/bookings/{mid}/reschedule-requests", async (HttpContext ctx, InboundVerifier verifier, AppDbContext db, BookingService svc, string mid) =>
        {
            var (req, err) = await Signed<RescheduleReqIn>(ctx, verifier);
            if (err is not null) return err;
            var b = await db.Bookings.FirstOrDefaultAsync(x => x.MarketplaceBookingId == mid);
            if (b is null) return Err(404, "booking_not_found", "Booking not found.");
            if (req!.NewStart is null) return Err(422, "start", "newStart is required.");
            return Out(await svc.BuyerRescheduleAsync(b, req.RequestId, req.NewStart.Value));
        });
        write.MapPost("/bookings/{mid}/cancellations", async (HttpContext ctx, InboundVerifier verifier, AppDbContext db, BookingService svc, string mid) =>
        {
            var (req, err) = await Signed<BuyerCancelIn>(ctx, verifier);
            if (err is not null) return err;
            var b = await db.Bookings.FirstOrDefaultAsync(x => x.MarketplaceBookingId == mid);
            if (b is null) return Err(404, "booking_not_found", "Booking not found.");
            if (b.Status is BookingStatus.CancelledByBuyer) return Out(BRes.Success(BookingService.Summary(b)));   // idempotent
            return Out(await svc.CancelAsync(b, Actor.Buyer, req!.Reason));
        });
        write.MapPost("/bookings/{mid}/proposal-responses", async (HttpContext ctx, InboundVerifier verifier, AppDbContext db, BookingService svc, string mid) =>
        {
            var (req, err) = await Signed<ProposalAnswerIn>(ctx, verifier);
            if (err is not null) return err;
            var b = await db.Bookings.FirstOrDefaultAsync(x => x.MarketplaceBookingId == mid);
            if (b is null) return Err(404, "booking_not_found", "Booking not found.");
            return Out(await svc.AcceptProposalAsync(b, req!.Accept));
        });
        write.MapPost("/bookings/{mid}/messages", async (HttpContext ctx, InboundVerifier verifier, AppDbContext db, TimeProvider time, string mid) =>
        {
            var (req, err) = await Signed<BookingMessageIn>(ctx, verifier);
            if (err is not null) return err;
            var b = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.MarketplaceBookingId == mid && x.Status != BookingStatus.Hold);
            if (b is null) return Err(404, "booking_not_found", "Booking not found.");
            if (req!.MessageId is not { Length: > 0 and <= 64 } mm) return Err(422, "message_id", "messageId is required (up to 64 characters).");
            if (string.IsNullOrWhiteSpace(req.Text) || req.Text.Length > 2000) return Err(422, "text", "Write a message of up to 2000 characters.");
            if (await db.BookingMessages.AnyAsync(m => m.MarketplaceMessageId == mm)) return Results.Json(new { duplicate = true });
            db.BookingMessages.Add(new BookingMessage { BookingId = b.Id, SellerId = b.SellerId, Direction = "buyer", MarketplaceMessageId = mm, Text = req.Text.Trim(), CreatedAt = time.UtcNow() });
            try { await db.SaveChangesAsync(); } catch (DbUpdateException) { return Results.Json(new { duplicate = true }); }
            return Results.Json(new { duplicate = false }, statusCode: 201);
        });
        write.MapPost("/quote-requests", async (HttpContext ctx, InboundVerifier verifier, QuoteService svc) =>
        {
            var (req, err) = await Signed<QuoteIn>(ctx, verifier);
            return err ?? Out(await svc.CreateRequestAsync(req!));
        });
        write.MapPost("/quotes/{qid}/accept", async (HttpContext ctx, InboundVerifier verifier, QuoteService svc, string qid) =>
        {
            var (req, err) = await Signed<QuoteAcceptIn>(ctx, verifier);
            return err ?? Out(await svc.AcceptAsync(qid, req!));
        });
        write.MapPost("/quotes/{qid}/decline", async (HttpContext ctx, InboundVerifier verifier, QuoteService svc, string qid) =>
        {
            var (req, err) = await Signed<QuoteDeclineIn>(ctx, verifier);
            return err ?? Out(await svc.BuyerDeclineAsync(qid, req!.Reason));
        });

        if (AdminDevEndpoints.SimulatorEnabled(app.Environment))
            app.MapPost("/api/dev/bookings/maintenance/run", async (BookingMaintenance m) => Results.Ok(await m.RunAsync())).AllowAnonymous();
    }

    // ---- simulator (Development / Testing only) ---------------------------------------------------------------------------------
    public record SimBookIn(Guid? ServiceId, Guid? ProviderId, DateTime? Start, int? Minutes, string? Location, string? Mode, string? BuyerName, string? PostalCode, string? Line1, List<string>? AddOnIds, List<IntakeAnswer>? IntakeAnswers);
    public record SimEventIn(string? Type, DateTime? Start, string? Reason);
    public record SimQuoteIn(Guid? ServiceId, string? Details, string? Location, string? PostalCode, string? BuyerName);
    public record SimAcceptIn(DateTime? Start);

    public static void MapBookingSimulator(this WebApplication app, RouteGroupBuilder v1)
    {
        var sim = v1.MapGroup("/seller/dev").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireRateLimiting("bookings").RequirePermission(Permission.BrandWorkWrite);

        async Task<bool> Mine(AppDbContext db, AccountAccess a, Guid serviceId) =>
            await db.ProductsFor(a).AnyAsync(p => p.Id == serviceId && p.Type == ListingType.Service) && a.CanWorkOnBrand((await db.Products.AsNoTracking().FirstAsync(p => p.Id == serviceId)).BrandId, true);

        sim.MapGet("/services/{id:guid}/slots", async (Guid id, HttpContext ctx, AppDbContext db, BookingService svc, DateTime from, DateTime to, Guid? providerId, string? location, string? postalCode, int? minutes) =>
            !await Mine(db, ctx.GetAccess()!, id) ? Results.NotFound() : Out(await svc.AvailabilityAsync(id, from, to, providerId, location, postalCode, null, null, minutes, null)));

        sim.MapPost("/bookings", async (SimBookIn r, HttpContext ctx, AppDbContext db, BookingService svc) =>
        {
            if (r.ServiceId is not { } sid || !await Mine(db, ctx.GetAccess()!, sid)) return Results.NotFound();
            var mid = "SIM-" + Guid.NewGuid().ToString("N")[..12];
            var req = new BookingIn(mid, sid, r.ProviderId, r.Start, r.Minutes, r.Location, r.AddOnIds, new BuyerIn(r.BuyerName ?? "Sam Buyer", "relay-sim@relay.solvelt.example", "+1 555 000 2222"),
                new AddressIn(r.BuyerName ?? "Sam Buyer", r.Line1 ?? "5 Elm St", null, "Brooklyn", "NY", r.PostalCode, "US", null, null), r.IntakeAnswers);
            var mode = (r.Mode ?? "book").ToLowerInvariant();
            var res = await svc.CreateAsync(req, hold: mode == "hold" || mode == "confirm");
            if (res.Ok && mode == "confirm") res = await svc.ConfirmHoldAsync(mid, req);
            return Out(res);
        });

        sim.MapPost("/bookings/{marketplaceBookingId}/events", async (string marketplaceBookingId, SimEventIn r, HttpContext ctx, AppDbContext db, BookingService svc) =>
        {
            var a = ctx.GetAccess()!;
            var b = await db.Bookings.FirstOrDefaultAsync(x => x.MarketplaceBookingId == marketplaceBookingId && x.SellerId == a.SellerId);
            if (b is null || !a.CanWorkOnBrand(b.BrandId, true)) return Results.NotFound();
            return Out(r.Type?.ToLowerInvariant() switch
            {
                "cancel" => await svc.CancelAsync(b, Actor.Buyer, r.Reason ?? "changed_mind"),
                "reschedule" => r.Start is null ? BRes.Invalid("start is required", "start") : await svc.BuyerRescheduleAsync(b, "SIM-" + Guid.NewGuid().ToString("N")[..8], r.Start.Value),
                "accept_proposal" => await svc.AcceptProposalAsync(b, true),
                "decline_proposal" => await svc.AcceptProposalAsync(b, false),
                "confirm_hold" => await svc.ConfirmHoldAsync(marketplaceBookingId, new BookingIn(marketplaceBookingId, null, null, null, null, null, null, new BuyerIn("Sam Buyer", null, null), null, null)),
                _ => BRes.Invalid("type must be cancel, reschedule, accept_proposal, decline_proposal or confirm_hold", "type")
            });
        });

        sim.MapPost("/quotes", async (SimQuoteIn r, HttpContext ctx, AppDbContext db, QuoteService svc) =>
        {
            if (r.ServiceId is not { } sid || !await Mine(db, ctx.GetAccess()!, sid)) return Results.NotFound();
            return Out(await svc.CreateRequestAsync(new QuoteIn("SIMQ-" + Guid.NewGuid().ToString("N")[..12], sid, new BuyerIn(r.BuyerName ?? "Sam Buyer", "relay-sim@relay.solvelt.example", "+1 555 000 2222"),
                r.Details ?? "Please quote this job.", r.Location, new AddressIn(null, "5 Elm St", null, "Brooklyn", "NY", r.PostalCode, "US", null, null), null)));
        });
        sim.MapPost("/quotes/{marketplaceQuoteId}/accept", async (string marketplaceQuoteId, SimAcceptIn r, HttpContext ctx, AppDbContext db, QuoteService svc) =>
        {
            var a = ctx.GetAccess()!;
            if (!await db.QuoteRequests.AnyAsync(x => x.MarketplaceQuoteId == marketplaceQuoteId && x.SellerId == a.SellerId)) return Results.NotFound();
            return Out(await svc.AcceptAsync(marketplaceQuoteId, new QuoteAcceptIn("SIM-" + Guid.NewGuid().ToString("N")[..12], r.Start, null, null, null)));
        });
        sim.MapPost("/quotes/{marketplaceQuoteId}/decline", async (string marketplaceQuoteId, HttpContext ctx, AppDbContext db, QuoteService svc) =>
        {
            var a = ctx.GetAccess()!;
            if (!await db.QuoteRequests.AnyAsync(x => x.MarketplaceQuoteId == marketplaceQuoteId && x.SellerId == a.SellerId)) return Results.NotFound();
            return Out(await svc.BuyerDeclineAsync(marketplaceQuoteId, "simulated"));
        });
    }
}

/// <summary>A member with upcoming bookings cannot be removed or leave: the bookings must be reassigned or cancelled first.</summary>
public class BookingRemovalGuard(AppDbContext db, TimeProvider time) : IMemberRemovalGuard
{
    public async Task<string?> BlockReasonAsync(Guid sellerId, Guid membershipId)
    {
        var now = time.GetUtcNow().UtcDateTime;
        return await db.Bookings.AnyAsync(b => b.ProviderId == membershipId && b.EndUtc > now && CalendarService.BlockingStatuses.Contains(b.Status))
            ? "This person still has upcoming bookings. Reassign or cancel them first." : null;
    }
}

/// <summary>Availability limits: verified requests per service, failed (unsigned/forged) requests per client address.</summary>
public sealed class AvailabilityLimits(int perServicePerMinute, int failedPerIpPerMinute)
{
    private static PartitionedRateLimiter<string> Make(int permits) => PartitionedRateLimiter.Create<string, string>(key =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, permits), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    private readonly PartitionedRateLimiter<string> _service = Make(perServicePerMinute), _failed = Make(failedPerIpPerMinute);
    public bool TryService(Guid serviceId) { using var l = _service.AttemptAcquire(serviceId.ToString()); return l.IsAcquired; }
    public bool TryFailed(string ip) { using var l = _failed.AttemptAcquire(ip); return l.IsAcquired; }
}
