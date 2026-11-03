using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Bookings;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using Solvelt.Api.Products;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Assistant;

/// <summary>What the confirm card shows. Payload is the normalised, validated input (ids resolved) that is stored and re-validated at confirm time.</summary>
public sealed record Proposal(string TargetKind, Guid? TargetId, string TargetLabel, string Summary, List<DiffRow> Diff, string Risk, string Stamp, Dictionary<string, object?> Payload);
public sealed record Prepared(Proposal? Proposal, string? Error)
{
    public static Prepared Fail(string e) => new(null, e);
    public static Prepared Ok(Proposal p) => new(p, null);
}
public sealed record ExecResult(bool Ok, int Status, string Code, string Message, EntityRef? Ref = null)
{
    public static ExecResult Done(string msg, EntityRef? r = null) => new(true, 200, "ok", msg, r);
    public static ExecResult Fail(int status, string code, string msg) => new(false, status, code, msg);
}

/// <summary>
/// One kind of change the assistant can PREPARE. PrepareAsync is pure: it loads the target through the caller's scope, validates, and describes the change; it never writes.
/// The same method runs again at confirm time, so permission, scope and state are re-checked against the database as it is THEN. ExecuteAsync calls the existing service.
/// </summary>
public abstract class ActionDef
{
    public abstract string Type { get; }
    public virtual Permission Permission => Permission.BrandWorkWrite;
    public abstract string Description { get; }
    public abstract string SchemaJson { get; }
    public abstract string Label { get; }
    public abstract Task<Prepared> PrepareAsync(ToolContext c, JsonElement input);
    public abstract Task<ExecResult> ExecuteAsync(ToolContext c, JsonElement payload);

    protected static string Money(long? cents) => cents is null ? "none" : "$" + ReadTools.Usd(cents);
    protected static string J(object o) => JsonSerializer.Serialize(o);
}

public sealed class ActionRegistry
{
    private readonly List<ActionDef> _defs;
    public ActionRegistry(IEnumerable<ActionDef> defs) => _defs = defs.ToList();
    public ActionDef? Find(string type) => _defs.FirstOrDefault(d => d.Type == type);
    public IReadOnlyList<ActionDef> All => _defs;
}

/// <summary>Turns an <see cref="ActionDef"/> into the model-facing "propose_*" tool. Running it stores a pending action; nothing else.</summary>
public sealed class ActionTool(ActionDef def) : IAssistantTool
{
    public string Name => def.Type; public string Description => def.Description + " This only PREPARES the change: the user must confirm it on a card before anything happens.";
    public string SchemaJson => def.SchemaJson; public string Label => def.Label; public Permission Permission => def.Permission; public bool IsAction => true;
    public async Task<ToolOutput> RunAsync(ToolContext ctx, JsonElement input)
    {
        var p = await def.PrepareAsync(ctx, input);
        if (p.Proposal is null) return ToolOutput.Error(p.Error ?? "That can't be prepared.");
        var action = await ctx.Get<PendingActionService>().ProposeAsync(ctx, def, p.Proposal); // the call's own scope (and DbContext)
        ctx.Proposals.Add(action);
        return ToolOutput.Ok($"I prepared this for you to confirm: {action.Summary} Nothing has changed yet. Use the Confirm button on the card below.",
            new { proposed = true, actionId = action.Id, summary = action.Summary, diff = p.Proposal.Diff, expiresAt = action.ExpiresAt, note = "NOT executed. The user must press Confirm." });
    }
}

// ================================================================ products
public sealed class CreateProductAction : ActionDef
{
    public override string Type => "propose_create_product";
    public override string Label => "Preparing a draft listing…";
    public override string Description => "Prepare a NEW product listing as a Draft (never published). Give title, optional categorySlug (see list_categories), up to 5 bullets, description, price in dollars and stock quantity.";
    public override string SchemaJson => """{"type":"object","properties":{"title":{"type":"string"},"categorySlug":{"type":"string"},"bullets":{"type":"array","items":{"type":"string"}},"description":{"type":"string"},"price":{"type":"number"},"quantity":{"type":"integer"},"brandId":{"type":"string"}},"required":["title"]}""";

    public override async Task<Prepared> PrepareAsync(ToolContext c, JsonElement i)
    {
        var title = In.Str(i, "title", 400);
        if (title is null || title.Length > 200) return Prepared.Fail("A title of 1-200 characters is required.");
        var price = In.Dec(i, "price");
        if (price is { } pr && (pr < 0.01m || pr > 999_999.99m || decimal.Round(pr, 2) != pr)) return Prepared.Fail("The price must be between 0.01 and 999,999.99 with at most 2 decimals.");
        var qty = In.Int(i, "quantity");
        if (qty is { } q && (q < 0 || q > InventoryService.MaxStock)) return Prepared.Fail("The quantity must be between 0 and 1,000,000.");
        var bullets = In.Strs(i, "bullets", 5, 500);
        var desc = In.Str(i, "description", 10_000);
        Guid? catId = null; string? catName = null;
        if (In.Str(i, "categorySlug", 80) is { } slug)
        {
            var cat = await c.Db.Categories.AsNoTracking().FirstOrDefaultAsync(x => x.Slug == slug.ToLowerInvariant());
            if (cat is null) return Prepared.Fail($"Unknown category '{slug}'. Use list_categories to find a valid slug.");
            catId = cat.Id; catName = cat.Name;
        }
        var brand = await ResolveBrandAsync(c, In.Id(i, "brandId"));
        if (brand.Error is not null) return Prepared.Fail(brand.Error);
        var diff = new List<DiffRow> { new("Listing", null, title), new("Status", null, "Draft (not visible to buyers)"), new("Brand", null, brand.Brand!.Name) };
        if (catName is not null) diff.Add(new("Category", null, catName));
        if (price is not null) diff.Add(new("Price", null, "$" + price.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));
        if (qty is not null) diff.Add(new("Stock", null, qty.Value.ToString()));
        if (bullets is { Count: > 0 }) diff.Add(new("Key features", null, string.Join(" • ", bullets)));
        if (desc is not null) diff.Add(new("Description", null, ReadTools.Clip(desc, 300)));
        var payload = new Dictionary<string, object?> { ["title"] = title, ["categorySlug"] = In.Str(i, "categorySlug", 80)?.ToLowerInvariant(), ["bullets"] = bullets, ["description"] = desc, ["price"] = price, ["quantity"] = qty, ["brandId"] = brand.Brand.Id };
        return Prepared.Ok(new Proposal("product", null, title, $"Create a draft listing \"{title}\" in {brand.Brand.Name}" + (price is null ? "" : $" at ${price:0.00}") + (qty is null ? "" : $" with {qty} in stock") + ".", diff, "low", "brand:" + brand.Brand.Id, payload));
    }

