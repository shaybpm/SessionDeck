# SessionDeck — working notes for Claude Code

A WPF (.NET 10, Windows-only) control deck for Claude Code sessions. The UI and the CLI
are **the same exe**: launched with no arguments it raises the window plus a named-pipe
server (`\\.\pipe\sessiondeck`); launched with arguments it acts as a client against the
running instance. A companion VSCode extension (`vscode-extension/`) reports tabs and the
git branch over that pipe and opens sessions on request.

Public-facing overview: [`README.md`](README.md). Hook wiring, the waiting-detection
thresholds and the toggles: [`hooks/README.md`](hooks/README.md) — that file is the
authoritative source `Cli/HookInstaller.cs` must match exactly.

**Which file holds what:** [`ARCHITECTURE.md`](ARCHITECTURE.md) — the code map. Read it
before hunting for where a behaviour lives; `MainWindow.xaml.cs` holds most of the engine
and that is not obvious from the file list.

## Build, run, deploy

**The running app locks its own exe.** Building over a live instance fails with MSB3027.
Always:

```powershell
.\bin\Debug\net10.0-windows\SessionDeck.exe quit     # graceful — releases the AppBar
Start-Sleep -Milliseconds 1500
dotnet build -c Debug
Start-Process .\bin\Debug\net10.0-windows\SessionDeck.exe
```

Never `Stop-Process -Force`: the Reserved Zone is released in `OnClosing` only, and a
forced kill leaves Windows' work area permanently shrunk with no way for the user to
tell why.

To compile without touching a running instance (a syntax check that skips the copy step):

```powershell
dotnet msbuild SessionDeck.csproj -t:Compile -p:Configuration=Debug -v:m
```

`WinExe` means CLI output only reaches a parent console through
`AttachConsole(ATTACH_PARENT_PROCESS)` — expect no captured stdout from tooling.

## Tests

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\install-hooks.tests.ps1 -Exe <path to SessionDeck.exe>
```

38 cases over the `install-hooks` / `uninstall-hooks` merge: a missing settings file, an
empty one, hooks from an old path, another tool's hooks on the same event, malformed
JSON, a second run in a row, dry-run, and a shared group where only our entry may be
removed. Run it against the exe you actually built.

**These tests passing does not mean the status lifecycle works.** They cover the
installer only. A card can stay blue, blink wrongly or go quiet while all 38 pass —
see "Debugging status and blink" below.

## Versioning

Every code change bumps `<Version>` in `SessionDeck.csproj`. Three versions move
independently and are printed together by `install.ps1` so a mismatch is visible:

| Part | Where |
|---|---|
| App | `SessionDeck.csproj` → `<Version>` |
| Hook script | the `# Version:` header in `hooks/sessiondeck-hook.ps1` — `release.ps1` syncs it from the csproj |
| VSCode extension | `vscode-extension/package.json` → `version`, bumped only when the extension changes |

`hooks/sessiondeck-hook.ps1` is saved **UTF-8 with BOM** and must stay that way:
PowerShell 5.1 reads a BOM-less `.ps1` as ANSI and mangles the non-ASCII characters in
its comments.

## Whose fork this is

This checkout is Shay's fork. **Shay's requirements decide what this build does.** The
sections below are engineering context — how the thing is built, versioned and debugged, and
why some code looks the way it does upstream. None of it is an approval gate, and none of it
overrides a decision Shay makes. Do not cite an upstream convention back at him as a reason
not to do something; if a change diverges from upstream, just say so once, in a sentence, so
he can decide what to send on.

## Git

- Branch before editing. Never work directly on `main`.
- **Committing on a work branch needs no approval.** Commit whenever a change is coherent
  and verified, with a message that explains the why. A local commit changes nothing outside
  the machine and is reversible; withholding it only produces one large unreviewable blob.
- **Pushing, opening a PR, merging to `main` and cutting a release all run without asking**
  (Shay, 09-08-2026: "זה ריפו שלנו אז אין בעיה שתנהל אותו אתה"). This repo is his own fork;
  `main` tracks it, nobody else consumes it, and every step is revertible. Do the work, then
  report what moved in a line or two. The approval gate that used to sit on this line was
  asked for and granted three times in one session before he removed it: a gate written into
  a file outlives the conversation that satisfied it, so the file is where it had to change.
