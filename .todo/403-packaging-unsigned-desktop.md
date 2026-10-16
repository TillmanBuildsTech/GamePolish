---
id: 403
title: Packaging the unsigned desktop build
phase: 4
bucket: mvp
depends_on: [301, 303, 305]
blocks: [404, 405]
status: todo
area: desktop
---

# 403 — Packaging the unsigned desktop build

## Objective

Two reproducible publish commands — one per RID — turning the desktop app into a runnable, self-contained,
**unsigned** folder for `win-x64` and `osx-arm64`, with a stated output path, the `games/` descriptor folder
inside it, and a smoke-run instruction for the Windows output. This is the MVP's release form
(`.todo/README.md`): unsigned publish output, "runnable, not installable".

## Context

Nothing here is an installer and nothing is signed. **Signing and notarization are task 610; real installers
(`.msix`, WiX/NSIS `.exe`, `.dmg`) are task 611; the in-app auto-updater is task 605 — all phase 6 / v1.0.**
Do not reach for `codesign`, `signtool`, `notarytool`, MSIX or WiX, and do not add a signing property: none
of the secrets in `ci-release.md` → "GitHub Secrets Required" exist for v0.1.0.

`../game-polish-notes/docs/ci-release.md` describes the *finished* process; its "SDK & Runtime Pinning"
section is what binds here — release publishes are RID-specific and **self-contained**, so the runner's
installed runtimes do not affect the output. 404 wraps these commands in a tag-triggered workflow; 405
launches what they produce. Dependencies 301 (shell), 303 (Library) and 305 (Discover-lite) must be done
first; without them there is nothing worth publishing — say so instead of packaging an empty shell.

## Scope

- `<Version>0.1.0</Version>` in `GamePolish.Desktop/GamePolish.Desktop.csproj` — the one source of truth
  404's `check-version` guard compares against the git tag.
- The two publish commands and their output paths, run once with the output pasted into the report.
- Publish profiles for repeatability (with the `.gitignore` exception below), or the two commands documented
  in one place — one authority, not both half-way.
- Confirmation that `games/octowow.yaml` lands in both publish outputs.
- A Windows smoke run, with its output and the observed window state recorded.
- One honest paragraph on the macOS output, stating it was not executed here.

## Out of scope

- Signing, notarization, certificates, secrets (610); `.msix`, installer `.exe`, `.dmg` (611).
- `update-manifest.json` and any launch-time update check (605); the release workflow and tag (404).
- Trimming, ReadyToRun, NativeAOT, single-file — undecided here; app icons, branding, licenses (609).

## Implementation notes

**Self-contained, explicitly.** Verified here on SDK 10.0.401: `dotnet publish -c Release -r win-x64` alone
yields a *framework-dependent* output — 5 files and a `runtimeconfig.json`, no runtime beside it — because
the .NET 6+ SDK dropped the old "a RID implies self-contained" default; `--self-contained true` produced 193
files including the runtime. The design note requires self-contained and there is no installer to bootstrap
a runtime, so pass the flag:

```bash
dotnet publish GamePolish.Desktop -c Release -r win-x64   --self-contained true
dotnet publish GamePolish.Desktop -c Release -r osx-arm64 --self-contained true
```

Output lands at `GamePolish.Desktop/bin/Release/net10.0/<RID>/publish/`; `bin/` and `[Rr]elease/` are already
gitignored, so the output never enters the index.

**Do not enable `PublishTrimmed`** (or ReadyToRun / NativeAOT): Avalonia is reflection-heavy (XAML, view
locator), so trimming produces warnings and runtime failures that would burn 405's acceptance run for no MVP
benefit. Note the decision; don't implement it.

**Publish profiles.** The stock `dotnet new gitignore` here ignores `*.pubxml`, so a committed profile needs
an explicit exception or it silently never reaches CI — append
`!GamePolish.Desktop/Properties/PublishProfiles/*.pubxml` to `.gitignore`.

**`games/` rides along** via 401's MSBuild item
(`<None Include="$(MSBuildThisFileDirectory)../games/*.yaml" ... CopyToPublishDirectory="PreserveNewest" />`).
Verify it in *both* publish directories — the copy runs during a cross-RID publish too. Avalonia's `Assets/`
folder is already an `AvaloniaResource`: add nothing (a missing asset is 301's bug, not a packaging step).

**`<Version>` is read, not guessed.** `dotnet msbuild GamePolish.Desktop/GamePolish.Desktop.csproj
-getProperty:Version -nologo` prints `0.1.0` (verified here). Keep the element in the desktop csproj, as
`ci-release.md` says — 404's guard looks at that file.

**Windows smoke run** — from the publish directory, so the artifact is what is tested:

```bash
cd GamePolish.Desktop/bin/Release/net10.0/win-x64/publish && ./GamePolish.Desktop.exe; echo "exit=$?"
```

Expect the main window (301) with Library (303) and Discover-lite (305) reachable, and, on first run only,
`C:\Users\Brandon\.game-polish\` created by task 201 — writing there is expected and allowed (invariant 4).
Close the window and record the exit code.

**macOS honesty.** `dotnet publish -r osx-arm64` cross-compiles from Windows and should succeed, but nothing
here can execute or sign it. **Do not claim it runs.** Runtime proof needs the `macos-latest` CI job or a
physical Apple Silicon Mac: record it as unverified and paste the publish directory listing, including whether
the output is a bare apphost or a `.app` bundle (`CreateAppBundle`) — read that from the listing, not from
memory. A quarantined unsigned build is refused by Gatekeeper until the tester clears it with
`xattr -dr com.apple.quarantine <app>` or right-click → Open — the same consequence 404 puts in the release
notes. 404 owns the artifact filenames; do not invent them here.

## Acceptance criteria

- `dotnet publish -c Release -r win-x64 --self-contained true` succeeds with 0 errors, 0 warnings, and a
  launchable app exists at
  `GamePolish.Desktop/bin/Release/net10.0/win-x64/publish/GamePolish.Desktop.exe`.
- Both publish directories contain `games/octowow.yaml`.
- Launching the win-x64 executable opens the app window and exits cleanly on close.
- `dotnet msbuild ... -getProperty:Version -nologo` prints `0.1.0`.
- The report states plainly that the osx-arm64 output was **not** executed here, and what would verify it.
- No signing, installer or updater step appears in the diff — no `pfx`, `signtool`, `codesign`, `notarytool`.

## Verification

```bash
git status --short
dotnet publish GamePolish.Desktop -c Release -r win-x64 --self-contained true
dotnet publish GamePolish.Desktop -c Release -r osx-arm64 --self-contained true
ls GamePolish.Desktop/bin/Release/net10.0/osx-arm64/publish/
dotnet msbuild GamePolish.Desktop/GamePolish.Desktop.csproj -getProperty:Version -nologo    # 0.1.0
grep -rn -E "codesign|signtool|notarytool|\.pfx" --include='*.yml' --include='*.csproj' . || echo "none (expected)"
```

## References

- `../game-polish-notes/docs/ci-release.md` — "SDK & Runtime Pinning", "Release Artifact Names", "GitHub
  Secrets Required", "Workflow 2 — Release"
- `.todo/README.md` — MVP bucket; `.todo/000-CONVENTIONS.md` — definition of done, invariant 4
- Tasks: 201, 301, 303, 305, 401, 404, 405, 605, 610, 611 — depends on 301/303/305, blocks 404 and 405