    private static async Task<(Brand? Brand, string? Error)> ResolveBrandAsync(ToolContext c, Guid? requested)
    {
        var a = c.Access;
        if (requested is { } id)
        {
            var b = await c.Db.RequireBrandAccess(a, id, write: true);
            return b is null || b.Status != BrandStatus.Active ? (null, "That brand isn't available to you.") : (b, null);
        }
        var all = (await c.Db.BrandsFor(a).Where(b => b.Status == BrandStatus.Active).OrderBy(b => b.Name).ToListAsync()).Where(b => a.CanWorkOnBrand(b.Id, true)).ToList();
        var pick = all.Count == 1 ? all[0] : all.FirstOrDefault(b => b.IsDefault);
        return pick is null ? (null, "Choose a brand for this product (use list_brands and pass brandId).") : (pick, null);
    }

    public override async Task<ExecResult> ExecuteAsync(ToolContext c, JsonElement p)
    {
        var a = c.Access;
        var brand = await ResolveBrandAsync(c, In.Id(p, "brandId"));
        if (brand.Brand is null) return ExecResult.Fail(404, "brand_unavailable", brand.Error!);
        var cats = c.Get<CategoryService>();
        Guid? catId = In.Str(p, "categorySlug", 80) is { } s ? (await c.Db.Categories.FirstOrDefaultAsync(x => x.Slug == s))?.Id : null;
        var price = In.Dec(p, "price"); var qty = In.Int(p, "quantity");
        var now = c.Now;
        var prod = new Product
        {
            SellerId = a.SellerId, BrandId = brand.Brand.Id, CategoryId = catId, Title = In.Str(p, "title", 200)!, CreatedAt = now, UpdatedAt = now, CreatedBy = a.UserId,
            PriceCents = Money_(price), BulletsJson = JsonSerializer.Serialize(In.Strs(p, "bullets", 5, 500) ?? []), Description = In.Str(p, "description", 10_000)
        };
        await ProductRules.RefreshAsync(c.Db, cats, prod);
        c.Db.Products.Add(prod);
        await c.Db.SaveChangesAsync();
        if (qty is > 0) await c.Get<IInventoryService>().SetOnHandAsync(a.SellerId, prod.Id, null, qty.Value);
        c.Db.ChangeTracker.Clear();
        var fresh = await c.Db.LoadAsync(a, prod.Id, write: false);
        if (fresh is not null) { await ProductRules.RefreshAsync(c.Db, cats, fresh); await c.Db.SaveChangesAsync(); }
        return ExecResult.Done($"Draft listing \"{prod.Title}\" created. It stays a Draft until you activate it.", new EntityRef("product", prod.Id, prod.Title));
    }
    private static long? Money_(decimal? d) => Products.Money.ToCents(d);
}

public sealed class UpdateListingAction : ActionDef
{
    public override string Type => "propose_update_listing";
    public override string Label => "Preparing listing changes…";
    public override string Description => "Prepare edits to an existing listing's title, key features (max 5 bullets) and/or description. Use set_price_stock for price and stock.";
    public override string SchemaJson => """{"type":"object","properties":{"productId":{"type":"string"},"title":{"type":"string"},"bullets":{"type":"array","items":{"type":"string"}},"description":{"type":"string"}},"required":["productId"]}""";

