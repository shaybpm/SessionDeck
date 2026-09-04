# SessionDeck — where the code lives

A map for finding your way in, not a spec. It answers "which file do I open to change X"
and "what will that break". The reasoning behind individual decisions lives elsewhere and
is not repeated here:

- [`CLAUDE.md`](CLAUDE.md) — build/run/test/release, and the settled decisions by number.
- [`hooks/README.md`](hooks/README.md) — hook wiring, the waiting-detection thresholds and
  the measurements behind them. Authoritative for anything status-related.
- [`README.md`](README.md) — what the product does, from a user's side.

## The shape, in one paragraph

One exe is both the window and the CLI. Launched with no arguments it raises the WPF window
plus a named-pipe server at `\\.\pipe\sessiondeck`; launched with arguments it connects to
that pipe as a client, prints the response and exits (target: under 100ms, because Claude
Code hooks pay that cost on every event). A singleton mutex makes a second UI launch
activate the first instead of starting a rival. Two independent producers feed the deck:
the **hooks**, which push session status through the CLI, and the **VSCode extension**,
which holds an open pipe connection and reports tabs and the git branch. A third source is
internal: a **transcript scanner** on a 10-second timer, which is the only thing that sees a
tool call finish.

```
Claude Code hooks ─> sessiondeck-hook.ps1 ─> SessionDeck.exe session status ...
                                                      │  (pipe, one line, closes)
                                                      ▼
                                            SessionDeck (WPF, single instance)
                                                      ▲  (pipe, stays open both ways)
VSCode extension ─────────────────────────────────────┘
                                                      │
                       transcript scanner (10s timer, inside the app)
```

## File map

### Entry and plumbing

| File | What it owns |
|---|---|
| `Program.cs` | The UI-vs-CLI fork, the singleton mutex, startup-entry maintenance. 30 lines, read it first. |
| `Cli/CliClient.cs` | Client side: argv onto the pipe, response out. Owns the `help` text. |
| `Cli/CommandExecutor.cs` | Server side: every CLI verb. Runs **on the UI thread** (the pipe handler dispatches), so it can touch view-models directly. |
| `Cli/HookInstaller.cs` | `install-hooks` / `uninstall-hooks`. Runs locally without a live app. Must match `hooks/README.md` exactly. |
| `Services/PipeServer.cs` | The pipe. Two client kinds on one name, told apart by the first line: a CLI request (`{"Argv":[...]}`, one response, close) or a VSCode connector (`{"Type":"vscode-sync"}`, stays open). |

### The engine

`MainWindow.xaml.cs` is 1,861 lines and holds nearly all of it. Nothing else in the repo is
close in size, so "where is the logic for X" is usually answered here. Its sections, in file
order:

| Section | Roughly |
|---|---|
| startup / shutdown / persistence | `LoadFromConfig`, `BuildConfig`. Config to view-model and back. |
| workspaces | add, remove, hide, `RefreshMetadata` (branch + Peacock colour off the disk). |
| phantom / orphan / ghost sweeps | three different flavours of "this session is not really alive". |
| transcript titles | `RefreshTranscriptTitles`, background scan, only files whose mtime moved. |
| waiting inference | `EvaluatePendingWait`. The single subtlest function in the codebase. |
| historical sessions | past sessions read straight from the transcripts folder on expand. |
| search / filter | including the async content search over transcript files. |
| window binding | title-pattern matching, re-bind, drag-in. |
| sessions engine | `StartSession`, `SetSessionStatus`, `EndSession`: what the hooks actually drive. |
| VSCode connector | sync handling, tab correlation, open/resume. |
| focus / pin / stage / zone | window placement. |
| attention escalation | `UpdateAttentionEscalation`: balloon, taskbar badge, flash. |
| toolbar / menus / dialogs | the UI event handlers. |

`MainWindow.Tasks.cs` is a partial class holding the whole tasks-panel feature, which is
inert unless a file path is configured. Keeping it separate is deliberate: it is the one
feature that can be removed without touching the engine.

### State and view-models

