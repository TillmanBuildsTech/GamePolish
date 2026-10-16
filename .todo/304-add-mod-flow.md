---
id: 304
title: Add Mod flow
phase: 3
bucket: mvp
depends_on: [208, 303]
blocks: []
status: todo
area: desktop
---

# 304 — Add Mod flow

## Objective

A modal that installs an addon from a GitHub repo URL or from a ZIP the user already has. When this
task is done the user can paste a repo URL (or pick a `.zip`), confirm the addon folder name, watch the
install run, and get a truthful result — installed, or a specific failure they can act on. Discover
(`305`) is the other half of the MVP install story and calls the same Core operation.

## Context

- `303` built the Library mod list and owns the `[+ Add Mod]` button this modal opens; `301` built the
  shell, DI and navigation.
- **`208` is the operation this task fronts**: `207` acquires the source (shallow clone of a GitHub
  repo, or ZIP extraction into staging), `204` validates every source-supplied path, `205` creates the
  backup before the first write, `206` records the manifest entry, and `212` holds the operation log
  line the failure's details link reads. `208`'s result type carries the outcome and the operation id;
  read those contracts rather than reinventing them.
- OctoWoW specifics (`docs/games/octowow.md`): an addon is a folder at
  `<install>/Interface/AddOns/<AddonName>/`, installed as a `--depth=1` clone, so the version is the
  HEAD SHA or latest tag. WoW's loader reads the folder's `.toc` and ignores `.git`, READMEs and build
  scripts, so a repo containing those is still installable.
- Trust model: GitHub repos and user-supplied ZIPs only — the Gitea URLs (`octowow.st/git/...`) the
  incumbent handles are **not** MVP sources, so reject them with a clear message. Real repos to test
  with come from the OctoWoW seed registry: `https://github.com/brues-code/pfUI`,
  `https://github.com/The-Kludge-Bureau/pfQuest`.
- Inherited invariants: backup before mutation; never execute anything from the source (no repo build
  scripts, hooks or mod-provided code); validate every source-supplied path.

## Scope

- `ViewModels/AddModViewModel.cs` + `Views/AddModDialog.axaml`: a modal `Window` with two source modes —
  **GitHub repository** and **ZIP file** — as tabs or a radio choice.
- GitHub mode: a URL `TextBox`, shape validation with an inline message, and a **derived addon folder
  name** in an editable field pre-filled from the repo name. ZIP mode: a file picker (the window's
  `StorageProvider`), the chosen file shown, folder name derived from the top-level directory.
- Confirm → progress → result, with the dialog staying open on failure so the input is not lost.
- A result region rendering `208`'s outcome: on success the addon name, version/SHA and file count; on
  failure the mapped message plus a details link into `307`'s log drawer.
- On success: close the dialog, add the row to the `303` list (enabled by default), fire a success toast.

## Out of scope

- Discover's card-level install (`305`) — same Core call, different surface. Do not build a shared
  "install wizard" abstraction before the second caller exists.
- Update, repair and overwrite of an existing addon (`209`; update checks are `503`). A duplicate
  install is reported and refused, nothing more.
- Installing a specific version, tag or non-default branch (v0.2); drag-and-drop of a ZIP onto the
  window; installing from an unpacked folder on disk; dependency resolution between addons.

## Implementation notes

- **URL shape.** Accept `https://github.com/<owner>/<repo>` with an optional trailing `.git` and an
  optional `/tree/<ref>` suffix. Reject non-`https`, non-`github.com` hosts, `git://`, bare
  `owner/repo` shorthand and any URL with embedded credentials. This is a shape check for a good error
  message; the authoritative refusal is `207`/`208`'s.
- **Folder name is derived and confirmed.** It is the repo name with `.git` stripped, or the archive's
  single top-level directory for a ZIP, and it is **shown and editable before install**: WoW addons
  frequently need a name that differs from the repo (`pfQuest-turtle`, `aux-addon`). Validate it the
  way the loader will: non-empty, no path separators, no `..`, no leading/trailing whitespace or dots,
  valid on Windows and macOS. Never concatenate the destination in the view model — pass the name and
  the game id to Core and let `204` validate the path, or the UI owns a traversal bug.
- **Duplicate install.** If the destination folder exists or the manifest already lists that addon,
  stop before any write: `<name> is already installed (version <v>). Uninstall it first, or pick a
  different folder name.` No overwrite offer in the MVP — an overwrite is an update, owned by `209`.
