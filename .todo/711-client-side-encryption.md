---
id: 711
title: Client-side encryption
phase: 7
bucket: post-v1
depends_on: [708]
blocks: []
status: todo
area: service
---

# 711 — Client-side encryption

## Objective

Encryption is available before upload, so neither the storage provider nor the future Game Polish Cloud can read the data: AES-256 with a key derived from a user passphrase, a one-time printable recovery key as an alternative, and the unrecoverable-if-forgotten warning stated during setup. The change encryption makes to content addressing is handled honestly — hashes are not simply taken over ciphertext or plaintext — and this file says exactly which choice was made and why.

## Context

Phase 7 is the `post-v1` bucket: the account system exists to gate a *paid storage tier*, and that tier is only worth building once the desktop app has users — **do not start this bucket while the `v1.0` bucket still has open work**. Confirm the earlier buckets are `status: done` before beginning.

This task depends on 708 (the layout, the manifests and the content-addressed index) and touches 709 (a restore of an encrypted snapshot must ask for the key before it starts), 712 (saves are the most personal data in the set, and the strongest argument for encryption), and 713 (the managed tier supports encryption too, and must not be able to read user data either). The notes put it plainly: with encryption enabled, not even the storage provider can read save files.

There is no external account to create here; the only prerequisites are a working bucket (707) and a synced snapshot set to encrypt (708).

## Scope

- Off by default, enabled from Cloud Backup settings: a setup flow that takes a passphrase, states the warning, derives the key, shows the recovery key once for printing, and then encrypts from the next snapshot onward.
- AES-256 in an authenticated mode: **AES-256-GCM**, a fresh 12-byte random nonce per file stored alongside the ciphertext, tag verified on read. Whole-file framing is acceptable for these sizes; if saves can exceed a few hundred MB, use a chunked framing with sequence numbers so a large file can be streamed and a truncated file detected.
- Key derivation: **Argon2id**, memory 64 MiB, iterations 3, parallelism 1, 16-byte random salt (stored in `meta.json`, it is not a secret), 32-byte output. If Argon2id is unavailable for a target platform, the documented fallback is PBKDF2-HMAC-SHA256 with at least 600,000 iterations — and the report must say which was used and why. The justification is memory hardness: Argon2id's cost per guess is dominated by memory, which blunts the GPU/ASIC advantage that makes PBKDF2's cheap iterations attractive to an attacker with a stolen bucket.
- Envelope encryption: a random 256-bit **data key** encrypts content; the data key is wrapped once by the passphrase-derived key and once by the recovery key. That is why a recovery key can unlock data whose passphrase is gone, and why changing a passphrase re-wraps a 32-byte value instead of re-uploading the bucket.
- Recovery key: a 256-bit random code shown once with an explicit print/save step and an "I have recorded it" confirmation. Backing out without confirming must leave encryption disabled — a user who lost both keys has lost the data.
- Unrecoverable warning at setup, in the notes' terms: the data cannot be recovered if the passphrase (and the recovery key) are forgotten. Say it in the dialog and require acknowledgement, not just in the docs.
- Scope of encryption: everything under `snapshots/` — the snapshot manifest, `app-settings.json`, `games.json`, per-game manifests, settings files and saves. `meta.json` stays readable, because listing snapshots requires it; the report must enumerate exactly what that leaks (snapshot timestamps, machine names/ids, per-snapshot encryption flag, blob counts and sizes).
- Mixed buckets: each snapshot records `encryption: none | aes-256-gcm`. Previously written unencrypted snapshots stay readable, and encryption applies from the next snapshot onward. Re-encrypting history is a separate, expensive action with its own size warning — defer it and say so unless the user asks.
- Restore path (709) with an encrypted snapshot: prompt for the passphrase or recovery key before downloading, and fail cleanly on a wrong key without touching local state.

## Out of scope

- Key escrow, server-side key management, a "forgot passphrase" flow that works, or storing the passphrase anywhere.
- Per-file passphrases, hardware-backed keys (TPM/Secure Enclave), OS keychain-only modes with no passphrase at all.
- Encrypting local backups in `~/.game-polish/<game>/backups/` — they are already local; say so if asked.
- Compression before encryption (it leaks plaintext sizes and adds a second transform to get wrong), and encrypting filenames/paths in the bucket unless the addressing decision below implies it.
- Encrypting the `meta.json` index, and any form of deniable storage.

