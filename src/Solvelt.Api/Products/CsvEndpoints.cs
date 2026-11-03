using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Products;

public static class CsvFormat
{
    public const long MaxBytes = 1024 * 1024;
    public const int MaxRows = 500, MaxColumns = 60, MaxCell = 10_000;

    public static readonly string[] Columns =
    [
        "sku", "title", "brand", "category", "condition", "price", "compare_at_price", "quantity", "barcode", "description",
        "bullet_1", "bullet_2", "bullet_3", "bullet_4", "bullet_5",
        "weight_grams", "length_cm", "width_cm", "height_cm", "handling_days", "returns_days", "warranty_months", "search_terms"
    ];

    /// <summary>RFC 4180 parser (quotes, doubled quotes, CRLF/LF, embedded newlines). Throws FormatException on an unterminated quote.</summary>
    public static List<List<string>> Parse(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder();
        var inQ = false; var any = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQ)
            {
                if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else inQ = false; }
                else cell.Append(c);
                if (cell.Length > MaxCell) throw new FormatException("A cell is too long.");
                continue;
            }
            switch (c)
            {
                case '"' when cell.Length == 0: inQ = true; any = true; break;
                case ',': row.Add(cell.ToString()); cell.Clear(); any = true; break;
                case '\r': break;
                case '\n':
                    if (any || cell.Length > 0) { row.Add(cell.ToString()); rows.Add(row); }
                    row = []; cell.Clear(); any = false;
                    if (rows.Count > MaxRows + 1) throw new FormatException($"Too many rows (maximum {MaxRows}).");
                    break;
                default: cell.Append(c); any = true; break;
            }
            if (cell.Length > MaxCell) throw new FormatException("A cell is too long.");
            if (row.Count > MaxColumns) throw new FormatException("Too many columns.");
        }
        if (inQ) throw new FormatException("A quoted value is not closed.");
        if (any || cell.Length > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    /// <summary>Quotes a cell for output; neutralises spreadsheet formulas (leading = + - @ tab CR) with a leading apostrophe.</summary>
    public static string Cell(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        if (v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    /// <summary>Reverses <see cref="Cell"/>'s apostrophe so an exported file re-imports unchanged.</summary>
    public static string Unescape(string v) => v.Length > 1 && v[0] == '\'' && v[1] is '=' or '+' or '-' or '@' or '\t' or '\r' ? v[1..] : v;

    public static string Num(decimal? d) => d?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
}

public record CsvRowError(string Field, string Message);
public record CsvRow(int Row, bool Ok, string? Title, string? Sku, List<CsvRowError> Errors);

public static class CsvEndpoints
{
    /// <summary>Test seam only (null in production): runs between CSV validation and the first save.</summary>
    internal static Func<Task>? AfterValidate;

    private record Parsed(CsvRow Row, ProductWrite? Write, Guid BrandId, Guid? CategoryId);

    private static async Task<(List<Parsed>? Rows, IResult? Error, List<string> Ignored)> ParseFileAsync(IFormFile file, HttpContext ctx, AppDbContext db)
    {
        var a = ctx.GetAccess()!;
        var ignored = new List<string>();
        if (file.Length == 0 || file.Length > CsvFormat.MaxBytes) return (null, Validation.Invalid("file", "The CSV file must be between 1 byte and 1 MB."), ignored);
        if (!Path.GetExtension(file.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase)) return (null, Validation.Invalid("file", "Upload a .csv file."), ignored);
        string text;
        await using (var s = file.OpenReadStream()) { using var sr = new StreamReader(s, new UTF8Encoding(false, true)); try { text = await sr.ReadToEndAsync(); } catch (DecoderFallbackException) { return (null, Validation.Invalid("file", "The file must be UTF-8 text."), ignored); } }
        List<List<string>> rows;
        try { rows = CsvFormat.Parse(text); } catch (FormatException e) { return (null, Validation.Invalid("file", e.Message), ignored); }
        if (rows.Count < 2) return (null, Validation.Invalid("file", "The file needs a header row and at least one product row."), ignored);
        if (rows.Count - 1 > CsvFormat.MaxRows) return (null, Validation.Invalid("file", $"Too many rows (maximum {CsvFormat.MaxRows})."), ignored);
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant().Replace(' ', '_')).ToList();
        if (!header.Contains("title")) return (null, Validation.Invalid("file", "The first row must contain a \"title\" column. Download the template to see the format."), ignored);
        if (header.Any(h => h.Contains("image") || h.Contains("photo") || h.Contains("picture")))
            return (null, Validation.Invalid("file", "Image columns aren't supported. Import the products, then upload photos in the product editor."), ignored);
        if (header.Distinct().Count() != header.Count) return (null, Validation.Invalid("file", "Each column name can appear only once."), ignored);
        ignored = header.Where(h => h.Length > 0 && !CsvFormat.Columns.Contains(h)).ToList();

        var brands = (await db.BrandsFor(a).Where(b => b.Status == BrandStatus.Active).ToListAsync()).Where(b => a.CanWorkOnBrand(b.Id, true)).ToList();
        var defaultBrand = brands.Count == 1 ? brands[0] : brands.FirstOrDefault(b => b.IsDefault);
        var cats = await db.Categories.AsNoTracking().ToListAsync();
        var seenSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Parsed>();
        for (var n = 1; n < rows.Count; n++)
        {
            var raw = rows[n];
            string Get(string col) { var i = header.IndexOf(col); return i >= 0 && i < raw.Count ? CsvFormat.Unescape(raw[i].Trim()) : ""; }
            var errs = new List<CsvRowError>();
            void E(string f, string m) => errs.Add(new(f, m));
            if (raw.All(string.IsNullOrWhiteSpace)) continue;
            decimal? Dec(string col) { var v = Get(col); if (v.Length == 0) return null; if (decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) return d; E(col, "Enter a number like 19.99."); return null; }
            int? Int(string col) { var v = Get(col); if (v.Length == 0) return null; if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)) return d; E(col, "Enter a whole number."); return null; }

            var title = Get("title"); var sku = Get("sku");
            if (title.Length == 0) E("title", "Title is required.");
            Brand? brand = null;
            var bn = Get("brand");
            if (bn.Length == 0) { brand = defaultBrand; if (brand is null) E("brand", "Brand is required (you work with several brands)."); }
            else { brand = brands.FirstOrDefault(b => b.Name.Equals(bn, StringComparison.OrdinalIgnoreCase)); if (brand is null) E("brand", $"Unknown brand \"{bn}\" (or you can't add products to it)."); }
            Guid? catId = null;
            var cn = Get("category");
            if (cn.Length > 0)
            {
                var m = cats.Where(c => c.Slug.Equals(cn, StringComparison.OrdinalIgnoreCase)).ToList();
                if (m.Count == 0) m = cats.Where(c => c.Name.Equals(cn, StringComparison.OrdinalIgnoreCase)).ToList();
                if (m.Count == 1) catId = m[0].Id; else E("category", m.Count == 0 ? $"Unknown category \"{cn}\". Use the category's slug from the Categories list." : "That category name is ambiguous - use its slug.");
            }
            var cond = Get("condition");
            if (cond.Length > 0 && !Enum.TryParse<ProductCondition>(cond, true, out _)) E("condition", "Use New, Used or Refurbished.");
            var bullets = Enumerable.Range(1, 5).Select(i => Get($"bullet_{i}")).Where(b => b.Length > 0).ToList();
            var price = Dec("price"); var cmp = Dec("compare_at_price"); var qty = Int("quantity");
            var wg = Int("weight_grams"); var len = Dec("length_cm"); var wid = Dec("width_cm"); var hei = Dec("height_cm");
            var hd = Int("handling_days"); var rd = Int("returns_days"); var wm = Int("warranty_months");
            var bc = Barcode.Strip(Get("barcode"));
            if (bc.Length > 0 && !Barcode.Check(bc).Valid) E("barcode", Barcode.Check(bc).Error!);
            if (sku.Length > 0)
            {
                if (!ProductService.ValidSku(sku)) E("sku", "Use letters, numbers and . - _ / # (max 64 characters).");
                else if (!seenSkus.Add(sku)) E("sku", "This SKU appears twice in the file.");
                else if (await ProductService.SkuConflictAsync(db, a.SellerId, sku, null) is { } msg) E("sku", msg);
            }
            var w = new ProductWrite(1, brand?.Id, catId, title, null, cond.Length == 0 ? null : cond, null, sku.Length == 0 ? null : sku, price, cmp, null, null, null, null, qty, null, false, null,
                bc.Length == 0 ? null : bc, false, bullets, Get("description"), wg, len, wid, hei, hd, null, false, rd, null, wm, null, Get("search_terms"), null, null, false, null);
            foreach (var (f, v) in ProductService.Validate(w).Where(kv => !errs.Any(x => x.Field == kv.Key))) errs.Add(new(f, v[0]));
            result.Add(new(new CsvRow(n + 1, errs.Count == 0, title, sku.Length == 0 ? null : sku, errs), errs.Count == 0 ? w : null, brand?.Id ?? Guid.Empty, catId));
        }
        if (result.Count == 0) return (null, Validation.Invalid("file", "No product rows found."), ignored);
        return (result, null, ignored);
    }

    public static void MapCsvEndpoints(this RouteGroupBuilder g)
    {
        var c = g.MapGroup("/seller/products/csv").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireRateLimiting("catalog");

        c.MapGet("/template", () =>
        {
            var sb = new StringBuilder("﻿").AppendLine(string.Join(',', CsvFormat.Columns));
            var example = new Dictionary<string, string>
            {
                ["sku"] = "MUG-001", ["title"] = "Stoneware Coffee Mug 350 ml - Speckled Blue", ["brand"] = "", ["category"] = "kitchen-dining", ["condition"] = "New",
                ["price"] = "14.99", ["compare_at_price"] = "19.99", ["quantity"] = "40", ["barcode"] = "036000291452",
                ["description"] = "Hand-glazed stoneware mug. Dishwasher and microwave safe.", ["bullet_1"] = "350 ml capacity", ["bullet_2"] = "Dishwasher safe",
                ["weight_grams"] = "420", ["length_cm"] = "12", ["width_cm"] = "9", ["height_cm"] = "9", ["handling_days"] = "2", ["returns_days"] = "30", ["warranty_months"] = "12",
                ["search_terms"] = "mug cup coffee tea ceramic"
            };
            sb.AppendLine(string.Join(',', CsvFormat.Columns.Select(col => CsvFormat.Cell(example.GetValueOrDefault(col)))));
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", "solvelt-products-template.csv");
        }).RequirePermission(Permission.BrandWorkRead);

        c.MapPost("/validate", async (IFormFile file, HttpContext ctx, AppDbContext db) =>
        {
            var (rows, err, ignored) = await ParseFileAsync(file, ctx, db);
            if (err is not null) return err;
            return Results.Json(new
            {
                total = rows!.Count, valid = rows.Count(r => r.Row.Ok), invalid = rows.Count(r => !r.Row.Ok), ignoredColumns = ignored,
                rows = rows.Select(r => r.Row)
            }, ProductJson.Opts);
        }).RequirePermission(Permission.BrandWorkWrite).DisableAntiforgery();

        c.MapPost("/import", async (IFormFile file, HttpContext ctx, AppDbContext db, CategoryService cats, TimeProvider time, IAuditLog audit) =>
        {
            var a = ctx.GetAccess()!;
            var (parsed, err, _) = await ParseFileAsync(file, ctx, db);
            if (err is not null) return err;
            var rows = parsed!;
            if (AfterValidate is { } hook) await hook(); // test seam: lets a test take a SKU after validation, to exercise the unique-index path
            var now = time.UtcNow(); var created = new List<object>(); var failed = new List<CsvRow>();
            foreach (var r in rows.Where(r => r.Row.Ok))
            {
                var w = r.Write!;
                var p = new Product { SellerId = a.SellerId, BrandId = r.BrandId, CategoryId = r.CategoryId, CreatedAt = now, UpdatedAt = now, CreatedBy = a.UserId, Stock = w.Quantity ?? 0 };
                ProductService.Apply(p, w, null);
                await ProductRules.RefreshAsync(db, cats, p);
                db.Products.Add(p);
                await ProductService.SyncSkuClaimsAsync(db, p);
                try { await db.SaveChangesAsync(); created.Add(new { id = p.Id, row = r.Row.Row, title = p.Title }); }
                catch (DbUpdateException e) when (ProductEndpoints.IsUniqueViolation(e))
                {
                    // a parallel request took the SKU between validation and save: report it on the row instead of failing the whole import
                    db.ChangeTracker.Clear();
                    failed.Add(r.Row with { Ok = false, Errors = [new CsvRowError("sku", "You already use this SKU.")] });
                }
            }
            await audit.WriteAsync("product.csv_imported", a.UserId, null, $"seller={a.SellerId};imported={created.Count};skipped={rows.Count - created.Count}");
            return Results.Json(new { imported = created.Count, skipped = rows.Count - created.Count, created, rows = rows.Where(r => !r.Row.Ok).Select(r => r.Row).Concat(failed) }, ProductJson.Opts, statusCode: 201);
        }).RequirePermission(Permission.BrandWorkWrite).DisableAntiforgery();

        c.MapGet("/export", async (HttpContext ctx, AppDbContext db, Guid? brandId) =>
        {
            var a = ctx.GetAccess()!;
            var q = db.ProductsFor(a).AsNoTracking();
            if (brandId is { } bid) q = q.Where(p => p.BrandId == bid);
            var list = await q.OrderBy(p => p.Title).Take(5000).ToListAsync();
            var brands = await db.Brands.AsNoTracking().Where(b => b.SellerId == a.SellerId).ToDictionaryAsync(b => b.Id, b => b.Name);
            var cats = await db.Categories.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Slug);
            var cols = CsvFormat.Columns.Concat(["status"]).ToArray();
            var sb = new StringBuilder("﻿").AppendLine(string.Join(',', cols));
            foreach (var p in list)
            {
                var b = ProductJson.Bullets(p);
                string? Bullet(int i) => i < b.Count ? b[i] : null;
                var cells = new[]
                {
                    p.Sku, p.Title, brands.GetValueOrDefault(p.BrandId), p.CategoryId is { } c1 ? cats.GetValueOrDefault(c1) : null, p.Condition.ToString(),
                    CsvFormat.Num(Money.FromCents(p.PriceCents)), CsvFormat.Num(Money.FromCents(p.CompareAtCents)), p.HasVariants ? "" : p.Stock.ToString(CultureInfo.InvariantCulture), p.Barcode, p.Description,
                    Bullet(0), Bullet(1), Bullet(2), Bullet(3), Bullet(4),
                    p.WeightGrams?.ToString(CultureInfo.InvariantCulture), CsvFormat.Num(p.LengthMm / 10m), CsvFormat.Num(p.WidthMm / 10m), CsvFormat.Num(p.HeightMm / 10m),
                    p.HandlingDays?.ToString(CultureInfo.InvariantCulture), p.ReturnsDays?.ToString(CultureInfo.InvariantCulture), p.WarrantyMonths?.ToString(CultureInfo.InvariantCulture), p.SearchTerms,
                    p.Status.ToString()
                };
                sb.AppendLine(string.Join(',', cells.Select(CsvFormat.Cell)));
            }
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", "solvelt-products.csv");
        }).RequirePermission(Permission.BrandWorkRead);
    }
}
