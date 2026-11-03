using Solvelt.Api.Services;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Stripe;

namespace Solvelt.Api.Payments;

public record AccountSnapshot(string Id, bool DetailsSubmitted, bool ChargesEnabled, bool PayoutsEnabled, string[] CurrentlyDue, string[] PastDue,
    string? DisabledReason, DateTime? Deadline, string? PayoutInterval, int? PayoutDelayDays, string? BankLast4, string? BankName);
public record LinkResult(string Url, DateTime ExpiresAt);
public record TransferRequest(long AmountCents, string Currency, string DestinationAccountId, string IdempotencyKey, string TransferGroup, Guid TransferId);
public record TransferResult(string Id, string Status, long AmountCents, string Currency, string Destination, long ReversedCents = 0);
public class ProviderException(string message, bool transient) : Exception(message) { public bool Transient { get; } = transient; }
public sealed class UnconfiguredException() : ProviderException("Payments are not configured on this server.", false);

/// <summary>Everything the portal needs from Stripe. Real implementation: Stripe.net (test or live key). No key: <see cref="FakePaymentProvider"/>.</summary>
public interface IPaymentProvider
{
    bool IsFake { get; }
    Task<string> CreateExpressAccountAsync(Guid sellerId, string? email, string country, CancellationToken ct = default);
    Task<LinkResult> CreateAccountLinkAsync(string accountId, string returnUrl, string refreshUrl, CancellationToken ct = default);
    Task<LinkResult> CreateLoginLinkAsync(string accountId, CancellationToken ct = default);
    Task<AccountSnapshot> RetrieveAccountAsync(string accountId, CancellationToken ct = default);
    Task<TransferResult> CreateTransferAsync(TransferRequest r, CancellationToken ct = default);
    Task<TransferResult?> RetrieveTransferAsync(string transferId, CancellationToken ct = default);
}

/// <summary>Deterministic in-memory provider for development and tests (CI never calls Stripe). Idempotent transfers, scriptable failures.</summary>
public class FakePaymentProvider : IPaymentProvider
{
    public bool IsFake => true;
    private readonly object _l = new();
    private int _acct, _tr;
    /// <summary>Per-process suffix: ids stay unique across restarts of a dev server that keeps its database.</summary>
    private readonly string _run = Guid.NewGuid().ToString("N")[..6];
    public ConcurrentDictionary<string, AccountSnapshot> Accounts { get; } = new();
    public ConcurrentDictionary<string, TransferResult> Transfers { get; } = new();
    private readonly ConcurrentDictionary<string, TransferResult> _byKey = new();
    /// <summary>Number of Stripe-side transfer creations (idempotent replays are not counted).</summary>
    public int TransferCreates { get { lock (_l) return _tr; } }
    public int TransferCalls;
    /// <summary>When set, the next N transfer calls fail (transient unless <see cref="FailPermanently"/>).</summary>
    public int FailNextTransfers;
    public bool FailPermanently;
    public Func<TransferRequest, Task>? BeforeTransfer;

    public Task<string> CreateExpressAccountAsync(Guid sellerId, string? email, string country, CancellationToken ct = default)
    {
        string id; lock (_l) id = $"acct_fake_{_run}_{++_acct:D4}";
        Accounts[id] = new AccountSnapshot(id, false, false, false, ["individual.first_name", "external_account"], [], null, null, null, null, null, null);
        return Task.FromResult(id);
    }

    public Task<LinkResult> CreateAccountLinkAsync(string accountId, string returnUrl, string refreshUrl, CancellationToken ct = default) =>
        Accounts.ContainsKey(accountId)
            ? Task.FromResult(new LinkResult($"https://connect.fake-stripe.test/setup/{accountId}", DateTime.UtcNow.AddMinutes(5)))
            : throw new ProviderException("No such account", false);

