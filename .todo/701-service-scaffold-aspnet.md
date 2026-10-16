---
id: 701
title: Service scaffold (ASP.NET Core minimal API)
phase: 7
bucket: post-v1
depends_on: []
blocks: [703, 704, 705, 707]
status: todo
area: service
---

# 701 — Service scaffold (ASP.NET Core minimal API)

## Objective

`GamePolish.Service` exists as a running ASP.NET Core minimal API on `net10.0` serving the hostname
`gamepolish.app`: it answers a health probe, exposes route groups that match the endpoint tables in
`auth-accounts.md` (the four `/auth` routes public, every `/sync` and `/user` route behind bearer
authorization), validates Supabase-issued JWTs with shared-secret middleware, publishes an OpenAPI
document the desktop client can generate a typed contract from, and logs with secret redaction. At the
end of this task the authenticated routes return `401` to anonymous callers and `501 Not Implemented`
to valid ones — real handlers land in 703–708.

## Context

This is the first task of **phase 7, the `post-v1` bucket**. The bucket's ordering rule is explicit in
the roadmap: the account system exists to gate a *paid storage tier*, and that tier is only worth
building once the desktop app has users — **do not start this bucket while the `v1.0` bucket still has
open work**. Before writing a line, confirm that every task in the `mvp`, `v0.2` and `v1.0` buckets is
`status: done` (read `.todo/README.md` and each task's frontmatter; check `git log` for the code those
tasks should have produced). If any of them is open, stop and say so rather than starting here.

This task depends on nothing inside phase 7 and blocks 703, 704, 705 and 707 — every later task in the
bucket talks to this API, so the route groups and the auth middleware must be right before anything
else is built on them.

`gamepolish.app` is the API base URL consumed by the desktop app and the console SDK
(`../game-polish-notes/docs/auth-accounts.md`, "Domains"). `gamepolish.com` is a separate surface
(task 706) sharing the same Supabase project and JWT secret. The repo is greenfield-by-docs: check
`git status --short` and the tree before claiming any project exists.

## Scope

- Create the project with the CLI — `dotnet new web -n GamePolish.Service -o GamePolish.Service`, then `dotnet sln add`; no hand-written `.csproj`. Target `net10.0` per `global.json`; reference neither `GamePolish.Desktop` nor Avalonia.
- Configuration and secrets: `appsettings.json` carries non-secret defaults only; local development uses `dotnet user-secrets`;
  production uses environment variables; nothing credential shaped is ever committed. Declared keys: `SUPABASE_URL`, `SUPABASE_ANON_KEY`,
  `SUPABASE_SERVICE_ROLE_KEY`, `SUPABASE_JWT_SECRET`, `ASPNETCORE_URLS`, plus the Stripe keys 713 needs (declared, unused).
- A public `GET /health` (liveness: `{ status, version, time }`) and a readiness probe reporting whether the Supabase project is reachable.
- Route groups matching the notes' tables exactly. Public: `POST /auth/token`, `POST /auth/refresh`, `GET /auth/google`,
  `POST /auth/google/exchange`. Authenticated: `GET /sync/snapshots`, `POST /sync/upload-url`, `POST /sync/download-url`,
  `PUT /sync/meta`, `GET /user/me`, `GET /user/devices`, `DELETE /user/devices/:id`, `DELETE /user/account`, `GET /user/quota`.
  Handlers are stubs returning `501`; behaviour arrives per task.
- Bearer-token middleware: JWT validation against the Supabase project (see 702 for the secret) with `iss`/`aud`/`exp` and 30s clock
  skew, plus a default-deny fallback policy so a new route is authenticated unless explicitly marked public.
- OpenAPI generation (`Microsoft.AspNetCore.OpenApi` plus build-time document emission) with the document checked in, so a typed
  client (Kiota or Refitter) can be generated without a running server.
- Structured logging with a secret-redacting enricher, and no request-body logging on `/auth/*`.
- A written deploy-target decision (host, TLS termination, process supervision) recorded in `../game-polish-notes/docs/ci-release.md`, which owns CI/release/deployment facts.

## Out of scope

- Any real auth behaviour (703, 704), the database (702), devices (705), the website (706), S3 (707),
  sync (708), billing (713), the console SDK (714).
