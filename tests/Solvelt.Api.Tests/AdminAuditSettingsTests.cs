using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Admin;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Tests;

public class CsvTests
{
    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")", "\"'=HYPERLINK(\"\"http://evil\"\")\"")]
    [InlineData("+1+1", "\"'+1+1\"")]
    [InlineData("-2+3", "\"'-2+3\"")]
    [InlineData("@SUM(A1)", "\"'@SUM(A1)\"")]
    [InlineData("\t=1", "\"'\t=1\"")]
    [InlineData("\r=1", "\"'\r=1\"")]
    [InlineData("plain, with \"quotes\"", "\"plain, with \"\"quotes\"\"\"")]
    [InlineData(null, "\"\"")]
    public void Cells_are_quoted_and_formula_safe(string? input, string expected) => Assert.Equal(expected, Csv.Cell(input));
}

public class AdminAuditTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Audit_viewer_filters_by_actor_target_action_and_date_and_marks_staff_events()
    {
        var supportEmail = await StaffKit.Create(f, StaffRole.Support);
        var support = await StaffKit.Login(f, supportEmail);
        var s1 = new Api(f); var e1 = await s1.SignupVerified(); var s2 = new Api(f); var e2 = await s2.SignupVerified();
        var u1 = StaffKit.UserId(f, e1); var u2 = StaffKit.UserId(f, e2); var actor = StaffKit.UserId(f, supportEmail);
        await support.Post($"/api/v1/admin/users/{u1}/sign-out-everywhere", new { reason = "Ticket A-1" });
        f.Time.Advance(TimeSpan.FromHours(3));
        var cut = f.Time.GetUtcNow().UtcDateTime.ToString("O");
        support = await StaffKit.Login(f, supportEmail); // the earlier session went idle during the jump
        await support.Post($"/api/v1/admin/users/{u2}/clear-lockout", new { reason = "Ticket A-2" });

        var ro = await StaffKit.LoginNew(f, StaffRole.ReadOnly);
        async Task<List<JsonElement>> Q(string qs) => (await ro.JsonBody(await ro.Get("/api/v1/admin/audit?pageSize=100&" + qs))).GetProperty("items").EnumerateArray().ToList();
        var byActor = await Q($"actor={actor}&staffOnly=true");
        Assert.Equal(2, byActor.Count(e => e.GetProperty("type").GetString()!.StartsWith("admin.user.")));
        Assert.All(byActor, e => Assert.Equal("Support", e.GetProperty("actorRole").GetString() ?? "Support"));
        Assert.Single(await Q($"target={u1}"));
        var byAction = await Q($"action=user.lockout_cleared&target={u2}");
        Assert.Equal("Ticket A-2", byAction.Single().GetProperty("reason").GetString());
        Assert.Equal(2, (await Q($"actor={Uri.EscapeDataString(supportEmail[..14])}&action=user.")).Count);               // actor by e-mail fragment
        Assert.Single(await Q($"actor={actor}&action=user.&from={Uri.EscapeDataString(cut)}"));                            // date window
        Assert.Single(await Q($"actor={actor}&action=user.&to={Uri.EscapeDataString(cut)}"));
        // ordinary (non-staff) events are in the same log
        Assert.Contains(await Q($"actor={u1}"), e => e.GetProperty("type").GetString() == "email.verified");
        Assert.DoesNotContain(await Q($"actor={u1}&staffOnly=true"), e => e.GetProperty("type").GetString() == "email.verified");
        var page1 = (await ro.JsonBody(await ro.Get("/api/v1/admin/audit?pageSize=2&page=1")));
        Assert.Equal(2, page1.GetProperty("items").GetArrayLength()); Assert.True(page1.GetProperty("total").GetInt32() > 2);
    }

    [Fact]
    public async Task Export_is_super_admin_step_up_audited_and_formula_safe()
    {
        var saEmail = await StaffKit.Create(f, StaffRole.SuperAdmin);
        var sa = await StaffKit.Login(f, saEmail);
        var victim = new Api(f); var vEmail = await victim.SignupVerified();
        var vid = StaffKit.UserId(f, vEmail);
        const string evil = "=HYPERLINK(\"http://evil.example/\",\"click\")";
        Assert.Equal("step_up_required", await StaffKit.Err(await sa.Get("/api/v1/admin/audit/export")));
        await StaffKit.StepUp(f, sa, saEmail);
        await sa.Post($"/api/v1/admin/users/{vid}/sign-out-everywhere", new { reason = evil });
        await sa.Post($"/api/v1/admin/users/{vid}/clear-lockout", new { reason = "+cmd|' /C calc'!A0" });
        await sa.Post($"/api/v1/admin/users/{vid}/send-password-reset", new { reason = "@SUM(1+1)" });
        var res = await sa.Get("/api/v1/admin/audit/export?staffOnly=true");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/csv", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.StartsWith("attachment;", res.Content.Headers.ContentDisposition!.ToString());
        var csv = await res.Content.ReadAsStringAsync();
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("\"id\",\"at\",\"type\"", lines[0]);
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil.example/\"\",\"\"click\"\")\"", csv);
        Assert.Contains("\"'+cmd|' /C calc'!A0\"", csv);
        Assert.Contains("\"'@SUM(1+1)\"", csv);
        // no cell may START with a formula character
        foreach (var line in lines.Skip(1))
            foreach (var cell in SplitCsv(line)) Assert.False(cell.Length > 0 && cell[0] is '=' or '+' or '-' or '@' or '\t' or '\r', "unsafe cell: " + cell);
        Assert.Contains(f.WithDb(db => db.AuditEvents.Where(e => e.Type == "admin.audit.exported").ToList()), e => e.UserId == StaffKit.UserId(f, saEmail));
        var support = await StaffKit.LoginNew(f, StaffRole.Support);
        Assert.Equal(HttpStatusCode.Forbidden, (await support.Get("/api/v1/admin/audit/export")).StatusCode);
    }

    private static List<string> SplitCsv(string line)
    {
        var cells = new List<string>(); var cur = new System.Text.StringBuilder(); var q = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (q) { if (ch == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; } else q = false; } else cur.Append(ch); }
            else if (ch == '"') q = true; else if (ch == ',') { cells.Add(cur.ToString()); cur.Clear(); } else cur.Append(ch);
        }
        cells.Add(cur.ToString());
        return cells;
    }

    [Fact]
    public async Task Audit_rows_never_contain_secrets_tokens_codes_or_pins()
    {
        var saEmail = await StaffKit.Create(f, StaffRole.SuperAdmin);
        var sa = await StaffKit.Login(f, saEmail); await StaffKit.StepUp(f, sa, saEmail);
        var inviteeEmail = StaffKit.NewEmail();
        await sa.Post("/api/v1/admin/staff/invitations", new { email = inviteeEmail, role = "Support", reason = StaffKit.Why });
        var token = Uri.UnescapeDataString(System.Text.RegularExpressions.Regex.Match(f.Mails(inviteeEmail, "staff-invite").Last().Text, @"token=([^\s&]+)").Groups[1].Value);
        var all = string.Join("\n", f.WithDb(db => db.AuditEvents.Select(e => (e.Detail ?? "") + (e.Reason ?? "") + (e.Subject ?? "")).ToList()));
        Assert.DoesNotContain(token, all);
        Assert.DoesNotContain(Solvelt.Api.Services.Crypto.Sha256(token), all);
        Assert.DoesNotContain(ApiFactory.Strong, all);
    }
}

