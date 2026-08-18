# SessionDeck — wiring into the Claude Code hooks

`sessiondeck-hook.ps1` translates Claude Code hook events into `sessiondeck session ...` commands, and forwards **everything the payload provides** to SessionDeck:

| Hook | Command | Status | Extra data forwarded |
|------|---------|--------|----------------------|
| `SessionStart` | `session start` | idle (grey) | `cwd` (creates the workspace if needed), `source` (startup/resume/clear/compact) |
| `UserPromptSubmit` | `session status --state working` | steady blue | the **prompt** itself (`--detail`, trimmed to 400 chars) |
| `Notification` | `session status --state waiting` | blinking orange | the waiting message (`--detail` — e.g. "needs your permission to use Bash") |
| `PermissionRequest` | `session status --state waiting --permission-dialog` | blinking orange | the tool and its argument (`--detail` — e.g. `Write: C:\Windows\Temp\x.txt`) |
| `Stop` | `session status --state done` | blinking purple → steady once clicked | `--agents` — how many subagents the payload's `background_tasks` still lists as running (see below); a non-zero count lands on `working` instead |
| `StopFailure` | `session status --state error` | red | the error message that killed the turn |
| `PreToolUse` (AskUserQuestion / ExitPlanMode) | `session status --state waiting` | blinking orange | the question text / "Waiting for plan approval" — question forms are not permission requests, so they never raise `PermissionRequest` |
| `PostToolUse` (same tools) | `session status --state working` | steady blue | the user answered — Claude is working again |
| `PostToolUse` (`Agent`) | `session agents --launched` | unchanged — the count only | one background subagent was just dispatched: +1 to the 🤖 chip, so it appears with the agents instead of at the end of the turn (see below) |
| `Elicitation` | `session status --state waiting` | blinking orange | an input request from an MCP server — a real block that produces no `tool_use`, so the scanner is blind to it |
| `ElicitationResult` | `session status --state working` | steady blue | the user answered the MCP server |
| `SessionEnd` | `session end` | the card closes | `reason` (clear/logout/prompt_input_exit/other) |

Every event also forwards, when present: `transcript_path`, `permission_mode`, and
`--entrypoint` (see below).

### Telling a headless session apart (v0.9.41)

A scheduled task or a runner firing `claude --print` produces hooks that are
**indistinguishable** from a session opened in the IDE, so each one earns a card of its own.
On this machine that meant cards for `shimi-agent` (four timers, one every two minutes),
`system32` (scheduled tasks that never set a working directory, so Windows hands them
`C:\WINDOWS\system32`), and a long tail of `scratchpad` / `tool-results` / `memory` folders
that some background run happened to stand in.

The discriminator is `CLAUDE_CODE_ENTRYPOINT`: `claude-vscode` for a session in the IDE,
`sdk-cli` for a headless run. It is **not in the hook payload** — measured 14-08-2026 by
dumping the raw stdin of `SessionStart`, `UserPromptSubmit`, `Stop` and `SessionEnd`, none of
which carry it — so the bridge reads it off its own environment, which Claude Code sets on the
process it spawns, and sends it as `--entrypoint` on every event rather than only on
`SessionStart`, so a session that predates this version is classified on its next event.

Two things worth knowing before touching this:

- **The variable is inherited, and that hole is NOT harmless (fixed in v0.9.45).** A
  `claude --print` launched from *inside* an IDE session — or through a hidden `wscript` /
  `powershell` launcher that a session started — reports `claude-vscode` and reads as
  interactive. Measured 18-08-2026: a wave of four `claude -p` runs sat on a card as four
  sessions the user could click, resume and be blinked at, none of which he opened. The old
  text called this "right in spirit, the run belongs to a session he opened"; the card is the
  wrong place to say so, because a session row offers to *resume* something nobody can talk to.
  `--print-mode` now closes it from the other side (see below). A run from Task Scheduler with
  a clean environment still reports `sdk-cli` and needs none of this.
