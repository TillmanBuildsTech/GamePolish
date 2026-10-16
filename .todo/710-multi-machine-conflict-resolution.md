---
id: 710
title: Multi-machine conflict resolution
phase: 7
bucket: post-v1
depends_on: [708]
blocks: []
status: todo
area: service
---

# 710 — Multi-machine conflict resolution

## Objective

When two machines have changed the same thing, the app says so instead of silently picking a winner: config and settings use last-write-wins with per-machine timestamps, surfaced through the conflict banner "Settings were changed on LAPTOP-A since your last sync. Keep yours or pull theirs?"; game saves, being binary, are never merged — both versions are shown with their timestamps, the user chooses explicitly, and the losing version is kept as a local backup.

## Context

Phase 7 is the `post-v1` bucket: the account system exists to gate a *paid storage tier*, and that tier is only worth building once the desktop app has users — **do not start this bucket while the `v1.0` bucket still has open work**. Confirm the earlier buckets are `status: done` before beginning.

This task depends on 708 (the snapshots, `meta.json` and the per-machine stamps it records) and is referenced by 709 (a restore that lands on a machine that already has local changes) and 712 (saves are the case that can never be auto-merged). It exists because BYO S3 has **no server to arbitrate**: there is no central coordinator, so detection has to be built from what each machine records in the bucket, and the resolution has to happen in the client.

Verification needs two machines, or two data roots on one machine with distinct machine identities. Say which you used — a two-root simulation on one PC is a fair test of the logic, but it is not the same evidence as two real hosts, and the report must not blur that.

## Scope

- Detection: compare per-machine timestamps since the **last shared snapshot** — the newest snapshot that both machines have recorded as seen in `meta.json`. A machine's own newer, unseen snapshots do not count as "shared".
- The per-file data that makes detection reliable, recorded by 708 and required here:
  - a stable `machine_id` — a UUID generated once per data root and persisted, never the hostname (hostnames are editable, and clones collide);
  - per file in the snapshot manifest: the path relative to the data root (or the game id), the content hash, the `changed_at` timestamp (ISO-8601 UTC with fractional seconds) and the `machine_id` that changed it;
  - in `meta.json`: the machine registry with each machine's display name and its last seen snapshot.
- Resolution rule for config and settings: if only one machine changed a file since the last shared snapshot, sync it silently; if two or more changed the same file, do not choose — show the banner with the other machine's display name and the two actions, exactly as the notes word it.
- Banner wording preserved verbatim: "Settings were changed on LAPTOP-A since your last sync. Keep yours or pull theirs?"
- "Keep yours": the local version goes up in the next snapshot. "Pull theirs": back up the local version first (invariant 1, into the local backup tree), then apply theirs. In both cases record the resolution so the banner does not return.
- Game saves: **no automatic merge** — saves are binary. Show both versions with timestamps (and sizes and the origin machine), require an explicit choice, and keep the losing version as a local backup that the Backups view can restore.
- Clock-skew guard: if a machine's clock differs from the bucket's time by more than a small tolerance (measure against the S3 response `Date` header), do not fall back to silent last-write-wins on an ambiguous file — surface the conflict and warn about the clock.
- The app-open check from 708's trigger table is what raises conflicts; a sync that would overwrite local changes never happens without the user's decision.

## Out of scope

- Merging the *contents* of a changed file: no three-way diffs, no line-level merges of INI/JSON, no CRDTs. The notes specify last-write-wins or user choice, and inventing a merge is a behaviour change.
- Conflict handling for the Settings Editor's own edit buffer (602/603) — that is a local diff problem, not a cross-machine one.
- Server-side arbitration, a web UI to resolve conflicts, push notifications, or email alerts.
- Mod files (never synced) and local backups (excluded by 708).
- Deleting or pruning superseded remote snapshots — that is a retention question the notes do not answer; raise it.

## Implementation notes

- The interesting case is not "two files changed" but "two machines changed the same file differently". Compare by content hash: identical hashes on both sides are not a conflict, even if both machines touched the timestamp.
- Do not compare filesystem mtimes across machines: they are meaningless across OSes and timezones. Compare the recorded `changed_at` plus the hash, and treat the recorded value as advisory ordering only.
- `meta.json` is a read-modify-write shared by every machine with no transactions. Two simultaneous syncs can lose a registry entry. Re-read and merge the machine registry after uploading rather than blind-overwriting, and log when a merge was needed. A lost `meta.json` pointer is recoverable because snapshots are content-addressed and self-describing — say that in the code comment so nobody "simplifies" `meta.json` into the only copy of anything.
- Device display names come from 705's registry (the same name the dashboard shows) when signed in, or from the snapshot's recorded machine name otherwise — this feature must work with no account, so never require the API to render a banner.
- Superseded versions are not deleted: the older snapshot still holds them. "Keep yours" is therefore recoverable in principle; mention that in the report rather than claiming the losing version is gone.
- Keep the resolution state in `~/.game-polish/sync-state.json` (the same file 708 owns) keyed by conflict id, so a crash mid-resolution does not reprompt or, worse, half-apply.
- Test hooks: a way to inject a `changed_at` and a foreign `machine_id` into a test snapshot manifest, so clock-skew and conflict paths can be exercised deterministically. Do not stitch timestamps into production reads to make tests pass.

## Acceptance criteria

- **Two-machine simulation** (two data roots, distinct `machine_id`s, one bucket): A syncs; B changes a settings value and syncs; A opens the app and gets the banner carrying B's device name and the exact sentence from the notes.
- "Keep yours" uploads A's version into the next snapshot; "Pull theirs" applies B's version locally and leaves A's previous file in the local backup tree, restored successfully from the Backups view.
- One-sided change: a file changed only on one machine syncs silently with no banner.
- Saves conflict: with both sides changed, both versions are offered with timestamps and origins; choosing one keeps the other as a local backup, and the choice is recorded so it is not asked twice.
- Clock skew beyond tolerance produces a conflict plus a warning, never a silent overwrite.
- The snapshot manifest really carries `machine_id`, content hash and `changed_at` per file, and `meta.json` lists machines with their last seen snapshot — paste both.
- A resolved conflict does not reappear after relaunch, and no resolution state is left half-applied.
- The whole path works with **no Game Polish account and no service involved** — the conflict UI is driven by the bucket contents alone.

## Verification

```bash
# two data roots, e.g. via the CLI harness from 213:
dotnet run --project GamePolish.Cli -- sync --data-root "$TMPDIR/gp-a" --machine-id A
dotnet run --project GamePolish.Cli -- sync --data-root "$TMPDIR/gp-b" --machine-id B
aws --endpoint-url https://<endpoint> s3 cp s3://<bucket>/game-polish/meta.json - | jq '.machines'
aws --endpoint-url https://<endpoint> s3 ls s3://<bucket>/game-polish/snapshots/ --recursive | tail -20
```

Paste the `meta.json` machine registry, the two snapshot manifests (showing per-file hashes, `changed_at` and `machine_id`), the banner text captured from the UI (screenshot or headless assertion), and the listing of the local backup tree holding the losing file. State whether two real machines or two data roots were used.

## References

- `../game-polish-notes/docs/views/cloud-backup-sync.md` — "Sync Trigger Logic" (conflict resolution paragraph and the banner wording), "S3 Storage Structure", "Edge Cases".
- `.todo/000-CONVENTIONS.md` — invariants 1 and 4, definition of done, evidence rules.
- Tasks: 708 (snapshots, `meta.json`, sync state), 709 (restore onto a machine with local changes), 712 (the binary saves case), 213 (the CLI harness used for the two-root test).