- DNS, TLS certificate issuance and CDN configuration for `gamepolish.app`.
- Account/rate-limit policy for the auth endpoints: those land with the handlers in 703 (the notes do
  not specify rate limits; do not invent a policy here, raise it as a question).
- Customer-facing API versioning beyond serving the document under a fixed path.

## Implementation notes

- **Invariant 7 is the reason this task exists.** The service derives the acting user from the
  validated token's subject claim and never from a request body, query string, or header. A client
  may send `user_id`, `device_id`, `plan` or `storage_used_bytes`; the service ignores all of them.
  Row-level security in Postgres (702) is the second, independent enforcement point — the service
  queries through a user-scoped client so RLS applies even if a handler forgets a filter.
- Two Supabase keys with different trust levels: the anon key plus the caller's JWT for user-scoped
  reads and writes (RLS applies), and the service-role key only for elevated work (account deletion in
  705, the Stripe webhook in 713). The service-role key must never be returned to a client or logged.
- Tokens minted at `gamepolish.com` must validate at `gamepolish.app`; that is what the shared JWT
  secret buys (see 702). If the Supabase project is configured with asymmetric signing keys instead of
  the legacy shared secret, the middleware must validate against the project's JWKS endpoint — check
  the project's Auth settings and report which path applies rather than assuming HS256.
- Errors use `application/problem+json` (RFC 9457) with `type`, `title`, `detail`, so the desktop
  client can render the `detail` verbatim and branch on `type`.
- Redaction is enforced by a log enricher, not by discipline: `Authorization`, `password`,
  `refresh_token`, `access_token`, `secret`, `access_key` and the Stripe keys are masked at the sink.
  Add a test that logs a fake bearer token and asserts it does not appear in captured output.
- External accounts may not exist. Check before asking: `nslookup gamepolish.app` (does the hostname
  resolve?), `gh auth status`, and whether any hosting account or Supabase project already exists.
  If a hostname, hosting account or Supabase project is missing, **ask the user** — never invent a
  project reference, a subscription, or credentials.

## Acceptance criteria

- `dotnet build` from the repo root compiles `GamePolish.Service` with 0 warnings and 0 errors.
- `GET /health` returns `200` with JSON and requires no authorization.
- Every `/sync/*` and `/user/*` route returns `401` with a problem+json body when called without a
  token, and none of them is reachable anonymously.
- A test enumerates the app's endpoint data source and asserts each `/sync` and `/user` endpoint
  carries a bearer security requirement, while each `/auth` endpoint does not — so a later task cannot
  accidentally add an unauthenticated sync route.
- The OpenAPI document is produced at build time, is committed, and lists all four `/auth` routes and
  every `/sync` and `/user` route.
- The redaction test passes, and no startup diagnostic prints a key or secret value.
- `git status --short` shows no secret material; the repository contains no keys, tokens or connection
  strings (user-secrets live under `%APPDATA%\Microsoft\UserSecrets`, outside the repo).
- The deploy target is written into `ci-release.md`, or the task states that the decision is blocked on
  a user answer about hosting.

## Verification

```bash
dotnet restore
dotnet build GamePolish.Service -c Release          # expect 0 warnings, 0 errors
dotnet test                                          # paste real pass/fail counts
dotnet run --project GamePolish.Service
```

```bash
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:5099/health          # 200
curl -s -i http://localhost:5099/user/me | head -5                             # 401 problem+json
curl -s -i http://localhost:5099/sync/snapshots | head -5                      # 401 problem+json
dotnet user-secrets list --project GamePolish.Service                          # keys present; never paste values
```

A `200` on `/health`, `401` on every authenticated route, and an endpoint-metadata test that passes
are the proof. If no Supabase project exists yet (702 not done), the "valid token returns 501" check
cannot be run — report it as blocked on 702 rather than fabricating a token.

## References

- `../game-polish-notes/docs/auth-accounts.md` — "Domains", "API (gamepolish.app)", "Security
  Considerations".
- `../game-polish-notes/docs/ci-release.md` — release/CI contract the deploy decision must extend.
- `.todo/000-CONVENTIONS.md` — invariant 7, secrets-in-keychain rule, definition of done, evidence
  rules.
- Tasks: 702 (Supabase project and schema), 703 (auth: email and password), 704 (Google OAuth PKCE),
  705 (devices), 707 (S3 credentials), 713 (billing webhook, which needs its own unauthenticated but
  signature-verified route).