- **The one thing that still stops:** anything aimed at `eyalBPM/SessionDeck` (its push URL
  is deliberately `DISABLED_read_only_use_a_PR`). Sending work upstream is Shay's own
  initiative, never a step inside other work. Watch `gh` here: it resolves a fork's default
  base to the PARENT, so `gh pr create` with no `--repo` opened a PR against Eyal's repo on
  09-08-2026. `gh repo set-default shaybpm/SessionDeck` is set on this machine; on a fresh
  clone, set it before the first `gh` command.
- Temporary zip/publish artifacts: add the pattern to `.gitignore` *before* creating them.

## UI language and text direction

The UI chrome is **English and LTR** upstream (v0.9.0), because that repo is public.

Text that comes from **outside** the app is a different matter and must stay
direction-aware: workspace, session and task names, descriptions, tooltips, status values
from the tasks file, search input, git branch names. They are frequently Hebrew.

Exception in this fork, and the reason for it: the **task card** header (`TaskItemView.xaml`)
is pinned RightToLeft rather than derived per string. Deriving it left a Hebrew task and an
English task in the same list aligned differently, which breaks scanning the list. Same card:
id hard left, then status, then the name.

- `Services/FlowDirectionConverter.cs` (keyed `Rtl` in `App.xaml`) resolves direction from
  the first strong character. Bind a control's `FlowDirection` through it rather than
  hard-coding a direction.
- `App.xaml` already applies it to every `ToolTip`, `MenuItem` and `ComboBoxItem`, and to
  the ComboBox selection box. New controls that display external text need it explicitly.
- Free-text `TextBox`es bind `FlowDirection` to their own `Text`. ASCII-only fields
  (paths, ids, colors, sizes) override with `FlowDirection="LeftToRight"`.
- **Remember that `FlowDirection` mirrors layout, not just glyphs.** `HorizontalAlignment`,
  `DockPanel.Dock` and `Margin` all flip with it. When changing a container's direction,
  re-check the alignment of everything inside it.
- One deliberate exception: the toolbar's `IconStrip` in `MainWindow.xaml` is
  `RightToLeft` as a **layout device** — it controls which icons overflow to the second row
  first, so ⚙ keeps the top row. It holds no text. Don't "fix" it.

## Debugging status and blink

Card status is driven from two independent sources, and most bugs live in the seam
between them:

1. **Claude Code hooks** → `sessiondeck session status ...`. The leading edge: immediate
   and certain, but `PermissionRequest` has no matching "resolved" event.
2. **The transcript scanner** (every 10s) — a `tool_use` with no `tool_result`. The only
   thing that sees a call *finish*. Driven by the transcript's mtime, which stops growing
   while a permission dialog is open.
3. **`background_tasks` in the `Stop` payload** (v0.9.38) — the live registry of what is
   still running when a turn ends. Neither of the other two can see a background subagent:
   its `Agent` call returns in ~3ms with `async_launched`, so the transcript holds a
   completed call, and the `Stop` hook is genuine. A `done` carrying `--agents N` (N>0)
   lands on `working` instead, because those agents wake the session themselves. Count the
   snapshot, never a `SubagentStart`/`SubagentStop` tally — one background agent produced
   four pairs of those in a controlled run. The same scan (v0.9.40) also reads the
   `<status>stopped</status>` task-notification — agents that died with the session's previous
   process, which no hook reports either — and marks the card `error` with a ⚠ chip. Details
   and the measurements: [`hooks/README.md`](hooks/README.md).

**A `SessionStart` is not always a fresh start.** Clicking a card makes the deck send an open
command, VSCode answers with `SessionStart source=resume`, and `StartSession` used to reset any
known session to `idle` silently — so looking at a session destroyed the state you clicked to
read. Since v0.9.40 `resume` and `compact` keep the status and the agent count; only `startup`
and `clear` reset. That path now logs what it did, which it never used to.

