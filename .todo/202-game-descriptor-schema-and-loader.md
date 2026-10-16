---
id: 202
title: Game descriptor schema and loader
phase: 2
bucket: mvp
depends_on: [101, 102]
blocks: [203, 207, 208, 209, 210, 211, 401]
status: todo
area: core
---

# 202 — Game descriptor schema and loader
## Objective
`games/<id>.yaml` parses into a validated `GameDescriptor` object, and `descriptor.ModLayout.Destination` becomes the only place in the entire codebase from which the
addon directory is derived. A malformed or incomplete descriptor fails loudly, at load, with a message that names the file and the key.

## Context
The repo is greenfield; phase 1 (`101`, `102`) created the solution and the Core + test projects. Phase 2 order: **201/202/203 first; 204 and 205 are the two tasks
everything else leans on; then 206–210; then 211–213.** This task has no dependency on 201: descriptors live in the repo (`games/`), not in the data root.

The authoritative shapes are:
- `../game-polish-notes/docs/games/octowow.md` § *Game YAML (draft)* — the literal five-key YAML.
- Same file, § *Addon Layout*: "Addons live in `<install_path>/Interface/AddOns/<AddonName>/`", and the operation table (install / update / uninstall / version).
- Same file, § *Install Path*: "No Steam registry or auto-detection. The user browses to their install folder manually… Verify the folder is correct by checking for
  `WoW.exe` in the root."
- `CLAUDE.md` § *Game Config YAML Schema (draft)* and § *Key Design Constraints* — "Game support is additive: adding a new game means adding a YAML + a registry JSON
  entry — no code changes required for a well-behaved game."

