---
id: 704
title: "Auth: Google OAuth with PKCE"
phase: 7
bucket: post-v1
depends_on: [703]
blocks: []
status: todo
area: desktop
---

# 704 — Auth: Google OAuth with PKCE

## Objective

A user can sign in with Google from the desktop app: the app generates a PKCE verifier and challenge, gets the Google URL from the service, opens it in the **system browser**, captures the `gamepolish://auth/callback?code=…` redirect through the OS URI-activation handler, exchanges the code with the verifier, and stores the resulting tokens in the OS keychain — with the same account later reachable by password sign-in when the same email is used.

## Context

Phase 7 is the `post-v1` bucket, gated on the desktop app having users: the account system exists to gate a *paid storage tier*. **Do not start this bucket while the `v1.0` bucket still has open work**; confirm the earlier buckets are `status: done` before beginning.

This task depends on 703 (the token model, keychain storage and the problem+json error shapes) and shares the notes' "Desktop App Auth Flow / Google OAuth (in-app, PKCE)" steps 1–8. The custom URI scheme work is platform work: Windows MSIX manifest, macOS `Info.plist`, plus Avalonia's activation lifecycle. Two external things must be verified before building: a Google Cloud Console OAuth client, and the Supabase project's provider configuration (702). If no OAuth client exists, **ask the user** to create it (or authorise creating it) — never invent a client id or secret, and never commit either.

## Scope

- PKCE generation per step 2: a high-entropy verifier (32 random bytes, base64url, RFC 7636 length rules) and its S256 challenge, plus a `state` value bound to the pending verifier. Reject any callback whose `state` does not match a pending attempt — an addition beyond the notes' text, recorded as such, because it costs nothing and closes drive-by-login.
- `GET https://gamepolish.app/auth/google?code_challenge=<challenge>`: returns a Google OAuth URL that carries `redirect_uri=gamepolish://auth/callback`.
- Opening the URL in the **system browser** — never an embedded WebView. The notes' rationale stands verbatim and must be preserved in the code comment and the design record: it "avoids credential phishing concerns and keeps Google happy with OAuth policies". On Windows launch via the shell; on macOS via `open`.
- Capturing the redirect: a URI activation handler for the `gamepolish` scheme, including the cold-start case where the app is not running and the OS launches it with the activation argument (macOS `Info.plist` `CFBundleURLSchemes: ["gamepolish"]` surfaced through Avalonia's `IApplicationLifetime` URI activation event; Windows `Extensions > Protocol` in the MSIX app manifest). Handle warm activation into the running instance without opening a second window.
- `POST https://gamepolish.app/auth/google/exchange` with the code and the verifier, returning the same token shape as `/auth/token` (703), stored in the keychain by the same code path.
- Google Cloud Console registration: the redirect URI allowed for the client must be exactly the one the flow uses, and nothing else, so an open redirect cannot be constructed. Register the Supabase callback URL that Google actually validates, and allow-list `gamepolish://auth/callback` in the Supabase project's redirect URLs. The notes describe only the latter as registered with Google; if the live console disagrees with the notes, report the discrepancy with a proposed correction rather than silently picking one.
- Identity linking: with identity linking enabled, a Google-created account and a later password sign-up for the same email resolve to one `profiles` row. Verify it, and report what actually happened.
- A 5-minute timeout with a Cancel action on the waiting state, so a closed browser tab cannot leave the app waiting forever.

## Out of scope

- The website's Google sign-in button (706) and the console's identity flow (714/715).
- Any second OAuth provider, account-linking UI to merge two *different* emails, or unlinking an identity — the notes say nothing about them.
- Registering the URI scheme by writing to the registry at runtime. Invariant 4 forbids registry writes: the scheme ships in the MSIX manifest. A dev-only run that needs the scheme registered needs the user's explicit consent and a documented, disposable registration (task 213's harness style), not app behaviour.
- Token refresh mechanics (703) and device registration on sign-in (705) beyond calling them.

## Implementation notes

