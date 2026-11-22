namespace Solvelt.Api.Domain;

/// <summary>
/// Ledger accounts. Every journal's entries sum to ZERO per currency (double entry). Seller-owned accounts hold what the platform owes the seller
/// (positive = owed); <see cref="Platform"/> and <see cref="Fees"/> are the counter accounts.
/// <see cref="PlatformSola"/> (C19) is the counter account of buyer money held at Sola (phone orders); <see cref="Platform"/> holds Stripe money (the balance transfers are paid from).
/// </summary>
public enum LedgerAccount { Platform, Fees, Pending, Available, Reserved, Settled, PaidOut, PlatformSola }

/// <summary>Payment providers that can hold the buyer's money (C19). Stored lower case; absent = stripe.</summary>
public static class PaymentProviders
{
    public const string Stripe = "stripe";
    public const string Sola = "sola";
    /// <summary>The counter account that holds the buyer money of this provider.</summary>
    public static LedgerAccount Counter(string provider) => provider == Sola ? LedgerAccount.PlatformSola : LedgerAccount.Platform;
    /// <summary>Journal key prefix: Sola journals whose key could collide with a Stripe one (refund ids, dispute ids) are prefixed with "sola:".</summary>
    public static string KeyPrefix(string provider) => provider == Sola ? "sola:" : "";
}

