You are worker N (marketplace contract), continuing on the Solvelt seller portal backend: https://github.com/Yanky1/Yanky1-test-login-backend. This brief is an approved assignment (see CLAUDE.md). Never end your turn to ask: decide, record each decision in docs/DECISIONS.md, and keep going until the work is built, tested, green, merged and pushed.

## C12 follow-up: provider display names must not leak sign-in addresses (from the orchestrator's review of N7, main f482df0)
`ServiceOptions.PublicName` (src/Solvelt.Api/Bookings/ServiceOptions.cs:17-24) derives the public `displayName` from the provider's sign-in email. With a one-token local part, it returns the whole local part: `jsmith@…` becomes "Jsmith", and `johnsmith1985@…` becomes "Johnsmith". This is published through `GET /services/{id}/options` and on the buyer site, so it exposes most of a sign-in address and makes the address guessable. The same helper is used for driver tracking, per M42; check that too.

**Fix:**
- Never publish more than the first name and last initial, and only when they can be identified reliably from at least two alphabetic tokens (e.g. `john.smith@`, giving "John S.").
- Otherwise publish a neutral label that doesn't come from the email, e.g. "Provider" plus the provider's ordinal within the service, "Provider 1", "Provider 2". Choose one and record it in DECISIONS.
- Apply the same rule to every place that derives a public name from an email: options, driver tracking, and any buyer-facing event.
- Optional, if it's cheap and needs no new migration: let the team member set a public display name in the portal, and prefer it over the derived one. Otherwise, add this to the ROADMAP.

**Tests:**
- `jsmith@x` and `johnsmith1985@x` never expose their local part;
- `john.smith@x` gives "John S.";
- no email substring longer than the first name ever appears in the options JSON.
- Revert the fix and confirm the tests fail.

## Rules
- Fold this into whichever N PR comes next (N8 or N10/N11), or use its own small PR.
- Respect the merge queue.
- Never skip, delete or weaken a test.
- Don't touch other repositories.
