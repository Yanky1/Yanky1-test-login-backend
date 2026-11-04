using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Bookings;
using Solvelt.Api.Domain;
using Solvelt.Api.Products;
using Solvelt.Api.Services;

namespace Solvelt.Api.Catalog;

/// <summary>
/// Builds the PUBLIC listing document of the catalog feed. Everything here is an allow-list: a field is exposed only if it is written below. Never exposed: costs, internal notes,
/// quality scores, reserved/exact stock, seller contact data and address, brand contact e-mail, buyer names, moderation reasons, draft data.
/// </summary>
public class CatalogReader(AppDbContext db, TimeProvider time)
{
    public const string Currency = "USD";
    private static readonly JsonSerializerOptions J = ProductJson.Opts;

    private static object DeliveryOf(DeliveryInfo d) => new { ships = d.Ships, localDelivery = d.LocalDelivery, serviceLocations = d.ServiceLocations };

    public object Tombstone(CatalogTombstone t) => new { listingId = t.ListingId, version = t.Version, listed = false, reason = t.Reason };

    public object Tombstone(Product p) => new { listingId = p.Id, version = p.CatalogVersion, listed = false, reason = p.CatalogUnlistReason };

    /// <summary>Documents for visible listings (in the order of <paramref name="products"/>). Callers pass only products with <c>CatalogListed</c>.</summary>
    public async Task<List<(Guid Id, object Doc)>> ListingsAsync(IReadOnlyList<Product> products, CancellationToken ct)
    {
        if (products.Count == 0) return [];
        var ids = products.Select(p => p.Id).ToList();
        var now = time.UtcNow();

        var variants = (await db.Variants.AsNoTracking().Where(v => ids.Contains(v.ProductId) && v.Enabled).OrderBy(v => v.SortOrder).ThenBy(v => v.Id).ToListAsync(ct)).ToLookup(v => v.ProductId);
        var options = (await db.ProductOptions.AsNoTracking().Where(o => ids.Contains(o.ProductId)).OrderBy(o => o.SortOrder).ToListAsync(ct)).ToLookup(o => o.ProductId);
        var images = (await db.ProductImages.AsNoTracking().Where(i => ids.Contains(i.ProductId)).OrderBy(i => i.SortOrder).ThenBy(i => i.CreatedAt).ToListAsync(ct)).ToLookup(i => i.ProductId);
        var attrs = (await db.ProductAttributeValues.AsNoTracking().Where(a => ids.Contains(a.ProductId)).OrderBy(a => a.Key).ToListAsync(ct)).ToLookup(a => a.ProductId);
        var faqs = (await db.ProductFaqs.AsNoTracking().Where(f => ids.Contains(f.ProductId)).OrderBy(f => f.SortOrder).ToListAsync(ct)).ToLookup(f => f.ProductId);
        var qa = (await db.Questions.AsNoTracking().Where(q => ids.Contains(q.ProductId) && q.State == QuestionState.Published && q.Answer != null).OrderBy(q => q.AskedAt).ThenBy(q => q.Id).ToListAsync(ct)).ToLookup(q => q.ProductId);
        var services = (await db.ServiceDetails.AsNoTracking().Where(s => ids.Contains(s.ProductId)).ToListAsync(ct)).ToDictionary(s => s.ProductId);
        var delivery = await DeliveryContext.LoadAsync(db, products.Select(p => p.SellerId).Distinct().ToList(), ct);

        var brandIds = products.Select(p => p.BrandId).Distinct().ToList();
        var brands = (await db.Brands.AsNoTracking().Where(b => brandIds.Contains(b.Id)).ToListAsync(ct)).ToDictionary(b => b.Id);
        var sellerIds = products.Select(p => p.SellerId).Distinct().ToList();
        var sellers = (await db.Sellers.AsNoTracking().Where(s => sellerIds.Contains(s.Id)).Select(s => new { s.Id, s.VendorName, s.Country }).ToListAsync(ct)).ToDictionary(s => s.Id);

        var cats = await db.Categories.AsNoTracking().ToListAsync(ct);
        var catById = cats.ToDictionary(c => c.Id);
        var defs = await db.CategoryAttributes.AsNoTracking().ToListAsync(ct);
        object? CategoryOf(Guid? id)
        {
            if (id is not { } cid || !catById.TryGetValue(cid, out var c)) return null;
            var path = new List<object>(); var cur = c;
            for (var guard = 0; guard < 8; guard++)
            {
                path.Insert(0, new { id = cur.Id, name = cur.Name, slug = cur.Slug });
                if (cur.ParentId is not { } pid || !catById.TryGetValue(pid, out cur!)) break;
            }
            return new { id = c.Id, name = c.Name, slug = c.Slug, path };
        }
        CategoryAttribute? Def(Guid? categoryId, string key)
        {
            for (var cur = categoryId; cur is { } id && catById.ContainsKey(id);)
            {
                if (defs.FirstOrDefault(d => d.CategoryId == id && d.Key == key) is { } d) return d;
                cur = catById[id].ParentId;
            }
            return null;
        }

        var res = new List<(Guid, object)>(products.Count);
        foreach (var p in products)
        {
            var vs = variants[p.Id].ToList();
            var imgs = images[p.Id].Where(i => i.VariantId is null || vs.Any(v => v.Id == i.VariantId)).ToList();   // images of disabled variants stay private
            var isService = p.Type == ListingType.Service;
            services.TryGetValue(p.Id, out var sd);
            var baseCents = isService ? sd?.PriceCents ?? p.PriceCents : p.PriceCents;
            var state = CatalogRules.ProductState(p.Type, p.HasVariants, p.Stock, p.Reserved, p.AllowBackorder, p.LowStockAlert, vs.Select(v => CatalogRules.StockState(v.Stock, v.Reserved, p.AllowBackorder, p.LowStockAlert)));
            brands.TryGetValue(p.BrandId, out var brand);
            sellers.TryGetValue(p.SellerId, out var seller);

            res.Add((p.Id, new
            {
                listingId = p.Id,
                version = p.CatalogVersion,
                listed = true,
                type = isService ? "service" : "product",
                title = p.Title,
                handle = p.Handle,
                condition = p.Condition.ToString().ToLowerInvariant(),
                conditionNote = p.ConditionNote,
                sku = p.Sku,
                barcode = p.Barcode,
                bullets = ProductJson.Bullets(p),
                searchTerms = SearchTerms.ForFeed(p.SearchTerms),
                description = p.Description,
                descriptionHtml = SafeMarkdown.Render(p.Description),
                price = new
                {
                    amountMinor = baseCents, currency = Currency, compareAtMinor = p.CompareAtCents,
                    sale = p.SaleCents is { } sale ? new { amountMinor = sale, startsAt = p.SaleStart, endsAt = p.SaleEnd, active = ProductRules.EffectiveSale(p, now) is not null } : null
                },
                availability = new { state, allowBackorder = p.AllowBackorder },
                weightGrams = p.WeightGrams,
                dimensionsMm = p.LengthMm is { } dl && p.WidthMm is { } dw && p.HeightMm is { } dh ? new { l = dl, w = dw, h = dh } : null,
                options = options[p.Id].Select(o => new { name = o.Name, values = ProductJson.Values(o.ValuesJson) }).ToList(),
                variants = vs.Select(v => new
                {
                    variantId = v.Id, sku = v.Sku, barcode = v.Barcode, values = ProductJson.Values(v.ValuesJson),
                    price = new { amountMinor = v.PriceCents ?? baseCents, currency = Currency },
                    weightGrams = p.WeightGrams,   // variants have no weight of their own: they ship at the product's weight
                    availability = new { state = CatalogRules.StockState(v.Stock, v.Reserved, p.AllowBackorder, p.LowStockAlert) },
                    imageIds = imgs.Where(i => i.VariantId == v.Id).Select(i => i.Id).ToList()
                }).ToList(),
                category = CategoryOf(p.CategoryId),
                brand = brand is null ? null : new { brandId = brand.Id, name = brand.Name, slug = brand.Slug, description = brand.Description, website = brand.Website },
                seller = new { sellerId = p.SellerId, displayName = seller?.VendorName, country = seller?.Country },
                attributes = attrs[p.Id].Select(a =>
                {
                    var d = Def(p.CategoryId, a.Key);
                    return new { key = a.Key, label = d?.Label ?? a.Key, unit = d?.Unit, value = JsonSerializer.Deserialize<JsonElement>(a.ValueJson, J) };
                }).ToList(),
                faqs = faqs[p.Id].Select(f => new { question = f.Question, answer = f.Answer }).ToList(),
                // published Q&A only; the asker's name is deliberately not included (no buyer data in the catalog)
                questions = qa[p.Id].Select(q => new { questionId = q.ExternalId ?? q.Id.ToString(), question = q.Text, answer = q.Answer, askedAt = q.AskedAt, answeredAt = q.AnsweredAt }).ToList(),
                images = imgs.Select(i => new { imageId = i.Id, variantId = i.VariantId, width = i.Width, height = i.Height, contentType = i.ContentType, sizeBytes = i.Size, alt = i.Alt, isMain = i.IsMain, sortOrder = i.SortOrder, path = $"/api/internal/v1/catalog/images/{i.Id}" }).ToList(),
                delivery = DeliveryOf(delivery.For(p, sd)),
                policies = new { returnsDays = p.ReturnsDays, returnsCondition = p.ReturnsCondition, warrantyMonths = p.WarrantyMonths, warrantyNote = p.WarrantyNote, handlingDays = p.HandlingDays },
                service = !isService || sd is null ? null : new
                {
                    pricingModel = sd.PricingModel.ToString().ToLowerInvariant(), priceMinor = sd.PriceCents, minMinutes = sd.MinMinutes, maxMinutes = sd.MaxMinutes, incrementMinutes = sd.IncrementMinutes,
                    defaultDurationMinutes = sd.DefaultDurationMinutes, online = sd.Online, atCustomer = sd.AtCustomer, atSeller = sd.AtSeller, bookingMode = sd.BookingMode.ToString().ToLowerInvariant(),
                    travelFeeMinor = sd.TravelFeeCents,
                    policy = PolicyView.Of(sd)
                },
                publishedAt = p.PublishedAt,
                updatedAt = p.UpdatedAt
            }));
        }
        return res;
    }
}
