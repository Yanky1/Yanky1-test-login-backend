using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;

namespace Solvelt.Api.Offers;

/// <summary>An offer the marketplace priced the order with (C18): <c>offers: [{offerId, offerVersion, giftUnavailable}]</c>.</summary>
public sealed record IngestOfferRef(Guid OfferId, int OfferVersion, bool GiftUnavailable = false);

/// <summary>One line as the intake check sees it.</summary>
public sealed record IntakeLine(int Index, IngestLine L, Product P);

/// <summary>The outcome of the check: what to store on each line, which groups lost their gift, or the mismatch.</summary>
public sealed class OfferPlan
{
    public Dictionary<int, (Guid OfferId, int Version)> Lines { get; } = [];
    public Dictionary<int, (Guid OfferId, int Version)> Gifts { get; } = [];
    public HashSet<(Guid SellerId, Guid BrandId)> GiftUnavailable { get; } = [];
    public OfferMismatch? Mismatch { get; set; }
}

/// <summary>The 422 <c>offer_mismatch</c> body parts: one entry per problem, and the figures the portal expected for every offer involved.</summary>
public sealed record OfferMismatch(List<object> Errors, List<object> Expected);

/// <summary>
/// C18 order intake: recomputes the tiers and gifts from the offer versions the order names and rejects anything that does not match (docs/CONTRACT.md, docs/ORDERS.md 3.2).
/// The order's placed-at time decides which versions apply; a version that stopped applying less than <see cref="OfferOptions.VersionGraceMinutes"/> before it still counts.
/// </summary>
public class OfferIntake(AppDbContext db, IOptions<OfferOptions> opt)
{
    private sealed class InEffect
    {
        public required Offer Offer { get; init; }
        public required OfferVersionView V { get; init; }
        public bool Declared { get; init; }
        public bool GiftUnavailable { get; init; }
    }

    public static bool IsGiftLine(IngestLine l) => l.IsGift || l.GiftOfOfferId is not null;