public class AdminSettingsTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    private static readonly object[] Original =
    [
        new { key = "sells_food", label = "Do you sell food?", type = "yesno", options = (string[]?)null, parentKey = (string?)null, parentValue = (string?)null, required = true, sortOrder = 1 },
        new { key = "food_types", label = "What kind of food do you sell?", type = "select", options = (string[]?)new[] { "Bakery", "Meat", "Dairy", "Produce", "Packaged goods", "Other" }, parentKey = (string?)"sells_food", parentValue = (string?)"yes", required = true, sortOrder = 2 },
        new { key = "is_kosher", label = "Is your food kosher?", type = "yesno", options = (string[]?)null, parentKey = (string?)"sells_food", parentValue = (string?)"yes", required = true, sortOrder = 3 },
        new { key = "kosher_agency", label = "Kosher certifying agency", type = "select", options = (string[]?)new[] { "OU", "OK", "Star-K", "CRC", "Kof-K", "Local rabbinate", "Other" }, parentKey = (string?)"is_kosher", parentValue = (string?)"yes", required = true, sortOrder = 4 },
        new { key = "kosher_certificate", label = "Kosher certificate (PDF, PNG or JPG, max 5 MB)", type = "file", options = (string[]?)null, parentKey = (string?)"is_kosher", parentValue = (string?)"yes", required = true, sortOrder = 5 },
    ];

    [Fact]
    public async Task Settings_page_shows_values_and_feature_flags_without_secrets()
    {
        var ro = await StaffKit.LoginNew(f, StaffRole.ReadOnly);
        var text = await (await ro.Get("/api/v1/admin/settings")).Content.ReadAsStringAsync();
        var d = JsonDocument.Parse(text).RootElement;
        Assert.Equal(1000, d.GetProperty("marketplace").GetProperty("feeBasisPoints").GetInt32());
        Assert.Equal(10m, d.GetProperty("marketplace").GetProperty("feePercent").GetDecimal());
        Assert.Equal("2026-01", d.GetProperty("legal").GetProperty("termsVersion").GetString());
        Assert.Equal("Testing", d.GetProperty("featureFlags").GetProperty("Environment").GetString());
        foreach (var secret in new[] { ApiFactory.AdminPassword, OrdersFactory.InSecret, OrdersFactory.OutSecret, "test-hmac-secret", "1x0000000000000000000000000000000AA" }) Assert.DoesNotContain(secret, text);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Put("/api/v1/admin/settings/fee", new { feeBasisPoints = 500, reason = StaffKit.Why })).StatusCode);
    }

    [Fact]
    public async Task Marketplace_fee_change_needs_super_admin_step_up_reason_and_applies_to_new_orders()
    {
        var shop = await Orders.NewShop(f);
        var before = await Orders.Place(shop, price: 10000, qty: 1);
        Assert.Equal(1000, f.WithDb(db => db.Orders.Single(o => o.Id == before.OrderId).FeeCents));
        var saEmail = await StaffKit.Create(f, StaffRole.SuperAdmin);
        var sa = await StaffKit.Login(f, saEmail);
        Assert.Equal("step_up_required", await StaffKit.Err(await sa.Put("/api/v1/admin/settings/fee", new { feeBasisPoints = 1500, reason = StaffKit.Why })));
        await StaffKit.StepUp(f, sa, saEmail);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await sa.Put("/api/v1/admin/settings/fee", new { feeBasisPoints = 1500 })).StatusCode);
        foreach (var bad in new[] { -1, 5001 }) Assert.Equal(HttpStatusCode.BadRequest, (await sa.Put("/api/v1/admin/settings/fee", new { feeBasisPoints = bad, reason = StaffKit.Why })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sa.Put("/api/v1/admin/settings/fee", new { feeBasisPoints = 1500, reason = "Board decision 2026-11" })).StatusCode);
        var after = await Orders.Place(shop, price: 10000, qty: 1);
        Assert.Equal(1500, f.WithDb(db => db.Orders.Single(o => o.Id == after.OrderId).FeeCents));
        Assert.Equal(1000, f.WithDb(db => db.Orders.Single(o => o.Id == before.OrderId).FeeCents)); // existing orders keep their fee
        var audit = f.WithDb(db => db.AuditEvents.Single(e => e.Type == "admin.settings.fee_changed"));
        Assert.Contains("1000", audit.Detail); Assert.Contains("1500", audit.Detail); Assert.Equal("Board decision 2026-11", audit.Reason);
        await sa.Put("/api/v1/admin/settings/fee", new { feeBasisPoints = 1000, reason = "Restore default for the rest of the tests" });
    }

    [Fact]
    public async Task Terms_and_seller_agreement_version_bump_applies_to_new_acceptances()
    {
        var existing = new Api(f); await existing.SignupVerified();
        var saEmail = await StaffKit.Create(f, StaffRole.SuperAdmin);
        var sa = await StaffKit.Login(f, saEmail); await StaffKit.StepUp(f, sa, saEmail);
        Assert.Equal(HttpStatusCode.BadRequest, (await sa.Put("/api/v1/admin/settings/legal", new { termsVersion = "bad version!", reason = StaffKit.Why })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sa.Put("/api/v1/admin/settings/legal", new { reason = StaffKit.Why })).StatusCode);
        Assert.Equal("unchanged", await StaffKit.Err(await sa.Put("/api/v1/admin/settings/legal", new { termsVersion = "2026-01", reason = StaffKit.Why })));
        Assert.Equal(HttpStatusCode.OK, (await sa.Put("/api/v1/admin/settings/legal", new { termsVersion = "2026-07", sellerAgreementVersion = "2026-07", reason = "New arbitration clause" })).StatusCode);
        var cfg = await new Api(f).JsonBody(await new Api(f).Get("/api/v1/public-config", false));
        Assert.Equal("2026-07", cfg.GetProperty("termsVersion").GetString()); Assert.Equal("2026-07", cfg.GetProperty("sellerAgreementVersion").GetString());
        var fresh = new Api(f); await fresh.SignupVerified();
        var app = await fresh.JsonBody(await fresh.Get("/api/v1/seller/application"));
        Assert.Equal("2026-07", app.GetProperty("legal").GetProperty("termsVersion").GetString());
        await fresh.CompleteApplicationAndSubmit();
        Assert.Equal("2026-07", (await fresh.JsonBody(await fresh.Get("/api/v1/seller/application"))).GetProperty("legal").GetProperty("sellerAgreementVersion").GetString());
        var old = await existing.JsonBody(await existing.Get("/api/v1/seller/application"));
        Assert.Equal("2026-01", old.GetProperty("legal").GetProperty("termsVersion").GetString()); // not forced: documented in docs/ADMIN.md
        var ro = await StaffKit.LoginNew(f, StaffRole.ReadOnly);
        Assert.True((await ro.JsonBody(await ro.Get("/api/v1/admin/settings"))).GetProperty("legal").GetProperty("sellersOnOlderTerms").GetInt32() >= 1);
    }

    [Fact]
    public async Task Seller_field_editor_validates_previews_branching_and_changes_the_wizard()
    {
        var saEmail = await StaffKit.Create(f, StaffRole.SuperAdmin);
        var sa = await StaffKit.Login(f, saEmail); await StaffKit.StepUp(f, sa, saEmail);
        var current = await sa.JsonBody(await sa.Get("/api/v1/admin/settings/seller-fields"));
        Assert.Equal(5, current.GetProperty("fields").GetArrayLength());

        object F(string key, string type = "text", string? parent = null, string? pv = null, int order = 10, string[]? options = null) =>
            new { key, label = "L " + key, type, options, parentKey = parent, parentValue = pv, required = true, sortOrder = order };
        async Task<HttpResponseMessage> Put(params object[] extra) => await sa.Put("/api/v1/admin/settings/seller-fields", new { fields = Original.Concat(extra), reason = "Compliance request" });
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(F("BAD KEY"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(F("sells_food"))).StatusCode);                                   // duplicate key
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(F("a1", "bogus"))).StatusCode);                                 // unknown type
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(F("a2", "select"))).StatusCode);                                // select needs options
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(F("a3", parent: "nope", pv: "yes"))).StatusCode);              // unknown parent
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(F("a4", parent: "kosher_certificate", pv: "yes"))).StatusCode); // file parents cannot branch
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(F("a5", parent: "sells_food", pv: "maybe"))).StatusCode);       // yes/no value
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(F("a6", parent: "sells_food", pv: "yes", order: 1))).StatusCode); // parent must come earlier
        Assert.Equal(HttpStatusCode.BadRequest, (await sa.Put("/api/v1/admin/settings/seller-fields", new { fields = Original.Take(3), reason = StaffKit.Why })).StatusCode); // certificate field must stay
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await sa.Put("/api/v1/admin/settings/seller-fields", new { fields = Original })).StatusCode);

        var withNew = Original.Concat([F("business_license", "text", "sells_food", "no", 6)]).ToArray();
        var preview = await sa.JsonBody(await sa.Post("/api/v1/admin/settings/seller-fields/preview", new { fields = withNew, answers = new Dictionary<string, string> { ["sells_food"] = "no" } }));
        var vis = preview.GetProperty("fields").EnumerateArray().ToDictionary(x => x.GetProperty("key").GetString()!, x => x.GetProperty("visible").GetBoolean());
        Assert.True(vis["sells_food"]); Assert.True(vis["business_license"]); Assert.False(vis["is_kosher"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await sa.Post("/api/v1/admin/settings/seller-fields/preview", new { fields = new[] { F("x", parent: "y", pv: "yes") } })).StatusCode);

        try
        {
            Assert.Equal(HttpStatusCode.OK, (await Put(F("business_license", "text", "sells_food", "no", 6))).StatusCode);
            var seller = new Api(f); await seller.SignupVerified();
            var wizard = await seller.JsonBody(await seller.Get("/api/v1/seller/field-config"));
            Assert.True(wizard.ValueKind == JsonValueKind.Array, wizard.GetRawText());
            Assert.Contains(wizard.EnumerateArray(), x => x.GetProperty("key").GetString() == "business_license");
            Assert.True((await seller.Put("/api/v1/seller/application/business", Api.ValidBusiness)).IsSuccessStatusCode);
            Assert.True((await seller.Put("/api/v1/seller/application/kosher", new { answers = new Dictionary<string, string> { ["sells_food"] = "no" } })).IsSuccessStatusCode);
            var submit = await seller.Post("/api/v1/seller/application/submit", new { acceptSellerAgreement = true });
            Assert.Equal(HttpStatusCode.BadRequest, submit.StatusCode);
            Assert.Contains("business_license", await submit.Content.ReadAsStringAsync());
            Assert.True((await seller.Put("/api/v1/seller/application/kosher", new { answers = new Dictionary<string, string> { ["sells_food"] = "no", ["business_license"] = "L-123" } })).IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.OK, (await seller.Post("/api/v1/seller/application/submit", new { acceptSellerAgreement = true })).StatusCode);
            var audit = f.WithDb(db => db.AuditEvents.OrderByDescending(e => e.Id).First(e => e.Type == "admin.settings.seller_fields_changed"));
            Assert.Contains("business_license", audit.Detail); Assert.Equal("Compliance request", audit.Reason);
        }
        finally { Assert.Equal(HttpStatusCode.OK, (await sa.Put("/api/v1/admin/settings/seller-fields", new { fields = Original, reason = "Restore the default wizard" })).StatusCode); }
        Assert.Equal(5, (await sa.JsonBody(await sa.Get("/api/v1/admin/settings/seller-fields"))).GetProperty("fields").GetArrayLength());
    }
}
