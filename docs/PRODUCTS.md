# Seller products

Code: `src/Solvelt.Api/Products/**`, entities in `Domain/Products.cs`. Tests: `Product*Tests.cs`. Account/role/brand rules come from `docs/TEAMS.md`.

## Data model
```
Category 1─* Category (tree)            Category 1─* CategoryAttribute (data-driven details, inherited by descendants)
SellerAccount 1─* Product *─1 Brand     Product *─1 Category
Product 1─* ProductOption               (name + values, max 3 options)
        1─* ProductVariant              (combination of option values; sku, price, stock, barcode, enabled)
        1─* ProductImage                (product level, or VariantId set: max 9 each)
        1─* ProductAttributeValue       (key → JSON value, validated against the category config)
        1─* ProductFaq                  (seller-written Q&A, ordered)
        1─* ProductQuestion             (customer questions, answered by the seller)
```
* `Product.Status`: Draft / Active / Inactive / Archived. `Version` is an application-managed optimistic-concurrency token (an int, so the same code works on SQLite and PostgreSQL). Money is stored in cents (`long`), single currency (USD) for now. `QualityScore` (0-100) and `IsReady` are denormalised and refreshed on every change so the list can filter/sort on them.
* Every product belongs to exactly one brand of the active account. Stock lives in `Product.Stock/Reserved` (no variations) or `ProductVariant.Stock/Reserved`.
* Weight in grams, dimensions in mm (API: cm). Shipping options are a small JSON document (`standard`, `express`, free-shipping threshold). Description is stored as **Markdown**; the API also returns `descriptionHtml`.

