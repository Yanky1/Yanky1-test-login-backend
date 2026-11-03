using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Solvelt.Api.Tests;

public class WizardTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF");

    private static HttpRequestMessage Upload(string name, byte[] data, string? contentType = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "application/octet-stream");
        form.Add(file, "file", name);
        return new HttpRequestMessage(HttpMethod.Post, "/api/v1/seller/application/kosher/certificate") { Content = form };
    }

    [Fact]
    public async Task Signup_validates_email_terms_and_password_rules()
    {
        var c = new Api(f);
        async Task<HttpStatusCode> Try(object body) => (await c.Post("/api/v1/auth/signup", body, false)).StatusCode;
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new { email = "nope", password = ApiFactory.Strong, acceptTerms = true }));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new { email = Api.NewEmail(), password = ApiFactory.Strong, acceptTerms = false }));
        foreach (var weak in new[] { "Short1!a", "alllowercase1!x", "ALLUPPERCASE1!X", "NoNumbers!!abc", "NoSymbols12345Ab", "Password1!", "Qwerty123!" })
            Assert.Equal(HttpStatusCode.BadRequest, await Try(new { email = Api.NewEmail(), password = weak, acceptTerms = true }));
        Assert.Equal(HttpStatusCode.Accepted, await Try(new { email = Api.NewEmail(), password = ApiFactory.Strong, acceptTerms = true }));
    }

    [Fact]
    public async Task Signup_records_terms_version()
    {
        var api = new Api(f);
        await api.SignupVerified();
        var app = await api.JsonBody(await api.Get("/api/v1/seller/application"));
        Assert.Equal("2026-01", app.GetProperty("legal").GetProperty("termsVersion").GetString());
        Assert.NotEqual(JsonValueKind.Null, app.GetProperty("legal").GetProperty("termsAcceptedAt").ValueKind);
    }

    [Fact]
    public async Task Signup_gives_no_session_and_unverified_accounts_cannot_sign_in()
    {
        var email = Api.NewEmail();
        var c = new Api(f);
        var res = await c.Post("/api/v1/auth/signup", new { email, password = ApiFactory.Strong, acceptTerms = true }, false);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.DoesNotContain("accessToken", await res.Content.ReadAsStringAsync());
        Assert.False(res.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.Post("/api/v1/auth/login", new { email, password = ApiFactory.Strong }, false)).StatusCode);
    }

    [Fact]
    public async Task Email_code_expires_after_10_minutes_and_resend_has_60_second_cooldown()
    {
        var api = new Api(f);
        var email = Api.NewEmail();
        Assert.Equal(HttpStatusCode.Accepted, (await api.Post("/api/v1/auth/signup", new { email, password = ApiFactory.Strong, acceptTerms = true }, false)).StatusCode);
        Assert.Single(f.Mails(email, "verify-code"));
        // immediate resend: same 202 answer, but no second mail inside the 60 s cool-down
        Assert.Equal(HttpStatusCode.Accepted, (await api.Post("/api/v1/auth/resend-code", new { email }, false)).StatusCode);
        Assert.Single(f.Mails(email, "verify-code"));
        f.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.Accepted, (await api.Post("/api/v1/auth/resend-code", new { email }, false)).StatusCode);
        Assert.Equal(2, f.Mails(email, "verify-code").Count);
        var second = f.LastCode(email);
        f.Time.Advance(TimeSpan.FromMinutes(11)); // past the 10 minute validity
        var exp = await api.Post("/api/v1/auth/verify-email", new { email, code = second }, false);
        Assert.Equal(HttpStatusCode.BadRequest, exp.StatusCode);
        f.Time.Advance(TimeSpan.FromSeconds(61));
        await api.Post("/api/v1/auth/resend-code", new { email }, false);
        var ok = await api.Post("/api/v1/auth/verify-email", new { email, code = f.LastCode(email) }, false);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(email, (await ok.Content.ReadFromJsonAsync<Login>(Api.Json))!.Email);
    }

    [Fact]
    public async Task Email_code_attempts_are_limited()
    {
        var api = new Api(f);
        var email = Api.NewEmail();
        await api.Post("/api/v1/auth/signup", new { email, password = ApiFactory.Strong, acceptTerms = true }, false);
        var real = f.LastCode(email);
        var wrong = real == "000000" ? "111111" : "000000";
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.BadRequest, (await api.Post("/api/v1/auth/verify-email", new { email, code = wrong }, false)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post("/api/v1/auth/verify-email", new { email, code = real }, false)).StatusCode); // dead after 5 attempts
    }

    [Fact]
    public async Task Business_step_saves_draft_validates_and_records_sms_consent()
    {
        var api = new Api(f);
        await api.SignupVerified();
        var bad = await api.Put("/api/v1/seller/application/business", new { vendorName = "", contactName = "x", contactPhone = "abc", addressLine1 = "a", city = "c", postalCode = "1", country = "USA" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        // opt-in unchecked by default => no consent stored
        var ok = await api.Put("/api/v1/seller/application/business", Api.ValidBusiness);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.False(f.WithDb(db => db.Sellers.First(s => s.Id == api.Session!.SellerId).SmsOptIn));
        // opt-in stores timestamp + wording version
        var withSms = new { vendorName = "Acme Co", contactName = "Dana", contactPhone = "+1 555 123 4567", addressLine1 = "1 Main", city = "NYC", postalCode = "10001", country = "us", smsOptIn = true };
        Assert.Equal(HttpStatusCode.OK, (await api.Put("/api/v1/seller/application/business", withSms)).StatusCode);
        var s = f.WithDb(db => db.Sellers.First(x => x.Id == api.Session!.SellerId));
        Assert.True(s.SmsOptIn); Assert.NotNull(s.SmsConsentAt); Assert.Equal("2026-01", s.SmsConsentWordingVersion);
        Assert.Equal("US", s.Country);
        // draft is persisted server-side: a fresh login sees it
        var again = new Api(f);
        await again.LoginAs(api.Session!.Email, ApiFactory.Strong);
        var app = await again.JsonBody(await again.Get("/api/v1/seller/application"));
        Assert.Equal("Acme Co", app.GetProperty("business").GetProperty("vendorName").GetString());
        Assert.True(app.GetProperty("draftStep").GetInt32() >= 4);
    }

    [Fact]
    public async Task Kosher_branching_clears_hidden_answers_on_the_server()
    {
        var api = new Api(f);
        await api.SignupVerified();
        var full = new Dictionary<string, string?> { ["sells_food"] = "yes", ["food_types"] = "Bakery", ["is_kosher"] = "yes", ["kosher_agency"] = "OU" };
        Assert.Equal(HttpStatusCode.OK, (await api.Put("/api/v1/seller/application/kosher", new { answers = full })).StatusCode);
        // upload a certificate
        Assert.Equal(HttpStatusCode.OK, (await api.Send(Upload("cert.pdf", Pdf))).StatusCode);
        // Kosher? No -> agency + certificate are removed
        var step2 = new Dictionary<string, string?> { ["is_kosher"] = "no" };
        var r = await api.JsonBody(await api.Put("/api/v1/seller/application/kosher", new { answers = step2 }));
        var answers = r.GetProperty("kosher").GetProperty("answers");
        Assert.False(answers.TryGetProperty("kosher_agency", out _));
        Assert.Equal(JsonValueKind.Null, r.GetProperty("kosher").GetProperty("certificate").ValueKind);
        Assert.Empty(f.WithDb(db => db.SellerFiles.Where(x => x.SellerId == api.Session!.SellerId).ToList()));
        // Sells food? No -> everything else is cleared, even answers sent in the same request
        var sneaky = new Dictionary<string, string?> { ["sells_food"] = "no", ["food_types"] = "Meat", ["is_kosher"] = "yes", ["kosher_agency"] = "OK" };
        var r2 = await api.JsonBody(await api.Put("/api/v1/seller/application/kosher", new { answers = sneaky }));
        var a2 = r2.GetProperty("kosher").GetProperty("answers");
        Assert.Equal(new[] { "sells_food" }, a2.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task Field_config_endpoint_exposes_seeded_branching_rules()
    {
        var api = new Api(f);
        await api.SignupVerified();
        var cfg = await api.JsonBody(await api.Get("/api/v1/seller/field-config"));
        var keys = cfg.EnumerateArray().Select(x => x.GetProperty("key").GetString()).ToArray();
        Assert.Contains("sells_food", keys); Assert.Contains("kosher_certificate", keys);
        var agency = cfg.EnumerateArray().First(x => x.GetProperty("key").GetString() == "kosher_agency");
        Assert.Equal("is_kosher", agency.GetProperty("parentKey").GetString());
        Assert.Equal("yes", agency.GetProperty("parentValue").GetString());
    }

    [Fact]
    public async Task Certificate_upload_validates_type_size_and_content_and_is_owner_only()
    {
        var owner = new Api(f);
        await owner.SignupVerified();
        // not allowed before the answers make it visible
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.Send(Upload("c.pdf", Pdf))).StatusCode);
        await owner.Put("/api/v1/seller/application/kosher", new { answers = new Dictionary<string, string?> { ["sells_food"] = "yes", ["is_kosher"] = "yes" } });
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.Send(Upload("c.exe", Pdf))).StatusCode);                    // bad extension
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.Send(Upload("c.pdf", Encoding.ASCII.GetBytes("<html>x</html>")))).StatusCode); // sniff fails
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.Send(Upload("c.png", Pdf))).StatusCode);                    // ext/content mismatch
        var big = new byte[5 * 1024 * 1024 + 10]; Array.Copy(Pdf, big, Pdf.Length);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.Send(Upload("big.pdf", big))).StatusCode);                  // too large
        var ok = await owner.Send(Upload("../../evil.pdf", Pdf, "text/html"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var meta = await owner.JsonBody(ok);
        Assert.Equal("application/pdf", meta.GetProperty("contentType").GetString());
        Assert.Equal("evil.pdf", meta.GetProperty("name").GetString());
        // owner can download; headers are safe
        var dl = await owner.Get("/api/v1/seller/application/kosher/certificate");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Equal("application/pdf", dl.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", dl.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(Pdf, await dl.Content.ReadAsByteArrayAsync());
        // anonymous and another seller get nothing
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Get("/api/v1/seller/application/kosher/certificate", false)).StatusCode);
        var other = new Api(f);
        await other.SignupVerified();
        Assert.Equal(HttpStatusCode.NotFound, (await other.Get("/api/v1/seller/application/kosher/certificate")).StatusCode);
    }

    [Fact]
    public async Task Submit_requires_agreement_and_complete_answers_then_stores_agreement_and_sends_email()
    {
        var api = new Api(f);
        var email = await api.SignupVerified();
        await api.Put("/api/v1/seller/application/business", Api.ValidBusiness);
        await api.Put("/api/v1/seller/application/kosher", new { answers = new Dictionary<string, string?> { ["sells_food"] = "yes", ["is_kosher"] = "yes" } });
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post("/api/v1/seller/application/submit", new { acceptSellerAgreement = false })).StatusCode);
        var incomplete = await api.Post("/api/v1/seller/application/submit", new { acceptSellerAgreement = true });
        Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode); // food_types, agency and certificate still required
        await api.Put("/api/v1/seller/application/kosher", new { answers = new Dictionary<string, string?> { ["sells_food"] = "no" } });
        var ok = await api.JsonBody(await api.Post("/api/v1/seller/application/submit", new { acceptSellerAgreement = true }));
        Assert.Equal("PendingReview", ok.GetProperty("status").GetString());
        Assert.Equal("2026-01", ok.GetProperty("legal").GetProperty("sellerAgreementVersion").GetString());
        Assert.NotEqual(JsonValueKind.Null, ok.GetProperty("legal").GetProperty("sellerAgreementAcceptedAt").ValueKind);
        Assert.Single(f.Mails(email, "application-received"));
        // submitted application is read-only
        Assert.Equal(HttpStatusCode.Conflict, (await api.Put("/api/v1/seller/application/business", Api.ValidBusiness)).StatusCode);
    }

    [Fact]
    public async Task Admin_status_changes_are_audited_send_emails_and_follow_the_state_machine()
    {
        var api = new Api(f);
        var email = await api.SignupVerified();
        await api.CompleteApplicationAndSubmit();
        var id = api.Session!.SellerId;
        var admin = new Api(f);
        await admin.LoginAs(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var url = $"/api/v1/admin/sellers/{id}/status";
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Post(url, new { status = "Suspended" })).StatusCode);   // PendingReview -> Suspended illegal
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Post(url, new { status = "Nonsense" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.Post(url, new { status = "NeedsInfo", note = "Please upload your license." })).StatusCode);
        Assert.Contains("Please upload your license.", f.Mails(email, "needs-info").Single().Text);
        // seller can edit and resubmit in NeedsInfo
        Assert.Equal(HttpStatusCode.OK, (await api.Put("/api/v1/seller/application/business", Api.ValidBusiness)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Post("/api/v1/seller/application/submit", new { acceptSellerAgreement = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.Post(url, new { status = "Approved" })).StatusCode);
        Assert.Single(f.Mails(email, "approved"));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Post(url, new { status = "Live" })).StatusCode);   // Live was removed as a step
        Assert.Equal(HttpStatusCode.OK, (await admin.Post(url, new { status = "Suspended" })).StatusCode);
        Assert.True(f.WithDb(db => db.AuditEvents.Count(a => a.Type == "seller.status_changed") >= 5));
        var rej = new Api(f);
        var email2 = await rej.SignupVerified();
        await rej.CompleteApplicationAndSubmit();
        await admin.Post($"/api/v1/admin/sellers/{rej.Session!.SellerId}/status", new { status = "Rejected" });
        Assert.Single(f.Mails(email2, "rejected"));
    }

    [Fact]
    public async Task Admin_endpoint_is_policy_protected()
    {
        var seller = new Api(f);
        await seller.SignupVerified();
        var url = $"/api/v1/admin/sellers/{Guid.NewGuid()}/status";
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Post(url, new { status = "Approved" }, false)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await seller.Post(url, new { status = "Approved" })).StatusCode);
    }

    [Fact]
    public async Task Public_config_and_application_expose_marketplace_url()
    {
        var api = new Api(f);
        await api.SignupVerified();
        var cfg = await api.JsonBody(await api.Get("/api/v1/public-config", false));
        Assert.StartsWith("http", cfg.GetProperty("marketplaceUrl").GetString());
        Assert.Contains("text messages", cfg.GetProperty("smsWording").GetString());
    }
}
