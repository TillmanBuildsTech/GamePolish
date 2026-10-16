---
id: 713
title: Managed cloud tier and billing
phase: 7
bucket: post-v1
depends_on: [705, 708]
blocks: []
status: todo
area: service
---

# 713 — Managed cloud tier and billing

## Objective

The managed **Game Polish Cloud** offering exists — no credentials to manage, a monthly fee — which is the thing the entire account system was built to gate: `profiles.plan` decides entitlement, Stripe handles billing with a webhook that sets the plan, `GamePolish.Service` enforces the gate server-side and never trusts the client, all uploads and downloads go through presigned URLs scoped to the user's own prefix so raw storage keys never reach the client, and `GET /user/quota` reports storage used against the plan's limit. The reserved columns stop being reserved in this task and in no earlier one.

## Context

Phase 7 is the `post-v1` bucket, and its ordering rule is the reason this task is last but one: the account system exists to gate a paid storage tier, and **that tier is only worth building once the desktop app has users**. Do not start this bucket while the `v1.0` bucket still has open work — and do not launch this offering just because the code compiles. The notes' marker stands until the product has an established user base: "> **Future:** A managed \"Game Polish Cloud\" tier (no credentials to manage, monthly fee) is planned once the product has an established user base. The account system and backend are designed to support it; it is not active yet."