**Every session records WHICH VSCode instance it runs in** (this fork, v0.9.65). Three instances
share `C:\Users\Shay\.claude` and a card is a folder, so until this the deck merged their tab
lists into one union and no store anywhere — deck, hook payload, transcript — answered "where is
this session". `StampSessionGroups` writes `SessionConfig.GroupId` per connection, from
`GroupIdOf`, whenever a window's own tabs match a session; it is persisted, shown on the card,
printed by `list` as `@<group>`, and logged as `window session=… runs in "green"`. **A stamp is
never cleared** — a window that closed does not un-say where its sessions were, and that record
is the entire point.

**And routing follows the stamp, not the focus.** `FindConnector` used to fall back to whichever
window was focused last, so once a session's tab was gone its card opened a blank tab in a
sibling instance that had never heard of it. Now a stamped session resolves only to its own
group; if that instance is down, `OpenSessionInVscode` parks the request under the group and
launches it through its own launcher, exactly as a new grouped session does. Measured 05-09-2026:
the green instance went down carrying five sessions, nothing said which they had been, and every
click landed in purple.

Before theorizing about a blink or status bug, **read the diagnostic log** at
`%APPDATA%\SessionDeck\logs`. Payload-level checks and the test suite both pass while the
lifecycle is broken; the log is what shows the actual ordering of hook arrival versus
scan. The thresholds, the false-alarm measurements behind them and the
`PermissionDialogScanMark` bound are documented in [`hooks/README.md`](hooks/README.md).

**`he` is the exception to both** (this fork, v0.9.6). No hook produces it and no scan
clears it: it is set explicitly with `session status --state he` when a session has been
closed out for good, and `SetSessionStatus` refuses to let a later `done` or `idle`
overwrite it — the `Stop` hook of the turn that set it arrives right behind it and would
otherwise undo it within a second. `working` / `waiting` / `error`, or a `SessionStart` on
the same id, do clear it. If an `he` card goes purple on its own, that ordering is where to
look, and the log line to grep for is `kept he`.

**`replaced` is its sibling for a session that no longer exists** (this fork, v0.9.61). The
switch-session relay (`~/.claude/scripts/session-relay.ps1`) opens a successor tab, waits for
the new session to be alive, kills the caller's process and only then marks the caller
`replaced` — a killed process fires no `SessionEnd`, so before this the card sat on `done`
looking like a live session waiting for Shay, next to a dead tab carrying the same label as
the successor's. Three things differ from `he`, all because the process is known dead: the
transcript scanner never infers `waiting` for it (`EvaluatePendingWait` returns early), the
orphan sweep closes it after `ReplacedTtl` (15s of swept time) with no silence guard and logs
`replaced close`, and in `ReapplyTabCorrelation` it picks a tab LAST so the live successor with
the same label is never left to the sweep. The hold against `done`/`idle` is the same
`keepMark` as `he`, logged `kept replaced`.

**The dead tab is closed through the connector, by a UNIQUE label, and a dead session is never
revealed** (v0.9.62/0.6.12, reshaped in v0.9.63/0.6.13). `RequestCloseReplacedTab` sends
`closeSession` with the session's labels; the extension closes the one Claude tab carrying one
of them, refuses when several do, and never calls `claude-vscode.editor.open` for it. The
0.6.12 design did reveal first (Claude Code's own id→panel registry is the one thing that can
tell two same-label tabs apart) and was withdrawn the same night: revealing a dead session makes
Claude Code start a fresh CLI on the old transcript, and the revived session carries on from
wherever its context says it was — dd17e1bb came back at 00:13:39 on 05-09-2026 and worked
twenty minutes on the package its successor already held. For the same reason
`HandleSessionClick` never sends `openSession` for a `replaced` card: it closes the tab when the
window's extension can, else raises the window and says so in the status line. Sent on the mark,
then on each sweep while the tab is still there, three tries at most (`CloseTabMaxAttempts`),
every outcome spending an attempt. **Mixed fleet after an upgrade:** the sync carries the
extension `Version`, a window keeps the version it loaded with until it reloads, and
`VscodeConnection.SupportsCloseSession` gates every ask (`closeSession NOT sent` once in the
log for an older window). The extension's refusals are written in its Output channel
("SessionDeck"), not in the deck log. **The revival itself is refused outside this repo:**
`session-relay.ps1` v2.7.0 writes `~/.claude/state/replaced-sessions/<id>.json` after its kill
and `~/.claude/hooks/replaced-session-guard.ps1` blocks every prompt to such a session, tells it
to stand down on `SessionStart`, and re-marks the card `replaced` — so a card that flips
white→grey→white on its own is a ghost being refused, not a bug.

