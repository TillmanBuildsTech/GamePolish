---
id: 610
title: Code signing and notarization
phase: 6
bucket: v1.0
depends_on: [403, 404]
blocks: [611]
status: todo
area: ops
---

# 610 — Code signing and notarization

## Objective

The release workflow produces artifacts code-signed on Windows and signed **and notarized** on macOS, using the
secrets in `ci-release.md`, and fails loudly when a required secret is absent — never shipping an unsigned artifact.

## Context

Authoritative: `../game-polish-notes/docs/ci-release.md` (GitHub Secrets Required; the `build-windows` and
`build-macos-silicon` job steps). **Extends `403` (packaging) and `404` (the tag/release workflow):** `404`
produced the tag and the `check-version` guard; this makes the builds *trusted* by the OS.

**Environment truth.** This repo is developed on Windows 11 with SDK `10.0.401` only. The `osx-arm64` artifacts
**cannot be signed, notarized or launched from this host**; the proof is CI on `macos-latest` (arm64) or a real Mac.

**Secrets may not exist yet.** Treat the signing material as optional-until-provided: when a secret is absent the
job must **fail with a clear message**, never fall through to an unsigned build. That is the key rule here.

## Scope

- Windows: decode the base64 `.pfx`, publish, package, then sign the packaged artifacts with `signtool` + RFC-3161.
- macOS: import the `.p12` into a temp keychain, publish, `codesign`, build the installer, `notarytool submit --wait`, `stapler staple`.
- A fail-loud secret guard on both platforms.
- Base64 secret handling that does not leak the secret into logs.
- Temporary keychain setup and guaranteed cleanup.
- The honest boundary: what is provable here and what needs CI / a Mac.

## Out of scope

- Building the installers themselves — MSIX/WiX/DMG construction is `611`; this task signs what `611` and
  `403` produce.
- Managing or rotating the certificates (an ops task outside the repo).
- The auto-updater (`605`), which consumes the signed artifacts.

## Implementation notes

- **Secrets (exactly the ci-release.md list).** Windows: `WINDOWS_CERT_BASE64`, `WINDOWS_CERT_PASSWORD`; macOS:
  `APPLE_CERTIFICATE`, `APPLE_CERTIFICATE_PASSWORD`, `APPLE_SIGNING_IDENTITY`, `APPLE_ID`, `APPLE_PASSWORD`,
  `APPLE_TEAM_ID`. All eight are required; each platform's job needs that platform's subset.
- **Fail-loud guard (run first, before any build).** For each required secret, assert non-empty and exit
  non-zero with a `::error::` annotation naming it:

  ```bash
  if [ -z "${WINDOWS_CERT_BASE64}" ]; then
    echo "::error::WINDOWS_CERT_BASE64 is not set — refusing to produce an unsigned Windows artifact"
    exit 1
  fi
  ```

  The guard reads secrets via `env:` (mapped from `secrets.*`), never interpolating a secret directly into
  a shell line (that risks leaking it in the log). Never use `continue-on-error` or a fallback that
  uploads an unsigned file. The `publish` job `needs` the build jobs, so a failed guard stops the release
  from being created at all.
- **Windows — decode and sign.**
  1. Write the certificate to a temp path under `$RUNNER_TEMP` (auto-cleaned), decoding base64 without a shell echo:
     `printf '%s' "$WINDOWS_CERT_BASE64" | base64 --decode > "$RUNNER_TEMP/cert.pfx"`.
  2. `dotnet publish GamePolish.Desktop -c Release -r win-x64 --self-contained` (optionally with the
     `/p:SigningCertificate=` properties ci-release.md names — where they do not apply, `signtool` is the real signing).
  3. Package (`611`), then sign **every packaged artifact**:
     `signtool sign /fd SHA256 /td SHA256 /tr http://timestamp.digicert.com /f "$RUNNER_TEMP/cert.pfx" /p "$WINDOWS_CERT_PASSWORD" GamePolish_<v>_x64.msix`, and again for `GamePolish_<v>_x64-setup.exe`.
     `signtool` lives under the Windows SDK on the runner (locate via `vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.Windows11SDK.*`).
  4. **Timestamping is mandatory.** `/tr http://timestamp.digicert.com /td SHA256` embeds an RFC-3161 timestamp
     so the signature stays valid after the cert expires; without it every install starts warning. Do not omit it.
  5. Verify: `signtool verify /pa /v <artifact>`. A non-zero exit fails the job.