    public override async Task<Prepared> PrepareAsync(ToolContext c, JsonElement i)
    {
        if (In.Id(i, "productId") is not { } id) return Prepared.Fail("productId must be a listing id.");
        var p = await c.Db.LoadAsync(c.Access, id, write: true);
        if (p is null) return Prepared.Fail("Listing not found, or you can't edit it.");
        if (p.Status == ProductStatus.Archived) return Prepared.Fail("Archived listings can't be edited.");
        var title = In.Str(i, "title", 400); var bullets = In.Strs(i, "bullets", 6, 600); var desc = In.Str(i, "description", 10_100);
        if (title is { Length: > 200 }) return Prepared.Fail("The title can be at most 200 characters.");
        if (bullets is { Count: > 5 }) return Prepared.Fail("At most 5 key features.");
        if (bullets?.Any(b => b.Length > 500) == true) return Prepared.Fail("Each key feature can have at most 500 characters.");
        if (desc is { Length: > 10_000 }) return Prepared.Fail("The description can be at most 10,000 characters.");
        var diff = new List<DiffRow>();
        if (title is not null && title != p.Title) diff.Add(new("Title", p.Title, title));
        if (bullets is not null && !bullets.SequenceEqual(ProductJson.Bullets(p))) diff.Add(new("Key features", string.Join(" • ", ProductJson.Bullets(p)), string.Join(" • ", bullets)));
        if (desc is not null && desc != p.Description) diff.Add(new("Description", ReadTools.Clip(p.Description, 300), ReadTools.Clip(desc, 300)));
        if (diff.Count == 0) return Prepared.Fail("Nothing would change.");
        var payload = new Dictionary<string, object?> { ["productId"] = id, ["title"] = title, ["bullets"] = bullets, ["description"] = desc };
        return Prepared.Ok(new Proposal("product", id, p.Title, $"Update {string.Join(", ", diff.Select(d => d.Field.ToLowerInvariant()))} of \"{p.Title}\".", diff, "medium", $"v{p.Version}:{p.Status}", payload));
    }

    public override async Task<ExecResult> ExecuteAsync(ToolContext c, JsonElement pl)
    {
        var p = await c.Db.LoadAsync(c.Access, In.Id(pl, "productId")!.Value, write: true);
        if (p is null) return ExecResult.Fail(404, "not_found", "Listing not found.");
        if (In.Str(pl, "title", 200) is { } t) p.Title = t;
        if (In.Strs(pl, "bullets", 5, 500) is { } b) p.BulletsJson = JsonSerializer.Serialize(b);
        if (In.Str(pl, "description", 10_000) is { } d) p.Description = d;
        await ProductRules.RefreshAsync(c.Db, c.Get<CategoryService>(), p);
        p.Version++; p.UpdatedAt = c.Now;
        if (!await ProductEndpoints.TrySaveAsync(c.Db)) return ExecResult.Fail(409, "version_conflict", "The listing was changed somewhere else.");
        return ExecResult.Done($"Updated \"{p.Title}\".", new EntityRef("product", p.Id, p.Title));
    }
}

public sealed class SetPriceStockAction : ActionDef
{
    public override string Type => "propose_set_price_stock";
    public override string Label => "Preparing price/stock change…";
    public override string Description => "Prepare a new price (dollars) and/or stock quantity for a listing without variations.";
    public override string SchemaJson => """{"type":"object","properties":{"productId":{"type":"string"},"price":{"type":"number"},"quantity":{"type":"integer"}},"required":["productId"]}""";

    public override async Task<Prepared> PrepareAsync(ToolContext c, JsonElement i)
    {
        if (In.Id(i, "productId") is not { } id) return Prepared.Fail("productId must be a listing id.");
        var p = await c.Db.LoadAsync(c.Access, id, write: true);
        if (p is null) return Prepared.Fail("Listing not found, or you can't edit it.");
        var price = In.Dec(i, "price"); var qty = In.Int(i, "quantity");
        if (price is null && qty is null) return Prepared.Fail("Give a price and/or a quantity.");
        var diff = new List<DiffRow>();
        if (price is { } pr)
        {
            if (pr < 0.01m || pr > 999_999.99m || decimal.Round(pr, 2) != pr) return Prepared.Fail("Enter a price between 0.01 and 999,999.99.");
            var cents = Products.Money.ToCents(pr)!.Value;
            if (p.SaleCents is { } sc && sc >= cents) return Prepared.Fail("The price must stay above the sale price. Edit the listing to change both.");
            if (p.CompareAtCents is { } cp && cp <= cents) return Prepared.Fail("The price must stay below the compare-at price. Edit the listing to change both.");
            diff.Add(new("Price", Money(p.PriceCents), "$" + pr.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));
        }
        if (qty is { } q)
        {
            if (q is < 0 or > InventoryService.MaxStock) return Prepared.Fail("Enter a quantity between 0 and 1,000,000.");
            if (p.HasVariants) return Prepared.Fail("This listing has variations: change the quantity of each variation in the editor.");
            if (q < p.Reserved) return Prepared.Fail($"{p.Reserved} unit(s) are reserved by open orders, so the quantity can't go below {p.Reserved}.");
            diff.Add(new("Stock", p.Stock.ToString(), q.ToString()));
        }
        var payload = new Dictionary<string, object?> { ["productId"] = id, ["price"] = price, ["quantity"] = qty };
        return Prepared.Ok(new Proposal("product", id, p.Title, $"Change {string.Join(" and ", diff.Select(d => d.Field.ToLowerInvariant()))} of \"{p.Title}\".", diff, price is not null && p.Status == ProductStatus.Active ? "high" : "medium", $"v{p.Version}:{p.Stock}:{p.PriceCents}", payload));
    }

    public override async Task<ExecResult> ExecuteAsync(ToolContext c, JsonElement pl)
    {
        var a = c.Access;
        var prod = await c.Db.LoadAsync(a, In.Id(pl, "productId")!.Value, write: true);
        if (prod is null) return ExecResult.Fail(404, "not_found", "Listing not found.");
        var price = In.Dec(pl, "price"); var qty = In.Int(pl, "quantity");
        if (price is { } pr) prod.PriceCents = Products.Money.ToCents(pr);
        await using var tx = await c.Db.Database.BeginTransactionAsync();
        await ProductRules.RefreshAsync(c.Db, c.Get<CategoryService>(), prod);
        prod.Version++; prod.UpdatedAt = c.Now;
        if (!await ProductEndpoints.TrySaveAsync(c.Db)) return ExecResult.Fail(409, "version_conflict", "The listing was changed somewhere else.");
        if (qty is { } q && await c.Get<IInventoryService>().SetOnHandAsync(a.SellerId, prod.Id, null, q) is { Ok: false })
        { await tx.RollbackAsync(); c.Db.ChangeTracker.Clear(); return ExecResult.Fail(409, "below_reserved", "The quantity can't go below what open orders reserved."); }
        await tx.CommitAsync();
        return ExecResult.Done($"Updated price/stock of \"{prod.Title}\".", new EntityRef("product", prod.Id, prod.Title));
    }
}