- **`SessionViewModel.IsHeadless` is a blacklist of the `sdk*` entrypoints, deliberately, not
  a whitelist of the interactive ones.** An unknown entrypoint treated as interactive shows a
  card the user may not want, which is only today's behaviour; treated as headless it would
  silently swallow a session he is waiting on. Precision over coverage.

**`--print-mode`: the process, not the environment (v0.9.45).** The only thing that cannot be
inherited is the claude process's own command line. A spawned run reads
`claude  -p --dangerously-skip-permissions`; a session opened in the IDE reads
`...\extensions\anthropic.claude-code-<v>\resources\native-binary\claude.exe --output-format
stream-json ...`. Reading it costs ~1.2s through CIM — three times the whole bridge's budget for
one event — so the hook asks in two stages, at `SessionStart` only:

1. **Cheap (10ms), on every session:** `(Get-Process -Id $env:CLAUDE_PID).Path`. A session opened
   in the IDE always runs the extension's own binary. If the path agrees with the environment,
   the answer is no and nothing more is read.
2. **Expensive (~1.2s), only on the contradiction:** the entrypoint claims the IDE while the
   binary is a standalone CLI. Only then is the command line read, and only that one session pays
   for it — a session the user really opened never reaches this call.

Nothing is hidden on the cheap signal alone: a machine whose IDE binary lives somewhere
unexpected would otherwise have its real sessions swallowed, which is the failure direction this
file keeps refusing. The result is persisted (`PrintMode` in the config) because a process cannot
stop being a print run and a restart must not un-hide a wave that is still going, and it is ORed
into `IsHeadless`, so the existing filter and its setting do the rest.

The question is asked on `SessionStart`, `UserPromptSubmit` and `Stop` — the three events that can
CREATE a session record. Not only the first, because a session whose `SessionStart` the deck
missed (it was closed, restarting, or being upgraded mid-run) is recreated from whichever event
arrives next, and a recreated print run that was never asked comes back as a session the user
appears to have opened. Upgrading the deck while a wave was in flight is exactly how that was
found, an hour after the fix went in (18-08-2026): two runs from before the install were still
sitting on a card.

The setting is **Settings ⚙ → "Show headless sessions"**, off by default (`ShowHeadlessSessions`
in the config). When off, the sessions are hidden and a card left with none of its own stops
counting as open, so it drops off the deck under "Open only" instead of lingering empty. A
search overrides the filter, for the same reason "Open only" stands down while searching.

Of the **31** hook events Claude Code exposes (the authoritative list is the JSON schema of `settings.json` itself), SessionDeck registers these 11. The rest are irrelevant to session state: they either don't change it (`InstructionsLoaded`, `MessageDisplay`, `FileChanged`, `ConfigChange`), are already covered indirectly (`PreCompact`/`PostCompact` — `SessionStart` arrives with `source: compact`), or belong to flows not used here (`WorktreeCreate`, `TeammateIdle`, `TaskCreated`).

### What the bridge costs, measured

A claim that keeps being repeated, including once in this repo's own code comments, is that
every tool call pays for two PowerShell starts (`PreToolUse` and `PostToolUse`). It is wrong.
Claude Code honours the matcher, so those two registrations run **only** for `AskUserQuestion`
and `ExitPlanMode`. Sampling every `powershell.exe` start on a busy machine for 65 seconds
caught 55 of them across a dozen tool calls, and not one was `sessiondeck-hook.ps1`; the four
to five processes per call all belonged to that machine's own unrelated guard hooks.

What one invocation costs (Windows PowerShell 5.1, warm, median of 11 runs, 2026-08-06):

| Step | Wall | CPU |
|---|---:|---:|
| `powershell.exe -NoProfile -Command exit`, the floor | 135ms | 188ms |
| the script up to the exe call (process start, then parse stdin) | 208ms | 240ms |
| `SessionDeck.exe session status ...` alone, pipe round trip included | 154ms | 67ms |
| **the whole bridge, one event, end to end** | **422ms** | **~310ms** |

The exe on its own is already over the sub-100ms target in `CliClient`, and PowerShell roughly
doubles it. What keeps that from mattering is how rarely it happens. Over 3 days on one machine,
136 sessions and 15,489 tool calls:

| Event | Invocations |
|---|---:|
| `UserPromptSubmit` | 1,126 |
| `Stop` | 786 |
| `SessionStart` + `SessionEnd` | ~272 |
| `PermissionRequest` | 3 |
| `PreToolUse` + `PostToolUse` (matched) | 2 |

About 2,200 invocations in 72 hours: **0.5 per minute for the whole machine**, near 0.3% of one
core. Replacing PowerShell with a `sessiondeck hook <Event>` subcommand that reads the payload
itself would save roughly 250ms per event, which is a quarter of a second per prompt and per
turn end, in exchange for a new subcommand and a JSON parser in the exe. Measured that way it
does not pay, which is why the bridge still looks like this. If the registered set ever grows to
something that fires per tool call, this arithmetic changes and the subcommand becomes the
obvious move.

`Agent` joined the `PostToolUse` matcher in v0.9.44, which adds exactly one invocation per
dispatched subagent — the cheapest possible leading edge, and still nowhere near per-tool-call.

### Background subagents — the one thing neither the hooks nor the scanner used to see

A session that dispatches subagents with `run_in_background` ends its turn and then sits
there: the agents run, report back on their own and resume it. Until v0.9.38 the deck read
that as the user's turn, because the `Stop` hook is genuine — the turn really did end. Every
returning agent produced another `done`, so a wave of them blinked "your turn" repeatedly at
a user with nothing to answer.

**The transcript cannot help here.** A background `Agent` call returns in about 3ms with
`{"status": "async_launched", "agentId": ...}`, so the transcript holds a completed tool call
with its `tool_result` in place; the scanner has nothing pending to look at. (A *foreground*
agent does leave a pending call, which is why `Agent` appears in the threshold table below —
excluded from ageing, but visible.)

**What does carry it is the `Stop` payload itself**, which the deck was already receiving and
throwing away (measured 2026-08-14 against Claude Code 2.1.232):

```json
"background_tasks": [
  {"id": "a70134...", "type": "subagent", "status": "running",
   "description": "Delayed sleep test agent", "agent_type": "general-purpose"}
]
```

The hook counts the `subagent` entries and passes `--agents <n>` on every `Stop`, zero
included, so the next turn clears it. `SetSessionStatus` turns a `done` with `n > 0` into
`working`, and the card shows a 🤖 chip so the blue is explained rather than mysterious.

**The snapshot is late, though, and that reads as broken (v0.9.44).** It arrives only when the
turn ENDS. A session that dispatches a wave, says so in the chat and then keeps working shows an
empty card for the whole turn — reported 18-08-2026 as "it says it sent agents and the card shows
nothing", and the log of that session says exactly why: three agents launched at 09:17:21, the
chip appeared at 09:18:47 with the turn's `Stop`, and cleared at 09:22:46 when they were done. So
`PostToolUse` on `Agent` now supplies the leading edge, one invocation per dispatched agent:

- **Only an async launch counts.** `tool_response.status == "async_launched"` (with `isAsync`,
  `agentId` and the description beside it) is the discriminator. A *foreground* agent's
  `PostToolUse` fires when the agent has finished, and counting one would add an agent that no
  longer exists.
- **It is a tally, and tallies drift — this one is bounded.** It can only overcount, only until
  the turn ends, and the `Stop` snapshot then overwrites it with the truth. An agent finishing
  wakes the session, and that wake ends in exactly such a `Stop`.
- **Status is deliberately untouched**, which is why this is its own CLI verb rather than a
  `session status --state working`: the event fires mid-turn, and pushing a state from there would
  clear `he` and the lost-agents mark off an ordinary tool call.
- **`background_tasks` is on `Stop` and `SubagentStop` only** — measured 18-08-2026 against Claude
  Code 2.1.233. Not on `PostToolUse`, `UserPromptSubmit`, `SubagentStart` or `Notification`, so
  the leading edge cannot be a snapshot and there is nothing cheaper to read.

Three things learned the hard way, all worth keeping:

