---
id: 702
title: Supabase project and schema
phase: 7
bucket: post-v1
depends_on: [701]
blocks: [703]
status: todo
area: service
---

# 702 — Supabase project and schema

## Objective

A Supabase project backs Game Polish: the `profiles` and `devices` tables exist exactly as specified
in the notes — including the three columns explicitly marked reserved — created by migration files
under version control rather than by clicking in a dashboard; row-level security is enabled on both
tables with policies that scope every read *and* every write to the authenticated user's own id; the
shared JWT secret lets tokens issued at `gamepolish.com` validate at `gamepolish.app`; and the
12-character minimum password rule is enforced by the project's auth configuration. The task is not
done until a two-user test proves that one user cannot read or write another's rows.

## Context

Phase 7 is the `post-v1` bucket. Its ordering rule: the account system exists to gate a *paid storage
tier*, and that tier is only worth building once the desktop app has users — **do not start this task
while the `v1.0` bucket still has open work**. Confirm the `v1.0` tasks are `status: done` before
beginning.

This task depends on 701 (the service project and its configuration/key plumbing). Its output is
consumed by 703 (token endpoints), 705 (devices), 706 (the website's account pages, which share this
project) and 713 (the reserved columns, which stay reserved until that task runs).

**A Supabase account and project are external and may not exist.** Check first — look for an existing
project reference in the user's environment, a `.env`-style file outside the repo, or the Supabase
dashboard the user already has. If there is none, ask the user to create the project (or to authorise
creating it) and to supply the project URL, anon key, service-role key and JWT secret through the
approved secret channels. Never invent a project reference, a key, or a JWT secret.

## Scope

- Provision the project (name it `game-polish`, region near the expected users) and link it locally so migrations can be applied and
  inspected from the CLI (`npx supabase --version`, `supabase link`). Record the project reference outside the repo.
- Create both tables exactly as the notes specify:
  - `profiles` — `id` uuid primary key referencing the auth user (cascade on delete); `plan` text not null default `'free'`
    (**reserved**: `'free' | 'pro'`, premium tier not yet active); `storage_used_bytes` bigint not null default 0 (**reserved** for
    future quota tracking); `stripe_customer_id` text (**reserved** for future billing); `created_at` timestamptz default now.
  - `devices` — `id` uuid primary key defaulted to a random uuid; `user_id` uuid not null referencing `profiles(id)` (cascade on
    delete); `name` text not null (e.g. `'DESKTOP-B7X'`); `last_seen` timestamptz not null default now; `created_at` timestamptz not
    null default now.
- A trigger or equivalent database function creating the `profiles` row when an auth user is created, so "one row per
  `auth.users` entry" holds without the client inserting it.
- Migrations under version control (`supabase/migrations/`), applied with the CLI and re-runnable from scratch on a fresh project.
  Dashboard-only schema edits are a bug: fold anything changed while exploring back into a migration and verify the fresh path.
- RLS enabled on both tables with policies scoping reads and writes to the caller: `profiles` matches the caller's own id; `devices`
  matches `user_id` to the caller and rejects inserting or updating a row whose `user_id` is not the caller's.
- The reserved columns carry a comment in the migration in the notes' own words and are written by no code: no plan gating, no quota
  accounting, no Stripe writes until 713.
- Shared JWT secret: configure the project's signing secret for `GamePolish.Service` so tokens issued at `gamepolish.com` are valid
  at `gamepolish.app`. Determine whether the project uses the legacy shared secret or asymmetric keys with a JWKS endpoint, and report
  which — 701's middleware must match.
- Password rule: set the Auth password policy to a 12-character minimum and add the auth hook rejecting shorter passwords if the
  project's plan provides it. If the hook is unavailable on this plan, say so and treat the Auth policy as the enforcement point —
  never claim a hook that does not exist.
- A short written RLS-test procedure in the report, so 705 and 713 can repeat the same proof.

## Out of scope