public sealed class SetListingStatusAction : ActionDef
{
    public override string Type => "propose_set_listing_status";
    public override string Label => "Preparing status change…";
    public override string Description => "Prepare activating (publishing) or deactivating (hiding) a listing.";
    public override string SchemaJson => """{"type":"object","properties":{"productId":{"type":"string"},"status":{"type":"string","enum":["active","inactive"]}},"required":["productId","status"]}""";

    public override async Task<Prepared> PrepareAsync(ToolContext c, JsonElement i)
    {
        if (In.Id(i, "productId") is not { } id) return Prepared.Fail("productId must be a listing id.");
        var p = await c.Db.LoadAsync(c.Access, id, write: true);
        if (p is null) return Prepared.Fail("Listing not found, or you can't edit it.");
        var to = In.Str(i, "status", 12)?.ToLowerInvariant();
        if (to is not ("active" or "inactive")) return Prepared.Fail("status must be active or inactive.");
        var target = to == "active" ? ProductStatus.Active : ProductStatus.Inactive;
        if (p.Status == target) return Prepared.Fail($"The listing is already {p.Status}.");
        if (p.Status == ProductStatus.Archived) return Prepared.Fail("Restore the archived listing first.");
        if (target == ProductStatus.Active)
        {
            if (c.Access.AccountStatus != SellerStatus.Approved) return Prepared.Fail("Your seller account must be approved before listings can be activated.");
            var r = ProductRules.Evaluate(p, await c.Get<CategoryService>().AttributesAsync(p.CategoryId));
            if (!r.CanPublish) return Prepared.Fail("The listing isn't ready to publish: " + string.Join(" ", r.Issues.Where(x => x.Severity == "blocker").Take(4).Select(x => x.Message)));
        }
        var payload = new Dictionary<string, object?> { ["productId"] = id, ["status"] = to };
        return Prepared.Ok(new Proposal("product", id, p.Title, $"{(target == ProductStatus.Active ? "Activate" : "Deactivate")} \"{p.Title}\" ({(target == ProductStatus.Active ? "buyers will be able to buy it" : "buyers will no longer see it")}).",
            [new("Status", p.Status.ToString(), target.ToString())], "medium", $"v{p.Version}:{p.Status}", payload));
    }

    public override async Task<ExecResult> ExecuteAsync(ToolContext c, JsonElement pl)
    {
        var p = await c.Db.LoadAsync(c.Access, In.Id(pl, "productId")!.Value, write: true);
        if (p is null) return ExecResult.Fail(404, "not_found", "Listing not found.");
        var err = await ProductEndpoints.ChangeStatusAsync(c.Db, c.Get<CategoryService>(), c.Access, p, In.Str(pl, "status", 12), c.Get<IAuditLog>(), c.Time);
        if (err is IStatusCodeHttpResult { StatusCode: { } sc }) return ExecResult.Fail(sc, "refused", sc == 403 ? "Your seller account must be approved first." : "The listing isn't ready or the change isn't allowed.");
        return ExecResult.Done($"\"{p.Title}\" is now {p.Status}.", new EntityRef("product", p.Id, p.Title));
    }
}

// ================================================================ orders
public sealed class AcceptOrderAction : ActionDef
{
    public override string Type => "propose_accept_order";
    public override string Label => "Preparing to accept the order…";
    public override string Description => "Prepare accepting one new order (by orderId or number).";
    public override string SchemaJson => """{"type":"object","properties":{"orderId":{"type":"string"},"number":{"type":"string"}}}""";

    public override async Task<Prepared> PrepareAsync(ToolContext c, JsonElement i)
    {
        var o = await OrderTools.LoadWritable(c, i);
        if (o is null) return Prepared.Fail("Order not found, or you can't act on it.");
        if (!OrderStateMachine.Can(o.Status, OrderStatus.Accepted)) return Prepared.Fail($"Order {o.Number} is {OrderStateMachine.Label(o.Status)} and can't be accepted.");
        var payload = new Dictionary<string, object?> { ["orderId"] = o.Id };
        return Prepared.Ok(new Proposal("order", o.Id, "Order " + o.Number, $"Accept order {o.Number} ({ReadTools.ShortName(o.BuyerName)}, ${ReadTools.Usd(o.TotalCents)}).",
            [new("Status", o.Status.ToString(), "Accepted"), new("Ship by", null, o.ShipByAt.ToString("yyyy-MM-dd"))], "low", $"v{o.Version}:{o.Status}", payload));
    }
    public override async Task<ExecResult> ExecuteAsync(ToolContext c, JsonElement pl)
    {
        var o = await OrderTools.LoadWritable(c, pl, tracked: true);
        if (o is null) return ExecResult.Fail(404, "not_found", "Order not found.");
        var r = await c.Get<OrderActions>().AcceptAsync(o, c.Access.UserId, o.Version);
        return r.Ok ? ExecResult.Done($"Order {o.Number} accepted.", new EntityRef("order", o.Id, "Order " + o.Number)) : ExecResult.Fail(r.Status, r.Code, r.Message);
    }
}

