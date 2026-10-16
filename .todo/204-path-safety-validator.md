---
id: 204
title: Path safety validator
phase: 2
bucket: mvp
depends_on: [101, 102]
blocks: [205, 207, 208, 209, 210]
status: todo
area: core
---

# 204 — Path safety validator
## Objective
One component that takes an untrusted relative path from a mod source (a git tree, a ZIP entry, or a registry-supplied folder name) plus a trusted destination root, and
returns an absolute path proven to be inside that root — or refuses with a typed reason. Every write of mod content in phases 2–3 goes through it.

## Context
Greenfield repo; phase 1 (`101`, `102`) created the solution and test projects. Phase 2 order: **201/202/203 first; 204 and 205 are the two tasks everything else leans
on; then 206–210; then 211–213.** You can start as soon as the projects exist: this task depends on nothing else in phase 2 except the destination root, which the
caller supplies.

Restated invariant **(#3)**, verbatim from `000-CONVENTIONS.md` § Hard invariants: *"Validate every path that originates from a mod source. Reject traversal out of the
destination, absolute paths, symlinks/junctions that escape, and NTFS alternate data streams. GitHub is not a trusted input."* Also **(#2)**: nothing downloaded is ever
executed — so this task never shells out, and never resolves a path by asking the OS to open it.

**This task is a hard dependency of `205`–`210`.** A bug here is a bug in every write path in the product. Write the payload table first, then the implementation.

## Scope
- `GamePolish.Core/Paths/UnsafePathReason.cs` — the reason enum.
- `GamePolish.Core/Paths/PathValidationFailure.cs` — one rejected input.
- `GamePolish.Core/Paths/PathValidationException.cs` — carries **all** failures.
- `GamePolish.Core/Paths/IPathValidator.cs` + `PathValidator.cs` — the resolver.
- `GamePolish.Core/Paths/PathComparer.cs` — the one place filesystem case/path comparison is decided.
- Tests: the payload table below, one test per row, plus batch-level case-collision tests.

## Out of scope
- Content filtering (which files an addon needs). The engine copies what is there, minus `.git` (see `207`); it does not judge `.lua` content.
- Validating *descriptor* destinations — `202` already did that at load (relative, no `..`, no root). This task validates the *mod-supplied* leaf names beneath that
  destination.

## Implementation notes
```csharp
public enum UnsafePathReason
{
    Empty, NullByte, TooLong, InvalidCharacters, RootedPath, TraversalEscape, AlternateDataStream,
    DeviceName, TrailingDotOrSpace, ReparsePointEscape, CaseCollision, OutsideDestination, DestinationMissing
}
public sealed record PathValidationFailure(string Input, UnsafePathReason Reason, string Message);
public sealed class PathValidationException : Exception
{
    public IReadOnlyList<PathValidationFailure> Failures { get; }
}
public interface IPathValidator
{
    // Trusted root + one untrusted relative path -> absolute path proven to be under root.
    string ResolveSafe(string destinationRoot, string untrustedRelativePath);
    // Trusted root + an untrusted single folder name (an addon folder name) -> absolute directory path.
    string ResolveSafeDirectoryName(string destinationRoot, string untrustedFolderName);
    // Batch view: validate a whole tree manifest at once so collisions are detectable.
    IReadOnlyList<PathValidationFailure> ValidateAll(string destinationRoot,
                                                     IEnumerable<string> untrustedRelativePaths);
}
```
Resolve algorithm (order matters — every step catches a class the previous cannot):
1. Reject `null`, `""`, whitespace-only. Then reject any `'\0'`.
2. Reject an input longer than 255 chars for a single segment, and a resulting full path longer than 4096 chars (Windows practical limit; the same cap applies on macOS
  for predictability).
3. Normalize separators: treat `\` as a separator on every platform (a ZIP built on Windows may use `\`, and a git tree on macOS may mix). Do **not** call
  `Path.GetFullPath` on the input alone first — see 5.
4. Reject early, by pattern, regardless of what normalization would produce:
- rooted / absolute: `^[A-Za-z]:`, `^[\\/]`, `^\\\\`, `^//`, `^\\?\\`, `^\\.\\`
- alternate data stream: a `:` anywhere after the first character (covers `file.txt:zone.txt`, `file.txt::$DATA`, `dir/x.toc:secret`)
- any segment equal to `..`
- device names as a segment stem, case-insensitive, with or without an extension: `CON PRN AUX NUL COM1-COM9 COM¹ COM² COM³ LPT1-LPT9`
- any segment ending in `.` or space (Windows strips both, so `Addon .` and `Addon.` alias `Addon`)
- any segment containing `< > " | ? *` or a control char (invalid on Windows, and they alias nothing useful)
5. Compose `candidate = Path.GetFullPath(Path.Combine(destinationRoot, normalized))`. Note explicitly in a comment that `GetFullPath` **normalizes but does not reject**
  `..` — it happily returns a path outside the root, which is why step 4 runs first and step 6 runs after.
6. Containment check: compute `relative = Path.GetRelativePath(root, candidate)`. Reject when `relative` is rooted, is `".."`, or starts with `".." + separator`.
  Compare with the platform comparer from `PathComparer`. Also require `destinationRoot` itself to be fully qualified and existing (else `DestinationMissing`).
7. Reparse-point check (symlink / junction / mount point escape): for each existing directory prefix of `candidate`, if
  `File.GetAttributes(prefix).HasFlag(FileAttributes.ReparsePoint)`, resolve it with `Directory.ResolveLinkTarget(prefix, returnFinalTarget: true)` and re-run step 6
  against the resolved target. A *final* path that is itself a reparse point is rejected outright — never follow one, never write through one.
8. Return the candidate.
```csharp
public static class PathComparer
{
    // The single decision point: OrdinalIgnoreCase on Windows and macOS, Ordinal on Linux.
    public static StringComparison PathComparison { get; }
    public static bool AreEqual(string a, string b);
}
```
- `ValidateAll` additionally builds an `OrdinalIgnoreCase` (or `Ordinal`) set of the inputs and reports a `CaseCollision` failure listing **both** colliding inputs
  whenever `PathComparison` is case-insensitive. This is what stops a ZIP with `Addon/Readme.txt` and `Addon/README.TXT` from producing one file and a silent overwrite
  on Windows.
- Decoding: do not URL-decode, do not trim, do not `Path.GetFileName` the input before checking. A `%2e%2e%2f` in a ZIP entry name is a literal file name, and would be
  written literally — that is acceptable; what must not happen is decoding it *and then* accepting it. If any caller decodes first (a URL-derived path), it must decode
  **then** call `ResolveSafe`, and the payload table includes the encoded form to prove the decode-then-validate order.
- **No filesystem writes, no `Directory.CreateDirectory`, no temp files** in this component. It is pure inspection. That property is what makes it safe to call before
  the backup step.

## Acceptance criteria
Every row below is a test that must assert the **reason**, not merely "threw". Payloads are validated against a root of the fixture's temp addon directory.

| # | Input | Expected |
|---|---|---|
| 1 | `../evil.txt` | `TraversalEscape` |
| 2 | `..\..\evil.txt` | `TraversalEscape` |
| 3 | `a/../../evil` | `TraversalEscape` |
| 4 | `a/b/../../../evil` | `TraversalEscape` |
| 5 | `/etc/passwd` | `RootedPath` |
| 6 | `C:\Windows\System32\evil.dll` | `RootedPath` |
| 7 | `\\\\server\\share\\x` | `RootedPath` |
| 8 | `\\\\?\\C:\\x` | `RootedPath` |
| 9 | `addon.toc:zone.txt` | `AlternateDataStream` |
| 10 | `file.txt::$DATA` | `AlternateDataStream` |
| 11 | `dir/file.toc:secret` | `AlternateDataStream` |
| 12 | `CON`, `nul.toc`, `LPT1.lua` | `DeviceName` |
| 13 | `Addon.` , `Addon ` (trailing dot / space segment) | `TrailingDotOrSpace` |
| 14 | `a\0b` | `NullByte` |
| 15 | `""`, `"   "`, `"."`, `".."` | `Empty` / `TraversalEscape` |
| 16 | `%2e%2e%2fevil` passed after a single decode | `TraversalEscape` |
| 17 | `Addon/a.txt` + `Addon/A.TXT` in one `ValidateAll` batch, case-insensitive FS | `CaseCollision`, listing both |
| 18 | a directory prefix that is a symlink pointing outside the root | `ReparsePointEscape` |
| 19 | a destination root that does not exist | `DestinationMissing` |

Plus:
20. A valid input (`pfUI/pfUI.toc`) resolves to `<root>/pfUI/pfUI.toc` (compare with `Path.GetFullPath`).
21. `ResolveSafe` writes nothing: the fixture root's file count and last-write time are unchanged after the entire payload table has run.
22. `PathValidationException.Failures` in a batch call contains every rejected row, not just the first.

## Verification
```bash
dotnet test GamePolish.Core.Tests --filter "FullyQualifiedName~PathValidator"
# expect: 19 payload tests + the 3 above pass. A test that only asserts "throws" is a review failure.
# prove no writes happen in the validator:
grep -rn "CreateDirectory\|File\.\|Directory\.Delete\|Move" GamePolish.Core/Paths/Validator*.cs
# expect: only File.GetAttributes / Directory.ResolveLinkTarget / Directory.Exists style reads
```
On Windows, run the symlink payloads (`18`) with **Developer Mode or elevation**: creating a symlink in a test fixture requires `SeCreateSymbolicLinkPrivilege`. If the
test host cannot create one, skip that single test with an explicit `Skip="requires symlink privilege"` — do **not** delete it. On macOS the equivalent payload is a
`Directory.CreateSymbolicLink`.

## References
- `000-CONVENTIONS.md` § Hard invariants (#2, #3), § Definition of done ("Anything consuming a path from a mod source")
- `CLAUDE.md` § *Key Design Constraints* — "Validate all paths in `GamePolish.Core` to prevent directory traversal"
- `../game-polish-notes/docs/games/octowow.md` § *Addon Layout* (the destination shape this guards)
- Related tasks: `202` (`AddonRoot` supplies the root), `205` (backs up exactly the paths this validated), `207` (ZIP extraction calls this per entry), `208`–`210`
  (every mutation path).