- Any service handler or endpoint code (701/703/705).
- Plan gating, quota accounting, Stripe objects or webhooks (713) — the reserved columns stay reserved.
- Email template design and the website pages (706); this task only configures the project's URLs so
  the confirmation and reset emails point at `gamepolish.com`.
- Backups/point-in-time recovery policy for the Supabase project — raise it with the user if they want
  one; do not silently pick a paid tier.
- Data migration from anywhere: there is no existing data.

## Implementation notes

- **RLS is the enforcement point, and it must be tested as a user, not as an admin.** The service-role
  key bypasses RLS entirely, so a test that uses it proves nothing. The two-user test must run with
  each user's own access token against a user-scoped client.
- The notes describe a `devices` table with no uniqueness constraint. Task 705 must not create a
  duplicate row every time the same machine re-registers, which needs a uniqueness guarantee on
  (user, name). Add it as a *follow-up migration* in this folder and record the delta in the report so
  the notes can be corrected — do not silently edit the documented table definition in place. Keep the
  documented column list unchanged; only add the constraint.
- Policy completeness: RLS with only a read policy leaves writes open. Add both a read and a write
  policy per table (separate policies for insert/update/delete where the check differs), then verify by
  attempting a write of another user's row.
- Determinism: migrations must apply cleanly in order on an empty database. Test that by resetting a
  local database stack (`supabase db reset` or the documented equivalent) before declaring success.
- Reserved-marker hygiene: whatever comment style you use, write the reserved columns as reserved in
  the same words the notes use, so 713 can find them and nothing in between assumes a live plan.

## Acceptance criteria

- A fresh project applies all migrations in order with no manual steps, and re-running `db reset`
  reproduces the schema.
- `profiles` and `devices` match the notes' column lists exactly; the three reserved columns exist,
  `plan` defaults to `'free'`, `storage_used_bytes` defaults to 0, `stripe_customer_id` is nullable,
  and no code path in this task writes any of them.
- Creating a user produces exactly one `profiles` row.
- **Two-user RLS test passes**: with user A's token, reading `devices` returns only A's rows and
  returns zero rows when filtering on B's row id; updating or deleting B's row affects zero rows;
  inserting a device row with `user_id` = B's id is rejected. B's side of the test returns the mirror
  result. Both runs are pasted into the report.
- The same test against `profiles` shows A cannot read or modify B's profile row.
- A sign-up with an 11-character password is rejected and a 12-character one succeeds.
- The service (701) can validate a token issued for this project — or the task reports exactly which
  piece (asymmetric keys, missing secret) blocks that and what the user must decide.
- Nothing credential shaped is committed: `git status --short` is clean of secrets, and keys appear
  only in the approved secret store.

## Verification

```bash
npx supabase --version
npx supabase link --project-ref <ref from the user>            # ref supplied by the user, never invented
npx supabase migration list                                    # every migration applied
npx supabase db reset                                          # fresh-apply path proves determinism
```

Then run the two-user RLS script twice — once minted with A's token, once with B's — and paste both
outputs, showing zero rows and rejected writes for the other user. Independently, inspect the policy
catalogue in the project's SQL editor (list the policies on `public.profiles` and `public.devices`) and
paste it, so "policies exist" and "cross-user access is denied" are two separate pieces of evidence.
Finally, sign up a throwaway test user with an 11-character password and show the rejection, then with
12 characters and show the confirmation email arriving.

## References

- `../game-polish-notes/docs/auth-accounts.md` — "Domains", "Sign-Up & Login Options", "Supabase Schema
  (key tables)", "Plan Gating", "Security Considerations".
- `.todo/000-CONVENTIONS.md` — invariant 7 (server-side checks), secrets in the OS keychain, evidence
  rules.
- Tasks: 701 (service and config), 703 (token endpoints depend on this), 705 (devices), 706 (website
  shares this project), 713 (the reserved columns come alive only there).