public sealed class MarkShippedAction : ActionDef
{
    public override string Type => "propose_mark_shipped";
    public override string Label => "Preparing the shipment…";
    public override string Description => "Prepare marking a whole order as shipped with a carrier and tracking number (the buyer is notified).";
    public override string SchemaJson => """{"type":"object","properties":{"orderId":{"type":"string"},"number":{"type":"string"},"carrier":{"type":"string"},"trackingNumber":{"type":"string"}},"required":["carrier","trackingNumber"]}""";

    public override async Task<Prepared> PrepareAsync(ToolContext c, JsonElement i)
    {
        var o = await OrderTools.LoadWritable(c, i);
        if (o is null) return Prepared.Fail("Order not found, or you can't act on it.");
        if (o.Status == OrderStatus.New) return Prepared.Fail("Accept this order before shipping it.");
        if (!OrderStateMachine.CanShip(o.Status)) return Prepared.Fail($"Order {o.Number} is {OrderStateMachine.Label(o.Status)} and can't be shipped.");
        var carrier = Tracking.Carrier(In.Str(i, "carrier", 40));
        if (carrier is null) return Prepared.Fail("Choose a carrier: " + string.Join(", ", Tracking.Carriers) + ".");
        var tn = Tracking.Normalize(In.Str(i, "trackingNumber", 60));
        if (!Tracking.Valid(carrier, tn)) return Prepared.Fail($"That doesn't look like a {carrier} tracking number.");
        var payload = new Dictionary<string, object?> { ["orderId"] = o.Id, ["carrier"] = carrier, ["trackingNumber"] = tn };
        return Prepared.Ok(new Proposal("order", o.Id, "Order " + o.Number, $"Mark order {o.Number} as shipped with {carrier} tracking {tn}.",
            [new("Status", o.Status.ToString(), "Shipped"), new("Carrier", null, carrier), new("Tracking number", null, tn)], "medium", $"v{o.Version}:{o.Status}", payload));
    }
    public override async Task<ExecResult> ExecuteAsync(ToolContext c, JsonElement pl)
    {
        var o = await OrderTools.LoadWritable(c, pl, tracked: true);
        if (o is null) return ExecResult.Fail(404, "not_found", "Order not found.");
        var r = await c.Get<OrderActions>().ShipAsync(o, c.Access.UserId, new ShipRequest(o.Version, null, In.Str(pl, "carrier", 40), null, In.Str(pl, "trackingNumber", 60), null));
        return r.Ok ? ExecResult.Done($"Order {o.Number} marked as shipped.", new EntityRef("order", o.Id, "Order " + o.Number)) : ExecResult.Fail(r.Status, r.Code, r.Message);
    }
}

public static class OrderTools
{
    public static async Task<SellerOrder?> LoadWritable(ToolContext c, JsonElement i, bool tracked = false)
    {
        var q = c.Get<OrderActions>().Full(c.Db.OrdersFor(c.Access));
        if (!tracked) q = q.AsNoTracking();
        SellerOrder? o = null;
        if (In.Id(i, "orderId") is { } id) o = await q.FirstOrDefaultAsync(x => x.Id == id);
        else if (In.Str(i, "number", 40) is { } n) o = await q.FirstOrDefaultAsync(x => x.Number == n);
        return o is not null && c.Access.CanWorkOnBrand(o.BrandId, true) ? o : null;
    }
}

// ================================================================ buyer messages
public sealed class BuyerReplyAction : ActionDef
{
    public override string Type => "propose_buyer_reply";
    public override string Label => "Drafting the reply…";
    public override string Description => "Prepare SENDING a reply to the buyer in an EXISTING conversation. The exact text is shown to the user for confirmation. To merely draft, write the draft in chat instead. Never include contact details or links.";
    public override string SchemaJson => """{"type":"object","properties":{"conversationId":{"type":"string"},"text":{"type":"string"}},"required":["conversationId","text"]}""";

    public override async Task<Prepared> PrepareAsync(ToolContext c, JsonElement i)
    {
        if (In.Id(i, "conversationId") is not { } id) return Prepared.Fail("conversationId must be a conversation id.");
        var conv = await c.Db.ConversationsFor(c.Access).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (conv is null || !c.Access.CanWorkOnBrand(conv.BrandId, true) || conv.PiiRedactedAt != null) return Prepared.Fail("Conversation not found, or you can't reply to it.");
        var text = In.Str(i, "text", 4000);
        if (text is null) return Prepared.Fail("The reply text is required (max 4000 characters).");
        if (ContactDetector.Detect(text).Count > 0) return Prepared.Fail("The reply looks like it shares contact details, links or payment instructions, which marketplace rules forbid. Rewrite it without them.");
        var last = await c.Db.Messages.AsNoTracking().Where(m => m.ConversationId == id && m.Direction == MessageDirection.Buyer).OrderByDescending(m => m.CreatedAt).Select(m => m.Text).FirstOrDefaultAsync();
        var payload = new Dictionary<string, object?> { ["conversationId"] = id, ["text"] = text };
        return Prepared.Ok(new Proposal("conversation", id, "Conversation with " + ReadTools.ShortName(conv.BuyerName), $"Send this reply to {ReadTools.ShortName(conv.BuyerName)} (they will see it).",
            [new("Buyer's last message", ReadTools.BuyerText(last, 300), null), new("Your reply", null, text)], "medium", $"m{conv.MessageCount}:{conv.BuyerMessageCount}", payload));
    }
    public override async Task<ExecResult> ExecuteAsync(ToolContext c, JsonElement pl)
    {
        var conv = await c.Db.ConversationsFor(c.Access).FirstOrDefaultAsync(x => x.Id == In.Id(pl, "conversationId"));
        if (conv is null || !c.Access.CanWorkOnBrand(conv.BrandId, true)) return ExecResult.Fail(404, "not_found", "Conversation not found.");
        var r = await c.Get<MessageService>().ReplyAsync(conv, c.Access.UserId, new ReplyRequest(In.Str(pl, "text", 4000), null, false));
        return r.Status >= 400 ? ExecResult.Fail(r.Status, r.Code, r.Message) : ExecResult.Done("Reply sent.", new EntityRef("conversation", conv.Id, "Conversation with " + ReadTools.ShortName(conv.BuyerName)));
    }
}