- The verifier never leaves the machine except in the exchange call; it must not be written to app-settings.json, logs, or the S3 snapshot (invariant: secrets live in the keychain). Keep it in memory keyed by `state`, or in the keychain if the app can be restarted mid-flow.
- Only one pending attempt at a time per app instance; a second click invalidates the first rather than stacking.
- The exchange is a code-for-token call made by the service; the service never receives the verifier before the exchange, so an intercepted code is useless without it (the point of PKCE).
- Cold start matters: on macOS the OS launches the app with the URL; on Windows MSIX the OS activates the running instance or starts one. Test both paths — a warm instance must not spawn a duplicate window per activation.
- Failure modes to handle explicitly and test: user closes the browser (timeout/cancel), state mismatch, code already used, verifier lost (restart mid-flow), network failure during exchange, and clock skew producing an invalid `expires_at` comparison.
- Desktop verification runs on Windows and macOS; macOS-specific activation cannot be proven on this Windows host — say so and require a Mac or CI to confirm it rather than claiming success.
- The URL returned by the service is used immediately and never cached; the only state held between the request and the callback is the state/verifier pair.
- Windows specifics: the protocol extension lives in the MSIX manifest, so an unpackaged dev build receives no callback at all. Document that symptom so it is not mistaken for a code bug, and never register the scheme from application code (invariant 4).
- Cold-start ordering: on a cold start the activation can arrive before the shell (and its keychain access) is ready. Buffer the activation argument, then run the exchange once the app is initialised.
- Sign-in is never a precondition for anything else: the Library, Discover and Backups views work signed out, and the Google button is reachable only from the Cloud Backup settings section.

## Acceptance criteria

- The Google sign-in button completes the flow against a real OAuth client in a test project and the tokens land in the keychain (verified by a keychain listing, not by a log line).
- The authorization page opens in the real system browser: the codebase contains no WebView control on the auth path, and the sign-in page visibly shows the browser's own chrome.
- Cold-start activation works: with the app closed, pasting a real callback URL into a browser launches the app and completes the exchange; with the app running, it completes without a second window.
- A callback with a mismatched `state`, a stale verifier, or a reused code fails cleanly, stores nothing, and shows a recoverable error.
- The redirect allow-list is provably tight: an authorization request carrying a different `redirect_uri` is refused by Google/Supabase.
- The verifier, code and tokens appear nowhere in logs, app-settings.json or the data root.
- Same-email linking: a Google account then a password account for the same address both resolve to one `profiles.id` (one row), proven by querying the project.
- macOS activation is either verified on a Mac/CI or reported as unverified with the reason.
- Exactly one callback is registered per platform, and a callback that arrives while a different account is signed in is rejected rather than switching that session.
- After a cancelled or failed Google sign-in the app is fully functional signed out: Library, Discover and Backups are unaffected.
- The waiting state is cancellable and expires after five minutes with a clear message; no orphaned pending attempt survives a cancel or a restart.
- The Google client secret never reaches the desktop process: the client only ever holds a URL and a code, and the client id appears in logs at most as a prefix.

## Verification

```bash
# Windows: confirm the scheme is registered by the installed package, then exercise the cold-start path
start "" "gamepolish://auth/callback?code=test&state=test"      # app must receive the activation and reject the bogus code
# macOS (on a Mac or CI):
open "gamepolish://auth/callback?code=test&state=test"
```

```bash
grep -ri -e code_verifier -e access_token -e refresh_token ~/.game-polish/   # expect no output
```

Then run `dotnet test` (swallowing the state/verifier failure cases) and paste the counts, plus the real sign-in transcript: URL opened, browser shown, callback received, tokens stored, and a subsequent authenticated API call returning 200. Name the exact test OAuth client id used by prefix (e.g. `<id>.apps.googleusercontent.com`) — never paste a secret.

## References

- `../game-polish-notes/docs/auth-accounts.md` — "Sign-Up & Login Options" (Google OAuth), "Desktop App Auth Flow" (Google OAuth in-app, steps 1–8), "Custom URI Scheme Registration", "Security Considerations".
- `.todo/000-CONVENTIONS.md` — invariant 4 (no registry writes), secrets in the keychain, definition of done.
- Tasks: 703 (token model and keychain), 705 (device registration), 706 (website's Google button), 213 (local harness style for dev-only scheme registration).
