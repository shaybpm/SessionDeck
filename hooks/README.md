# SessionDeck — wiring into the Claude Code hooks

`sessiondeck-hook.ps1` translates Claude Code hook events into `sessiondeck session ...` commands, and forwards **everything the payload provides** to SessionDeck:

| Hook | Command | Status | Extra data forwarded |
|------|---------|--------|----------------------|
| `SessionStart` | `session start` | idle (grey) | `cwd` (creates the workspace if needed), `source` (startup/resume/clear/compact) |
| `UserPromptSubmit` | `session status --state working` | steady blue | the **prompt** itself (`--detail`, trimmed to 400 chars) |
| `Notification` | `session status --state waiting` | blinking orange | the waiting message (`--detail` — e.g. "needs your permission to use Bash") |
| `PermissionRequest` | `session status --state waiting --permission-dialog` | blinking orange | the tool and its argument (`--detail` — e.g. `Write: C:\Windows\Temp\x.txt`) |
| `Stop` | `session status --state done` | blinking purple → steady once clicked | |
| `StopFailure` | `session status --state error` | red | the error message that killed the turn |
| `PreToolUse` (AskUserQuestion / ExitPlanMode) | `session status --state waiting` | blinking orange | the question text / "Waiting for plan approval" — question forms are not permission requests, so they never raise `PermissionRequest` |
| `PostToolUse` (same tools) | `session status --state working` | steady blue | the user answered — Claude is working again |
| `Elicitation` | `session status --state waiting` | blinking orange | an input request from an MCP server — a real block that produces no `tool_use`, so the scanner is blind to it |
| `ElicitationResult` | `session status --state working` | steady blue | the user answered the MCP server |
| `SessionEnd` | `session end` | the card closes | `reason` (clear/logout/prompt_input_exit/other) |

Every event also forwards, when present: `transcript_path` and `permission_mode`.

Of the **31** hook events Claude Code exposes (the authoritative list is the JSON schema of `settings.json` itself), SessionDeck registers these 11. The rest are irrelevant to session state: they either don't change it (`InstructionsLoaded`, `MessageDisplay`, `FileChanged`, `ConfigChange`), are already covered indirectly (`PreCompact`/`PostCompact` — `SessionStart` arrives with `source: compact`), or belong to flows not used here (`WorktreeCreate`, `TeammateIdle`, `TaskCreated`).

### Detecting "waiting" from the transcript — what the hooks alone can't give

In the **built-in Claude Code UI inside VSCode** (as opposed to the terminal) `Notification` **does not fire**, and per Anthropic that is deliberate, not a bug: its semantics are tied to the TUI, and `PermissionRequest` is given in its place. The result at the time was that when Claude stopped and waited, the card stayed blue "working" (issue 2026-07-20) — and that is where the transcript scanner came from.

**Update 2026-08-04 (T-0318, verified empirically against Claude Code 2.1.220):**

- `PermissionRequest` **does fire in VSCode**, the moment the dialog opens, with full `tool_name` and `tool_input`. It does **not** fire for auto-approved calls — so it produces no false alarms.
- `PostToolUse` **does fire in VSCode**. The opposite claim from v0.6.17 no longer holds; it was fixed along with `PermissionRequest`.
- `PermissionRequest` **has no matching "resolved" event** — it announces that the dialog opened, not that it closed. So it is registered with `--permission-dialog`, and clearing the `waiting` is handed back to the scanner.
- ⚠️ **The flag does not mark `WaitingFromTranscript` directly** (trying that in v0.8.0 produced an orange→blue→orange flicker). The `tool_use` is indeed written to the **file** about 0.5s before the hook fires, but what matters is when SessionDeck **scanned** it — and scanning is driven by the transcript's mtime, which stops growing exactly while the dialog is open.
  So `PermissionDialogScanMark` stores `TranscriptScannedAt` as it was when the hook arrived:
  - As long as it hasn't moved, the scanner hasn't read the file since the dialog opened, and an empty `PendingCall` proves nothing. Hold.
  - Once it moves, a scan has seen the file and `PendingCall` can be trusted. No call = answered, so release.

  That bound is essential: without it a **fast Deny** (before the scanner caught up) would leave the card orange until the end of the turn.
- **Known limitation — a subagent's dialog:** a subagent call is filtered out (`isSidechain`) and will never appear in `PendingCall`, so it is released after a single scan. A subagent dialog therefore flashes orange briefly and returns to blue. That is the pre-v0.8.0 behavior (where it wasn't detected at all), not a regression. Telling "answered" apart from "subagent" properly requires knowing whether the payload carries an `agent_id` — not investigated.

**What this changes in the division of labour:** the hook gives the leading edge — immediate and certain, for every tool, including ones outside the calibration table. The scanner gives the trailing edge — it is the only one that sees the `tool_result` arrive. The thresholds below dropped from primary detection to being a **safety net** (old extension, terminal, hooks disabled); recalibrating them in that light hasn't been done yet.

