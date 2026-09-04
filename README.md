# SessionDeck

**A Windows control deck for your running Claude Code sessions.**

SessionDeck tiles every VSCode window into a live grid and shows each Claude Code session inside it as a status card — grey when idle, blue while working, blinking orange when Claude is waiting for you, purple when the turn is done, green when the session has been wrapped up for good, white when it handed off to a successor and closed itself. One click focuses the window, activates the right tab, and clears the alert.

**Built for one setup, on purpose:** Claude Code running inside **VSCode** — through its [Claude Code extension](https://marketplace.visualstudio.com/items?itemName=anthropic.claude-code) — on **Windows 10/11**. A session is a VSCode tab here, and that assumption runs through the whole tool.

![The SessionDeck window: four workspace cards, each with a live thumbnail and its sessions](assets/screenshots/deck.png)

> Actively developed. MIT licensed. — [latest release](https://github.com/eyalBPM/SessionDeck/releases/latest)

---

## The problem

Running one Claude Code session is easy. Running five is not — and the expensive part isn't the work, it's *noticing*. A session finishes, or stops to ask permission, and then sits there while you are heads-down in another window. The cost is the minutes between "Claude stopped" and "you looked".

SessionDeck turns that into a glance. A blinking orange border means *some* session needs an answer:

![A session card blinking orange while Claude waits for permission](assets/screenshots/blink.gif)

Click it, and the deck focuses the VSCode window, reveals that session's tab, and stops the blink. If you answer the session directly in VSCode instead, the deck notices and stops blinking on its own.

## Features

- **Live window grid** — real DWM thumbnails (`DwmRegisterThumbnail`), rendered by the Windows compositor. No screen capture, no code injection, near-zero CPU.
- **Workspace cards** — one card per VSCode workspace, showing the project name and the current git branch. Workspaces are persistent: a card survives closing the window and re-binds automatically when a matching window reappears.
- **Session cards** — one sub-card per Claude Code session, with a status-coloured border driven by Claude Code hooks (`idle` / `working` / `waiting` / `done` / `error`), plus two set from outside: `he` for a session you have closed out yourself, and `replaced` for one that handed its work to a new session and was then killed — its dead tab is the only thing left, and the card is swept the moment that tab is closed. The status → colour/blink mapping lives in config, not in code.
- **Click to resume** — clicking a session card focuses the VSCode window, activates that session's tab (via the companion extension), and acknowledges the blink. A closed session resumes with its full history.
- **Windows notifications** — when the deck itself might be buried, a session that needs attention escalates to a native notification and a taskbar badge — and both withdraw the moment the cause is gone, including when you handle it outside the deck.
- **Reserved Zone** — SessionDeck can claim a quarter, half, all, or any custom fraction (e.g. `2/7`) of a monitor as an AppBar, so maximized windows and snap never cover it. While zoned, the window is locked in place until the zone is turned off.
- **Stage / Pin** — define a target rectangle once, then send any window to it from the UI or the CLI.
- **Full CLI** — everything is scriptable over a named pipe, with a <100ms round trip so hooks stay cheap.
- **Starts with Windows** and restores the complete layout, zone and stage.

### A tasks panel, if you want one

Point SessionDeck at a JSON file and it grows a read-only tasks panel: a full page listing your tasks next to your live sessions, and optionally a collapsed strip of task squares beside the deck (⚙ → *Tasks strip on the deck*, off by default). Click a task to open a session in its workspace — a new one, or a resume of a session already linked to it. The toolbar's **Run task** box does the same from a task number, without finding the card first.

If the file also describes the tree its tasks came from, the page draws a two-column grid of numbered squares down its right edge — the top level, and the selected item's children — so you can see where you are and jump anywhere in a click.

![The tasks page: tasks on the right, live sessions grouped by workspace on the left](assets/screenshots/tasks-page.png)

SessionDeck only ever reads the file, and reloads within a second of any change. The producer owns the content, the ordering and the status colours — the panel is deliberately agnostic about where your tasks come from. The full contract is behind the dialog's **📋 Copy spec** button. Leave the path empty and the feature does not exist.

### Session groups: which window a new session opens in

One folder can be open in several VSCode instances at once - each with its own
`--user-data-dir`, so each can be signed into a different Claude account. They share a single
card, because a card is a folder, and until now a new session went to whichever of them you
focused last: invisible, and it moves under you.

A **session group** names one of those instances and gives it a modifier. Hold it as you click
*+ New session* or a task, and the session opens in that instance - that account - every time.
No modifier is a group too, so the plain click has a fixed home rather than a guess. In the
**Run task** box the same choice is a word after the number (`4.0 green`, `4.0 ירוק`).

Groups are config, under `SessionGroups` in `%APPDATA%\SessionDeck\config.json`: an id, the
modifier, a marker that appears in that instance's window titles (a coloured square in
`window.title` is ideal - one per instance, and nothing else carries it), the folder it applies
to, and optionally the script that starts that instance, which lets the deck bring it up when it
is not running. The deck runs that script rather than composing a `Code.exe` command line of its
own, because what binds a window to an account is an environment variable the launcher sets, and
a window started without it looks identical and uses the wrong one. `sessiondeck groups` prints
every group with its state. No groups configured, or a card no group names: nothing changes.

### Toolbar toggles

Toggles are flags for *your* processes. Each one is a toolbar button whose 1/0 state is written to a file any script can read, so you can gate a watcher, a deploy or a hook on a click. SessionDeck neither knows nor cares what a toggle drives.

## How it works

```
Claude Code hooks ──> sessiondeck-hook.ps1 ──> sessiondeck session status --id ... --state ...
                                                        │
                          named pipe  \\.\pipe\sessiondeck
                                                        ▼
VSCode windows ──DWM thumbnails──>  SessionDeck (WPF)  <──── VSCode extension (tab activate / tab labels)
                                                        │
                              transcript scanner (independent "waiting" detection)
```

Hooks give the leading edge — immediate and certain. But `PermissionRequest` has no matching "resolved" event, so nothing tells the deck when you *answered*. SessionDeck therefore also scans the session transcript for a `tool_use` with no matching `tool_result` — the only signal that sees a call finish.

Per-tool thresholds were calibrated on 11,000+ real tool calls and chosen for **false-alarm rate**, not coverage: `Read`/`Edit`/`Write` at 15s (0.04–0.12%), `Bash`/`PowerShell` at 120s (~1%), and `Agent` excluded entirely — 37% of subagent runs legitimately exceed two minutes, so no threshold there is both useful and quiet. A false alarm teaches you to ignore the deck, which costs more than a missed one. See [`hooks/README.md`](hooks/README.md) for the full table and the reasoning.

## Architecture

| Component | Role | Tech |
|---|---|---|
| UI shell | Cards grid, editing, window picker | WPF, .NET 10 |
| Thumbnail host | Live window previews | DWM Thumbnail API via `HwndHost` |
| Window tracker | Title change / create / destroy → re-bind | `SetWinEventHook` (no polling) |
| AppBar service | Reserved Zone | `SHAppBarMessage` |
| Pipe server | CLI command intake | `NamedPipeServerStream`, JSON |
| Transcript reader | Hook-independent "waiting" detection | Polls session `.jsonl` |
| Config store | Persistence | JSON in `%APPDATA%\SessionDeck` |

No admin rights, no injection into foreign processes, per-monitor DPI aware (v2).

The UI is English and left-to-right, but anything that comes from **outside** the app — workspace, session and task names, descriptions, tooltips, your search text, branch names — follows its own language, so Hebrew or Arabic content renders right-to-left inside an otherwise LTR window.

## Getting started

**Requirements:** Windows 10/11, and VSCode with the Claude Code extension. No .NET runtime needed — the release build is self-contained.

A note on what depends on what: the **hooks** are what colour the cards, and they work anywhere Claude Code runs — a session in a terminal will appear on the deck with a live status like any other. What needs the VSCode extension is everything that treats a session as a *tab*: revealing it on click, the live tab titles, and the auto-acknowledge when you answer a session in VSCode without touching the deck. Neither macOS nor Linux is supported, and the window layer (DWM thumbnails, the AppBar zone) is Windows-specific enough that this is unlikely to change.

1. Download `SessionDeck-<version>-win-x64.zip` from [Releases](https://github.com/eyalBPM/SessionDeck/releases) and extract it anywhere.
2. Run the installer (no admin rights required — everything is per-user):

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
   ```

That's it. The script installs to `%LOCALAPPDATA%\Programs\SessionDeck`, adds it to your user PATH, installs the VSCode extension, registers the Claude Code hooks in `~/.claude/settings.json` (after backing it up) and starts the app. It ends with a summary of the three installed versions — app, extension, hooks.

**Upgrading** = download the newer zip and run `install.ps1` again; every step is idempotent. Your settings in `%APPDATA%\SessionDeck` survive. Uninstall with `uninstall.ps1`.

### From source

```powershell
git clone https://github.com/eyalBPM/SessionDeck.git
cd SessionDeck
dotnet build -c Release          # requires the .NET 10 SDK
.\bin\Release\net10.0-windows\SessionDeck.exe
```

The first launch starts the UI and the pipe server. Any later invocation with arguments acts as a CLI client against it.

**Wire up the hooks** — `SessionDeck.exe install-hooks` merges the eleven hooks into `~/.claude/settings.json`, pointing at the hook script next to the exe (backup + idempotent; `uninstall-hooks` reverts). See [`hooks/README.md`](hooks/README.md) for what each hook does.

**Build the VSCode extension** (enables tab activation and live tab labels). The `.vsix` is not checked in:

```powershell
cd vscode-extension
npm install
npx @vscode/vsce package
code --install-extension .\sessiondeck-connector-*.vsix
```

## CLI

```
sessiondeck list [--all]                 # workspaces + sessions
sessiondeck add <folder path>            # add a workspace
sessiondeck remove <target>
sessiondeck set <target> [--title "..."] [--desc "..."] [--color <c>]
sessiondeck focus <target>               # activate the window in place
sessiondeck pin <target>                 # move it to the Stage, then activate
sessiondeck stage --monitor <n> --half left|right | --full | --rect x,y,w,h
sessiondeck zone  --monitor <n> --half left|right | --quarter left|right | --custom left|right [--size 2/7|40%|0.4] | --full | --off
sessiondeck toggle list | get <id> | set <id> on|off
sessiondeck tasks [--file <path> | --off]
sessiondeck groups                       # the VSCode instances a new session can be aimed at
sessiondeck status
sessiondeck reconcile                    # close sessions whose tab or window is gone, now
sessiondeck quit                         # close the running app cleanly
sessiondeck install-hooks [--settings <path>] [--dry-run]   # register the Claude Code hooks
sessiondeck uninstall-hooks              # remove them (both run locally, no app needed)

sessiondeck session start  --id <session_id> --workspace <name> [--title "..."]
sessiondeck session status --id <session_id> --state working|waiting|done|he|replaced|error|idle
sessiondeck session open   --id <session_id>
sessiondeck session end    --id <session_id>
sessiondeck session new    <target> [--prompt "..."] [--group <id>]
sessiondeck session list   [--workspace <name>] [--all]
```

`<target>` is a stable numeric workspace id or `--match "<regex>"`. Exit code 0 on success, non-zero with a message on stderr otherwise.

## Known limitations

- **An inactive VSCode tab has no thumbnail.** VSCode and DWM don't render it, so session cards are text plus a coloured border. A hard platform limit, not a missing feature.
- **A minimized window freezes its thumbnail** on the last frame — keep tracked windows restored. Being covered by other windows is fine.
- Claude Code exposes no dedicated `error` hook beyond a failed turn; the `error` state exists in the model and the CLI but is mapped conservatively.
- There is no auto-update. The app shows its version in the ⚙ menu so a bug report can name one.

## Documentation

- [`CLAUDE.md`](CLAUDE.md) — how to build, test and release it, plus the settled design decisions.
- [`ARCHITECTURE.md`](ARCHITECTURE.md) — the code map: which file owns what, and what breaks when you change it.
- [`hooks/README.md`](hooks/README.md) — hook wiring and the waiting-detection heuristics.
- [`vscode-extension/README.md`](vscode-extension/README.md) — the companion extension.

## License

[MIT](LICENSE) © BPM Ltd.
