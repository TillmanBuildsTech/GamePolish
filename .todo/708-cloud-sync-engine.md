---
id: 708
title: Cloud sync engine
phase: 7
bucket: post-v1
depends_on: [707]
blocks: [709, 710, 711, 712, 713]
status: todo
area: service
---

# 708 — Cloud sync engine

## Objective

A configured bucket actually receives snapshots: the engine writes the documented prefix layout, uploads only changed content (content-addressed, so unchanged files are referenced rather than re-sent), honours the three toggleable categories and their defaults, syncs on the documented triggers, keeps sync state in the data root, queues work while offline and retries later, and records sync history the History view can render. Mod files and local backup snapshots are never uploaded, on purpose.

## Context

Phase 7 is the `post-v1` bucket: the account system exists to gate a *paid storage tier*, and that tier is only worth building once the desktop app has users — **do not start this bucket while the `v1.0` bucket still has open work**. Confirm the earlier buckets are `status: done` before beginning.

This task depends on 707 (a verified, writable bucket and a keychain-held credential). It blocks four later tasks: 709 (restore reads what this writes), 710 (conflict resolution needs this task's per-machine stamps), 711 (encryption changes how content is addressed and what is uploaded), 712 (saves are a category of this engine), and 713 (the managed tier reuses the same layout behind presigned URLs). It is the only task in the bucket that writes to a user's bucket — get the layout right, because 709 and 713 both read it.

BYO S3 works with **no Game Polish account**: this engine is the reason the notes say "No Game Polish account required for this feature". Do not add an account dependency to it.

## Scope

- The prefix layout, exactly as the notes document it, under a single top-level prefix so a user can inspect, migrate or delete everything at once:
  ```
  game-polish/
  ├── meta.json                     # snapshot index + machine registry
  ├── snapshots/
  │   └── 2026-10-15T21-42-00Z/
  │       ├── manifest.json         # what is in this snapshot
  │       ├── app-settings.json
  │       ├── games.json
  │       ├── games/<game-id>/manifest.json
  │       ├── games/<game-id>/settings/<files>
  │       └── saves/<game-id>/…     # only if opted in (712)
  └── latest                        # symlink-style pointer recorded in meta.json
  ```
  Folder names are colon-free ISO-8601 UTC (`2026-10-15T21-42-00Z`); "latest" is a pointer inside `meta.json`, not a second upload of the data.
- Incremental, content-addressed uploads: `meta.json` keeps a content-addressed index (SHA-256 → object key); unchanged files are referenced by hash and not re-uploaded. If you introduce a blob area to avoid duplicating unchanged files inside each snapshot folder, place it under `game-polish/blobs/` and say so in the report — the notes' tree does not show one, so a layout delta must be recorded, not hidden.
- The three toggleable categories with the notes' defaults: **Game Polish Config** (`games.json`, per-game `manifest.json`, app settings) — always on; **Game Settings** (INI/TOML/JSON files managed by the Settings Editor) — on; **Game Saves** (directories declared in the game YAML) — off, opt-in.
- Deliberately **not** backed up, and the engine must not be "helpful" about it:
  - The mod files themselves — they can be re-downloaded from source, and paying to store gigabytes of mod assets in S3 is pointless.
  - The local backup snapshots in `~/.game-polish/<game>/backups/` — already redundant with S3.
- The trigger table, implemented as written: mod installed/uninstalled (config); mod enabled/disabled (config); game settings saved (settings); app settings changed (config); game save file changed via polling, debounced 30 seconds, only if saves are enabled; app opened (check for remote changes from other machines); app closed (final sync if anything is pending). Plus the three modes in the panel — "Automatically after changes", "On app close only", "Manual only" — and a [Sync Now] action, with the status line "Last synced: Today at 9:42 PM".
- Sync state in `~/.game-polish/sync-state.json` (last synced timestamp, hash index, pending queue), per the notes' "Data Requirements"; the snapshot index is fetched from S3 `meta.json` on demand rather than cached forever.
- Offline queueing: silent queueing while offline, retry on next app open, and the amber "Last synced: 3 days ago" state. A failed sync never touches existing data in S3: uploads are additive and `meta.json` is only replaced after its new content is safely written.
- Sync history entries with the fields the History view shows: timestamp, kind (Auto-sync / Manual sync), success or failure, file count, bytes uploaded, the "(initial)" marker for the first full sync, and a [Retry] on failures.

## Out of scope

- Multi-machine conflict detection and the conflict banner UI — that is 710. This task only records the per-machine stamps 710 needs.
- Encryption (711), game-save discovery and sizing (712), the restore wizard (709), managed-tier presigned URLs and quotas (713).
- Restoring anything, or deleting old snapshots (retention/pruning of remote snapshots is not in the notes; raise it as a question rather than inventing a policy).
- Uploading mod files, backups, logs, caches, or registry caches.
- A server-side sync service: BYO S3 sync is client-driven. `/sync/*` endpoints exist for the managed tier (713) and are unused here.

## Implementation notes

- One writer per machine: a single-writer lock so two app instances (or a fast double-trigger) cannot interleave `meta.json` updates. Object storage has no transactions — a read-modify-write of `meta.json` must be serialized locally and made last-write-wins remotely with clear logging.
- Bounded parallelism (about four uploads at once), with `meta.json` written only after all of its referenced blobs exist. A snapshot that references a blob which failed to upload is a corrupted snapshot; never publish one.
- Debouncing is the observable contract, not the mechanism: the notes say "polling" for save changes and 30 seconds of debounce; a filesystem watcher with the same debounce satisfies it. A second change inside the window coalesces into one sync, which is what the history should show.
- Dry-run support is worth building (a `--dry-run` flag on the CLI harness from 213) so the "only changed files" claim can be proven without watching the UI.
- Hash and compare before hashing and uploading again: a file whose hash is unchanged must produce zero bytes of traffic. The history entry records the real byte count, so a bogus "0 KB uploaded" on a first sync would be visible immediately.
- Sync history storage: keep it local (a bounded ring in `sync-state.json` or a sibling file), consistent with the operation-log design from 212, and never upload it.
- Never log object bodies, credentials (707 owns redaction) or absolute local paths in a way that leaks the user's username — relative paths inside the data root are enough.

## Acceptance criteria

- A first sync of a real data root uploads exactly the declared categories and nothing else: a recursive listing of the bucket matches the expected file list, with no `backups/`, no mod files, no logs or caches.
- A second sync with no changes uploads **zero bytes** (history entry and object listing both show it).
- Changing one settings file uploads only that file; the new snapshot's manifest references the unchanged files by their existing hashes.
- The layout on the bucket matches the documented tree, including colon-free snapshot folder names and a `latest` pointer in `meta.json`.
- Each of the seven trigger rows behaves as written — verified at least for install, uninstall, enable/disable, settings saved, app settings changed, app open and app close — and the saves debounce coalesces two changes 10 seconds apart into one sync (with 712's saves plumbing stubbed if it has not landed).
- Offline: with the network disabled, a change queues and the status shows the amber stale state; re-enabling the network uploads it on the next app open without user action.
- A forced failure (deny `PutObject` in the bucket policy) leaves every pre-existing object intact — list before and after and show no deletions.
- Sync history shows the notes' four example entries with correct counts, kinds and markers.
- The whole feature works signed out of any Game Polish account.

## Verification

```bash
aws --endpoint-url https://s3.us-west-004.backblazeb2.com s3 ls s3://<bucket>/game-polish/ --recursive
aws --endpoint-url https://s3.us-west-004.backblazeb2.com s3 cp s3://<bucket>/game-polish/meta.json - | head -40
```

Paste: the recursive listing after a first sync and after a no-op sync (proving zero new objects), the `sync-state.json` contents with secrets absent, the history entries, and the byte counts. Run the CLI harness in dry-run mode for the incremental claim, and paste the failure-path listing showing the bucket was not damaged when writes were denied.

## References

- `../game-polish-notes/docs/views/cloud-backup-sync.md` — "What Gets Backed Up", "Layout", "S3 Storage Structure", "Sync Trigger Logic", "Security", "Data Requirements", "Edge Cases".
- `.todo/000-CONVENTIONS.md` — invariants 1, 4 and 7; secrets in the keychain; evidence rules.
- Tasks: 707 (this task's dependency), 709, 710, 711, 712, 713.
