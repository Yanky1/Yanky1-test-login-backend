using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Orders;
using static Solvelt.Api.Tests.Orders;

namespace Solvelt.Api.Tests;

/// <summary>Records every database command that FAILED (even one the app catches and recovers from: EF logs those as errors, e.g. "UNIQUE constraint failed").</summary>
public sealed class FailedCommandRecorder : DbCommandInterceptor
{
    public static readonly ConcurrentBag<string> Failures = [];
    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken ct = default)
    { Failures.Add(eventData.Exception.Message); return Task.CompletedTask; }
    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => Failures.Add(eventData.Exception.Message);
}

public class MarkReadFactory : OrdersFactory
{
    protected override void ConfigureTestServices(IServiceCollection services) { base.ConfigureTestServices(services); services.ConfigureDbContext<Solvelt.Api.Data.AppDbContext>(o => o.AddInterceptors(new FailedCommandRecorder())); }
}

/// <summary>Backend sweep bugs: concurrent mark-as-read, Google flag in public-config, a single forwarded-headers middleware.</summary>
public class MarkReadConcurrencyTests(MarkReadFactory f) : IClassFixture<MarkReadFactory>
{
    [Fact]
    public async Task Parallel_first_time_mark_reads_for_the_same_conversation_and_user_never_hit_the_unique_key()
    {
        var s = await NewShop(f);
        var (mid, _, number, _) = await Place(s);
        var (_, _, conv, _) = await Msg.BuyerMessage(f, "order", mid, number);
        FailedCommandRecorder.Failures.Clear();
        f.WithDb(db => { try { db.Database.ExecuteSqlRaw("SELECT * FROM table_that_does_not_exist"); } catch (Exception) { /* expected */ } return 0; });
        Assert.NotEmpty(FailedCommandRecorder.Failures);   // the recorder really sees failed commands (otherwise this test proves nothing)
        FailedCommandRecorder.Failures.Clear();
        for (var round = 0; round < 5; round++)
        {
            var user = Guid.NewGuid();   // a brand-new (conversation, user): every caller finds no row and tries to create it
            using var gate = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
            {
                using var scope = f.Services.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<MessageService>();
                gate.Wait();
                await svc.MarkReadAsync(conv, user, DateTime.UtcNow.AddSeconds(i), buyerCount: i);
            })).ToArray();
            gate.Set();
            await Task.WhenAll(tasks);   // none may throw
            var row = f.WithDb(db => db.ConversationReads.AsNoTracking().Single(r => r.ConversationId == conv && r.UserId == user));
            Assert.Equal(15, row.ReadBuyerCount);   // the read position only moves forward, whatever the order
        }
        Assert.Empty(FailedCommandRecorder.Failures.Where(m => m.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)));
        Assert.Empty(FailedCommandRecorder.Failures);
    }

    [Fact]
    public async Task Marking_read_never_moves_the_position_backwards()
    {
        var s = await NewShop(f);
        var (mid, _, number, _) = await Place(s);
        var (_, _, conv, _) = await Msg.BuyerMessage(f, "order", mid, number);
        var user = Guid.NewGuid(); var t = DateTime.UtcNow;
        using var scope = f.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<MessageService>();
        await svc.MarkReadAsync(conv, user, t, 5);
        await svc.MarkReadAsync(conv, user, t.AddMinutes(-10), 2);
        var row = f.WithDb(db => db.ConversationReads.AsNoTracking().Single(r => r.ConversationId == conv && r.UserId == user));
        Assert.Equal(5, row.ReadBuyerCount);
        Assert.True(row.LastReadAt >= t.AddSeconds(-1));
    }
}

public class GoogleFlagTests
{
    private static async Task<JsonNode> Config(ApiFactory f) => (await f.NewClient().GetFromJsonAsync<JsonNode>("/api/v1/public-config"))!;

    [Fact]
    public async Task Public_config_says_google_is_off_when_no_client_id_is_configured()
    {
        using var f = new ApiFactory();
        Assert.False((bool)(await Config(f))["googleEnabled"]!);
    }

    [Fact]
    public async Task Public_config_says_google_is_on_when_a_client_id_is_configured()
    {
        using var f = new ApiFactory();
        f.Extra["ExternalAuth:GoogleClientId"] = "test-client-id"; f.Extra["ExternalAuth:GoogleClientSecret"] = "test-secret";
        Assert.True((bool)(await Config(f))["googleEnabled"]!);
    }
}

public class SingleForwardedHeadersMiddlewareTests
{
    private static readonly object EnvLock = new();

    /// <summary>Builds the host with the platform switch ASPNETCORE_FORWARDEDHEADERS_ENABLED=true set (it is read while the host is built), then restores the variable.</summary>
    private static ProxyFactory HostWithPlatformSwitch(params (string, string)[] settings)
    {
        var f = new ProxyFactory();
        foreach (var (k, v) in settings) f.Extra[k] = v;
        lock (EnvLock)
        {
            var old = Environment.GetEnvironmentVariable("ASPNETCORE_FORWARDEDHEADERS_ENABLED");
            Environment.SetEnvironmentVariable("ASPNETCORE_FORWARDEDHEADERS_ENABLED", "true");
            try { _ = f.Services; } finally { Environment.SetEnvironmentVariable("ASPNETCORE_FORWARDEDHEADERS_ENABLED", old); }
        }
        return f;
    }

    [Fact]
    public void The_host_switch_does_not_add_a_second_forwarded_headers_middleware()
    {
        using var f = HostWithPlatformSwitch(("App:TrustedProxies:0", "10.1.0.0/16"));
        var filters = f.Services.GetServices<IStartupFilter>().Select(x => x.GetType().Name).ToList();
        Assert.DoesNotContain("ForwardedHeadersStartupFilter", filters);
    }

    [Fact]
    public async Task With_the_host_switch_on_only_the_nearest_hop_is_consumed()
    {
        using var f = HostWithPlatformSwitch(("App:TrustedProxies:0", "10.1.0.0/16"));
        // two trusted hops in a row: a second middleware would consume 10.1.2.2 AND then 198.51.100.1; exactly one consumes just the nearest
        var who = await f.WhoAmI("10.1.77.5", "198.51.100.1, 10.1.2.2");
        Assert.Equal("10.1.2.2", (string?)who["ip"]);
    }

    [Fact]
    public async Task With_the_host_switch_on_and_nothing_trusted_the_headers_are_still_ignored()
    {
        using var f = HostWithPlatformSwitch();
        Assert.Equal("203.0.113.9", (string?)(await f.WhoAmI("203.0.113.9", "198.51.100.1"))["ip"]);
    }
}
