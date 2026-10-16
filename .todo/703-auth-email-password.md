---
id: 703
title: "Auth: email and password"
phase: 7
bucket: post-v1
depends_on: [701, 702]
blocks: [704, 705, 706]
status: todo
area: service
---

# 703 — Auth: email and password

## Objective

Email/password accounts work end to end: `POST /auth/token` proxies Supabase `signInWithPassword` and returns `{ access_token, refresh_token, expires_at }`; `POST /auth/refresh` rotates the refresh token; email confirmation is required before an account is active; a password reset email link restores access; the 12-character minimum holds in both places it is enforceable; and on the desktop side tokens live in the OS keychain, are attached as a bearer header only to `gamepolish.app`, and are silently refreshed on launch — with a "Session expired — sign in again" prompt when the refresh token is dead.

## Context

Phase 7 is the `post-v1` bucket: the account system exists to gate a *paid storage tier*, and that tier is only worth building once the desktop app has users — **do not start this bucket while the `v1.0` bucket still has open work**. Confirm the `mvp`, `v0.2` and `v1.0` tasks are `status: done` before beginning.

This task depends on 701 (API scaffold, configuration, `/auth` route group, middleware) and 702 (the Supabase project, the `profiles` row trigger, the JWT secret, the password policy). It blocks 704 (Google PKCE reuses the token model), 705 (device registration happens on sign-in) and 706 (the website's account pages mirror these rules). The notes' "Token Lifecycle" block is the contract: access token one hour, refresh token 30 days rotated on each use, stored in the OS keychain, **never** in `app-settings.json`, logs or S3 snapshots.

The external Supabase project must exist (702). A test account is needed for verification: ask the user whether one exists, or create a disposable one in the project — never use a real user's credentials and never invent keys.

## Scope

- `POST /auth/token` — accepts `{ email, password }`, calls Supabase `signInWithPassword` with the anon key, and returns `{ access_token, refresh_token, expires_at }`. An unconfirmed email is a distinguishable error, not a generic 401.
- `POST /auth/refresh` — accepts the refresh token, exchanges it for a new access token and a **rotated** refresh token, and returns the same shape. Reuse of an already-rotated token must fail visibly.
- Email confirmation before activation: sign-up happens on `gamepolish.com` (706) against Supabase directly; this task ensures the service refuses to treat unconfirmed users as active and that the confirmation link lands on the website's `/verify-email` page.
- Password reset: the flow starts at `gamepolish.com/reset-password` (706) using Supabase's own reset-by-email call; the desktop app links out to that page instead of adding an endpoint the notes do not list. Adding a fourth auth route requires a notes change first — raise it, do not ship it.
- The 12-character minimum: enforced in the Supabase password policy/hook (702) and client-side in the website form (706). The desktop app has no sign-up form, so state plainly that its role is only to surface Supabase's rejection.
- Desktop token storage: reuse the keychain abstraction that task 504 built for the GitHub PAT — one secret store, not a second mechanism. Windows Credential Manager on Windows, Keychain on macOS; if the platform store is unavailable, fail loudly rather than falling back to a file.
- Bearer attachment: one delegating HTTP handler attaches `Authorization: Bearer <access token>` to every `gamepolish.app` request and to nothing else — never to `github.com` or any other host.
- Silent refresh on launch before the first API call, single-flight so two concurrent calls cannot rotate the same refresh token twice (rotation-on-use makes a double refresh a self-inflicted logout).
- The "Session expired — sign in again" prompt in App Settings → Cloud Backup → Game Polish Cloud when the refresh token is expired or revoked.

## Out of scope

- Google OAuth and PKCE (704), device registration rows and revocation (705), the website's forms and pages (706), billing and plan gating (713).
- An in-app sign-up form or an in-app password reset: the notes place both on the website.
- Multi-factor authentication, passkeys, sessions management beyond device revocation — no notes exist; raise them as questions if asked.
- Account deletion (wired in 705, which owns `/user/account`).

## Implementation notes

- **Invariant 7.** The service never trusts a client claim: a token request may not specify a user id, and every later request derives the user from the validated token subject. Supabase is the only authority on credentials.
- Token bodies are never logged, and `/auth/*` request bodies are excluded from request logging at the middleware level (701 sets this up; this task proves it). Errors return problem+json with a `type` the client can branch on (`auth/invalid_credentials`, `auth/email_not_confirmed`, `auth/refresh_expired`).
- Refresh semantics: Supabase rotates on every use, so persist the new refresh token atomically before returning success. If persistence fails, the user is logged out at next launch — treat that as a real failure and report it, not an invisible one.
- Keychain identity: one entry per (app, account) pair, so signing out of one account does not orphan another's token. A sign-out clears tokens **and** the cached device registration.
- Rate limiting and lockout for `/auth/token` should be settled here with the user; the notes specify none, so propose per-IP and per-email limits and record the answer.
- Desktop verification of "not in app-settings.json" is a grep of the data root after a sign-in, plus a test that the settings serializer has no token field.
- Weak-password handling is Supabase's message passed through unchanged where it is user-facing — do not invent local wording the notes do not contain.
- Access tokens live in memory only; the refresh token is the only credential at rest, and only in the keychain. No temporary plaintext file on either platform, not even briefly.
- Clock handling: refresh proactively about 60 seconds before `expires_at` rather than reacting to a 401 after the fact, and keep a small margin for skew.
- Offline behaviour: with no network the app keeps using the cached, still-valid access token; once it expires the app shows the signed-out state — and **never blocks the core mod-management features on it**, because no account is required for those (the notes' opening promise).
- The service keeps no session state of its own between requests: Supabase is the sole authority for every token check, so restarting the service cannot invalidate or resurrect a session.
- Record in the report whether the password hook or only the Auth policy rejected a short password, since 702 may have found the hook unavailable on the current plan.

## Acceptance criteria

- A correct email/password pair returns all three fields from `/auth/token`; a wrong password returns 401 problem+json; an unconfirmed email returns a distinguishable `auth/email_not_confirmed`.
- `/auth/refresh` returns a new access token and a *different* refresh token; replaying the old refresh token fails.
- A 12-character password is accepted at sign-up and an 11-character one rejected — proving both the client check and the Supabase policy.
- After a sign-in, the keychain entry exists (Windows: `cmdkey /list`; macOS: `security find-generic-password -s <service>`), and the data root contains no token: `grep -ri -e access_token -e refresh_token ~/.game-polish/` returns nothing.
- **Tokens are never written to `app-settings.json`, never written to logs, and never included in S3 snapshots.** A test asserts the settings file's field set, a log-capture test asserts no token appears, and (once 708 exists) a snapshot manifest listing contains no token material.
- The bearer header is attached to `gamepolish.app` calls and absent from requests to other hosts (asserted by a test handler or a local proxy capture).
- With a live refresh token the app launches without prompting; with a revoked one it shows "Session expired — sign in again" in the Cloud Backup settings section.
- Two concurrent API calls on an expired access token cause exactly one refresh (single-flight), leaving the session usable.
- Signing out clears the keychain entry and the cached device registration; the next launch shows the signed-out state.
- **No account is required for the core mod-management features**: with no stored token the app never prompts on launch and never blocks the Library, Discover or Backups views — the promise the notes open with survives everything built here.
- Revoking the session in the Supabase dashboard and relaunching the app produces the "Session expired — sign in again" prompt in the Cloud Backup settings section.
- Restarting `GamePolish.Service` does not invalidate a valid access token (no server-side session state to lose).

## Verification

```bash
curl -s -X POST https://gamepolish.app/auth/token -H 'content-type: application/json' \
  -d '{"email":"<test account from the user>","password":"<test password>"}' | jq '{token: (.access_token|length), refresh: (.refresh_token|length), expires_at}'
curl -s -o /dev/null -w '%{http_code}\n' -X POST https://gamepolish.app/auth/refresh \
  -H 'content-type: application/json' -d '{"refresh_token":"<the rotated-away token>"}'   # expect 401
```

Then run `dotnet test` with the keychain, redaction and single-flight tests, and paste the counts. On the desktop, sign in, `grep -ri -e access_token -e refresh_token ~/.game-polish/` (expect no output), inspect the keychain entry, revoke the session from the Supabase dashboard, relaunch, and paste the prompt text you see.

## References

- `../game-polish-notes/docs/auth-accounts.md` — "Sign-Up & Login Options", "Desktop App Auth Flow", "Token Lifecycle", "API (gamepolish.app)", "Security Considerations".
- `.todo/000-CONVENTIONS.md` — invariants 4 and 7, secrets in the OS keychain, definition of done.
- Tasks: 701, 702, 704, 705, 706, 708 (snapshot contents), 713.