The scanner (which runs every 10 seconds anyway) looks for a `tool_use` **with no matching `tool_result`** — a hook-independent sign that Claude stopped. There are two confidence levels, because in the transcript an open permission dialog looks **identical** to a tool that is simply still running:

| What was found | Confidence | When it turns orange |
|---|---|---|
| `AskUserQuestion` / `ExitPlanMode` with no result | Certain — the tool *is* the wait | Immediately |
| `Read` / `Edit` / `Write` / `Grep` / `Glob` … | Strong inference | After 15 seconds |
| `Bash` / `PowerShell` | Reasonable inference | After 120 seconds |
| `Agent` and any tool not listed | Cannot be inferred | Never |

The thresholds were derived by measuring 11,000+ real tool calls. The decisive column is the **share of legitimate calls that exceed the threshold** — that is, the false-alarm rate:

| Tool | Threshold | False alarms |
|---|---:|---:|
| `Read` / `Edit` / `Write` | 15s | 0.04% / 0.08% / 0.12% |
| `Bash` | 120s | 1.03% |
| `PowerShell` | 120s | 0.53% |
| `Agent` | — | 37% at 120s → **excluded** |

`Agent` is excluded on purpose: 65% of subagent runs exceed 30 seconds and 37% exceed 120, so no threshold is both short enough to be useful and quiet enough to trust. A false alarm corrects itself — the card returns to blue as soon as the tool finishes.

- The answer appeared → back to `working`; the `Stop` hook (which does fire in VSCode) takes it from there to `done`.
- Subagent lines (`isSidechain`) are filtered out — only the main conversation can block the user.
- A `waiting` state that came from a hook is not cleared by the scanner — except `PermissionRequest`, which explicitly asks for it through `--permission-dialog`, because no hook closes it.
- The countdown runs against the call held in memory, not against the file, because **the transcript freezes while the dialog is open** — re-reading it would never notice time passing.
- Calibration lives in `%APPDATA%\SessionDeck\config.json` under `PermissionWaitToolSeconds` — a `tool → seconds` map. Only tools listed there are checked; an empty map disables the inference entirely (questions are still detected). Adding `Agent` is at your own risk.

The hooks are still installed and still useful: in the terminal they work fully, and they provide immediate detection (no waiting for a scan).

**Where you see it:** the session card's sub-line shows the latest detail (prompt/message) when there is no manual description; the tooltip shows everything — id, detail, source, permission mode, transcript, timestamps and reason.

## Installation