// ================================================================ services: bookings, quotes, calendar
public sealed class DecideBookingAction : ActionDef
{
    public override string Type => "propose_decide_booking";
    public override string Label => "Preparing the booking decision…";
    public override string Description => "Prepare approving or declining a booking request that is waiting for the seller.";
    public override string SchemaJson => """{"type":"object","properties":{"bookingId":{"type":"string"},"decision":{"type":"string","enum":["approve","decline"]},"reason":{"type":"string"}},"required":["bookingId","decision"]}""";

    public override async Task<Prepared> PrepareAsync(ToolContext c, JsonElement i)
    {
        var b = await BookingTools.LoadActable(c, In.Id(i, "bookingId"));
        if (b is null) return Prepared.Fail("Booking not found, or you can't act on it.");
        var d = In.Str(i, "decision", 10)?.ToLowerInvariant();
        if (d is not ("approve" or "decline")) return Prepared.Fail("decision must be approve or decline.");
        var act = d == "approve" ? BookingStateMachine.Act.Approve : BookingStateMachine.Act.Decline;
        if (!BookingStateMachine.Can(b.Status, act)) return Prepared.Fail($"A {b.Status} booking can't be {d}d.");
        var reason = In.Str(i, "reason", 300);
        var payload = new Dictionary<string, object?> { ["bookingId"] = b.Id, ["decision"] = d, ["reason"] = reason };
        return Prepared.Ok(new Proposal("booking", b.Id, $"Booking {b.Number}", $"{(d == "approve" ? "Approve" : "Decline")} booking {b.Number} for {b.ServiceTitle} on {BJson.Utc(b.StartUtc):yyyy-MM-dd HH:mm} UTC.",
            [new("Status", b.Status.ToString(), d == "approve" ? "Confirmed" : "Declined"), new("When", null, BJson.Utc(b.StartUtc).ToString("yyyy-MM-dd HH:mm") + " UTC")],
            d == "approve" ? "low" : "medium", $"v{b.Version}:{b.Status}", payload));
    }
    public override async Task<ExecResult> ExecuteAsync(ToolContext c, JsonElement pl)
    {
        var b = await BookingTools.LoadActable(c, In.Id(pl, "bookingId"), tracked: true);
        if (b is null) return ExecResult.Fail(404, "not_found", "Booking not found.");
        var svc = c.Get<BookingService>(); var actor = Actor.Seller(c.Access.UserId);
        var r = In.Str(pl, "decision", 10) == "approve" ? await svc.ApproveAsync(b, actor, b.Version) : await svc.DeclineAsync(b, actor, In.Str(pl, "reason", 300), b.Version);
        return r.Ok ? ExecResult.Done($"Booking {b.Number} {(In.Str(pl, "decision", 10) == "approve" ? "approved" : "declined")}.", new EntityRef("booking", b.Id, $"Booking {b.Number}")) : ExecResult.Fail(r.Status, r.Code, r.Message);
    }
}

public sealed class RescheduleBookingAction : ActionDef
{
    public override string Type => "propose_reschedule";
    public override string Label => "Preparing the new time…";
    public override string Description => "Prepare proposing a different start time for a booking (UTC ISO time). For a pending request the buyer is asked to accept the new time; a confirmed booking is moved.";
    public override string SchemaJson => """{"type":"object","properties":{"bookingId":{"type":"string"},"newStart":{"type":"string","description":"UTC ISO 8601"}},"required":["bookingId","newStart"]}""";

    public override async Task<Prepared> PrepareAsync(ToolContext c, JsonElement i)
    {
        var b = await BookingTools.LoadActable(c, In.Id(i, "bookingId"));
        if (b is null) return Prepared.Fail("Booking not found, or you can't act on it.");
        if (In.Time(i, "newStart") is not { } start || start <= c.Now) return Prepared.Fail("newStart must be a future UTC time such as 2026-12-01T15:00:00Z.");
        var mode = BookingStateMachine.Can(b.Status, BookingStateMachine.Act.Propose) ? "propose" : BookingStateMachine.Can(b.Status, BookingStateMachine.Act.Reschedule) ? "reschedule" : null;
        if (mode is null) return Prepared.Fail($"A {b.Status} booking can't be rescheduled.");
        var payload = new Dictionary<string, object?> { ["bookingId"] = b.Id, ["newStart"] = start, ["mode"] = mode };
        return Prepared.Ok(new Proposal("booking", b.Id, $"Booking {b.Number}", $"{(mode == "propose" ? "Propose" : "Move to")} {start:yyyy-MM-dd HH:mm} UTC for booking {b.Number} ({b.ServiceTitle}).",
            [new("Start", BJson.Utc(b.StartUtc).ToString("yyyy-MM-dd HH:mm") + " UTC", start.ToString("yyyy-MM-dd HH:mm") + " UTC")], "medium", $"v{b.Version}:{b.Status}", payload));
    }
    public override async Task<ExecResult> ExecuteAsync(ToolContext c, JsonElement pl)
    {
        var b = await BookingTools.LoadActable(c, In.Id(pl, "bookingId"), tracked: true);
        if (b is null) return ExecResult.Fail(404, "not_found", "Booking not found.");
        var svc = c.Get<BookingService>(); var actor = Actor.Seller(c.Access.UserId); var start = In.Time(pl, "newStart")!.Value;
        var r = In.Str(pl, "mode", 12) == "propose" ? await svc.ProposeAsync(b, actor, start, b.Version) : await svc.RescheduleAsync(b, actor, start, b.Version);
        return r.Ok ? ExecResult.Done($"Booking {b.Number}: new time {start:yyyy-MM-dd HH:mm} UTC.", new EntityRef("booking", b.Id, $"Booking {b.Number}")) : ExecResult.Fail(r.Status, r.Code, r.Message);
    }
}