Restated invariant **(#6)**: a new game is a YAML file plus a registry entry. If supporting a well-behaved new game would require touching C#, that is a finding to
report, not a thing to implement here.

## Scope
- The `YamlDotNet` package reference on `GamePolish.Core`, with the version recorded in the report.
- `GamePolish.Core/Games/GameDescriptor.cs` — the record graph (all `required`, all `init`).
- `GamePolish.Core/Games/GameDescriptorLoader.cs` — file/directory discovery + parse.
- `GamePolish.Core/Games/GameDescriptorValidator.cs` — shape and safety rules, all problems collected.
- `GamePolish.Core/Games/GameDescriptorValidationException.cs`.
- `GamePolish.Core/Games/DescriptorPaths.cs` — the single composition point for the addon root.
- Tests: a good descriptor round trip, one test per rejection rule, and the `grep` gate.

## Out of scope
- Implementing `settings_files` behaviour (that is the settings editor, task `601`). This task only keeps the field reserved and un-parsed-as-behaviour.
- Auto-detecting an install (the descriptor explicitly says `manual_only: true`); path verification at add-game time belongs to `203`.
- Fetching `registry_url` (`211`), and writing `games/octowow.yaml` itself (that is task `401`).
- Path-traversal hardening of mod-supplied paths (`204`) — this task only rejects an *unsafe destination* written by a descriptor author.

## Implementation notes
**YAML library: `YamlDotNet`.** Rationale: it is the conventional .NET YAML library, MIT-licensed, zero-dependency, and it is the parser whose object model supports the
snake_case → PascalCase naming convention mapping this schema wants. Verified 2026-10-15 on nuget.org: latest release **18.1.0**, which targets `net8.0` /
`netstandard2.0` / `net461` (no `net10.0` asset group — `net10.0` resolves the `net8.0` assets, which NuGet reports as compatible). **First step, before writing any
code:** `dotnet add GamePolish.Core package YamlDotNet` and confirm the restore picks 18.1.0 and prints no `NU1701`/framework-fallback warning. If it does not resolve
cleanly, stop and report — do not hand-roll a YAML parser.

YAML/JSON parsing is the *only* third-party dependency this task adds. ZIP handling (`207`) uses `System.IO.Compression` from the BCL; do not add SharpZipLib or
similar.
```csharp
public sealed record GameDescriptor
{
    public required string Id { get; init; }                  // "octowow"; lowercase [a-z0-9-]+, max 64
    public required string Name { get; init; }                // "OctoWoW"
    public required InstallDetection InstallDetection { get; init; }
    public required ModLayout ModLayout { get; init; }
    public required string RegistryUrl { get; init; }
    public IReadOnlyList<SettingsFileDescriptor> SettingsFiles { get; init; } = []; // reserved: 601
}
public sealed record InstallDetection(bool ManualOnly, string? VerifyFile);   // verify_file: "WoW.exe"
public sealed record ModLayout(string Type, string Destination);              // type: "addons_folder"
public sealed record SettingsFileDescriptor(string Path, string Format);      // reserved: 601
```
- Map YAML with `new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).IgnoreUnmatchedProperties().Build()`.
  `IgnoreUnmatchedProperties()` is deliberate and load-bearing: it lets a descriptor carry `settings_files` before task `601` understands it, which is what "reserved"
  means.
- Do **not** enable YamlDotNet's type-resolution paths. Deserialize to the record graph only; never `Deserialize<object>` and never allow a `!!` tag to select a CLR
  type.
- Cap input size (1 MiB) and use `Parser`-level depth limits if the library exposes them — a descriptor is a config file, not untrusted-rich content, but it is fetched
  from the repo (`401`) and cheap to bound.
```csharp
public interface IGameDescriptorLoader
{
    IReadOnlyList<string> DescriptorDirectories { get; }        // shipping dir first, user override second
    GameDescriptor LoadFromFile(string path);
    GameDescriptor? LoadById(string id);                        // null when absent
    IReadOnlyList<GameDescriptor> LoadAll();                    // throws if any file is invalid
}
public sealed class GameDescriptorValidationException : Exception
{
    public string DescriptorPath { get; }
    public IReadOnlyList<string> Problems { get; }              // every problem, not just the first
}
```
- `DescriptorDirectories`: `<app base dir>/games` (shipped next to the app) then `<data root>/games` (user-supplied). Resolution order is deterministic; a duplicate
  `id` across both is an error whose message names **both** files.
- Descriptor file name must equal `<id>.yaml`; a file whose stem disagrees with its `id:` key is an error.
- **Validation rules, each with its own message:**
- `id` — required, matches `^[a-z0-9][a-z0-9-]*$`, ≤ 64 chars, equals the file stem.
- `name` — required, non-empty after trim.
- `install_detection` — required; `manual_only` must be present; when `manual_only` is `true`, `verify_file` must be present (that is what `203` checks to accept a
  user-browsed folder).
- `mod_layout.type` — required; this task accepts only `addons_folder` (the loader is the place future layout kinds are added — do not build a plugin system for one
  value).
- `mod_layout.destination` — required, **relative**, non-empty, no `..` segment, no rooted path, no trailing separator, no `.` segment. Reject `"Interface/AddOns/"`.
  Reject `C:\...`, `/...`, `\\?\...`.
- `registry_url` — required, absolute `https://` URI.
- `settings_files` — optional; if present, each entry needs a non-empty `path` and a `format` from `{ini, toml, json}` — validated but not used.
- Every problem message must contain: the descriptor path, the offending key (`mod_layout.destination`), the offending value, and what was expected. "Invalid
  descriptor" alone is a failed criterion.
```csharp
public static class DescriptorPaths
{
    // The ONE place the addon directory is composed. Nothing else in Core may build this path.
    public static string AddonRoot(this GameDescriptor descriptor, string installRoot);
}
```
- `AddonRoot` = `Path.GetFullPath(Path.Combine(installRoot, descriptor.ModLayout.Destination))`. Callers hand the result to `204`'s `IPathValidator` as the destination
  root; `AddonRoot` itself does no sandbox enforcement beyond what validation already guaranteed at load.

## Acceptance criteria
1. Loading `games/octowow.yaml` yields `Id == "octowow"`, `Name == "OctoWoW"`, `InstallDetection == (true, "WoW.exe")`, `ModLayout == ("addons_folder",
  "Interface/AddOns")`, and the `raw.githubusercontent.com/game-polish/registries/...` URL from the design note.
2. `grep -rn "Interface/AddOns" GamePolish.Core/` produces **no output** after this task — the literal string exists only in `games/octowow.yaml`.
3. A descriptor missing a required key throws `GameDescriptorValidationException` whose `Problems` contain the key name and the file path; the message is asserted in
  the test.
4. A descriptor with an absolute, rooted, or `..`-containing `mod_layout.destination` is rejected; a descriptor with an unknown `mod_layout.type` is rejected and the
  message lists the accepted values.
5. A descriptor carrying an unknown top-level key (simulating `settings_files` arriving early) loads successfully and the unknown key is ignored, not thrown on.
6. Two descriptor files sharing an `id` in the two search directories produce one exception naming both paths.
7. `AddonRoot(installRoot)` for OctoWoW returns `<installRoot>/Interface/AddOns` on Windows and macOS, compared with `Path.GetFullPath` on both sides.
8. No Avalonia, no `Microsoft.Extensions.*` and no other new package lands in `GamePolish.Core` besides `YamlDotNet`.

## Verification
```bash
dotnet add GamePolish.Core package YamlDotNet   # expect: Successfully added 'YamlDotNet' (18.1.0)
grep -n "YamlDotNet" GamePolish.Core/GamePolish.Core.csproj
dotnet test GamePolish.Core.Tests --filter "FullyQualifiedName~GameDescriptor"
# expect: every pass; the rejection tests assert message substrings, not just exception type
grep -rn "Interface/AddOns" GamePolish.Core/          # expect: no output
grep -rn "AddOns" GamePolish.Core/                    # expect: no output (no other spelling sneaks in)
dotnet build && dotnet test                          # from the repo root; paste real counts
```
## References
- `../game-polish-notes/docs/games/octowow.md` § *Install Path*, § *Addon Layout*, § *Game YAML (draft)*
- `CLAUDE.md` § *Game Config YAML Schema (draft)*, § *Key Design Constraints*
- `000-CONVENTIONS.md` § Hard invariants (#6), § Definition of done ("A new supported game")
- Related tasks: `203` (consumes `install_detection.verify_file`), `204` (validates every mod-supplied path against `AddonRoot`), `208`–`210` (place files at
  `AddonRoot`), `401` (authors the real YAML), `601` (implements `settings_files`).