| File | What it owns |
|---|---|
| `ViewModels/MainViewModel.cs` | The workspace collection, global switches, the status-summary dots. |
| `ViewModels/WorkspaceViewModel.cs` | One card. Colour precedence, branch, the reported VSCode tabs, `ActiveClaudeTabLabel` (which expires, on purpose). |
| `ViewModels/SessionViewModel.cs` | One session card. Title precedence, status, and the blink decision (`BlinkActive`). Several runtime-only fields here exist purely to make the hook/scanner seam work. |
| `ViewModels/NavSquareViewModel.cs` | One square of the tasks page's navigation grid. Fill says structure (parent vs unit of work), border says status. |
| `Models/AppConfig.cs` | The persisted schema. Also the status-to-style map and the per-tool wait thresholds, both of which are config rather than code so they change without a build. |

### Services

| File | What it owns |
|---|---|
| `TranscriptReader.cs` | Single pass over a `.jsonl`: titles, label candidates, and the pending tool call. All the "what is Claude doing" intelligence is here. |
| `WorkspaceMetadata.cs` | Reads the branch from `.git/HEAD` directly (no git.exe) and the colour from `.vscode/settings.json`. Also the window-title pattern. |
| `ConfigStore.cs` | Auto-save with a 1s debounce, atomic write (temp + rename). There is no save button. |
| `BlinkEngine.cs` | One shared 100ms timer for every blinking border. Never a timer per card. |
| `AppBarService.cs` | The Reserved Zone, via `SHAppBarMessage`. Also the window lock while zoned. |
| `AttentionNotifier.cs` | Balloon, taskbar overlay badge, flash. |
| `WindowTracker.cs` / `WindowEnumerator.cs` / `WindowActions.cs` | `SetWinEventHook` (no polling), candidate enumeration, focus/move/close. |
| `TasksFileService.cs` / `TasksFileWatcher.cs` | The external tasks JSON, read-only, reloaded within ~1s of a change. |
| `LogService.cs` | The diagnostic log. Read it before theorising about a status bug. |
| `ColorUtil.cs` | Named colours and `#RRGGBB`. Shared by cards, borders and the badge. |

### UI

`MainWindow.xaml` is the shell: toolbar, search row, status bar, and a `Grid` that swaps the
deck for the tasks page. `TasksPageView` is that page: live session squares on the left, the
task list in the middle, and on the right the **navigation grid** — two vertical columns of
numbered squares (the tree's top level, and the selected top-level item's direct children)
drawn from the optional `navIndex` the tasks file may carry. Column A only previews; column B
navigates. Without a `navIndex` no grid is drawn at all. `WorkspaceCardView` is one card, and its code-behind is almost
entirely DWM thumbnail maths (registering, clipping to the scroll viewport, letterboxing).
`App.xaml` holds the app-wide styles and, importantly, applies the direction converter to
every tooltip, menu item and combo item.

### Outside the app

`vscode-extension/src/extension.ts` (370 lines) is the whole extension. It pushes a sync on
tab/branch/focus change (carrying its own `Version` since 0.6.12), heartbeats every 2s while
focused, and delegates opening a session to Claude Code's own `claude-vscode.editor.open` with a
terminal fallback. `closeSession` (0.6.12) rides on that same reveal: Claude Code's id→panel
registry brings the named session's tab to the front, and the tab that became active is closed
— the only way to close a dead session's tab when a live one carries the same label.
`hooks/sessiondeck-hook.ps1` translates each hook event into one CLI call, swallows every
failure, and must stay PowerShell 5.1 compatible and UTF-8 **with BOM**.

## Two mechanisms worth understanding before you touch them

**The status seam.** Status has two independent sources that must agree. Hooks are the
leading edge: immediate and certain, but `PermissionRequest` has no "resolved" counterpart,
so nothing tells the deck the dialog closed. The scanner is the trailing edge: it sees the
`tool_result` arrive, but it is driven by the transcript's mtime, which stops moving exactly
while a dialog is open. `PermissionDialogScanMark` bridges that: it holds the wait until a
scan has demonstrably read the file since the dialog opened. Remove the bound and a fast
Deny leaves the card orange for the rest of the turn; clear the wait eagerly instead and you
get the orange-blue-orange flicker. Both failure modes have already shipped once.

**Tab correlation.** The VSCode tab API exposes no session id, so a tab is matched to a
session by string-comparing its label, which is itself truncated with a trailing ellipsis.
Hence a whole candidate list per session rather than one title, and hence the rule that an
ambiguous label (two sessions answering to it) resolves to **nothing**. The governing
principle, which shows up all over this area: a card that keeps blinking is a recoverable
failure, an alert that vanishes quietly is not.

