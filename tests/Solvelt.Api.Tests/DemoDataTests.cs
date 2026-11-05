using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Tests;

public class DemoFactory : PayFactory
{
    public DemoFactory() { Extra["Demo:Enabled"] = "true"; Extra["Demo:PerHour"] = "100"; }
    public List<string> Errors { get; } = [];
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(l => l.AddProvider(new Capture(Errors)));
    }
    private sealed class Capture(List<string> sink) : Microsoft.Extensions.Logging.ILoggerProvider, Microsoft.Extensions.Logging.ILogger
    {
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string c) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel l) => l >= Microsoft.Extensions.Logging.LogLevel.Error;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel l, Microsoft.Extensions.Logging.EventId e, TState st, Exception? ex, Func<TState, Exception?, string> fmt)
        { if (l >= Microsoft.Extensions.Logging.LogLevel.Error) lock (sink) sink.Add(fmt(st, ex) + " :: " + ex); }
    }
}

public class DemoFillTests(DemoFactory f) : IClassFixture<DemoFactory>
{
    [Fact]
    public async Task Explore_fill_on_an_approved_account()
    {
        var (api, seller) = await ProdApi.Approved(f);
        var r = await api.Post("/api/v1/seller/demo-data");
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == HttpStatusCode.Created, body + string.Join("\n", f.Errors));
    }
}