**The recommended way (v0.6.29+):** `sessiondeck install-hooks` — merges the 11 hooks into `~/.claude/settings.json` with the real installation path, after a backup. Idempotent; `sessiondeck uninstall-hooks` removes them. Supports `--settings <path>` (a specific project's settings, for instance) and `--dry-run`.

**Manual installation (reference):** add this to `~/.claude/settings.json` — replace `D:\Eyal\SessionDeck\hooks` with the real path of the script on your machine. **Keep `-WindowStyle Hidden`, and keep it before `-File`** (everything after `-File` is passed to the script): `PreToolUse` and `PostToolUse` fire on every tool call of every session, so without the flag a handful of open sessions produce dozens of console windows a minute. That is not a cosmetic flicker — creating and destroying windows at that rate is shell work, and on 2026-08-06 it drove `explorer.exe` to 103% of a core and dropped the taskbar.

```json
{
  "hooks": {
    "SessionStart": [
      { "hooks": [ { "type": "command", "command": "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"D:\\Eyal\\SessionDeck\\hooks\\sessiondeck-hook.ps1\" SessionStart" } ] }
    ],
    "UserPromptSubmit": [
      { "hooks": [ { "type": "command", "command": "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"D:\\Eyal\\SessionDeck\\hooks\\sessiondeck-hook.ps1\" UserPromptSubmit" } ] }
    ],
    "Notification": [
      { "hooks": [ { "type": "command", "command": "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"D:\\Eyal\\SessionDeck\\hooks\\sessiondeck-hook.ps1\" Notification" } ] }
    ],
    "PermissionRequest": [
      { "hooks": [ { "type": "command", "command": "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"D:\\Eyal\\SessionDeck\\hooks\\sessiondeck-hook.ps1\" PermissionRequest" } ] }
    ],
    "Stop": [
      { "hooks": [ { "type": "command", "command": "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"D:\\Eyal\\SessionDeck\\hooks\\sessiondeck-hook.ps1\" Stop" } ] }
    ],
    "StopFailure": [
      { "hooks": [ { "type": "command", "command": "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"D:\\Eyal\\SessionDeck\\hooks\\sessiondeck-hook.ps1\" StopFailure" } ] }
    ],
    "SessionEnd": [
      { "hooks": [ { "type": "command", "command": "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"D:\\Eyal\\SessionDeck\\hooks\\sessiondeck-hook.ps1\" SessionEnd" } ] }
    ],
    "PreToolUse": [
      { "matcher": "AskUserQuestion|ExitPlanMode", "hooks": [ { "type": "command", "command": "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"D:\\Eyal\\SessionDeck\\hooks\\sessiondeck-hook.ps1\" PreToolUse" } ] }
    ],
    "PostToolUse": [
      { "matcher": "AskUserQuestion|ExitPlanMode", "hooks": [ { "type": "command", "command": "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"D:\\Eyal\\SessionDeck\\hooks\\sessiondeck-hook.ps1\" PostToolUse" } ] }
    ],
    "Elicitation": [
      { "hooks": [ { "type": "command", "command": "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"D:\\Eyal\\SessionDeck\\hooks\\sessiondeck-hook.ps1\" Elicitation" } ] }
    ],
    "ElicitationResult": [
      { "hooks": [ { "type": "command", "command": "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"D:\\Eyal\\SessionDeck\\hooks\\sessiondeck-hook.ps1\" ElicitationResult" } ] }
    ]
  }
}
```

> **Why not `PostToolUse` on every tool?** It would close `PermissionRequest`'s `waiting` directly, but at the price of spawning a PowerShell process **on every single tool call** — a fixed cost on every session, even when no dialog ever appears. The scanner already does the same job at zero cost, and the delay (up to 10 seconds) falls on the harmless end: the return to blue, not the alert itself.

## Toggles (flags) — driving external processes from the toolbar

SessionDeck lets you define toggles that act as **flags for external processes**. It neither knows nor cares what a toggle drives — it only manages the flag: it shows a toolbar button and writes the state to a file any process can read. The Claude Code hook is just one example of such a consumer.

1. Define a toggle from ⚙ → **"Toggles (flags)..."**: icon, **id**, name and default.
   - The **id** is the flag file name and is therefore **locked after creation** — renaming the display name never moves a path external processes already rely on.
2. Every click writes `1` (on) or `0` (off) to `%APPDATA%\SessionDeck\toggles\<id>`. The file survives restarts and can be read while the app is closed. A missing file means on.
3. The **ℹ** button on a toggle's row opens a details page with everything needed to wire a process up — id, full path, current state, CLI commands, a PowerShell check snippet, and a ready-to-paste prompt for an AI agent. Every field has a copy button.

Checking the flag from an external process (PowerShell):

```powershell
$flag = "$env:APPDATA\SessionDeck\toggles\<id>"
if ((Test-Path $flag) -and ((Get-Content $flag -Raw).Trim() -eq '0')) { exit 0 }
```

- Also controllable from the CLI: `sessiondeck toggle list` / `toggle get <id>` / `toggle set <id> off`.
- The default only applies the first time (while no flag file exists yet).

## Notes

- The script is fire-and-forget: every failure is swallowed (`exit 0`) so it can never disrupt a session; PowerShell 5.1 compatible.
- The file is saved as **UTF-8 with BOM**. Its user-facing strings are ASCII since v0.9.0, but the comments still contain non-ASCII characters, and PS 5.1 reads a BOM-less .ps1 as ANSI — keep the same encoding when editing.
- `he` state (blinking green): **no hook drives it** — it is set by hand, or by whatever runs
  your end-of-session routine, with `session status --state he`. `done` only means the turn
  stopped, which happens dozens of times a day; `he` means the session was closed out for
  good, and it is the one state a hook cannot overwrite: the `Stop` hook of the very turn
  that set it arrives a second later and would otherwise undo it. Anything that shows real
  activity (`working` / `waiting` / `error`), or a `SessionStart` on the same id, clears it.
- `error` state: `StopFailure` gives it a dedicated hook. Claude Code exposes no generic error event, so anything that isn't a failed turn stays unmapped.
  The state is still available from the CLI (`--state error`) for other scripts; SessionEnd's `reason` is stored and displayed.
- Manual check without Claude Code:
  ```powershell
  $exe = "D:\Eyal\SessionDeck\bin\Debug\net10.0-windows\SessionDeck.exe"
  & $exe session start  --id test1 --workspace "D:\Eyal\SessionDeck" --source startup
  & $exe session status --id test1 --state working --detail "prompt test"
  & $exe session status --id test1 --state waiting --detail "Claude needs your permission"
  & $exe session status --id test1 --state done
  & $exe session end    --id test1 --reason other
  ```
  A hand-driven session like this carries no `--transcript`, so the deck has nothing to
  scan, correlate or resume for it. Since v0.9.4 such a session is removed once it has
  been titleless and silent for 30 minutes — skipping the `session end` above leaves the
  card up for that long, not forever.
- **Session ids with no conversation behind them.** A Claude Code CLI launch mints more
  session ids than it uses — agent mode produced five in one minute (2026-08-05), of which
  one carried the conversation. The unused ones still fire `SessionStart`, and their
  `transcript_path` points at a `.jsonl` that is never written. Since v0.9.5 the 30-minute
  rule above covers them too: titleless **and** no transcript file on disk, whatever the
  status. It used to apply only to a missing `--transcript` and, separately, only to `idle`
  sessions — so a single `Notification` on such an id (agent mode sends one when the run
  finishes) pinned a blinking orange card to the deck permanently.