**One folder, more than one window.** A card is a FOLDER, and the same folder can be open
in two VSCode windows at once (a second window signed into another account is what Shay
runs). Nothing in a hook payload says which window a session belongs to — the payload
carries `cwd` — so both windows' sessions land on the same card, by design. What must not
collapse is the window-level state: each `VscodeConnection` keeps its OWN tab list and its
own focus, the card's tab list is their union, and the active tab comes only from a window
that currently has focus. Commands (`openSession`, `newSession`) go to the window that
already holds the session's tab, else the window last focused. Before that, the card's
window bind is moved onto the same window.

**Which INSTANCE, when you get to choose.** Several instances holding one folder is not only a
thing to survive - on this machine it is how the accounts are separated, one Claude login per
`--user-data-dir`. `SessionGroups` (config) names each instance by a marker in its window titles
and binds it to a modifier, and `NewSessionInVscode` takes the resulting group instead of asking
`FindConnector` to guess. A group is never approximated: with its instance not running the deck
starts it by running that group's own launcher script (`LaunchGroup` - never a command line of
the deck's own, because the account is bound by an environment variable only that script sets)
and parks the request under the group's id, so the next connector to appear cannot claim it
unless it is the right one. Opening or resuming an EXISTING session is
untouched - it goes where its tab already is.

**Which OS window is a connector in?** The extension cannot see its own `HWND`, and the two
obvious answers both fail. The TITLE cannot tell two windows on one folder apart and misses
entirely when a window sets a custom `window.title`. The PID cannot help either: Electron
creates every window inside the Electron main process, and the extension host is a utility
child of that same main process, so all of one instance's windows and hosts report one pid
(measured 22-08-2026: four windows, four hosts, pid 53380 for all eight). `OwnerPid` therefore
identifies the VSCode INSTANCE, which is still worth having — a second window signed into
another account is a second instance — and nothing finer.

The answer is focus correlation (`CorrelateConnectorWindow`): when the extension reports that
its window has OS focus, `GetForegroundWindow()` says which window that is, and the pair is
recorded on the connection as `Hwnd`. One API call per sync, no extension change, and it
self-corrects — whatever a window was thought to be, the next time the user works in it, it
says so itself. A connector that has not been focused since it connected has no `Hwnd` yet,
and `RebindToConnectorWindow` falls back to the title, weakest-last: the card's own pattern,
then any window of that instance whose title merely NAMES the folder (what a custom title
still does), then a lone window in the instance — skipping windows another card already owns.

## Where state lives

| Path | Contents |
|---|---|
| `%APPDATA%\SessionDeck\config.json` | Everything persistent. Hand-editable; unknown keys are filled from defaults on load. |
| `%APPDATA%\SessionDeck\logs` | The diagnostic log. `sessiondeck log --debug on` raises the level and persists it. |
| `%APPDATA%\SessionDeck\toggles\<id>` | One file per user toggle, `1` or `0`, for external processes. |
| `~\.claude\projects\<slug>\*.jsonl` | Claude Code's transcripts. Read-only to us, and the slug is derived in `DefaultTranscriptDir`. |
| `<workspace>\.vscode\settings.json` | Read for the card colour only. Never written. |

## Things that will bite

- **The running app locks its own exe.** `quit`, wait, build, start. Never force-kill: the
  Reserved Zone is released in `OnClosing` only, and a killed process leaves the user's work
  area permanently shrunk.
- **`CommandExecutor` is on the UI thread.** Anything slow added there freezes the window
  and blows the sub-100ms hook budget.
- **Background scans dispatch back.** `RefreshTranscriptTitles` and the content search run on
  the thread pool and must return to the dispatcher before touching a view-model.
- **`FlowDirection` mirrors layout, not just glyphs.** Changing a container's direction flips
  alignment, docking and margins inside it.
- **Timeouts are tuned, not arbitrary.** Phantom 30min, orphan 15min, pending open 90s,
  active-tab 6s against a 2s heartbeat, metadata scan 10s. Each was set against an observed
  failure; changing one without knowing which is how the old bug comes back.
- **Precision over coverage, always.** A false alarm teaches the user to ignore the deck,
  which costs more than a missed one. Measure the false-alarm rate before lowering any
  threshold.