## Implementation notes

- **Content addressing is the honest hard part, and it must be resolved rather than hand-waved.** Hashing the ciphertext destroys dedupe: GCM's random nonce means the same plaintext encrypts differently every time, so no blob ever compares equal and every snapshot re-uploads everything. Hashing the plaintext, on the other hand, hands anyone with the bucket a confirmation oracle — guess a file's contents, hash it, and see whether that hash is present. The resolution adopted here is a **keyed address**: `HMAC-SHA256(addressing_key, plaintext)` truncated to 32 bytes, where the addressing key is derived from the data key. Same content → same address → dedupe preserved; without the key, a guess cannot be confirmed. Local change detection may keep plaintext hashes in `sync-state.json`, which never leaves the machine.
- Never write the passphrase or the derived key to disk. Keys live in memory for the session. An opt-in "remember on this device" stores the *derived key* in the OS keychain — the same trust boundary as the S3 credentials (707) and a deliberate trade-off; label it as such in the UI rather than hiding it.
- Wrong-key detection is the AEAD tag's job: a failed tag means failure, not corrupted output. A partially written snapshot must never be published, and a failed restore must leave local files untouched.
- Argon2id at 64 MiB is deliberately slow: run it off the UI thread with progress, cache the derived key for the session, and do not re-derive per file.
- Verify the claim rather than asserting it: download a blob with the raw S3 credentials (bypassing the app) and show that known plaintext is absent from the bytes.
- Keep nonces unique per (key, file) write; a repeated nonce under GCM is a catastrophic failure, so generate them from a CSPRNG and never reuse a stored one.
- Tests must cover a fixed key and fixed nonce (test-only) so encryption output can be asserted deterministically without weakening production randomness.

## Acceptance criteria

- Round trip: enable encryption, sync, then restore onto a second data root with the passphrase — every restored file is byte-identical to the original (`sha256sum` comparison), and the app reports success.
- **Raw bucket inspection**: with the S3 credentials, download objects directly and show they contain no readable plaintext — a grep for a known mod id, an INI key, and a save-file magic string all fail; `meta.json` contains only the documented metadata.
- A wrong passphrase fails cleanly: no local files change, no bucket objects are modified, and the message says the key is wrong rather than that the data is corrupt.
- Recovery key path: with the passphrase unavailable (simulated by discarding it), the recovery key restores the data successfully.
- Dedupe survives encryption: an unchanged file is not re-uploaded in the next snapshot, and identical content across two snapshots resolves to one blob (keyed addressing in action).
- Changing the passphrase re-wraps the data key and uploads approximately zero bytes of file content.
- Enabling encryption on an existing bucket leaves earlier snapshots readable and encrypts everything from the next snapshot onward.
- With encryption off, the layout, manifests and sync history are unchanged from 708 (no regression), and a bucket without encryption is still restorable by an app that has never seen a key.

## Verification

```bash
aws --endpoint-url https://<endpoint> s3 cp s3://<bucket>/game-polish/snapshots/<ts>/games/<game>/settings/<file> - | strings | grep -i '<known-plaintext-token>'   # expect no match
aws --endpoint-url https://<endpoint> s3 cp s3://<bucket>/game-polish/meta.json - | jq '.snapshots[0].encryption'
```

```bash
sha256sum ~/.game-polish/<game>/settings/<file> "$TMPDIR/gp-b/.game-polish/<game>/settings/<file>"   # identical
```

Paste: the `strings`/grep failure on the ciphertext, the `meta.json` excerpt showing only the documented fields, the hash comparison after restore, the wrong-passphrase message, the recovery-key transcript, and the zero-byte result of a passphrase change. Name the KDF and parameters actually used.

## References

- `../game-polish-notes/docs/views/cloud-backup-sync.md` — "Security" (client-side encryption, AES-256, passphrase-derived key), "Edge Cases" (forgotten passphrase, recovery key), "Data Requirements".
- `.todo/000-CONVENTIONS.md` — invariants 1, 4 and 7; secrets in the OS keychain; evidence rules.
- Tasks: 707 (credentials), 708 (layout and content addressing), 709 (restore prompts for the key), 712 (saves), 713 (the managed tier must not be able to read user data either).