    public Task<LinkResult> CreateLoginLinkAsync(string accountId, CancellationToken ct = default) =>
        Accounts.TryGetValue(accountId, out var a) && a.DetailsSubmitted
            ? Task.FromResult(new LinkResult($"https://connect.fake-stripe.test/express/{accountId}", DateTime.UtcNow.AddMinutes(5)))
            : throw new ProviderException("The account has not finished onboarding.", false);

    public Task<AccountSnapshot> RetrieveAccountAsync(string accountId, CancellationToken ct = default) =>
        Accounts.TryGetValue(accountId, out var a) ? Task.FromResult(a) : throw new ProviderException("No such account", false);

    /// <summary>Test helper: completes onboarding the way Stripe would after the seller finished the hosted flow.</summary>
    public AccountSnapshot Complete(string accountId, string last4 = "4242")
    {
        var s = new AccountSnapshot(accountId, true, true, true, [], [], null, null, "daily", 2, last4, "STRIPE TEST BANK");
        Accounts[accountId] = s; return s;
    }

    public async Task<TransferResult> CreateTransferAsync(TransferRequest r, CancellationToken ct = default)
    {
        Interlocked.Increment(ref TransferCalls);
        if (BeforeTransfer is not null) await BeforeTransfer(r);
        if (_byKey.TryGetValue(r.IdempotencyKey, out var existing)) return existing;
        bool fail; lock (_l) { fail = FailNextTransfers > 0; if (fail) FailNextTransfers--; }
        if (fail) throw new ProviderException("Stripe is unavailable (fake).", !FailPermanently);
        if (!Accounts.TryGetValue(r.DestinationAccountId, out var acc) || !acc.PayoutsEnabled) throw new ProviderException("Destination account cannot receive transfers.", false);
        lock (_l)
        {
            if (_byKey.TryGetValue(r.IdempotencyKey, out existing)) return existing;
            var t = new TransferResult($"tr_fake_{_run}_{++_tr:D5}", "paid", r.AmountCents, r.Currency, r.DestinationAccountId);
            Transfers[t.Id] = t; _byKey[r.IdempotencyKey] = t; return t;
        }
    }

    public Task<TransferResult?> RetrieveTransferAsync(string transferId, CancellationToken ct = default) =>
        Task.FromResult(Transfers.TryGetValue(transferId, out var t) ? t : null);
}

/// <summary>Used in Production when no Stripe key is configured: every call fails with 503 instead of silently faking money movement.</summary>
public class UnconfiguredPaymentProvider : IPaymentProvider
{
    public bool IsFake => false;
    public Task<string> CreateExpressAccountAsync(Guid sellerId, string? email, string country, CancellationToken ct = default) => throw new UnconfiguredException();
    public Task<LinkResult> CreateAccountLinkAsync(string accountId, string returnUrl, string refreshUrl, CancellationToken ct = default) => throw new UnconfiguredException();
    public Task<LinkResult> CreateLoginLinkAsync(string accountId, CancellationToken ct = default) => throw new UnconfiguredException();
    public Task<AccountSnapshot> RetrieveAccountAsync(string accountId, CancellationToken ct = default) => throw new UnconfiguredException();
    public Task<TransferResult> CreateTransferAsync(TransferRequest r, CancellationToken ct = default) => throw new UnconfiguredException();
    public Task<TransferResult?> RetrieveTransferAsync(string transferId, CancellationToken ct = default) => throw new UnconfiguredException();
}

/// <summary>Stripe.net implementation (Express accounts, account links, login links, transfers).</summary>
public class StripePaymentProvider(IOptions<StripeOptions> opt) : IPaymentProvider
{
    public bool IsFake => false;
    private StripeClient Client => new(opt.Value.SecretKey);

    private static ProviderException Wrap(StripeException ex) =>
        new(ex.StripeError?.Message ?? "Stripe request failed", (int?)ex.HttpStatusCode is null or >= 500 or 429);

