You are a senior full-stack engineer and product designer. Build the SELLER PRODUCTS area of the Solvelt marketplace, autonomously. Solvelt is a general online marketplace like the big multi-seller marketplaces, and this is the seller portal where sellers list their products. No one will answer questions: never end your turn to ask. Decide, record it in docs/DECISIONS.md (backend) and docs/DESIGN.md (frontend), and keep going until everything below is built, tested, green, merged and pushed. I'm on a plan with usage limits: work efficiently and push regularly.

## Repos and coordination (don't touch any other repository)
- **Backend (your checkout):** https://github.com/Yanky1/Yanky1-test-login-backend (ASP.NET Core 10, EF Core, SQLite dev/tests, PostgreSQL prod).
- **Frontend:** https://github.com/Yanky1/test-login-frontend (React + TS + Vite). Attach it with add_repo (owner "Yanky1", repo "test-login-frontend", access "push") and clone it next to the backend.
- **Environment:**
  - .NET 10 SDK: `apt-get install -y dotnet-sdk-10.0` if it's missing.
  - Chromium at /opt/pw-browsers/chromium (PW_CHROMIUM_PATH); don't reinstall browsers.
- **Another agent works at the same time** on branch `feature/phone-sms` (phone/SMS channels, its own EF migration). So:
  - Work on `feature/products` in both repos, and `git fetch origin main && git rebase origin/main` often.
  - Keep ONE EF migration; if main gained migrations, delete yours and regenerate it on top of the latest model.
  - When everything is green and rebased, fast-forward-merge into main, push, confirm all workflows are green on main, then delete the branch.
  - Never force-push main or revert others' commits. If docs/AGENTS.md exists, update your row.
- **Read first:** both READMEs, docs/DESIGN.md, docs/DECISIONS.md and docs/SECURITY.md. REUSE the existing design system (tokens, components, layout, light/dark mode) so the new pages look like the same product. Reuse the existing auth, session, IFileStorage, FileSniffer, audit, rate-limit and seller-status code. Don't change auth behaviour.

## Product goal
Give sellers the full listing experience that the leading marketplace seller tools have in common: a product catalog they manage, and a rich product listing with title, photos, description, bullet-point features, price, stock, variations, product details/specifications, shipping, and Q&A. Follow the proven patterns those tools share, but in an original Solvelt design: no other company's branding, names, or copied layouts. The UI must stay calm, clean and never overwhelming. Use progressive disclosure: show the essentials first and reveal advanced or category-specific fields only when relevant. Write helpful inline guidance, e.g. "Good titles are 60–150 characters: brand + product + key attribute".

## Frontend
1. **App shell for signed-in sellers:** a clean sidebar (Products, Questions, Account/Security) with a top bar, responsive (drawer on mobile).
2. **Products (catalog) list:**
   - Search, filters (status: Draft / Active / Inactive / Archived, plus Out of stock and Needs attention), sort, and pagination.
   - Table on desktop, cards on mobile, with a thumbnail, title, SKU, price, stock, status and a listing-quality badge.
   - Inline quick-edit of price and stock. Bulk activate/deactivate/archive. Duplicate a listing.
   - A friendly empty state with an "Add your first product" CTA, plus skeleton loading.
3. **Add/Edit product:** start with a short "What are you selling?" step (category search + title), then ONE page with a sticky section navigator.
   - Sections:
     - **Basics:** title, brand, category (searchable tree), condition (New / Used / Refurbished, with condition notes for non-new).
     - **Media:** drag-and-drop multi-image upload with progress, reorder, set the main image, alt text, and guidance (white background, ≥1000px, up to 9 images), plus inline validation.
     - **Description:** up to 5 bullet-point key features, and a long description in a safe Markdown editor with live preview.
     - **Price & stock:** price, list/compare-at price, sale price with optional start/end dates, private cost with a margin hint, SKU, quantity, low-stock alert, allow backorders, max per order.
     - **Product identifiers (optional):** UPC/EAN/GTIN/ISBN with live check-digit validation, or a "This product has no barcode" switch.
     - **Variations:** up to 3 options (e.g. Size, Color, Material), auto-generated combinations, and a per-variation price/SKU/stock/barcode/image grid. Hidden until "This product comes in different options" is switched on.
     - **Product details / specifications:** category-driven attributes rendered from server config (e.g. electronics: model, warranty, power; clothing: size chart, material, fit; food: ingredients, allergens, nutrition, certifications such as organic or kosher; books: author, ISBN, pages). Data-driven, not hardcoded.
     - **Shipping & delivery:** package weight and dimensions, handling time, shipping options (standard/express/free shipping threshold), local pickup.
     - **Q&A / FAQ:** seller-written question/answer pairs shown on the listing, reorderable.
     - **Policies:** returns window and condition, and warranty.
     - **Search keywords & SEO:** backend search terms, URL handle, meta title/description, collapsed under "Advanced".
   - Autosave drafts (debounced, with a "Saved" indicator).
   - A listing-quality score plus a "Ready to publish" checklist that links to each missing or weak field.
   - A live "Preview as buyer" panel or modal showing the product page as a shopper would see it: gallery, title, price, bullets, variation selector, details table, Q&A.
   - An unsaved-changes guard, keyboard-friendly, and never losing input.
