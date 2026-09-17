---
name: manual-verify
description: The manual verification checklist for SessionDeck — the behaviour automation can't reach (live thumbnails, hook-driven statuses, blink and auto-acknowledge, the Reserved Zone, notifications, the tasks panel). Trigger before a release, after touching status/blink/zone/notification code, or when asked to verify or smoke-test the app.
---

# Manual verification

The 38 automated tests cover the `install-hooks` merge and nothing else. Everything below
needs a human looking at the screen. Run the sections that the change actually touches;
run all of them before a release.

**Running it:** `bin\Debug\net10.0-windows\SessionDeck.exe` (or F5 from Visual Studio).
Remember the deploy order — `quit`, build, start (see `CLAUDE.md`).
**CLI:** `SessionDeck.exe <command>` from that folder; `help` lists everything.
**Config:** `%APPDATA%\SessionDeck\config.json`. **Log:** `%APPDATA%\SessionDeck\logs`.

## 1. Workspace cards — adding and binding

- [ ] "+ Add workspace" → pick a folder → a card named after the folder.
- [ ] A VSCode window already open on that workspace binds immediately (live thumbnail).
- [ ] `SessionDeck.exe add "D:\path"` — same behaviour from the CLI.
- [ ] Adding the same folder twice is blocked with a message.
- [ ] A git project shows its branch (⎇); switching branch updates within ~10s (instantly with the extension).
- [ ] A Peacock project (`.vscode/settings.json`) takes the Peacock colour on border and title.
- [ ] Closing the VSCode window keeps the card ("No VSCode window open"); reopening re-binds.
- [ ] Dragging a VSCode window onto the deck creates/binds a card; a non-VSCode window is blocked.

## 2. Session cards — statuses from the hooks

Install the hooks per `hooks/README.md`, then open a Claude Code session:

- [ ] Opening a session → a grey (idle) sub-card, creating the workspace if needed.
- [ ] Submitting a prompt → steady blue (working).
- [ ] A permission request → blinking orange (waiting).
- [ ] Turn ends → blinking green (done); clicking → steady green + focus.
- [ ] Closing the session → the card goes; ▼ shows it dimmed as closed.
- [ ] Without Claude Code: run the command sequence at the end of `hooks/README.md`.
- [ ] `session status --id <sid> --state error` → blinking red until clicked.

## 3. Managing the deck

- [ ] Clicking a card focuses its VSCode window in place; with no window open it launches VSCode on the folder and binds.
- [ ] ▶ moves the window to the configured Stage.
- [ ] Edit → title/description/colour, including returning to "Automatic colour".
- [ ] Hide → the card goes; 👁 brings it back dimmed, and the menu item becomes "Show on the deck again".
- [ ] Active cards (open window / live session) float to the top.
- [ ] Many cards → vertical scrolling, wrapping by window width.
- [ ] 🔍 opens/closes the search row; ✕ and Escape both close it and clear the filter.
- [ ] Search filters by workspace fields (name/path/branch/description) or a matching session (title/detail/id); a matching session shows even when closed.
- [ ] "Search content too" also matches transcript contents; the status bar reports the count.

## 4. Zone / Stage

