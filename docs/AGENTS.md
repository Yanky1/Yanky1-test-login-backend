# Agent workstreams

Coordination table for the AI agents working on this repo and on
[test-login-frontend](https://github.com/Yanky1/test-login-frontend).
Each agent updates its own row (docs-only commit). Do not start work in an area another active workstream owns.

| Workstream | Orchestrator | Branch | Areas/files it owns | Status | Last updated |
|---|---|---|---|---|---|
| A. UI/UX redesign | account 2 | `feature/redesign` (frontend; backend only for email templates) | Frontend `src/**`, `e2e/**`, `e2e-ui/**`, `docs/DESIGN.md`, `docs/UX-AUDIT.md`, `docs/screenshots/`; backend `src/Solvelt.Api/Email/Templates.cs` | In progress (base: frontend main 3094fa3) | 2026-11-03 |
| B. Seller products | account 2 | `feature/products` (both repos) | Backend product/category entities, `/api/v1/seller/products`, `/seller/questions`, `/categories`, `docs/PRODUCTS.md`; frontend product/catalog/questions pages and seller app shell | Not started; starts only after A is merged and verified | 2026-11-03 |
| C. Phone (IVR) + SMS + AI | account 2 | `feature/phone-sms` (backend; frontend Settings cards after A merges) | Backend `src/Solvelt.Api/Phone/**`, `/api/v1/channels/**`, phone entities/options, `docs/PHONE-SMS.md`; frontend Phone number / Phone PIN Settings cards | Backend built and tested (144 tests green); docs done; frontend Settings cards pending redesign merge | 2026-11-03 |

Rules: feature branches only; `git fetch origin main && git rebase origin/main` often; keep ONE EF migration per
branch (regenerate on top of main if main gained migrations); fast-forward-merge into main only when every workflow
is green; never force-push main or revert others' commits.
