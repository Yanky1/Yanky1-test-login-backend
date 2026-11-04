# Transactional email style (Figma restyle, workstream J)

All emails come from one shell: `src/Solvelt.Api/Email/EmailLayout.cs` (`Html` + `Text`). `Templates`, `TeamTemplates` and `PhoneTemplates` only supply the words; they must not build their own HTML.

- **Client safety:** table layout, inline styles only (no `<style>`, no web-font requests), `bgcolor` attributes next to every background, a solid `#11362e` banner fallback for Outlook behind the gradient, a bulletproof button (a coloured table cell around an `<a>`), a hidden preheader.
- **Fonts:** body `'Plus Jakarta Sans', Arial, sans-serif`; headings `'Playfair Display', Georgia, serif` (clients without the web fonts fall back).
- **Colours:** page `#f4f6f4`, card white with 1px `#e2eae4` border and 16px radius, banner `#18483e → #11362e → #0a241e` with a gold "S" tile, "Solvelt ✦ MARKET" wordmark, heading `#0d231e` 26px, body `#40564e` 16/24, button `#1f4d40` + white text, code block `#f4f7f5` with 32px monospaced digits, footer text `#5b7369`, links `#22634d`. Contrast pairs are listed in the frontend `docs/DESIGN.md`.
- **Footer:** "Enquiries: <support address>" from `Phone:HelpContact` (copied to `EmailLayout.SupportEmail` at start-up; default `support@example.com`) plus the wordmark. No social icons: no real URLs are configured.
- **Safety:** every dynamic value (heading, paragraphs, button label and URL, code, support address) is HTML-encoded in the shell; the plain-text part is not encoded and always carries the link and the enquiries line.
- **Tests:** `tests/Solvelt.Api.Tests/EmailStyleTests.cs` (shell colours, bulletproof button, code block, encoding, support address) and the existing `SecurityTests.Email_templates_have_html_and_text_and_encode_input`.
- **For a designer:** replace the text wordmark with a hosted vector/PNG logo once one exists (needs an absolute image URL and alt text).