public static class BookingTools
{
    public static async Task<Booking?> LoadActable(ToolContext c, Guid? id, bool tracked = false)
    {
        if (id is null) return null;
        var q = await c.Db.BookingsForAsync(c.Access);
        if (!tracked) q = q.AsNoTracking();
        var b = await q.FirstOrDefaultAsync(x => x.Id == id);
        return b is not null && BookingAccess.CanAct(c.Access, b) ? b : null;
    }
}

public sealed class SendQuoteAction : ActionDef
{
    public override string Type => "propose_send_quote";
    public override string Label => "Preparing the quote…";
    public override string Description => "Prepare sending a price quote for a quote request: price in dollars, duration in minutes, 1-10 proposed start times (UTC ISO), optional note and expiry. Check get_service_availability first.";
    public override string SchemaJson => """{"type":"object","properties":{"quoteRequestId":{"type":"string"},"price":{"type":"number"},"durationMinutes":{"type":"integer"},"slots":{"type":"array","items":{"type":"string"}},"expiresAt":{"type":"string"},"note":{"type":"string"},"providerId":{"type":"string"}},"required":["quoteRequestId","price","durationMinutes","slots"]}""";

    public override async Task<Prepared> PrepareAsync(ToolContext c, JsonElement i)
    {
        if (In.Id(i, "quoteRequestId") is not { } id) return Prepared.Fail("quoteRequestId must be a quote request id.");
        var x = await BookingEndpoints.QuotesFor(c.Db, c.Access).AsNoTracking().FirstOrDefaultAsync(q => q.Id == id);
        if (x is null || c.Access.Role < AccountRole.Manager || !(c.Access.CanWorkOnBrand(x.BrandId, true) || c.Access.Role >= AccountRole.Admin)) return Prepared.Fail("Quote request not found, or you can't answer it.");
        if (x.Status is not (QuoteStatus.Requested or QuoteStatus.Quoted)) return Prepared.Fail($"A {x.Status} quote request can't be quoted.");
        var price = In.Dec(i, "price"); var dur = In.Int(i, "durationMinutes"); var slots = In.Times(i, "slots");
        if (price is not { } pr || pr < 0 || pr > 1_000_000m || decimal.Round(pr, 2) != pr) return Prepared.Fail("Enter a price between 0 and 1,000,000.");
        if (dur is not { } d || d is < 5 or > 1440) return Prepared.Fail("Enter the estimated duration (5 minutes to 24 hours).");
        if (slots is null or { Count: 0 } or { Count: > 10 }) return Prepared.Fail("Propose between 1 and 10 start times.");
        var expires = In.Time(i, "expiresAt") ?? c.Now.AddDays(7);
        if (expires <= c.Now || expires > c.Now.AddDays(60)) return Prepared.Fail("The quote must expire in the future (within 60 days).");
        var note = In.Str(i, "note", 2000);
        var payload = new Dictionary<string, object?> { ["quoteRequestId"] = id, ["price"] = pr, ["durationMinutes"] = d, ["slots"] = slots, ["expiresAt"] = expires, ["note"] = note, ["providerId"] = In.Id(i, "providerId") };
        return Prepared.Ok(new Proposal("quote", id, "Quote for " + x.ServiceTitle, $"Send a quote of ${pr:0.00} ({d} min) for {x.ServiceTitle}, valid until {expires:yyyy-MM-dd}. The buyer will see it.",
            [new("Price", x.QuotePriceCents is null ? null : Money(x.QuotePriceCents), "$" + pr.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)), new("Duration", x.QuoteDurationMinutes?.ToString(), d + " min"),
             new("Proposed times (UTC)", null, string.Join(", ", slots.Select(s => s.ToString("yyyy-MM-dd HH:mm")))), new("Expires", null, expires.ToString("yyyy-MM-dd")), new("Note", null, note)], "high", $"{x.Status}:{x.Version}", payload));
    }
    public override async Task<ExecResult> ExecuteAsync(ToolContext c, JsonElement pl)
    {
        var x = await BookingEndpoints.QuotesFor(c.Db, c.Access).FirstOrDefaultAsync(q => q.Id == In.Id(pl, "quoteRequestId"));
        if (x is null) return ExecResult.Fail(404, "not_found", "Quote request not found.");
        var r = await c.Get<QuoteService>().SendAsync(x, c.Access, new SendQuoteIn(In.Dec(pl, "price"), In.Int(pl, "durationMinutes"), In.Times(pl, "slots"), In.Id(pl, "providerId"), In.Time(pl, "expiresAt"), In.Str(pl, "note", 2000), x.Version));
        return r.Ok ? ExecResult.Done("Quote sent.", new EntityRef("quote", x.Id, "Quote for " + x.ServiceTitle)) : ExecResult.Fail(r.Status, r.Code, r.Message);
    }
}