This task depends on 705 (`plan` travels through `/user/me`, and account deletion must cancel billing) and 708 (the snapshot layout the managed tier stores on the user's behalf, and the client-side sync that will point at presigned URLs instead of a bucket). 711 applies unchanged: managed storage must not be able to read an encrypted snapshot either.

External prerequisites: a Stripe account in test mode first, a product and a monthly price, and a webhook secret. **Check before asking** whether Stripe is already set up; if it is not, ask the user to create it or authorise creating it. Never invent a price id, a webhook secret, or a live key, and never paste a secret into a report.

## Scope

- Plan gating through `profiles.plan`: `'free'` (default, no managed storage) and `'pro'` (the paid tier), with the gate enforced **only** in `GamePolish.Service`. The desktop client and website may display the plan; neither may decide entitlement.
- Stripe as the billing provider: a Checkout session to subscribe, the Billing Portal for card changes and cancellation, a product plus a monthly price, and `stripe_customer_id` written on first successful checkout.
- A webhook endpoint (`POST /billing/webhook`) that verifies the `Stripe-Signature` header against `STRIPE_WEBHOOK_SECRET`, handles `checkout.session.completed`, `customer.subscription.created`, `customer.subscription.updated`, `customer.subscription.deleted` and `invoice.payment_failed`, and sets `plan` accordingly. It is the one route outside the bearer group (701) that is nevertheless not open: signature verification is its authentication.
- Webhook idempotency: events are retried, so processed event ids must be recorded (a new table created by a migration in 702's mechanism, reported as a notes delta) and a replay must be a no-op. Without it, a retry can double-apply a plan change.
- Presigned URLs: `/sync/upload-url`, `/sync/download-url`, `/sync/snapshots` and `/sync/meta` serve the managed tier, issuing short-lived (about 15 minutes), single-purpose URLs. The service validates the requested object key server-side against the caller's own prefix before signing — no other user's prefix, no traversal, no absolute keys.
- Prefix layout for managed storage: one prefix per user (for example `game-polish/<user_id>/…`) holding the same snapshot structure 708 defines, so a user can migrate between BYO and managed without changing the client's format. The client never sees a bucket name, region or raw key.
- `GET /user/quota`: storage used versus limit, with `storage_used_bytes` (the reserved column, now live) maintained after each managed sync. Free plan: no managed storage, so the endpoints return a clear payment-required error; pro plan: the quota-bound limit.
- The launch switch: the whole offering sits behind a configuration flag (`ManagedCloud:Enabled`, default off) and a plan check, so enabling it is a deliberate operational act rather than a side effect of deploying. Write the launch checklist (test mode → live mode, webhook URL, price created, flag flipped, rollback = flag off) into `../game-polish-notes/docs/ci-release.md`.

## Out of scope

- The `/pricing` page and the dashboard's Plan section on `gamepolish.com` — the notes mark both as Future; they belong to the launch of this offering, and this task must not quietly ship them without the launch decision.
- Invoicing, tax handling, coupons, seat-based or per-GB metering, annual plans, free trials, and refund flows beyond Stripe's own portal.
- Migrating a BYO bucket's contents into managed storage automatically, and cross-provider sync.
- Storage-side lifecycle rules, object-versioning policy, and per-user bucket provisioning if the implementation instead chooses one shared bucket — record the choice either way.
- Any client-side gating: adding a plan check to the desktop app or website is out of scope and wrong (invariant 7).

## Implementation notes

- **Server-side authority.** The service reads `plan` from the database per request (or from a short-lived cache it owns), never from a token claim the client could influence, and never from a request body. A client that calls a managed endpoint with a free plan gets a refusal it cannot talk its way out of.
- The webhook is the only writer of `plan` and `stripe_customer_id`. Any other write path (an admin script, a client call) is a bug. Verify the Stripe-Signature on the raw request body before parsing it, and reject anything unsigned or stale.
- Ordering: Stripe may deliver `subscription.updated` before `checkout.session.completed`. Make the handler tolerant of out-of-order events by treating the subscription object as the source of truth for plan state and the customer id as the linking key, rather than assuming a sequence.
- Presigned URL hygiene: sign one method for one key, with a short expiry, and log which key was signed (never the signature). A presigned `PUT` must not permit overwriting `meta.json` out of order — that route stays a service-handled call (`PUT /sync/meta`) so the service can validate the payload.
- Quota accounting: `storage_used_bytes` is maintained on the managed path only. Compute it from the objects the service just accepted, and reconcile periodically against a prefix listing; the cost of the listing is why reconciliation is periodic and not per request.
- Billing lifecycle on account deletion (705): cancellation and customer deletion must be part of the delete-account flow, or the user keeps being charged after the account is gone. Say plainly what the flow does in what order.
- The BYO S3 path must remain entirely independent: no plan check, no account requirement, no service calls (the notes promise no account is required for cloud backup). Managed storage is an alternative, never a replacement.
- Reserved-column discipline: before this task, `plan`, `storage_used_bytes` and `stripe_customer_id` were reserved. This task is the one that un-reserves them; it must not change their names or types, and if it adds anything it goes in a migration with a notes delta.

## Acceptance criteria

- With `ManagedCloud:Enabled=true` in a test environment: a test-mode Checkout completes, the webhook fires (via `stripe listen`/`stripe trigger`), `plan` becomes `'pro'` and `stripe_customer_id` is set — with the row pasted.
- A replayed webhook event changes nothing (idempotency), and an unsigned or wrongly signed request is rejected with 400 and no state change.
- Presigned URL scoping: a URL issued for one user cannot write into another user's prefix; a hand-crafted request for a foreign key is refused before signing; an expired URL is refused by the bucket.
- `GET /user/quota` returns used bytes that match an independent listing of the account's prefix (paste both numbers), and refuses (payment required) on the free plan before any managed write can happen.
- Plan gating is server-side: flipping a row from `free` to `pro` through the webhook enables managed sync with no client change, and flipping it back refuses it with nothing else modified.
- The client never receives a bucket name, region, access key, secret or raw key: verified by inspecting responses and client logs.
- Account deletion cancels the subscription and deletes the Stripe customer, in that order, with the evidence pasted.
- The BYO S3 path still works with no account: signed out, a backup syncs to a user's own bucket unchanged.
- The Future markers on the `/pricing` page and the Plan section remain until the launch decision is made explicitly; the report states whether the flag is on anywhere in production (expected: no).

## Verification

```bash
stripe listen --forward-to localhost:5099/billing/webhook        # in one terminal
stripe trigger checkout.session.completed                        # in another
curl -s -H "Authorization: Bearer $PRO" https://gamepolish.app/user/quota | jq
curl -s -H "Authorization: Bearer $FREE" -X POST https://gamepolish.app/sync/upload-url \
  -H 'content-type: application/json' -d '{"key":"game-polish/<other-user-id>/x"}'    # refusal, no signature issued
aws --endpoint-url <managed-endpoint> s3 ls s3://<bucket>/game-polish/<user-id>/ --recursive | head
```

Paste: the profile row before and after the webhook, the duplicate-event result, the refusal responses (foreign prefix, free plan, expired URL), the quota number next to the listing total, and the deletion flow's Stripe state. Report separately whether the offering is actually launched — implementing it is not launching it, and the notes require an established user base first.

## References

- `../game-polish-notes/docs/auth-accounts.md` — "Plan Gating", "Supabase Schema (key tables)" (the reserved columns), "API (gamepolish.app)" (Cloud Backup endpoints, `/user/quota`), "/dashboard" (the Future pricing marker).
- `../game-polish-notes/docs/views/cloud-backup-sync.md` — "Storage Model" (the managed tier Future marker, BYO independence), "Security" (presigned URLs, session token, no raw keys), "Data Requirements".
- `.todo/000-CONVENTIONS.md` — invariant 7, secrets in the keychain, evidence rules.
- Tasks: 701 (route groups and the webhook exception), 702 (the reserved columns and migrations), 705 (plan in `/user/me`, account deletion), 708 (layout and client sync), 711 (encryption must defeat managed access too), 706 (the deferred pricing page).