## Authorization (every endpoint)
`/api/v1/seller/products`, `/seller/questions` and `/categories` require a signed-in, email-verified seller with an active account, then a permission: `BrandWorkRead` for reads, `BrandWorkWrite` for writes (Viewer = read only, Manager/Viewer limited to their brands). Products are loaded ONLY through `db.LoadAsync(access, id, write)` / `db.ProductsFor(access)`, which apply seller + brand scope + permission; anything else is **404** (never 403, so other sellers' resources look missing). Questions are scoped through their product. Writes are rate limited (`RateLimit:CatalogPerMinute`, default 240/user/min).

## API (`/api/v1`)
| | |
|---|---|
| `GET /categories`, `GET /categories/{id}/attributes` | flat tree (id, parentId, path, leaf) and the resolved attribute config of a category |
| `GET /seller/products` | `q`, `status`, `filter=outofstock|attention`, `brandId`, `categoryId`, `sort=updated|title|price|stock|quality`, `dir`, `page`, `pageSize`; returns items, total and status counts |
| `POST /seller/products` | `{title, categoryId?, brandId?}` → Draft (brand defaults to the only/default brand in scope) |
| `GET/PUT/DELETE /seller/products/{id}` | PUT replaces the whole main document (send the `version` you loaded; `409 version_conflict` if stale). Delete is refused while Active |
| `PATCH /seller/products/{id}/quick` | inline `{price?, quantity?}` |
| `POST /seller/products/{id}/status` `{status}`, `POST /seller/products/bulk-status` `{ids, status}` | `active|inactive|archived` (bulk reports `failed` per id) |
| `POST /seller/products/{id}/duplicate` | copy as Draft (no SKU/barcode/handle; images copied) |
| `GET /seller/products/{id}/readiness` | `{score, canPublish, issues[]}` |
| `POST /{id}/images` (multipart `file`, `alt`, `variantId`), `PUT /{id}/images/order`, `PUT/DELETE /{id}/images/{imageId}`, `GET /{id}/images/{imageId}/file` | |
| `PUT /{id}/variants`, `POST /{id}/variants/preview` | options + variations (missing combinations are generated; stock of surviving variations is kept) |
| `PUT /{id}/faqs` | ordered list, max 20 |
| `GET/PUT /seller/questions…` | `GET ?state=unanswered|answered|hidden|all&productId&brandId`, `PUT /{id}/answer {answer, publish?}`, `POST /{id}/publish`, `POST /{id}/hide` |
| `GET /seller/products/csv/template`, `POST …/csv/validate`, `POST …/csv/import`, `GET …/csv/export` | see CSV below |
| `POST /api/dev/questions` | **Testing/Development only**, anonymous stand-in for the buyer site |

## Publish rules and the quality score
`ProductRules.Evaluate` returns `{score, canPublish, issues[]}`; each issue has `key`, `section` (basics, media, description, price, identifiers, variations, details, shipping, policies, seo), `severity` (`required` blocks publishing, `recommended` only lowers the score) and a message. The UI checklist links each `key` to its field.

Required: title (≥ 5 chars), category, ≥ 1 product photo, price (or a price on every enabled variation), stock (or in-stock variations, or backorders), ≥ 1 option and enabled variation when "has variations" is on, every `Required` category attribute, and a **valid barcode if one was entered**. Recommended: title 60-150 chars, 3+ photos, ≥ 1000 px photos, alt text, 3+ bullets, description ≥ 150 chars, SKU, barcode or "no barcode", condition note for non-new, optional category details, weight, handling time, returns window, search terms. The score is earned points / total points.

Activating also needs the account to be **Approved** (`403 seller_not_approved` otherwise; drafts can always be saved). Unready listings answer `422 not_publishable` with the issue list. An **Active** listing cannot be saved into an unpublishable state (`422`), so edit autosave should apply to Draft/Inactive listings; live edits use an explicit Save. Activation generates the URL handle from the title when empty. Audit events: `product.activated|deactivated|archived|deleted|csv_imported`.

Barcodes: UPC-A (12), EAN-8, EAN-13, GTIN-14, ISBN-13 (EAN-13 starting 978/979) and ISBN-10, spaces/hyphens ignored, check digit verified (`Products/Barcode.cs`). While a listing is a draft an invalid number is **stored and flagged** (so autosave never loses typing) and blocks publishing; on Active listings it is rejected with 400.

## Category attributes (product details / specifications)
Rows of `category_attributes`: `Key, Label, Type (text|longtext|number|select|multiselect|bool), OptionsJson, Required, Group, Help, Unit, SortOrder`. A category inherits every attribute of its ancestors (a child key overrides the parent's). `CategorySeed` seeds ~15 departments and their children with sets such as electronics (model, warranty, power, connectivity), clothing (material, fit, size chart, care), food (ingredients, allergens, nutrition, certifications such as organic/kosher, net weight, shelf life) and books (author, publisher, pages, format). The UI renders the form from `GET /categories/{id}/attributes`; nothing is hard-coded in the client. To add or change a field, insert/update a `category_attributes` row (the seeder only runs on an empty `categories` table and never overwrites operator changes). On save, values whose key is not part of the current category's config are dropped, which is how "change category clears old details" works on the server.

## Images
Only JPEG/PNG/WebP; extension must match the magic bytes; ≤ 10 MB; 100-12000 px per side; max 9 product images and 9 per variation; random 32-hex storage keys through `IFileStorage`. **Metadata is stripped without a third-party library**: JPEG APP1 (EXIF/XMP), APP13 (IPTC) and COM segments, PNG `eXIf/tEXt/zTXt/iTXt/tIME` chunks and WebP `EXIF/XMP` chunks (VP8X flags cleared, RIFF size fixed) are removed by a small container-level rewriter (`ImageProcessing`); pixel data is not re-encoded (no quality loss). Trade-off: the orientation tag is also removed, so photos should be exported upright. Reason for not using ImageSharp/SkiaSharp: ImageSharp's license is not permissive for commercial use, and native Skia adds deployment weight; a PR can swap in a permissive encoder later behind `ImageProcessing.Process`. Files are served at `/seller/products/{id}/images/{imageId}/file` (authorised per request, `nosniff`, private immutable cache) because the buyer site does not exist yet.

## CSV
Columns: `sku,title,brand,category,condition,price,compare_at_price,quantity,barcode,description,bullet_1..bullet_5,weight_grams,length_cm,width_cm,height_cm,handling_days,returns_days,warranty_months,search_terms` (export adds `status`). `brand` is the brand name (empty = the default/only brand in scope), `category` the category slug. Limits: 1 MB, 500 rows, 60 columns, 10 000 chars per cell, UTF-8. Image/URL columns are refused. `validate` returns per-row errors without writing; `import` creates **drafts** for valid rows only and reports the rest. Export: all products in scope (max 5000); cells starting with `= + - @ TAB CR` get a leading `'` (formula injection), and import removes that apostrophe so exports round-trip. Variations are not part of the CSV.

## Inventory (for the orders workstream)
`IInventoryService` (`Products/ProductLogic.cs`) is the **only** code that changes stock after a listing exists. Every method is a single atomic `UPDATE … WHERE <guard>` (no read-modify-write), so parallel orders cannot oversell: `ReserveAsync` (holds available = on-hand − reserved, or ignores availability when the product allows backorders), `ReleaseAsync`, `CommitAsync` (consumes a reservation on shipping), `AdjustAsync` (±on-hand, never below 0), `SetOnHandAsync` (used by the listing form and quick edit), `FindBySkuAsync` (product SKU or variation SKU, seller-scoped). Pass the variation id when the product has variations. The seller form sets an absolute quantity through `SetOnHandAsync`, which can overwrite a very recent change by an order: a deliberate trade-off (orders use reserved/commit so their effect is not lost for `Reserved`).

## Customer questions
`ProductQuestion` rows are created by the future buyer site; until then by `POST /api/dev/questions {productId, askerName, text}`, mapped only in Testing/Development. States: Pending (unanswered or unpublished answer), Published, Hidden.

## Review fixes (fix/products-review)
* **Live listings stay publishable everywhere:** `PUT /{id}/variants` evaluates the would-be state for Active listings and answers `422 not_publishable` (nothing saved), like the main PUT.
* **Barcodes** are validated to ≤ 20 characters (the column size) on the product and on every variation. `TrySaveAsync` maps only optimistic-concurrency failures to `409 version_conflict`; a unique-index violation becomes a `400` field error (`sku`); any other database error is a real 500 (logged).
* **SKUs:** one table `sku_claims (SellerId, Sku normalised = trim + lower-case)` with a unique index is the single source of truth for products AND variations (same namespace). The pre-check gives a friendly message, the unique index makes parallel requests safe (exactly one wins, the rest get 400). A product cannot reuse one of its own variations' SKUs. The migration backfills existing SKUs.
* **Images:** `Product.ImageSeq` is a concurrency token bumped on every image add/remove, so parallel uploads can't both pass the 9-image check (the loser gets 409 and can retry).
* **Inventory:** on-hand can never drop below `Reserved`: `AdjustAsync`/`SetOnHandAsync` return `below_reserved` (with the counts); the product form, quick edit and variation grid answer 400 "N units are reserved by open orders".
* **Quick edit** requires `version` (400 otherwise).
* **Images:** JPEG output ends at the first end-of-image marker (appended bytes are dropped; a JPEG without one is rejected); PNG keeps only an allow-list of chunks.
* **CSV import** saves row by row; a SKU taken in the meantime is reported as a row error instead of a 500.

## Marketplace catalog feed
The buyer marketplace reads Active products and services of Approved sellers through the signed catalog feed (`GET /api/internal/v1/catalog/listings`, resumable by a per-listing `CatalogVersion`,
plus `listing.upserted` / `listing.unlisted` events, images, categories and brands) and sends buyer questions with `POST /api/internal/v1/products/{id}/questions`. Details: `docs/CATALOG.md`.
Product endpoints are unchanged; `Product.CatalogVersion/CatalogListed/CatalogEverListed/CatalogUnlistReason/CatalogStockState` are maintained only by the SaveChanges hook (`Catalog/CatalogTracking.cs`).
* **Search terms** (`searchTerms`, `search_terms` in the CSV): separate the terms with commas, semicolons or line breaks (`mug, coffee cup, tea`); a space is part of a term. At most 20 terms of 50 characters; control characters are stripped and duplicates dropped; terms with an e-mail address or a phone number are refused (a number starting with `+`, a 10-digit US-style number, or a plain run of 10+ digits; years, decimals and short number lists are fine) (`400` on the form, a row error in the CSV import). They are published to the marketplace as a list (`docs/CATALOG.md`).