- **macOS — temporary keychain, sign, notarize.**
  1. Create and use a throwaway keychain so the runner's state is not trusted and is discarded:
     ```bash
     security create-keychain -p "$KEYCHAIN_PW" build.keychain
     security default-keychain -s build.keychain
     security unlock-keychain -p "$KEYCHAIN_PW" build.keychain
     printf '%s' "$APPLE_CERTIFICATE" | base64 --decode > "$RUNNER_TEMP/cert.p12"
     security import "$RUNNER_TEMP/cert.p12" -k build.keychain -P "$APPLE_CERTIFICATE_PASSWORD" -T /usr/bin/codesign
     security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$KEYCHAIN_PW" build.keychain
     ```
     `set-key-partition-list` is required, or `codesign` prompts for permission and hangs headless.
  2. `dotnet publish GamePolish.Desktop -c Release -r osx-arm64 --self-contained`, then sign the bundle
     with the **hardened runtime** (required for notarization):
     `codesign --force --options runtime --timestamp --sign "$APPLE_SIGNING_IDENTITY" GamePolish.app`
     (prefer signing nested binaries over `--deep`, which Apple discourages for full bundles; if `--deep` is used,
     verify per-binary). Verify: `codesign --verify --deep --strict --verbose=2 GamePolish.app`.
  3. Build the `.dmg` (`611`), then notarize it and wait:
     `xcrun notarytool submit GamePolish_<v>_aarch64.dmg --apple-id "$APPLE_ID" --password "$APPLE_PASSWORD" --team-id "$APPLE_TEAM_ID" --wait`.
     `--wait` blocks the job on Apple's verdict; a non-zero exit fails the job.
  4. Staple the ticket so the artifact validates offline: `xcrun stapler staple GamePolish_<v>_aarch64.dmg`;
     validate with `xcrun stapler validate …` and `spctl -a -vvv -t install <artifact>`. If the `.app.tar.gz`
     is also distributed, staple the `.app` before archiving it.
  5. **Cleanup, always.** In an `if: always()` step: `security delete-keychain build.keychain` and remove
     `$RUNNER_TEMP/cert.p12` / `cert.pfx`. A leftover keychain on a self-hosted runner is a credential
     leak; on GitHub-hosted runners it is still bad hygiene.
- **Log hygiene.** Keep `set -x` off around secret use; never `echo` a decoded cert; use `::add-mask::` if a value
  must pass through an expression. Keep secrets out of paths so a derived value cannot leak into the log.
- **Escalation path.** Until the certificates exist the workflow is intentionally red on a tag — shipping an
  unsigned `.exe`/`.msix` that SmartScreen and Gatekeeper block is worse than a failed release. Record which
  secrets are present and which are still needed.

## Acceptance criteria

- Running the workflow with a required secret unset fails at the guard naming that secret; no artifact is uploaded.
- With the real secrets, the Windows MSIX and `.exe` pass `signtool verify /pa` and carry an RFC-3161
  timestamp (`signtool verify /v` shows the timestamp).
- The macOS `.dmg` is signed with a hardened runtime, notarized (`notarytool` returns Accepted) and
  stapled (`spctl -a -vvv -t install` succeeds, `stapler validate` passes).
- The temporary keychain and the exported certificates are deleted by an `if: always()` step (visible even on failure).
- The report states plainly that macOS artifacts could not be verified on this Windows host and names CI
  (`macos-latest`) or a real Mac as the proof.

## Verification

```bash
gh workflow run release.yml --ref main          # or push a throwaway tag
# Windows (on the runner, and on a Windows host for a self-signed test):
signtool verify /pa /v GamePolish_<v>_x64.msix
# macOS (CI only):
codesign --verify --deep --strict --verbose=2 GamePolish.app
xcrun stapler validate GamePolish_<v>_aarch64.dmg
spctl -a -vvv -t install GamePolish_<v>_aarch64.dmg
```

The macOS steps run only on a macOS runner; state that as the verification boundary. Paste the
`signtool verify` and `notarytool` outputs from the run.

## References

- `../game-polish-notes/docs/ci-release.md` — GitHub Secrets Required; build job steps.
- Tasks: `403`, `404`, `605` (consumes signed artifacts), `611` (installers this signs).