4. **Questions:** an inbox of customer questions across products (filter: unanswered/answered, per product), answer inline, publish or hide. There is no buyer site yet, so questions are created via an API endpoint enabled only in the Testing/Development environments.
5. **Bulk upload:** CSV upload for product fields (no image URLs, to avoid fetching remote content). Provide a downloadable template with an example row, a validation preview with row-level errors before import, and import into drafts. Also export the catalog to CSV.
6. **Standards:** WCAG 2.2 AA, responsive from 320px, light/dark mode, prefers-reduced-motion, code-split routes, axe checks in E2E with zero serious/critical violations.

## Backend
1. **Entities + migrations (SQLite + Postgres):**
   - Category (a seeded tree covering common marketplace departments: electronics, home & kitchen, clothing, beauty, toys, books, grocery/food, sports, etc.), CategoryAttribute config (data-driven).
   - Product, ProductVariant, ProductOption, ProductImage, ProductAttributeValue, ProductFaq, ProductQuestion, shipping fields, policies.
   - Status: Draft / Active / Inactive / Archived.
   - Optimistic concurrency (row version) on Product.
2. **API under /api/v1/seller/products (and /seller/questions, /categories):** CRUD, duplicate, list with search/filter/sort/pagination, bulk status, images (upload/reorder/delete/set main), variants, FAQ, questions (answer/hide), CSV validate/import/export, the listing-quality/missing-fields endpoint, and the test-only question-create endpoint.
3. **Security:**
   - EVERY endpoint enforces seller ownership; return 404 for other sellers' resources (no IDOR).
   - Only approved sellers (status Approved/Live) can activate listings; drafts are allowed while pending.
   - Image upload: content sniffing (JPEG/PNG/WebP), size limits, max 9 per product (per variant as well), random storage keys, and stripped metadata/EXIF if a permissively licensed library allows it (document the choice). Serve images through the API with nosniff.
   - Description: store Markdown and render it safely (no raw HTML).
   - Validate every field length/range. Validate barcode check digits server-side as well. SKU unique per seller.
   - CSV: size and row limits, values validated, and formula-injection-safe export.
   - Rate limits on writes. Audit-log activate/deactivate/delete.
4. **Publish rules:** title, category, at least 1 image, price, stock (or in-stock variations), required category attributes, and a valid barcode if one was entered. Return a structured "missing/weak" list that the UI checklist and quality score use.

## Out of scope (list these in docs/ROADMAP.md with a short design note)
Buyer storefront, orders and payments, reviews, advertising/sponsored listings, external marketplace or store sync, and image import by URL.

## Tests: write them, run them, fix until green
- Backend xUnit integration tests: ownership/IDOR on every endpoint (seller B can't read/edit/delete seller A's products, images, questions); publish rules; barcode check digits (valid/invalid UPC-A, EAN-13, GTIN-14, ISBN-13); image validation (spoofed extension rejected); variants generation; sale-price dates; concurrency conflict; CSV validation, import and export (formula-safe); question answering; duplicate.
- Frontend Vitest: product form sections, category-specific attributes and clearing on category change, quality checklist, barcode validation, variants grid, autosave, unsaved-changes guard, list filters.
- Playwright E2E (against the real backend): approved seller creates a product with images, bullets, a variation and specs → previews → activates → it appears in the list → edits price inline; a non-approved seller can save a draft but can't activate; answer a customer question; CSV import happy path; axe checks on every new page.
- Never skip, delete or weaken a test. All GitHub workflows must be green on main after your merge (confirm the final run status).

## Docs
- docs/PRODUCTS.md: data model, API, publish rules, how category attributes are configured.
- Update both READMEs.
- Add screenshots of the new pages (desktop + mobile) to the frontend's docs/screenshots/.

## Keep yourself running
I may be away. Use send_later or create_trigger (if available) to schedule check-ins into this session: every 2–3 hours, and 3 minutes after any usage-limit reset. That way you resume automatically if you stop before finishing.

## Finish
Post a final summary: what was built, test counts per suite, workflow run links on main, what's deferred, and where the screenshots are.

## Addendum: accounts, roles and brands (now on main)
Seller accounts now have members with roles (Owner/Admin/Manager/Viewer) and multiple brands (see docs/TEAMS.md). Every product belongs to exactly one brand of the active account. Enforce the active-account membership, role permissions and brand scope on every products/questions/CSV endpoint using the helpers documented in docs/TEAMS.md (Viewer = read-only; Manager = only their brands). Add a brand selector to the product form and a brand filter to the catalog list, and include brand in CSV import/export. Add tests for role and brand-scope enforcement alongside the IDOR tests.
An orders workstream (E) starts after you merge and will decrement stock by product/variant/SKU: keep stock changes in one service (e.g. IInventoryService with reserve/release/adjust, concurrency-safe) and document it in docs/PRODUCTS.md.
