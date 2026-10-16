---
id: 707
title: BYO S3 credential store
phase: 7
bucket: post-v1
depends_on: [701]
blocks: [708]
status: todo
area: desktop
---

# 707 — BYO S3 credential store

## Objective

App Settings → Cloud Backup → Storage Provider works end to end: the provider preset list, endpoint, bucket, region, access key and secret key fields, a [Test Connection] action that performs a real S3 API call proving the bucket is readable **and** writable under the configured prefix, and [Save] that puts the credentials in the OS keychain and only the non-secret coordinates in `app-settings.json`. Credentials are never written to disk in plaintext and never appear in snapshots or exports.

## Context

Phase 7 is the `post-v1` bucket: the account system exists to gate a *paid storage tier*, and that tier is only worth building once the desktop app has users — **do not start this bucket while the `v1.0` bucket still has open work**. Confirm the earlier buckets are `status: done` before beginning.

This task depends on 701 only for the project's configuration/secrets conventions; the work itself is desktop-side, in App Settings ("Cloud Backup" section) plus an S3 client in `GamePolish.Core`-adjacent code with no UI dependency. It blocks 708: nothing can sync until a real bucket is proven readable and writable.

The verification needs an actual S3-compatible bucket. **Check before asking**: is there an existing bucket and key pair the user wants to use? If not, ask the user — a Backblaze B2 free-tier bucket is the cheapest way to test, and saying so is more useful than leaving them to guess. Never invent an endpoint, bucket name or key, and never commit a credential.

## Scope

- Provider presets with prefilled coordinates, per the notes' list and example values:
  - Backblaze B2 (~$0.006/GB/month — recommended for most users): endpoint `s3.us-west-004.backblazeb2.com`, region `us-west-004`.
  - Cloudflare R2 (no egress fees — good for frequent syncs): endpoint `<account-id>.r2.cloudflarestorage.com`, region `auto`, path-style addressing.
  - AWS S3: `s3.<region>.amazonaws.com`, region as chosen.
  - Wasabi: `s3.<region>.wasabisys.com`.
  - Custom endpoint (MinIO and other S3-compatible services), free-form endpoint and region.
- Fields exactly as the notes' panel shows them: Provider, Endpoint URL, Bucket name, Region, Access Key ID, Secret Key (masked, with a [Show] toggle), then [Test Connection] and [Save], with the success line "✓ Connected · Bucket accessible · 0 bytes used".
- [Test Connection] performs real calls: a prefix listing under `game-polish/` (proving read access), then a put, a get and a delete of a tiny probe object at `game-polish/.probe/<uuid>` (proving write access), leaving the bucket exactly as it was found.
- Credentials go to the OS keychain through the same abstraction task 504 uses for the GitHub PAT — one secret store in the app, not two. The access key id lives there too, since the panel must display it masked back to the user.
- `app-settings.json` carries only non-secret coordinates under a `cloud_backup` key: provider, endpoint, bucket, region, plus the flags and categories 708 defines. No key material, ever.
- The provider field must be able to hold a session-token mode for the future managed tier (713) without a schema change — a value such as `gamepolish-cloud` reserved for that task.
- Credentials are excluded from snapshots (708) and from "Export all data" (task 606) by construction: both writers take an explicit field allow-list, and a test asserts the produced archive contains no key material.

## Out of scope

- Sync, upload/download, incremental snapshots and history (708), encryption (711), the managed tier and presigned URLs (713).
- Creating buckets, setting lifecycle/retention rules or IAM policies on the user's account. Instead, the panel's help text links to a documented minimal policy (list/put/get/delete scoped to `arn:aws:s3:::<bucket>/game-polish/*`, plus list on the bucket).
- Migrating an existing bucket between providers, or syncing one bucket to another.
- Multi-bucket configurations, per-game buckets or versioning settings.

## Implementation notes