- [ ] Zone "Right half" docks and shrinks the work area; "Off" releases it.
- [ ] "Left/Right quarter" takes a quarter width; a maximized window stays out.
- [ ] Custom zone: "Custom left…" opens the size dialog; `2/7` ≈ 28.6%, `40%` and `0.4` work, invalid input disables OK, Cancel restores the previous choice, clicking the active item reopens the dialog, and the size survives a restart.
- [ ] Zone lock: while active, no title-bar drag, no frame resize (the cursor doesn't change), no double-click maximize, no Win+Shift+Arrow. Minimize/restore work and return exactly to the zone. Turning the zone off restores normal drag/resize.
- [ ] Stage: monitor + full/half affect where ▶ lands.

## 5. Persistence and startup

- [ ] Close and reopen — workspaces, sessions (closed ones too), statuses and acknowledgements all return.
- [ ] `config.json` is readable; a `StatusStyles` colour change is picked up after a restart.
- [ ] "Start with Windows" creates/removes `HKCU\...\Run\SessionDeck`.
- [ ] After installing to a new folder, the Run value points at the new exe.
- [ ] `sessiondeck quit` closes cleanly **and the work area returns to full size** — check with an active zone by maximizing a window afterwards.

## 6. The VSCode extension (SessionDeck Connector)

Prerequisite: the VSIX is installed and **every VSCode window has been reloaded**
(Ctrl+Shift+P → "Developer: Reload Window"). Log: Output → the "SessionDeck" channel.

- [ ] A 📑 chip shows the open Claude tab count (tooltip lists their names); opening/closing a tab updates it within ~1s.
- [ ] Clicking a session card with its tab open → focus + the tab is revealed.
- [ ] Clicking one whose tab is closed → the tab reopens (resume) with its history.
- [ ] Clicking a closed session from ▼ → same, it comes back to life.
- [ ] "Maximized" opening collapses the sidebar/panel/auxiliary bar first (`OpenSessionMaximized: false` disables it).
- [ ] Clicking a session while VSCode is fully closed → VSCode launches and the tab opens by itself within seconds (a pending open).
- [ ] Sessions show a real name within ~10s: the tab title primary, the session title secondary when they differ.
- [ ] `session open --id <sid>` behaves the same from the CLI.
- [ ] Restarting SessionDeck → the extension reconnects on its own within ~5s ("connected" in the log).
- [ ] An old session that can't be resumed shows a status-bar message instead of opening an empty tab.
- [ ] Long titles wrap rather than truncate; hovering a session card lightens its background.

### 6a. Closing a live session's tab by session id (0.9.104 / connector 0.6.16)

The case that needs a human: two tabs carrying the SAME label. The by-label close refuses
one of those by design, so this is the only path that reaches it.

```powershell
SessionDeck.exe session new <workspace id> --no-focus     # twice: two untitled sessions
SessionDeck.exe list                                      # read the two new ids
SessionDeck.exe session close-tab --id <the first id>
```

- [ ] Both new tabs read "Claude Code" (an unprompted session keeps the label VSCode gave it).
- [ ] **The right tab closes** — the one whose id was named, not the other, not the active one.
- [ ] **The second tab stays open and alive**: clicking its card reveals it, nothing was resumed.
- [ ] The window's previously active tab is active again afterwards.
- [ ] Output → "SessionDeck" logs `closeSession <id> by id` then `closed "Claude Code"`.
- [ ] Repeat with the target's OWN tab active first: it still closes, and the log shows the
      extra step onto a neighbouring tab that makes the reveal observable.
- [ ] `session close-tab --id` of a session whose tab was already closed by hand: the log says
      the reveal RESUMED it into a new tab and that the tab was closed again — and no card in
      the deck is left working. (It is the one branch that cannot be reached by inspection.)
- [ ] `session end --id <id> --close-tab` does both, and a plain `session end` touches no tab.
- [ ] A window still on 0.6.15 refuses with "cannot close a tab by session id", and the log
      shows nothing was pushed to it.

## 7. Blink and auto-acknowledge

The subtlest area — most historical bugs live here.

- [ ] A session blinking green → switch to its tab directly in VSCode → the blink stops within ~1s.
- [ ] Tab already active and window focused when the session finishes → no blink starts at all.
- [ ] An older session with no `ai-title` in its transcript → opening its tab still stops the blink.
- [ ] Two sessions started from the identical prompt → opening the tab silences **neither** (a card that keeps blinking beats an alert that vanishes quietly).
- [ ] Claude renames the tab mid-turn, then the session finishes while focused → the blink still stops within ≤10s.
- [ ] Two Claude tabs in split editor groups → only the session in the **focused** group is acknowledged.
- [ ] Sitting on session A's tab, switching away, then A moves to waiting/done → the card **does** blink.
- [ ] Killing the VSCode window by force (Task Manager) with a Claude tab active → within ~6s the deck stops believing that tab is watched, and a later waiting **does** blink.

## 8. Waiting detection in the VSCode UI

- [ ] A question (AskUserQuestion) or a plan (ExitPlanMode) → blinking orange within ~10s, with the question text in the sub-line. Answering → blue; finishing → green.
- [ ] A permission request for `Edit`/`Write`/`Read` left unanswered → orange within ~15-25s; approving → blue.
- [ ] A permission request for `Bash`/`PowerShell` → orange within ~2-2.5 minutes.
- [ ] **No false alarms:** a `Bash`/`PowerShell` call under 2 minutes, or an `Agent` running for many minutes → the card **stays blue**.
- [ ] A fast **Deny** (before a scan could run) → the card does not stay stuck orange.
- [ ] Known and acceptable: a subagent's permission dialog flashes orange briefly and returns to blue.

## 9. Windows notifications

Conditions: "Windows notifications" ticked in ⚙, **and** 📌 off, **and** Zone "Off".

- [ ] Deck unfocused, a session moves to waiting/done/error → a balloon with the workspace and session name, a coloured dot on the taskbar icon, one orange flash.
- [ ] The dot persists while any session needs attention, after the balloon is gone.
- [ ] Clicking the balloon or tray icon brings the deck to the front (restoring from minimized).
- [ ] Deck focused → nothing at all: no balloon, no dot, no flash.
- [ ] Turning 📌 on (or a Zone ≠ Off) → dot and tray icon vanish immediately; new waiting raises no balloon.
- [ ] A session that stays orange raises a balloon **once**, even as the deck updates otherwise.
- [ ] Acknowledge, then the session returns to waiting → a new balloon does appear.
- [ ] Restarting with blinking sessions raises **no** burst of balloons.
- [ ] Several at once → the balloon names the most severe + "and N more"; the dot takes the most severe colour (error > waiting > done).
- [ ] No stuck tray icon after acknowledging everything or closing the app.
- [ ] Turning the switch off clears everything immediately and persists (`WindowsNotifications: false`); turning it back on restores the badge **without** a balloon for already-orange sessions.
- [ ] Handling a blinking session **directly in VSCode** withdraws the blink, the dot, the tray icon and the notification itself — including its entry in the Windows notification center.
- [ ] Two sessions, one handled → the notification and dot **stay** (one is still waiting); only when both are handled does everything clear.

## 10. The tasks panel (external JSON)

Turn it on: ⚙ → "Tasks file (JSON)..." → pick a file. The contract is behind that dialog's
"📋 Copy spec" button. Empty = fully off.

- [ ] **Opt-in:** with no path configured there is no UI change at all — no strip, no 🗒 on cards.
- [ ] A valid file → a collapsed strip on the right: a coloured square per task, pinned ones first with a white border and a separator; hover shows name/status/description.
- [ ] Editing the file while running updates the strip within ~1s (debounce + retry on a locked file).
- [ ] Broken JSON / deleted file / wrong version → ⚠ with the reason on the strip and an error state instead of the list on the page. Fixing the file restores it.
- [ ] A record with no `id` or `name` is skipped; the rest loads with a small ⚠ detailing it.
- [ ] 📋 opens the tasks page: the full list plus live session squares grouped by workspace on the left, statuses and blink live. ✕ / Esc return to the deck.
- [ ] Mutual exclusion: 🔍 is disabled on the tasks page; 📋 is disabled while the search row is open.
- [ ] A task with no `sessions` → a new session in its workspace (with `newSessionPrompt` filled into the input box if configured).
- [ ] A task with `sessions` → a dropdown: "+ New session" then the sessions; live (🟢) focuses, not-running (▶) really resumes, launching VSCode if needed.
- [ ] 🔗 "Open task" opens the `url` (obsidian:// and the like).
- [ ] Workspace cards show 🗒 with the linked task count (matched by path); 0 is shown disabled; clicking lists the tasks inside the card.
- [ ] Clearing the path removes all task UI and stops the watcher.

## 11. Text direction

The UI chrome is English and LTR. This section is about **external data**, which is often
Hebrew and must follow its own language:

- [ ] Hebrew workspace, session and task names render RTL and right-aligned; English ones LTR and left-aligned — on the same screen, in cards, tooltips, the tasks page and menus.
- [ ] Typing Hebrew into the search box, a card title or a toggle name flips that field to RTL as you type.
- [ ] Paths, ids, colours and zone sizes stay LTR regardless of what surrounds them.

## Known limits (not bugs)

- An inactive VSCode tab has no thumbnail — a hard platform limit.
- A minimized window freezes its thumbnail on the last frame; being covered is fine.
- The 📑 tag on a single session card is best-effort, by comparing tab label to title.
- `claude-vscode.editor.open` is an internal Claude Code command; if it disappears the
  extension falls back to a terminal with `claude --resume`.

*`SessionDeck.exe snapshot <path>.png` renders the UI to a PNG (without thumbnails) —
useful for remote debugging.*