- **Unreachable repo.** A 404, a private repo, a DNS failure and an offline machine are four different
  log lines and one honest UI message: `Couldn't reach the repository. Check the URL and your
  connection.` Always offer the details link; the log line is the actionable part.
- **ZIP with a bad structure.** Reject before writing anything: zero entries; no `.toc` in the first two
  levels (an OctoWoW addon without one never loads); entries flat rather than inside one top-level
  folder (offer the folder name derived from the ZIP filename instead of guessing); and any entry with
  `..`, a leading `/`, a drive letter, or a symlink/junction. `204` owns the rejection; surface it as
  `This archive contains files that escape the install folder.` rather than a raw exception.
- **Progress.** Cloning is one opaque step: an indeterminate `ProgressBar` (`IsIndeterminate="True"`)
  plus a stage line (`Cloning…`, `Validating…`, `Backing up…`, `Installing…`) driven through `307`'s
  progress scope so the same UI serves the row operations. Do not fake a percentage.
- **Cancel and modal mechanics.** Cancelling after the first write is not safe to promise in the MVP:
  disable Cancel once the install starts mutating and say why in the stage line. Show with
  `ShowDialog(owner)` and `WindowStartupLocation = WindowStartupLocation.CenterOwner`, usable at the
  window's minimum size, returning a result instead of reaching into `LibraryViewModel`.
- **What the UI must not do:** parse the repo, read the ZIP, decide where files go, retry network calls
  or duplicate Core's validation — that is how the Discover and Add Mod paths drift apart.

## Acceptance criteria

- An end-to-end add of one real OctoWoW addon repo succeeds: pasting
  `https://github.com/brues-code/pfUI`, confirming the folder name `pfUI` and pressing Install leaves
  `<install>/Interface/AddOns/pfUI/` existing with a `.toc` inside, a new `manifest.json` entry, a new
  row in Library and a success toast — and the first byte written was preceded by a new directory under
  `~/.game-polish/<game_id>/backups/` (invariant 1).
- An invalid URL (`http://example.com/x`, `git@github.com:o/r.git`, `owner/repo`) is refused inline
  before any network call, with the input preserved.
- An unreachable repo produces the "couldn't reach the repository" message plus a details link that
  opens the log drawer with the real log line.
- Installing the same addon twice stops before any write with the duplicate message, and both the
  install folder and `manifest.json` are unchanged by the second attempt.
- A ZIP with entries containing `../` is rejected with the escape message and nothing is written into
  the install directory (check the folder mtime is unchanged); a ZIP with no `.toc` in its first two
  levels is rejected with a message explaining that an addon needs a `.toc`.
- `dotnet build` clean; tests run with the real counts quoted.

## Verification

```bash
cd C:/Users/Brandon/Projects/game-polish
dotnet build                                     # expect: 0 Warning(s), 0 Error(s)
dotnet run --project GamePolish.Desktop

# happy path: [+ Add Mod] -> GitHub -> https://github.com/brues-code/pfUI -> folder pfUI -> Install
ls -d "D:/OctoWoW/Interface/AddOns/pfUI" ; ls "D:/OctoWoW/Interface/AddOns/pfUI"/*.toc
ls -1t ~/.game-polish/octowow/backups/ | head -1 # expect a backup dir from this install
cat ~/.game-polish/octowow/manifest.json         # expect a pfUI entry with a version/SHA
# negative paths (each leaves Interface/AddOns and manifest.json unchanged):
#  1) http://example.com/x -> inline refusal, no network call; 2) a repo that 404s ->
#     failure message + details link -> log drawer; 3) re-install pfUI -> duplicate message;
#  4) a hand-made zip with ../evil -> escape rejection; 5) a zip with no .toc -> structure rejection
dotnet test                                      # quote the real pass/fail counts
```

## References

- Design notes: `../game-polish-notes/docs/views/library.md` — **Right Panel — Mod List** (the Add Mod
  button), **Mod Row — Overflow Menu Actions**; `../game-polish-notes/docs/games/octowow.md` — **Addon
  Layout**, **Seed Registry** (real URLs), **Why This Game First** (the Gitea sources).
- Repo: `CLAUDE.md` (Installer Engine, Key Design Constraints), `000-CONVENTIONS.md` (invariants 1–5).
- Related tasks: `204`, `205`, `206`, `207`, `208`, `212` (Core), `209` (update/overwrite — the
  duplicate path this task refuses), `303` (host surface), `305` (the other caller), `307` (progress +
  error details link).