- Use the real AWS SDK (`AWSSDK.S3`, v4 line) with an explicit service URL so B2, R2, Wasabi and MinIO all work; R2 and MinIO generally need path-style addressing. Set the addressing style per preset rather than guessing at runtime.
- Prove both directions. A key with only read permission must fail the test with the exact missing operation named (a `PutObject` denial), not with a generic "connection failed". Likewise a missing bucket, a wrong region, wrong keys, a wrong endpoint, and a badly skewed clock are five different failures with five different messages: `NoSuchBucket`, a region/endpoint mismatch, `InvalidAccessKeyId`/`SignatureDoesNotMatch`, a DNS failure naming the endpoint, and `RequestTimeTooSkewed`.
- The probe object is always deleted, including on failure (use a finally-style path), and nothing outside `game-polish/` is ever written — the user's bucket may hold unrelated data, and invariant 4's spirit (touch only what is yours) applies to object storage too.
- "0 bytes used" is a prefix listing sum, not a billing figure. Say "used under this prefix" in the UI and keep it approximate; the exact accounting is 713's quota endpoint.
- Never log the secret key or the full access key id; log the endpoint, bucket and region instead, which is enough to debug. Redaction is inherited from 701's service rules, applied here to the desktop log.
- Failure UX must be actionable per the notes' edge cases: a permissions failure links to the S3 configuration panel and states that the user's existing data in S3 was not touched by the failure.
- Store a `last_test_result` (timestamp plus success/failure reason, no secrets) in `app-settings.json` so the panel can show a stale connection without re-testing on every open.

## Acceptance criteria

- Against a real bucket: [Test Connection] succeeds, shows "✓ Connected · Bucket accessible · 0 bytes used" (or the real byte count under the prefix), and a listing afterwards shows no leftover `.probe/` object.
- The bucket's unrelated objects are untouched by the test (list before and after, compare).
- Wrong secret → "Access key or secret key is incorrect." with no secret in any log; missing bucket → a `NoSuchBucket` message naming the bucket; read-only key → a message naming the missing write permission; wrong endpoint → a connection/DNS message naming the endpoint.
- After [Save], `app-settings.json` contains provider, endpoint, bucket and region and no key material: `grep -i -e access_key -e secret ~/.game-polish/app-settings.json` matches nothing.
- The keychain holds the credential pair (Windows: `cmdkey /list`; macOS: `security find-generic-password -s <service>`), and the panel renders the stored access key id masked.
- "Export all data" produces an archive with no credential material (a test asserts the field allow-list).
- Cloud Backup is fully usable with **no Game Polish account**: this task must not introduce an account requirement, and the settings section must work signed out.

## Verification

```bash
# B2/R2 with rclone or the AWS CLI configured against the same endpoint:
aws --endpoint-url https://s3.us-west-004.backblazeb2.com s3 ls s3://<bucket>/game-polish/ --recursive
# after a test run, this must be empty and the bucket's other keys unchanged
```

```bash
grep -i -e access_key -e secret_key -e aws_secret ~/.game-polish/app-settings.json   # expect no output
grep -ri -e <the access key id prefix> "$LOCALAPPDATA"/GamePolish/logs 2>/dev/null | head   # expect no match
```

Run the panel by hand in `dotnet watch` and paste the five failure messages (wrong key, missing bucket, read-only key, wrong endpoint, skewed clock) plus the success line; state plainly that the UI was verified manually, per the conventions' desktop rule, unless a headless test covers the view models.

## References

- `../game-polish-notes/docs/views/cloud-backup-sync.md` — "Storage Model", "S3 Configuration", "Security", "Data Requirements", "Edge Cases".
- `../game-polish-notes/docs/views/app-settings.md` — the App Settings / Cloud Backup entry point and the keychain rule for the GitHub PAT.
- `.todo/000-CONVENTIONS.md` — invariants 4 and 7, secrets in the OS keychain.
- Tasks: 504 (the keychain abstraction to reuse), 606 (export must exclude credentials), 708 (sync consumes this), 711, 713.
