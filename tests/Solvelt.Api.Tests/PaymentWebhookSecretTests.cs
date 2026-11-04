using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Solvelt.Api.Payments;

namespace Solvelt.Api.Tests;

public class BothSecretsFactory : PayFactory
{
    public const string ConnectSecret = "whsec_connect_fedcba9876543210";
    public BothSecretsFactory() { Extra["Stripe:ConnectWebhookSecret"] = ConnectSecret; }
}

public class ConnectOnlyFactory : PayFactory
{
    public ConnectOnlyFactory() { Extra["Stripe:WebhookSecret"] = ""; Extra["Stripe:ConnectWebhookSecret"] = BothSecretsFactory.ConnectSecret; }
}

public class SameSecretFactory : PayFactory
{
    public SameSecretFactory() { Extra["Stripe:ConnectWebhookSecret"] = HookSecret; }
}

/// <summary>Stripe issues a different signing secret for the platform endpoint and for the Connect endpoint: each event must be accepted with the secret of ITS endpoint.</summary>
public class PaymentWebhookSecretTests
{
    private const string Platform = PayFactory.HookSecret;
    private const string Connect = BothSecretsFactory.ConnectSecret;

    /// <summary>A platform event (no `account`): a dispute on an unknown charge is acknowledged and flagged.</summary>
    private static string PlatformEvent(ApiFactory f) => Pay.HookBody("charge.dispute.created", new { id = "dp_sec", charge = "ch_unknown", amount = 100, currency = "usd", status = "needs_response" }, f: f);
    /// <summary>A Connect event (it carries `account`).</summary>
    private static string ConnectEvent(ApiFactory f) => Pay.HookBody("account.updated", Pay.AccountObj("acct_unknown"), "acct_unknown", f: f);