public sealed class CalendarBlockAction : ActionDef
{
    public override string Type => "propose_calendar_block";
    public override string Label => "Preparing the calendar block…";
    public override string Description => "Prepare blocking time on a provider's calendar (own calendar by default). kind timeoff|busy; give start+end (UTC ISO) or an all-day date (yyyy-MM-dd, optional endDate).";
    public override string SchemaJson => """{"type":"object","properties":{"providerId":{"type":"string"},"kind":{"type":"string","enum":["timeoff","busy"]},"start":{"type":"string"},"end":{"type":"string"},"date":{"type":"string"},"endDate":{"type":"string"},"note":{"type":"string"}},"required":["kind"]}""";

    public override async Task<Prepared> PrepareAsync(ToolContext c, JsonElement i)
    {
        var member = In.Id(i, "providerId") ?? c.Access.MembershipId;
        if (!await c.Db.Memberships.AsNoTracking().AnyAsync(m => m.Id == member && m.SellerId == c.Access.SellerId && m.Status == MembershipStatus.Active)) return Prepared.Fail("That team member isn't on this account.");
        if (!BookingAccess.CanManageProvider(c.Access, member)) return Prepared.Fail("You can only change your own calendar, unless you're an Owner or Admin.");
        if (!await BookingAccess.IsProviderAsync(c.Db, member)) return Prepared.Fail("That team member isn't set up as a service provider.");
        var kind = In.Str(i, "kind", 10)?.ToLowerInvariant();
        if (kind is not ("timeoff" or "busy")) return Prepared.Fail("kind must be timeoff or busy.");
        var note = In.Str(i, "note", 500);
        string when;
        var allDay = In.Str(i, "date", 12) is not null;
        if (allDay)
        {
            if (!DateOnly.TryParseExact(In.Str(i, "date", 12), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d1)) return Prepared.Fail("Use a date like 2026-12-24.");
            var d2 = d1;
            if (In.Str(i, "endDate", 12) is { } e && !DateOnly.TryParseExact(e, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out d2)) return Prepared.Fail("Use an end date like 2026-12-31.");
            if (d2 < d1 || d2.DayNumber - d1.DayNumber > 365) return Prepared.Fail("The end date must be on or after the start date (at most a year).");
            when = d1 == d2 ? $"{d1:yyyy-MM-dd} (all day)" : $"{d1:yyyy-MM-dd} to {d2:yyyy-MM-dd} (all days)";
        }
        else
        {
            if (In.Time(i, "start") is not { } s || In.Time(i, "end") is not { } en || en <= s || (en - s).TotalDays > 366) return Prepared.Fail("Give a start and a later end (UTC ISO), at most a year apart.");
            when = $"{s:yyyy-MM-dd HH:mm} to {en:yyyy-MM-dd HH:mm} UTC";
        }
        var payload = new Dictionary<string, object?> { ["providerId"] = member, ["kind"] = kind, ["start"] = In.Time(i, "start"), ["end"] = In.Time(i, "end"), ["date"] = In.Str(i, "date", 12), ["endDate"] = In.Str(i, "endDate", 12), ["note"] = note };
        return Prepared.Ok(new Proposal("calendar", member, member == c.Access.MembershipId ? "Your calendar" : "Provider calendar", $"Block {when} as {(kind == "timeoff" ? "time off" : "busy")}.",
            [new("Calendar block", null, $"{(kind == "timeoff" ? "Time off" : "Busy")}: {when}"), new("Note", null, note)], "low", "", payload));
    }
    public override async Task<ExecResult> ExecuteAsync(ToolContext c, JsonElement pl)
    {
        var member = In.Id(pl, "providerId")!.Value;
        DateTime start, end; var allDay = In.Str(pl, "date", 12) is not null;
        if (allDay)
        {
            var zone = await c.Get<CalendarService>().ZoneAsync(c.Access.SellerId, member);
            var d1 = DateOnly.ParseExact(In.Str(pl, "date", 12)!, "yyyy-MM-dd"); var d2 = In.Str(pl, "endDate", 12) is { } e ? DateOnly.ParseExact(e, "yyyy-MM-dd") : d1;
            start = SlotEngine.LocalToUtc(zone, d1, 0); end = SlotEngine.LocalToUtc(zone, d2.AddDays(1), 0);
        }
        else { start = BJson.Utc(In.Time(pl, "start")!.Value); end = BJson.Utc(In.Time(pl, "end")!.Value); }
        if (await c.Db.CalendarBlocks.CountAsync(b => b.MembershipId == member) >= 1000) return ExecResult.Fail(409, "too_many_blocks", "Remove some old blocks first.");
        c.Db.CalendarBlocks.Add(new CalendarBlock { SellerId = c.Access.SellerId, MembershipId = member, Kind = In.Str(pl, "kind", 10) == "timeoff" ? BlockKind.TimeOff : BlockKind.Busy, StartUtc = start, EndUtc = end, AllDay = allDay, Note = In.Str(pl, "note", 500), CreatedAt = c.Now });
        await c.Db.SaveChangesAsync();
        return ExecResult.Done("Calendar block added.");
    }
}
