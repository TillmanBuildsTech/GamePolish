---
id: 705
title: Device registration and management
phase: 7
bucket: post-v1
depends_on: [703]
blocks: [706, 713]
status: todo
area: service
---

# 705 — Device registration and management

## Objective

Devices are real records: the desktop app registers itself on first successful sign-in with a name taken from the machine hostname and refreshes a `last_seen` heartbeat; `GET /user/devices` and `DELETE /user/devices/:id` back the website dashboard's device list and Revoke buttons (706); `GET /user/me` returns the current profile and plan; and session revocation has honest, tested semantics — a revoked device stops being able to use the API while other devices keep working.

## Context

Phase 7 is the `post-v1` bucket: the account system exists to gate a *paid storage tier*, and that tier is only worth building once the desktop app has users — **do not start this bucket while the `v1.0` bucket still has open work**. Confirm the earlier buckets are `status: done` first.

This task depends on 703 (a signed-in user with tokens in the keychain) and on the `devices` table plus its RLS policies from 702. It blocks 706 (the dashboard's DEVICES list and DANGER ZONE) and 713 (the plan comes from `/user/me`, and quota is per account, not per device). The notes' dashboard sketch is the user-visible contract: device name, "Last seen: Today", "Last seen: Oct 12", and a [Revoke] action per row.

Signing in requires the Supabase project (702). If no test account exists, ask the user for one rather than creating users in a production project.

## Scope

- `POST /user/devices` — register or refresh the calling machine: `name` comes from the machine hostname, `last_seen` is set to now, and the device id is returned. Re-registering the same machine updates the existing row instead of appending a duplicate.
- `GET /user/devices` — the list backing the dashboard: id, name, `last_seen`, `created_at`, scoped to the caller only (service filter **and** RLS).
- `DELETE /user/devices/:id` — revoke: the row is removed and the device can no longer use the API. Deleting the row alone does not invalidate a Supabase refresh token; implement the revocation properly (see implementation notes) or report exactly what remains possible.
- `GET /user/me` — current profile and plan. `plan` is returned as-is (`'free'` for everyone today) so clients are ready, with **no gating logic anywhere**: gating is 713's job and the reserved column stays reserved until then.
- `DELETE /user/account` — delete the account and all its data: Supabase rows cascade, and the account-deletion path triggers the S3 prefix deletion the notes describe (a Supabase Edge Function). The Edge Function can only work once storage credentials exist (707), so wire it now and say plainly that the bucket is not emptied until 707 lands.
- Desktop side: register on first successful sign-in (703), heartbeat `last_seen` on app open and at most hourly while running, and show the app's own device name in Cloud Backup settings.
- A migration adding the (user, name) uniqueness guarantee that keeps registration idempotent, recorded as a notes delta (see 702's implementation notes).

## Out of scope

- The dashboard UI and its routes (706); this task owns the API they call.
- Plan gating, quotas and Stripe (713).
- Device **rename** and per-device "sign out everywhere": the notes' dashboard has only Revoke. Note the gap; adding rename means a notes change first.
- Console device identity (714/715) — platform identity is a different authentication path.
- Notifying the user by email when a device is revoked — not in the notes.

## Implementation notes

- **Invariant 7.** The device list is filtered by the token's subject on the service side as well as by RLS. A client may pass any `user_id` it likes; it changes nothing.
- Revocation semantics, stated honestly. Supabase sessions are per-user, not per-device: deleting a `devices` row does not by itself stop the device's access token from working until it expires, nor the refresh token from minting new ones. The correct implementation stamps a device claim (the device id) into the token when `/auth/token` or `/auth/google/exchange` issues it (703), so the service can reject any request whose device id is revoked, and additionally calls Supabase's admin sign-out for that user when the user asks to revoke *all* devices. Per-device revocation with a user-wide session API is impossible — if that is what the dashboard implies, report it as a notes gap instead of shipping a Revoke button that does not revoke.
- **Honesty requirement about the hostname:** on Windows the machine hostname is not the username. Take the name from the machine name (`Environment.MachineName`), not from `USERNAME`, `USER`, `$HOME`, or any user-shaped variable; sanitise it for length and characters; and never imply the name identifies a person. If the hostname is unusable, the app must still register (a fallback name plus the OS-reported device class) and say so in the UI rather than failing the sign-in.
- Heartbeats are cheap and idempotent: one write per app open, then at most hourly. Do not heartbeat per API call — that turns a session into write traffic.
- Account deletion is destructive and irreversible: require an explicit confirmation token in the request, and log the deletion event (ids only, no personal data) so support can confirm it happened. No backup snapshot of a deleted account is kept in S3 — deletion means deletion; say so in the task report.
- The heartbeat updates one row by device id; `created_at` never changes, so the dashboard can show how long a device has been registered as well as when it was last seen.
- A heartbeat from a revoked device must NOT resurrect the session: the app treats the 401 as revocation, returns to the signed-out state and shows the "Session expired — sign in again" prompt instead of retrying forever.
- Sanitise the name before storing it (trim, collapse whitespace, cap length) and store it as plain text; the dashboard escapes it on render (706). Never put markup or control characters in the name field.
- Signing in on a new machine must not revoke the old ones: the notes define no automatic device cap, so do not invent one. Revocation is always the user's explicit action.

## Acceptance criteria

- Two devices registered for one user appear in `GET /user/devices`; a second user's token returns only that user's rows (the same two-user test as 702, now through the API).
- Re-registering the same hostname updates the existing row: the row count does not grow across three registrations.
- After `DELETE /user/devices/:id`, the revoked device's subsequent API call fails with 401 and a *new* access token cannot be minted from its refresh token; the other device still works. If one of those cannot be achieved with the current session model, the report says exactly which and why.
- `GET /user/me` returns the profile with `plan: "free"`, and no code path in this task reads `plan` to gate anything.
- The registered name matches the machine hostname (`hostname` output) and does not equal the logged-in username.
- Registering with a name that repeats for another user is isolated by RLS (proven by the two-user test), not by application logic alone.
- `DELETE /user/account` removes the profile and device rows, and the report states whether the S3 prefix was emptied (yes only if 707 has landed) — no claim of a clean bucket before that.
- The heartbeat cadence is observable: opening the app updates `last_seen` once, and several API calls inside the same minute do not produce additional updates.
- On macOS the registered name matches the machine's `hostname` output, again not the account name.
- The strings the dashboard renders from this data are the notes' own — "Last seen: Today" for a device seen today, otherwise "Last seen: Oct 12" — 706 renders them; this task supplies the timestamps that make them true.
- With no stored token at all, `/user/me` and the device endpoints are simply unreachable (401) and the app degrades to its signed-out state without blocking anything else.

## Verification

```bash
hostname                                   # must equal the name the app registered
curl -s -H "Authorization: Bearer $A" https://gamepolish.app/user/devices | jq '. | length'
curl -s -H "Authorization: Bearer $B" https://gamepolish.app/user/devices | jq '. | length'   # only B's rows
curl -s -X DELETE -H "Authorization: Bearer $A" https://gamepolish.app/user/devices/<B device id>   # 403/404, never a deletion
curl -s -o /dev/null -w '%{http_code}\n' -H "Authorization: Bearer $A" https://gamepolish.app/user/me   # 200
```

Then re-run the same call with the revoked device's token (expect 401), paste the `devices` rows from the project, and run `dotnet test` for the registration-idempotency and heartbeat tests. Report the account-deletion test result separately, naming what it did and did not delete.

## References

- `../game-polish-notes/docs/auth-accounts.md` — "/dashboard" (device list sketch, DANGER ZONE), "API (gamepolish.app)" (Account endpoints), "Supabase Schema", "Security Considerations" (account deletion and the S3 Edge Function).
- `.todo/000-CONVENTIONS.md` — invariants 4 and 7, secrets in the keychain, evidence rules.
- Tasks: 702 (table, RLS, uniqueness migration), 703 (token issuance, keychain), 706 (dashboard UI), 707 (the S3 credentials the deletion function needs), 713 (plan).