    private static async Task<HttpResponseMessage> Post(ApiFactory f, string path, string body, string secret)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Add("Stripe-Signature", StripeSignature.Header(secret, f.Time.GetUtcNow().ToUnixTimeSeconds(), body));
        return await f.NewClient().SendAsync(req);
    }

    private static int Stored(ApiFactory f) => f.WithDb(db => db.ProcessedPaymentEvents.Count(p => p.Source == "stripe"));

    [Fact]
    public async Task With_both_secrets_each_event_is_accepted_with_the_secret_of_its_endpoint()
    {
        using var f = new BothSecretsFactory();
        Assert.Equal(HttpStatusCode.OK, (await Post(f, "/api/webhooks/stripe", PlatformEvent(f), Platform)).StatusCode);     // platform endpoint -> platform secret
        Assert.Equal(HttpStatusCode.OK, (await Post(f, "/api/webhooks/stripe", ConnectEvent(f), Connect)).StatusCode);       // Connect endpoint -> Connect secret
        Assert.Equal(2, Stored(f));
    }

    [Fact]
    public async Task With_both_secrets_an_event_signed_with_the_other_endpoints_secret_is_refused_and_stores_nothing()
    {
        using var f = new BothSecretsFactory();
        var wrongConnect = await Post(f, "/api/webhooks/stripe", ConnectEvent(f), Platform);          // a Connect event signed with the platform secret
        Assert.Equal(HttpStatusCode.BadRequest, wrongConnect.StatusCode);
        Assert.Equal("wrong_endpoint_secret", await Orders.Code(wrongConnect));
        var wrongPlatform = await Post(f, "/api/webhooks/stripe", PlatformEvent(f), Connect);          // and the other way round
        Assert.Equal(HttpStatusCode.BadRequest, wrongPlatform.StatusCode);
        Assert.Equal("wrong_endpoint_secret", await Orders.Code(wrongPlatform));
        var unknown = await Post(f, "/api/webhooks/stripe", PlatformEvent(f), "whsec_somebody_else");
        Assert.Equal("bad_signature", await Orders.Code(unknown));                                      // a secret that is neither stays a plain bad signature
        Assert.Equal(0, Stored(f));
        Assert.Equal("missing", await Orders.Code(await f.NewClient().PostAsync("/api/webhooks/stripe", new StringContent(PlatformEvent(f), Encoding.UTF8, "application/json"))));
    }

    [Fact]
    public async Task With_one_secret_it_is_used_for_everything_like_before_the_Stripe_CLI_case()
    {
        using var f = new PayFactory();                                                                 // only Stripe:WebhookSecret
        Assert.Equal(HttpStatusCode.OK, (await Post(f, "/api/webhooks/stripe", PlatformEvent(f), Platform)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(f, "/api/webhooks/stripe", ConnectEvent(f), Platform)).StatusCode);
        using var c = new ConnectOnlyFactory();                                                         // only the Connect secret: it is the single secret
        Assert.Equal(HttpStatusCode.OK, (await Post(c, "/api/webhooks/stripe", PlatformEvent(c), Connect)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(c, "/api/webhooks/stripe", ConnectEvent(c), Connect)).StatusCode);
        Assert.Equal("bad_signature", await Orders.Code(await Post(c, "/api/webhooks/stripe", ConnectEvent(c), Platform)));
    }

    [Fact]
    public async Task Two_equal_secrets_are_the_single_secret_and_accept_every_event()
    {
        using var f = new SameSecretFactory();
        Assert.Equal(HttpStatusCode.OK, (await Post(f, "/api/webhooks/stripe", PlatformEvent(f), Platform)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(f, "/api/webhooks/stripe", ConnectEvent(f), Platform)).StatusCode);
    }

    [Fact]
    public async Task The_dedicated_Connect_path_takes_only_the_Connect_secret_or_the_single_secret()
    {
        using var f = new BothSecretsFactory();
        Assert.Equal(HttpStatusCode.OK, (await Post(f, "/api/webhooks/stripe/connect", ConnectEvent(f), Connect)).StatusCode);
        var refused = await Post(f, "/api/webhooks/stripe/connect", ConnectEvent(f), Platform);         // the platform secret does not open the Connect path
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode); Assert.Equal("bad_signature", await Orders.Code(refused));
        using var single = new PayFactory();
        Assert.Equal(HttpStatusCode.OK, (await Post(single, "/api/webhooks/stripe/connect", ConnectEvent(single), Platform)).StatusCode);   // no Connect secret: the single one applies
    }

    [Fact]
    public async Task Nothing_configured_is_still_503()
    {
        using var f = new NoSecretFactory();
        var r = await Post(f, "/api/webhooks/stripe", PlatformEvent(f), Platform);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
    }

    private sealed class NoSecretFactory : PayFactory { public NoSecretFactory() { Extra["Stripe:WebhookSecret"] = ""; } }

    // startup ----------------------------------------------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData("sk_live_abc", "whsec_a", "whsec_b", false, true)]
    [InlineData("sk_live_abc", "whsec_a", "", false, false)]            // live key with only one secret: refused
    [InlineData("sk_live_abc", "", "whsec_b", false, false)]
    [InlineData("sk_live_abc", "", "", false, false)]
    [InlineData("rk_live_abc", "whsec_a", "", false, false)]
    [InlineData("sk_live_abc", "whsec_a", "", true, true)]              // ... unless accepted on purpose
    [InlineData("sk_test_abc", "whsec_a", "", false, true)]             // a test key keeps working with one secret (Stripe CLI)
    [InlineData("", "", "", false, true)]                               // no key: the fake provider
    public void A_live_key_needs_both_webhook_secrets_unless_one_is_accepted_on_purpose(string key, string platform, string connect, bool allowSingle, bool ok)
    {
        var call = () => PaymentsModule.ValidateWebhookSecrets(key, platform, connect, allowSingle);
        if (ok) call(); else Assert.Contains("BOTH webhook secrets", Assert.Throws<InvalidOperationException>(call).Message);
    }

    [Fact]
    public void The_module_applies_the_check_and_reads_both_secrets_from_configuration_or_the_environment()
    {
        WebApplicationBuilder Builder(Dictionary<string, string?> cfg)
        {
            var b = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            b.Configuration.AddInMemoryCollection(cfg);
            return b;
        }
        // live key + one secret: the host refuses to start
        var one = Builder(new() { ["Stripe:SecretKey"] = "sk_live_abc", ["Stripe:WebhookSecret"] = "whsec_a" });
        Assert.Contains("BOTH webhook secrets", Assert.Throws<InvalidOperationException>(() => one.AddPayments()).Message);
        // both set: fine, and both land in the options
        var both = Builder(new() { ["Stripe:SecretKey"] = "sk_live_abc", ["Stripe:WebhookSecret"] = "whsec_a", ["Stripe:ConnectWebhookSecret"] = "whsec_b" });
        both.AddPayments();
        var o = both.Services.BuildServiceProvider().GetRequiredService<IOptions<StripeOptions>>().Value;
        Assert.Equal(("whsec_a", "whsec_b"), (o.WebhookSecret, o.ConnectWebhookSecret));
        // the explicit allow flag
        var allowed = Builder(new() { ["Stripe:SecretKey"] = "sk_live_abc", ["Stripe:WebhookSecret"] = "whsec_a", ["Payments:AllowSingleWebhookSecret"] = "true" });
        allowed.AddPayments();
        // the environment variable is accepted next to the configuration key, and configuration wins
        var old = Environment.GetEnvironmentVariable("STRIPE_CONNECT_WEBHOOK_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("STRIPE_CONNECT_WEBHOOK_SECRET", "  whsec_from_env  ");
            Assert.Equal("whsec_from_env", PaymentsModule.Pick("", "STRIPE_CONNECT_WEBHOOK_SECRET"));
            Assert.Equal("whsec_cfg", PaymentsModule.Pick("whsec_cfg", "STRIPE_CONNECT_WEBHOOK_SECRET"));
            var viaEnv = Builder(new() { ["Stripe:SecretKey"] = "sk_live_abc", ["Stripe:WebhookSecret"] = "whsec_a" });
            viaEnv.AddPayments();                                                                        // the env var completes the pair
            Assert.Equal("whsec_from_env", viaEnv.Services.BuildServiceProvider().GetRequiredService<IOptions<StripeOptions>>().Value.ConnectWebhookSecret);
        }
        finally { Environment.SetEnvironmentVariable("STRIPE_CONNECT_WEBHOOK_SECRET", old); }
    }
}