/// <summary>One balanced, append-only posting group. Never updated or deleted (corrections are new journals). <see cref="IdempotencyKey"/> is unique.</summary>
public class LedgerJournal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public string Currency { get; set; } = "USD";
    /// <summary>sale, policy_fee, extra_charge, refund, dispute, dispute_fee, dispute_won, release, reserve_release, adjustment, shipping_label (debit), shipping_label_refund (credit), transfer, transfer_failed, transfer_reversed, payout, payout_reversed</summary>
    public string Type { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public Guid? OrderId { get; set; }
    public Guid? BookingId { get; set; }
    /// <summary>Id of the source object (refund id, dispute id, Stripe transfer/payout id, ...).</summary>
    public string? SourceRef { get; set; }
    public string Memo { get; set; } = "";
    public Guid? ActorUserId { get; set; }
    public string? Reason { get; set; }
    /// <summary>C19: the provider that holds the buyer money of a sale / refund / dispute journal ("stripe" or "sola"); null on other journals (and on journals written before C19, which are Stripe).</summary>
    public string? Provider { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<LedgerEntry> Entries { get; set; } = [];
}

public class LedgerEntry
{
    public long Id { get; set; }
    public Guid JournalId { get; set; }
    public Guid SellerId { get; set; }
    public string Currency { get; set; } = "USD";
    public LedgerAccount Account { get; set; }
    /// <summary>Signed minor units. No floating point anywhere in the ledger.</summary>
    public long AmountCents { get; set; }
    /// <summary>items, shipping, fee, refund, fee_refund, ... (what this line is)</summary>
    public string Kind { get; set; } = "";
    public Guid? OrderId { get; set; }
    public Guid? BookingId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>The Stripe Express connected account of a seller ACCOUNT (not of a user). Only Stripe ids and Stripe-provided last-4 are stored.</summary>
public class PayoutAccount
{
    public Guid SellerId { get; set; }
    public string StripeAccountId { get; set; } = "";
    public bool DetailsSubmitted { get; set; }
    public bool ChargesEnabled { get; set; }
    public bool PayoutsEnabled { get; set; }
    public string RequirementsDueJson { get; set; } = "[]";
    public string RequirementsPastDueJson { get; set; } = "[]";
    public string? DisabledReason { get; set; }
    public DateTime? RequirementsDeadline { get; set; }
    public string? PayoutInterval { get; set; }
    public int? PayoutDelayDays { get; set; }
    public string? BankLast4 { get; set; }
    public string? BankName { get; set; }
    /// <summary>Stripe event time of the newest event applied (an older event never overwrites newer state).</summary>
    public DateTime? LastEventAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Staff payout hold. Active while <see cref="ReleasedAt"/> is null. Transfers are paused; sales keep accruing.</summary>
public class PayoutHold
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public string Reason { get; set; } = "";
    public Guid PlacedBy { get; set; }
    public DateTime PlacedAt { get; set; }
    public Guid? ReleasedBy { get; set; }
    public DateTime? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}

public enum TransferStatus { Pending, Created, Failed, Reversed }

/// <summary>A transfer of released funds to the seller's connected account (Stripe separate charges and transfers).</summary>
public class PaymentTransfer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public string Currency { get; set; } = "USD";
    public long AmountCents { get; set; }
    public TransferStatus Status { get; set; } = TransferStatus.Pending;
    /// <summary>Unique: one batch per seller + currency + UTC day. Also the Stripe idempotency key.</summary>
    public string IdempotencyKey { get; set; } = "";
    public DateTime BatchDate { get; set; }
    public string? StripeTransferId { get; set; }
    public string DestinationAccountId { get; set; } = "";
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? LockedUntil { get; set; }
    public string? LastError { get; set; }
    /// <summary>Number of staff retries (part of the re-debit journal key).</summary>
    public int Retries { get; set; }
    public DateTime? EventAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Informational mirror of Stripe payouts of the connected account.</summary>
public class PaymentPayout
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public string StripePayoutId { get; set; } = "";
    public string Currency { get; set; } = "USD";
    public long AmountCents { get; set; }
    /// <summary>pending, in_transit, paid, failed, canceled</summary>
    public string Status { get; set; } = "pending";
    public DateTime? ArrivalDate { get; set; }
    public string? FailureCode { get; set; }
    public DateTime EventAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A chargeback. ExternalId is the Stripe dispute id (the same dispute reported by the marketplace AND by Stripe is one row).</summary>
public class PaymentDispute
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    /// <summary>C19: "stripe" (dp_ id) or "sola" (Sola chargeback reference). A dispute row is unique per (provider, id, seller order): ids never match across providers.</summary>
    public string Provider { get; set; } = PaymentProviders.Stripe;
    public string ExternalId { get; set; } = "";
    /// <summary>The SELLER order this share of the dispute was booked against (a Stripe dispute can span several seller orders: the key is (ExternalId, OrderId)).</summary>
    public Guid? OrderId { get; set; }
    public Guid? BookingId { get; set; }
    public string Currency { get; set; } = "USD";
    public long AmountCents { get; set; }
    public long FeeCents { get; set; }
    /// <summary>needs_response, under_review, won, lost, warning_closed</summary>
    public string Status { get; set; } = "needs_response";
    public string? Reason { get; set; }
    public DateTime? EvidenceDueBy { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateTime EventAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A Stripe dispute on a charge that covers several seller orders and is not (fully) allocated by marketplace dispute.* events yet. Moves no money; open while the allocated shares are below the Stripe amount.</summary>
public class UnallocatedDispute
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ExternalId { get; set; } = "";
    public string? MarketplaceOrderId { get; set; }
    public string? ChargeRef { get; set; }
    public string Currency { get; set; } = "USD";
    /// <summary>The amount Stripe reports (all sellers together).</summary>
    public long AmountCents { get; set; }
    /// <summary>Sum of the per-seller-order shares the marketplace has allocated so far.</summary>
    public long AllocatedCents { get; set; }
    public string Status { get; set; } = "needs_response";
    public string? Reason { get; set; }
    public DateTime? EvidenceDueBy { get; set; }
    public DateTime EventAt { get; set; }
    public Guid? FlagId { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

/// <summary>The payment reference (Stripe charge / payment intent id) the marketplace reports for an order or booking.</summary>
public class PaymentReference
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "order";
    public string? MarketplaceOrderId { get; set; }
    public string? MarketplaceBookingId { get; set; }
    /// <summary>C19: "stripe" (charge / payment intent id) or "sola" (xRefNum). Stripe webhooks only ever match Stripe references.</summary>
    public string Provider { get; set; } = PaymentProviders.Stripe;
    public string Ref { get; set; } = "";
    public string Currency { get; set; } = "USD";
    public DateTime CapturedAt { get; set; }
    public string EventId { get; set; } = "";
}

/// <summary>Idempotency table of inbound events (marketplace and Stripe). Primary key (Source, EventId).</summary>
public class ProcessedPaymentEvent
{
    public string Source { get; set; } = "";
    public string EventId { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTime ReceivedAt { get; set; }
    public string Outcome { get; set; } = "applied";
}

/// <summary>Raised by the reconciliation job (or by an unattributable dispute) for staff to look at.</summary>
public class ReconciliationFlag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? SellerId { get; set; }
    public Guid? TransferId { get; set; }
    public string Kind { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public Guid? ResolvedBy { get; set; }
}

/// <summary>De-duplicates notification emails (unique per seller + key).</summary>
public class PaymentNotice
{
    public Guid SellerId { get; set; }
    public string Key { get; set; } = "";
    public DateTime SentAt { get; set; }
}

/// <summary>Lease row for the scheduled payment jobs: claimed with an atomic UPDATE, so several instances never run the same job at once.</summary>
public class PaymentJobLease
{
    public string Name { get; set; } = "";
    public DateTime LockedUntil { get; set; }
    /// <summary>Token of the runner that holds the lease: only that runner may renew or release it.</summary>
    public string? Owner { get; set; }
}