    public async Task<OfferPlan> CheckAsync(DateTime placedAt, List<IntakeLine> lines, List<IngestOfferRef> declared, CancellationToken ct = default)
    {
        var plan = new OfferPlan();
        var grace = TimeSpan.FromMinutes(Math.Max(0, opt.Value.VersionGraceMinutes));
        var t = placedAt; var tg = placedAt - grace;
        var errors = new List<object>();
        void Err(int index, string kind, Guid? offerId, string message, object? expected = null, object? actual = null) =>
            errors.Add(new { index, kind, offerId, message, expected, actual });

        var regular = lines.Where(x => !IsGiftLine(x.L)).ToList();
        var giftLines = lines.Where(x => IsGiftLine(x.L)).ToList();
        var groups = regular.Select(x => (x.P.SellerId, x.P.BrandId)).Distinct().ToList();
        var sellerIds = groups.Select(g => g.SellerId).Distinct().ToList();
        var declaredIds = declared.Select(d => d.OfferId).Distinct().ToList();
        if (groups.Count == 0 && declaredIds.Count == 0 && giftLines.Count == 0) return plan;

        // every offer that could matter: the declared ones (any seller: a foreign one is reported) and those of the sellers in the order
        var offers = await db.Offers.AsNoTracking().Where(o => declaredIds.Contains(o.Id) || sellerIds.Contains(o.SellerId)).ToListAsync(ct);
        offers = offers.Where(o => declaredIds.Contains(o.Id) || groups.Contains((o.SellerId, o.BrandId))).ToList();
        var oids = offers.Select(o => o.Id).ToList();
        var versions = oids.Count == 0 ? new List<OfferVersionView>() : (await db.OfferVersions.AsNoTracking().Where(v => oids.Contains(v.OfferId) && v.CreatedAt <= t).ToListAsync(ct))
            .Select(OfferVersionView.Of).ToList();
        bool Acceptable(OfferVersionView v) => v.ActiveInterval is { } iv && iv.From <= t && iv.To > tg;
        var tree = await CategoryTree.LoadAsync(db, ct);

        // 1. the declared offers and their versions
        var inEffect = new Dictionary<Guid, InEffect>();
        foreach (var d in declared)
        {
            var o = offers.FirstOrDefault(x => x.Id == d.OfferId);
            if (inEffect.ContainsKey(d.OfferId)) { Err(-1, "duplicate_offer", d.OfferId, "The offer is named twice in offers[]."); continue; }
            if (o is null) { Err(-1, "unknown_offer", d.OfferId, "Unknown offer."); continue; }
            var v = versions.FirstOrDefault(x => x.Row.OfferId == o.Id && x.Row.Version == d.OfferVersion);
            if (v is null || !Acceptable(v))
            {
                var ok = versions.Where(x => x.Row.OfferId == o.Id && Acceptable(x)).Select(x => x.Row.Version).OrderBy(x => x).ToList();
                Err(-1, "version_not_active", o.Id, $"Version {d.OfferVersion} of the offer was not active when the order was placed (grace {grace.TotalMinutes:0} minutes).",
                    new { offerVersions = ok }, new { offerVersion = d.OfferVersion });
                continue;
            }
            inEffect[o.Id] = new InEffect { Offer = o, V = v, Declared = true, GiftUnavailable = d.GiftUnavailable };
        }

        // 2. offers that applied during the whole grace window but were not named: their current version is expected (offer_missing on the lines it covers)
        foreach (var o in offers.Where(x => !inEffect.ContainsKey(x.Id) && !declaredIds.Contains(x.Id)))
        {
            var mine = versions.Where(x => x.Row.OfferId == o.Id && x.ActiveInterval is not null).OrderBy(x => x.ActiveInterval!.Value.From).ToList();
            var cur = tg;
            var moved = true;
            while (moved && cur <= t)
            {
                moved = false;
                foreach (var v in mine)
                {
                    var iv = v.ActiveInterval!.Value;
                    if (iv.From <= cur && iv.To > cur) { cur = iv.To; moved = true; }
                }
            }
            if (cur <= t) continue;   // not continuously active over [placedAt - grace, placedAt]: an order priced without it is fine
            var atT = mine.FirstOrDefault(x => x.ActiveInterval!.Value.From <= t && x.ActiveInterval!.Value.To > t);
            if (atT is not null) inEffect[o.Id] = new InEffect { Offer = o, V = atT, Declared = false };
        }

        // 3. which offer prices each line: the most specific eligible one (products, then category, then all); offers never stack
        var assigned = new Dictionary<int, InEffect>();
        foreach (var x in regular)
        {
            var best = inEffect.Values.Where(e => OfferService.Eligible(e.Offer, e.V, x.P, tree))
                .OrderBy(e => OfferPricing.Rank(e.V.Row.Scope)).ThenBy(e => e.Offer.CreatedAt).ThenBy(e => e.Offer.Id.ToString("D"), StringComparer.Ordinal).FirstOrDefault();   // C18.1-2 tie-break, reproducible from the feed
            if (best is not null) assigned[x.Index] = best;
            var said = x.L.OfferId;
            if (said == best?.Offer.Id) continue;
            if (said is { } s && !declaredIds.Contains(s)) Err(x.Index, "unknown_offer", s, "The line names an offer that is not in offers[] (name it there with its version).");
            else if (best is null) Err(x.Index, "not_eligible", said, "This line is not eligible for the offer it names (another seller or brand, outside the scope, or not a product).", new { offerId = (Guid?)null }, new { offerId = said });
            else if (said is null) Err(x.Index, "offer_missing", best.Offer.Id, "This line is eligible for an active offer that the order does not apply.", new { offerId = best.Offer.Id, offerVersion = best.V.Row.Version }, new { offerId = (Guid?)null });
            else Err(x.Index, "not_eligible", said, "A more specific offer applies to this line (offers do not stack).", new { offerId = best.Offer.Id, offerVersion = best.V.Row.Version }, new { offerId = said });
        }

        // 4. tiers: units are counted across all lines an offer prices, mixing products; the step reached applies to every one of them
        var units = inEffect.Keys.ToDictionary(k => k, k => regular.Where(x => assigned.TryGetValue(x.Index, out var e) && e.Offer.Id == k).Sum(x => x.L.Quantity));
        var expected = new List<object>();
        foreach (var e in inEffect.Values)
        {
            var mine = regular.Where(x => assigned.TryGetValue(x.Index, out var a) && a.Offer.Id == e.Offer.Id).ToList();
            var n = units[e.Offer.Id];
            var tier = OfferPricing.TierFor(e.V.Tiers, n);
            var earned = OfferPricing.GiftsFor(e.V.Gifts, n);
            foreach (var x in mine)
            {
                var want = OfferPricing.LineDiscount(x.L.UnitPriceCents, x.L.Quantity, tier);
                if (x.L.DiscountCents != want) Err(x.Index, "discount", e.Offer.Id, $"The discount of this line must be {want} cents ({n} eligible units).", new { discountCents = want }, new { discountCents = x.L.DiscountCents });
                else if (x.L.OfferId == e.Offer.Id) plan.Lines[x.Index] = (e.Offer.Id, e.V.Row.Version);
            }
            if (mine.Count > 0 || giftLines.Any(g => g.L.GiftOfOfferId == e.Offer.Id))
                expected.Add(new
                {
                    offerId = e.Offer.Id, offerVersion = e.V.Row.Version, eligibleUnits = n,
                    tier = tier is null ? null : new { minQuantity = tier.MinQuantity, unitPriceCents = tier.UnitPriceCents },
                    lines = mine.Select(x => new { index = x.Index, marketplaceLineId = x.L.MarketplaceLineId, discountCents = OfferPricing.LineDiscount(x.L.UnitPriceCents, x.L.Quantity, tier) }),
                    gifts = earned.Select(g => new { productId = g.ProductId, quantity = g.Quantity, minQuantity = g.MinQuantity })
                });
        }

        // 5. gifts: once per order, only when earned, at $0; a gift the buyer earned but that is missing needs giftUnavailable
        var sent = new Dictionary<(Guid Offer, Guid Product), (int Qty, int Index)>();
        foreach (var x in giftLines)
        {
            var of = x.L.GiftOfOfferId;
            if (of is null) { Err(x.Index, "gift_not_earned", null, "A gift line needs giftOfOfferId."); continue; }
            if (!inEffect.TryGetValue(of.Value, out var e))
            {
                if (!declaredIds.Contains(of.Value)) Err(x.Index, "unknown_offer", of, "The gift line names an offer that is not in offers[].");
                continue;   // a declared offer with a bad version was reported above
            }
            if (x.L.UnitPriceCents != 0 || x.L.DiscountCents != 0 || x.L.TaxCents != 0)
                Err(x.Index, "gift_price", of, "A gift line is free: unitPriceCents, discountCents and taxCents must be 0.", new { unitPriceCents = 0, discountCents = 0, taxCents = 0 },
                    new { unitPriceCents = x.L.UnitPriceCents, discountCents = x.L.DiscountCents, taxCents = x.L.TaxCents });
            if (x.L.VariantId is not null) { Err(x.Index, "gift_not_earned", of, "Gift products have no variations."); continue; }
            var key = (of.Value, x.P.Id);
            sent[key] = sent.TryGetValue(key, out var s) ? (s.Qty + x.L.Quantity, s.Index) : (x.L.Quantity, x.Index);
            plan.Gifts[x.Index] = (of.Value, e.V.Row.Version);
        }
        foreach (var e in inEffect.Values)
        {
            var earned = OfferPricing.GiftsFor(e.V.Gifts, units[e.Offer.Id]);
            foreach (var ((offerId, productId), (qty, index)) in sent.Where(kv => kv.Key.Offer == e.Offer.Id))
            {
                var g = earned.FirstOrDefault(x => x.ProductId == productId);
                if (g is null || qty > g.Quantity)
                    Err(index, "gift_not_earned", offerId, g is null ? $"This gift is not earned with {units[e.Offer.Id]} eligible units." : $"At most {g.Quantity} of this gift is earned.",
                        new { productId, quantity = g?.Quantity ?? 0 }, new { productId, quantity = qty });
                else if (qty < g.Quantity && !e.GiftUnavailable)
                    Err(index, "gift_missing", offerId, $"The buyer earned {g.Quantity} of this gift; send them all or set giftUnavailable.", new { productId, quantity = g.Quantity }, new { productId, quantity = qty });
            }
            if (e.GiftUnavailable) { if (earned.Count > 0) plan.GiftUnavailable.Add((e.Offer.SellerId, e.Offer.BrandId)); continue; }
            foreach (var g in earned.Where(z => !sent.ContainsKey((e.Offer.Id, z.ProductId))))
                Err(-1, "gift_missing", e.Offer.Id, $"The buyer earned a free gift ({g.Quantity} x product {g.ProductId}); add it as a $0 gift line or set giftUnavailable.",
                    new { productId = g.ProductId, quantity = g.Quantity }, new { productId = g.ProductId, quantity = 0 });
        }

        if (errors.Count > 0) plan.Mismatch = new OfferMismatch(errors, expected);
        return plan;
    }
}
