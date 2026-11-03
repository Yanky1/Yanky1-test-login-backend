# Agent workstreams

Coordination table for the AI agents working on this repo and on
[test-login-frontend](https://github.com/Yanky1/test-login-frontend).
Each agent updates its own row (docs-only commit). Do not start work in an area another active workstream owns.

| Workstream | Orchestrator | Branch | Areas/files it owns | Status | Last updated |
|---|---|---|---|---|---|
| A. UI/UX redesign | account 2 | `feature/redesign` (frontend; backend only for email templates) | Frontend `src/**`, `e2e/**`, `e2e-ui/**`, `docs/DESIGN.md`, `docs/UX-AUDIT.md`, `docs/screenshots/`; backend `src/Solvelt.Api/Email/Templates.cs` | In progress (base: frontend main 3094fa3) | 2026-11-03 |
| B. Seller products | account 2 | `feature/products` (both repos) | Backend product/category entities, `/api/v1/seller/products`, `/seller/questions`, `/categories`, `docs/PRODUCTS.md`; frontend product/catalog/questions pages and seller app shell | Done and merged: backend (ff13142) and frontend (ff8c2f6); CI and E2E green on both mains. Brief: docs/agent-briefs/B-products.md | 2026-11-03 |
| C. Phone (IVR) + SMS + AI | account 2 | `feature/phone-sms` (backend; frontend Settings cards after A merges) | Backend `src/Solvelt.Api/Phone/**`, `/api/v1/channels/**`, phone entities/options, `docs/PHONE-SMS.md`; frontend Phone number / Phone PIN Settings cards | Done: backend (7c88a62 and earlier) + frontend cards and /l page (3bc8a5f) merged, review rounds 1-2 fixed, all workflows green | 2026-11-03 |
| D. Team members, roles & brands | account 2 | `feature/teams` (both repos; frontend phase after A merges) | Backend memberships, invitations, roles/permissions, brands, account switching (`/api/v1/session/account`), seller-endpoint authorization, `docs/TEAMS.md`; frontend account picker/switcher, Team, Brands and invite pages | Backend phase 1 done on `feature/teams` (memberships, roles, brands, invitations, switching, docs/TEAMS.md); frontend phase waits for A | 2026-11-03 |
| E. Seller orders + buyer messaging | account 2 | `feature/orders` (both repos) | Backend orders/shipments/returns/refunds, buyer conversations/messages, internal marketplace API `/api/internal/v1/orders`, outbox, `docs/ORDERS.md`; frontend Orders list/detail, Messages inbox, order dashboard, notification settings | In progress (started early by user decision: backend first; frontend after B's frontend merges). Brief: docs/agent-briefs/E-orders.md | 2026-11-03 |

Rules: feature branches only; `git fetch origin main && git rebase origin/main` often; keep ONE EF migration per
branch (regenerate on top of main if main gained migrations); fast-forward-merge into main only when every workflow
is green; never force-push main or revert others' commits.
