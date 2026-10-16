---
id: 307
title: Toasts, progress, and the log drawer
phase: 3
bucket: mvp
depends_on: [212, 301]
blocks: []
status: todo
area: desktop
---

# 307 — Toasts, progress, and the log drawer

## Objective

The cross-cutting feedback layer every other phase-3 screen calls into: a toast host for success and
error messages, a progress contract for both per-row and per-view operations, and a log drawer that
shows the real operation-log entries Core produced — reachable from an error toast's details link and
never carrying a secret. At the end of this task, tasks `302`–`306` can report an outcome by calling
one interface instead of each inventing its own banner, spinner and error text.

## Context

- Task `301` built the shell and reserved a top-most overlay region in the root `Grid` for this task
  to fill; it also owns the DI container this task registers into.
- Phase-2 Core task providing this task's data: **`212`** — the operation log. It owns the entry
  model (timestamp, level, category, message, and the operation/correlation id), the retention and
  rotation of the log, the query surface the drawer reads, and the file the entries land in. This
  task must not assume the log's on-disk path or format; it reads entries **through `212`'s
  interface** and never opens a log file itself.
  Core's operation result types from `208` (install) and `210` (uninstall/enable/disable) carry the
  correlation id an error toast's details link resolves with.
- UI dependency: `301` (shell, overlay region, DI).
- Consumers: `302` (Add Game failures), `303` (toggle/uninstall failures — already specifies a row
  spinner and a locked checkbox during the operation), `304` (Add Mod stages and failures),
  `305` (card install spinner and the error toast with a details link),
  `306` (restore/delete progress and failures).
- Inherited invariant that governs the redaction guarantee: secrets — the GitHub PAT, S3 keys, auth
  tokens — live in the OS keychain, and must never appear in a JSON file, **in a log**, or in an
  export (`000-CONVENTIONS.md`). A log drawer renders log text on screen; it is the last place a
  leaked token could become a screenshot.
- The design notes' toast wording is Skyrim-flavoured and illustrative; the only verbatim strings this
  task must honour are the ones other work orders quote (e.g. `306`'s
  `Restored — a backup of the pre-restore state was also saved.`).

## Scope

- `Services/IUserFeedback.cs`: the interface consumers inject (contract below).
- `Services/UserFeedbackService.cs`: the implementation — toast queue, timers, progress scopes, and
  the drawer open/close state.
- `ViewModels/ToastViewModel.cs` and `Controls/ToastHost.axaml(.cs)`: the toast stack.
- `ViewModels/LogDrawerViewModel.cs` and `Views/LogDrawerView.axaml`: the drawer.
- `ViewModels/ProgressScope.cs` (or equivalent): the disposable progress handle.
- DI registration of `IUserFeedback` (singleton) in `301`'s bootstrap, plus XAML wiring of both
  controls into the shell's overlay region.

## Out of scope

- Writing, rotating, retaining or formatting log entries — that is `212`, in Core, with no UI
  reference.
- A general-purpose logging abstraction for the app (no `ILogger` facade, no Serilog, no file
  appenders). Core logs operations; this task displays them.
- Notifications outside the window: no Windows toast notifications, no dock/taskbar badges, no tray
  balloons.
- Modal or blocking error surfaces (a `Window` shown on every failure). Errors are non-blocking by
  design.
- Telemetry, crash reporting, or sending logs anywhere off the machine.
- Determinate progress from GitHub operations: there is no reliable percentage available, so
  percentages are out of scope (see Implementation notes).

## Implementation notes

- **The contract consumers code against.** Keep it this small; every extra member is a member five
  other views must implement against:

  ```csharp
  public interface IUserFeedback
  {
      void Success(string message);                                  // auto-dismisses
      void Error(string message, string? operationId = null);        // sticky; details link when operationId != null
      void Info(string message);                                     // short-lived, non-alarming
      IProgressScope BeginOperation(string title);                   // view-level progress
      IDisposable BeginRowOperation(object rowViewModel, string title); // row-level spinner/lock
      void OpenLogDrawer(string? operationId = null);                // drawer, optionally filtered
  }
  ```

  `IProgressScope` exposes `void Report(string stage)` and is `IDisposable`; disposing it ends the
  progress region. Names are a proposal — this task owns the file, so it owns the names; the two
  things that are **not** negotiable are the error/details-link pairing and the row-vs-view split.
  Task `301` registers the singleton; tasks `302`–`306` inject the interface and never construct a
  toast directly.
- **Landing order.** `307`'s `depends_on` is `[212, 301]`, and the README's phase-3 line puts it after
  `302`–`306`. That is a scheduling artefact, not a design statement: consumers inject
  `IUserFeedback`, so land this task early within phase 3. Until it exists, a consumer may bind a
  local `IsBusy`/message property and swap it in later — do **not** invent a second feedback
  mechanism that outlives this task.
- **Toast behaviour.** A stack anchored bottom-right (or top-right) inside the overlay region the
  shell reserved, `Panel.ZIndex` above content, `ItemsControl` bound to an
  `ObservableCollection<ToastViewModel>`. Success/info auto-dismiss after about four seconds via a
  `DispatcherTimer`; **error toasts persist until dismissed** — an error the user never saw is an
  error they will hit twice. Cap the visible stack (three) and collapse older entries rather than
  growing an unbounded column. Each toast has a close affordance; error toasts with an operation id
  also show a `Details` link.
- **Timers on the UI thread.** Use `DispatcherTimer` and mutate the collection on the UI thread. Never
  remove items from a background continuation.