**A prompt delivered by another session** (the relay's message mode, v2.6.0) arrives in the
transcript with `isMeta=true`, wrapped in `<cross-session-message …>`, with the harness's
guidance appended after the closing tag. `TranscriptReader.UnwrapCrossSession` and
`MainWindow.Sanitize` strip it, and the reader accepts that one `isMeta` entry as the first
prompt — without it the card read "session ca21e0de".

When tuning any detection: **precision over coverage**. A false alarm teaches the user to
ignore the deck, which costs more than a missed one. Measure the false-alarm rate before
lowering a threshold.

## Why the code looks like this (upstream design notes)

Numbered as they were in the original spec, because code comments cite them by number. These
explain existing behavior so a change is made knowingly — they are **not** decisions Shay is
bound by. If he wants one changed, change it.

| # | Decision |
|---|---|
| 11 | **Status scheme.** `working` = steady blue (orange is reserved exclusively for `waiting`), `waiting` = blinking orange, `done` = blinking green → steady on acknowledge, `error` = blinking red → steady, `idle` = grey. The status→colour/blink map lives in config (`StatusStyles`), so it changes without touching hooks or code. **Changed in this fork (v0.9.6):** `done` moved to purple and green went to a new terminal status, `he` — see below. **v0.9.61:** a second terminal status, `replaced` = steady white, for a session killed after handing off to a successor. |
| 12 | **A session that closes** disappears from the normal view and stays available in the card's expanded view (▼) with a resume option. Retention: the last ~20 closed sessions per workspace (`ClosedSessionRetention`). |
| 13 | **VSCode only in the UI.** The engine underneath stays generic (any top-level window can be tracked, pinned and driven), but the UI and the flows are filtered to VSCode. Terminal support: maybe some day, not now. |
| 15 | **The app is a control deck for Claude Code sessions**, not a generic window grid. Tile data from the pre-cards era is still round-tripped in config as a legacy field so nothing is lost, but it is never displayed. |
| 16 | **Workspaces are persistent entities** — remembered with no window open. Active ones (bound window or live session) float to the top; old ones can be hidden. Wide cards with a minimum size, wrapping by width, inside a vertical scroll area. |
| 17 | **Main card content:** project name + the current git branch. Custom title and description are supported on both card levels. |
| 18 | **Card colour comes from VSCode** — `.vscode/settings.json`, either `peacock.color` or `workbench.colorCustomizations."titleBar.activeBackground"` — when present. Precedence: manual override > Peacock > default. |
| 21 | **Adding a workspace**, in priority order: (1) picking a folder — primary, the path is known immediately so branch and colour resolve before any window exists; (2) reported by the VSCode extension; (3) drag-in — secondary, blocked for non-VSCode windows and duplicates; (4) the hook's `cwd` — the safety net, creating a workspace for a session that reports one the deck doesn't have. |

Two limits that keep coming back and are not bugs:

- **An inactive VSCode tab has no thumbnail.** VSCode/DWM don't render it, so session cards
  are text plus border. A hard platform limit, not a missing feature.
- **A minimized window freezes its DWM thumbnail** on the last frame. Being covered by
  other windows is fine; minimized is not.

## Procedures

Longer step-by-step procedures live as skills under `.claude/skills/`, loaded on demand:

- `release` — cutting and publishing a release, and the one-release-per-`major.minor` policy.
- `manual-verify` — the manual test checklist for what automation can't cover.