- **Count the snapshot, never a start/stop tally.** `SubagentStart` and `SubagentStop` do
  exist, both carrying `agent_id`, `agent_type` and the parent's `session_id` — but they are
  not a matched pair. One background agent produced **four** Start/Stop pairs in a controlled
  run: it stops and resumes every time it waits on something of its own. A running tally
  would drift; `background_tasks` is a snapshot of the live registry and cannot.
- **Only `subagent` counts.** The same list holds `type: "shell"` entries — a dev server, a
  long build, anything started with `run_in_background`. Those never wake the session, so
  counting them would pin a card blue forever with the user genuinely waiting.
- **No new hook registration, and no new cost.** Everything above rides on the `Stop` hook
  that was already registered. `SubagentStart` / `SubagentStop` would each add a PowerShell
  start *per agent*, which on a wave of ten is ten of them, for information the snapshot
  already delivers.

Not covered, deliberately: after a deck restart the count is 0 until that session's next
`Stop` (it isn't persisted — a stale count from before a crash would pin a card blue). A
`SessionStart` with `source` `startup` or `clear` resets it; `resume` and `compact` keep it,
because the same conversation is continuing (see the next section — this is the fix for a card
that dropped to idle the moment you clicked it).

### Agents that died with their session — the transcript is the only witness (v0.9.40)

When a session's process exits while background subagents are still running, the next
incarnation is told so, and nothing else is:

```
<task-notification><task-id>…</task-id><status>stopped</status>
<summary>No completion record was found for 2 background agents from the previous session:
"Re-measure tree 3 agenda delta" (a4bab…), "Measure git delivery gap across repos" (abd27…).
…their transcripts are saved on disk…</summary></task-notification>
```

Measured 2026-08-14 (Claude Code 2.1.232), the hooks are blind to it: `SessionStart` fires
about four seconds *before* the notification is written and carries nothing about it,
`UserPromptSubmit` reports the user's own prompt rather than the notification, and by then
`Stop`'s `background_tasks` is empty. So the scanner reads it, sets a ⚠ chip naming the lost
agents, and turns the card `error` — nothing else on the deck can say that work was started
and never finished. `waiting` and `he` outrank it: a live block on the user, and a deliberate
close-out, both mean more than a post-mortem.

Two guards, both paid for:

- **Whose text is it.** The first cut matched any line containing the marker and lit up a
  session that was merely *discussing* a lost agent — off its own tool output. The
  notification is now identified structurally: a `user` entry with
  `origin.kind == "task-notification"` whose content is a plain string starting with
  `<task-notification>`. That also rejects the `queue-operation` twin of the same
  notification, which carries its own timestamp and would fire a second time.
- **How old is it.** The deck rescans every transcript on startup, so without a bound a
  restart would light up every session that ever lost an agent. Notifications older than an
  hour are ignored.

The notification's own timestamp is the event's identity, so the 10-second scan reports it
once. The mark clears when the session does something again (any hook-driven `working`) or on
a `SessionStart` that resets the card.

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

Two conditions suppress the inference entirely, both added in v0.9.32:

- **A sibling call is still pending.** Claude Code writes the `tool_result`s of one assistant turn together, so a fast tool issued alongside a slow one shows no result for as long as the slow one runs. Excluding `Agent` from the table does not help here: the scanner reports the *newest* pending call, which is the `Edit`, not the `Agent` two seconds before it. Measured 2026-08-10 on a live session — an `Edit` issued 2s after an `Agent` stayed resultless for the subagent's full 3 minutes and pinned the card orange twice. A call with anything older still pending is therefore never aged.
- **The session runs in `bypassPermissions`.** No permission dialog can open there, so ageing a call into one is a guaranteed false alarm. `AskUserQuestion` / `ExitPlanMode` still block in that mode and are still detected, and a `PermissionRequest` hook that actually reported a dialog is still believed — only the guess is dropped.

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
      { "matcher": "AskUserQuestion|ExitPlanMode|Agent", "hooks": [ { "type": "command", "command": "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"D:\\Eyal\\SessionDeck\\hooks\\sessiondeck-hook.ps1\" PostToolUse" } ] }
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