- **Row progress.** `BeginRowOperation(rowViewModel, title)` sets the row's busy state for the
  duration — in `303` that means the row spinner appears and the checkbox is disabled until the
  returned handle is disposed. This is the mechanism behind the "spinner on the row and locked
  checkbox" requirement in `library.md` — Enable/Disable Mechanics; `303` must use it rather than its
  own flag, so a failure and a success look identical until they end.
- **View progress.** `BeginOperation(title)` drives an indeterminate `ProgressBar`
  (`IsIndeterminate="True"` — a real `ProgressBar` property) plus a stage line under the view's
  header. `Report(stage)` sets the stage text, e.g. `Cloning…`, `Validating…`, `Backing up…`,
  `Installing…`. Do **not** synthesise a percentage from a timer; a fake progress bar that reaches
  90% and hangs is worse than an honest spinner.
- **Error toast → details link → drawer.** The link calls `OpenLogDrawer(operationId)`. The drawer
  asks `212` for the entries correlated with that operation and renders them; with no id it renders
  the most recent entries for the current game. Each row shows timestamp, level, category and message,
  newest first, in a `SelectableTextBlock` (so the user can copy a line) inside a `ScrollViewer`. If
  the exact control name differs in this Avalonia version, confirm it before use and fall back to a
  read-only `TextBox` styled as text.
- **The drawer is a drawer, not a dialog.** An overlay panel (e.g. a right-hand `Border` or
  `SplitView` pane) with a close button and `Esc` to dismiss; it must not steal focus from the view
  behind it and must not block the app when a long operation is still running.
- **Redaction guarantee — state it in the UI, enforce it in the path.** Two obligations:
  1. The drawer renders what `212` recorded; it must never *add* anything to a line. Before display,
     run a display-time scrubber over the text for the two shapes that leak tokens in practice:
     `access_token=…` query parameters and `Authorization: …` headers — replace the value with
     `***`. A `Copy` button copies the scrubbed text, never the raw text
     (`TopLevel.GetTopLevel(control)?.Clipboard` — confirm the property name in this version).
  2. The GitHub PAT and any S3/token value must never reach the log in the first place (`212`'s
     responsibility, and the reason the PAT lives in the OS keychain). Add a test that pipes a line
     containing `access_token=abcdef` through the scrubber and asserts the token is absent from the
     rendered output.
- **No secret ever enters a toast message.** Toast text comes from fixed templates plus a mod/game
  name — never from a raw exception `ToString()`, which can carry a URL with credentials. Log the
  raw exception through `212` (which redacts) and show the user the template.

## Acceptance criteria

- An induced Core failure surfaces as an error toast carrying a `Details` link; clicking the link
  opens the log drawer containing **the real log line** for that failure — the same text `212`
  recorded for the same operation id, verified by reading the entry through `212`.
- The error toast stays on screen until dismissed (verify it is still present after ten seconds).
- A success message auto-dismisses within roughly four seconds and never stacks above three visible
  toasts.
- A row operation started through `BeginRowOperation` shows the row spinner and disables that row's
  checkbox until the handle is disposed; a second click during the operation cannot start a second
  operation.
- A view operation started through `BeginOperation` shows an indeterminate progress bar and the stage
  text updates as `Report` is called; it clears when disposed, including when the operation throws.
- The scrubber test passes: a line containing `access_token=abcdef` renders without `abcdef`, and the
  `Copy` output also lacks it.
- The drawer opens with no operation id and renders the most recent entries for the current game,
  newest first; `Esc` closes it.
- `dotnet build` clean; tests run with the real counts quoted.

## Verification

```bash
cd C:/Users/Brandon/Projects/game-polish
dotnet build                                     # expect: 0 Warning(s), 0 Error(s)
dotnet run --project GamePolish.Desktop

# induce a real Core failure: Add Mod with an unreachable repo
#   Library -> [+ Add Mod] -> https://github.com/brues-code/definitely-not-a-repo -> Install
# expect: sticky error toast with a Details link
# click Details -> drawer opens with the log line for that operation
# cross-check the same entry through Core (task 213's CLI harness, or 212's query surface):
#   dotnet run --project GamePolish.Core.Cli -- log --last 5     # adjust to 213's actual verb
# expect: the drawer's line and the CLI's line are the same text for the same operation id

# secret scrubbing
grep -rn "access_token" GamePolish.Desktop/Services/UserFeedbackService.cs   # expect the scrubber rule
dotnet test --filter "FullyQualifiedName~Redact"   # expect: pass, and the assertion names the token

# timing/behaviour
# success toast: install a real addon -> toast appears and is gone within ~4s
# row progress: toggle an addon -> spinner + locked checkbox, then settles
dotnet test                                      # quote the real pass/fail counts
```

## References

- Design notes: `../game-polish-notes/docs/views/discover.md` — **Install Flow (inline)** step 5
  ("error toast with a details link (opens a log drawer)"); **Empty / Edge States**.
  `../game-polish-notes/docs/views/library.md` — **Enable/Disable Mechanics** (row spinner, locked
  checkbox); **Mod Row — Overflow Menu Actions**.
  `../game-polish-notes/docs/views/backups.md` — **Restore Flow** (the verbatim success toast this
  layer displays). `../game-polish-notes/docs/views/app-settings.md` — **Security Notes** (no secret
  in a log; the PAT is never logged).
- Repo: `000-CONVENTIONS.md` (Secrets go in the OS keychain — never in a JSON file, never in a log,
  never in an export), `CLAUDE.md` (Security).
- Related tasks: `212` (Core operation log — the drawer's data source), `301` (shell, overlay region,
  DI registration), `302`–`306` (consumers of this contract; `306` for the verbatim restore toast),
  `503` (PAT handling, v0.2 — the value the scrubber exists for).