    public async Task<string> CreateExpressAccountAsync(Guid sellerId, string? email, string country, CancellationToken ct = default)
    {
        try
        {
            var acc = await Client.V1.Accounts.CreateAsync(new AccountCreateOptions
            {
                Type = "express", Country = country, Email = email,
                Capabilities = new AccountCapabilitiesOptions { Transfers = new AccountCapabilitiesTransfersOptions { Requested = true } },
                Metadata = new Dictionary<string, string> { ["sellerId"] = sellerId.ToString() }
            }, new RequestOptions { IdempotencyKey = "acct-" + sellerId }, ct);
            return acc.Id;
        }
        catch (StripeException ex) { throw Wrap(ex); }
    }

    public async Task<LinkResult> CreateAccountLinkAsync(string accountId, string returnUrl, string refreshUrl, CancellationToken ct = default)
    {
        try
        {
            var l = await Client.V1.AccountLinks.CreateAsync(new AccountLinkCreateOptions { Account = accountId, ReturnUrl = returnUrl, RefreshUrl = refreshUrl, Type = "account_onboarding" }, null, ct);
            return new LinkResult(l.Url, l.ExpiresAt);
        }
        catch (StripeException ex) { throw Wrap(ex); }
    }

    public async Task<LinkResult> CreateLoginLinkAsync(string accountId, CancellationToken ct = default)
    {
        try
        {
            var l = await Client.V1.Accounts.LoginLinks.CreateAsync(accountId, null, null, ct);
            return new LinkResult(l.Url, DateTime.UtcNow.AddMinutes(5));
        }
        catch (StripeException ex) { throw Wrap(ex); }
    }

    public async Task<AccountSnapshot> RetrieveAccountAsync(string accountId, CancellationToken ct = default)
    {
        try { return Map(await Client.V1.Accounts.GetAsync(accountId, null, null, ct)); }
        catch (StripeException ex) { throw Wrap(ex); }
    }

    public static AccountSnapshot Map(Account a)
    {
        var bank = a.ExternalAccounts?.Data?.OfType<BankAccount>().FirstOrDefault();
        var sch = a.Settings?.Payouts?.Schedule;
        return new AccountSnapshot(a.Id, a.DetailsSubmitted, a.ChargesEnabled, a.PayoutsEnabled, a.Requirements?.CurrentlyDue?.ToArray() ?? [], a.Requirements?.PastDue?.ToArray() ?? [],
            a.Requirements?.DisabledReason, a.Requirements?.CurrentDeadline, sch?.Interval, (int?)sch?.DelayDays, bank?.Last4, bank?.BankName);
    }

    public async Task<TransferResult> CreateTransferAsync(TransferRequest r, CancellationToken ct = default)
    {
        try
        {
            var t = await Client.V1.Transfers.CreateAsync(new TransferCreateOptions
            {
                Amount = r.AmountCents, Currency = r.Currency.ToLowerInvariant(), Destination = r.DestinationAccountId, TransferGroup = r.TransferGroup,
                Metadata = new Dictionary<string, string> { ["transferId"] = r.TransferId.ToString() }
            }, new RequestOptions { IdempotencyKey = r.IdempotencyKey }, ct);
            return new TransferResult(t.Id, t.Reversed ? "reversed" : "paid", t.Amount, t.Currency.ToUpperInvariant(), r.DestinationAccountId, t.AmountReversed);
        }
        catch (StripeException ex) { throw Wrap(ex); }
    }

    public async Task<TransferResult?> RetrieveTransferAsync(string transferId, CancellationToken ct = default)
    {
        try
        {
            var t = await Client.V1.Transfers.GetAsync(transferId, null, null, ct);
            return new TransferResult(t.Id, t.Reversed ? "reversed" : "paid", t.Amount, t.Currency.ToUpperInvariant(), t.DestinationId, t.AmountReversed);
        }
        catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
        catch (StripeException ex) { throw Wrap(ex); }
    }
}
