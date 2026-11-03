using Solvelt.Api.Payments;

namespace Solvelt.Api.StripeTests;

/// <summary>Runs only with STRIPE_SECRET_KEY=sk_test_... (never a live key). Skipped in THIS project only; the unit-test project has no skipped tests.</summary>
public sealed class StripeFactAttribute : FactAttribute
{
    public static string? Key => Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY") is { Length: > 0 } k ? k : null;
    public StripeFactAttribute()
    {
        if (Key is null) Skip = "no STRIPE_SECRET_KEY: Stripe test-mode tests are opt-in (docs/PAYMENTS.md)";
        else if (!Key.StartsWith("sk_test_") && !Key.StartsWith("rk_test_")) Skip = "refusing to run against anything but a Stripe TEST key";
    }
}

[Trait("Category", "StripeTestMode")]
public class StripeTestModeTests
{
    private static StripePaymentProvider Provider() => new(Microsoft.Extensions.Options.Options.Create(new StripeOptions { SecretKey = StripeFactAttribute.Key! }));

    [StripeFact]
    public async Task Express_account_onboarding_link_and_retrieval_work_in_test_mode()
    {
        var p = Provider();
        var seller = Guid.NewGuid();
        var id = await p.CreateExpressAccountAsync(seller, "payments-test@example.com", "US");
        Assert.StartsWith("acct_", id);
        Assert.Equal(id, await p.CreateExpressAccountAsync(seller, "payments-test@example.com", "US"));   // idempotency key acct-{sellerId}
        var link = await p.CreateAccountLinkAsync(id, "https://example.com/return", "https://example.com/refresh");
        Assert.StartsWith("https://connect.stripe.com/", link.Url);
        var snap = await p.RetrieveAccountAsync(id);
        Assert.False(snap.DetailsSubmitted);
        Assert.False(snap.PayoutsEnabled);
        Assert.NotEmpty(snap.CurrentlyDue);
        await Assert.ThrowsAsync<ProviderException>(() => p.CreateLoginLinkAsync(id));                     // onboarding not finished
    }

    [StripeFact]
    public async Task A_transfer_to_an_account_that_cannot_receive_it_fails_cleanly()
    {
        var p = Provider();
        var id = await p.CreateExpressAccountAsync(Guid.NewGuid(), null, "US");
        var ex = await Assert.ThrowsAsync<ProviderException>(() => p.CreateTransferAsync(new TransferRequest(100, "USD", id, "it-" + Guid.NewGuid(), "grp", Guid.NewGuid())));
        Assert.False(string.IsNullOrEmpty(ex.Message));
        Assert.Null(await p.RetrieveTransferAsync("tr_does_not_exist"));
    }
}
