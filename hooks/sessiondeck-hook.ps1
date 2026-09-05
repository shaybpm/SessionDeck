# SessionDeck hook bridge for Claude Code.
# Version: 0.9.67  (parsed by install.ps1 — keep in sync with SessionDeck.csproj; release.ps1 syncs automatically)
# Called by Claude Code hooks with the event name as argument; the hook payload
# (session_id, cwd, transcript_path, permission_mode + event-specific fields)
# arrives as JSON on stdin. Everything the payload provides is forwarded to
# SessionDeck. Fire-and-forget: never blocks or fails the Claude Code session.
# PowerShell 5.1 compatible.
param(
    [Parameter(Mandatory = $true)][string]$HookEvent
)

$ErrorActionPreference = 'SilentlyContinue'

# Resolve sessiondeck.exe: the exe that ships next to this script first (installed
# layout: <root>\hooks\<script> — immune to stale PATH in already-open processes),
# then PATH, then the default dev build location.
$exe = $null
$sibling = Join-Path (Split-Path $PSScriptRoot -Parent) 'SessionDeck.exe'
if (Test-Path $sibling) { $exe = $sibling }
if (-not $exe) { $exe = (Get-Command 'SessionDeck.exe' -ErrorAction SilentlyContinue).Source }
if (-not $exe) {
    $devBuild = 'D:\BPM\SessionDeck\bin\Debug\net10.0-windows\SessionDeck.exe'
    if (Test-Path $devBuild) { $exe = $devBuild }
}
if (-not $exe) { exit 0 }

# Read stdin as UTF-8 explicitly — PowerShell 5.1 defaults to the OEM codepage for
# piped input, which garbles the Hebrew in Claude Code's UTF-8 payload (bug 2026-07-19).
$stdinReader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), [System.Text.Encoding]::UTF8)
$payload = $stdinReader.ReadToEnd() | ConvertFrom-Json
$sid = $payload.session_id
if (-not $sid) { exit 0 }

# Bound free-text fields so the command line stays well under the 32K limit.
function Get-Trimmed([string]$s, [int]$max = 400) {
    if (-not $s) { return $null }
    $s = ($s -replace '\s+', ' ').Trim()
    if ($s.Length -gt $max) { return $s.Substring(0, $max) }
    return $s
}

# A readable subject for a permission dialog: the tool plus whatever argument identifies
# the operation. tool_input differs per tool, so probe the usual keys in order.
function Get-PermissionSubject($p) {
    $tool = $p.tool_name
    if (-not $tool) { return $null }
    $arg = $null
    if ($p.tool_input) {
        foreach ($key in 'command', 'file_path', 'path', 'url', 'pattern') {
            $value = $p.tool_input.$key
            if ($value) { $arg = [string]$value; break }
        }
    }
    if ($arg) { return "${tool}: $arg" }
    return $tool
}

# Is this session a `claude -p` run rather than one a human opened? CLAUDE_CODE_ENTRYPOINT is
# INHERITED by everything a session spawns, so a wave of `claude -p` launched from inside a
# session reports claude-vscode and reads as interactive - measured 18-08-2026, four wave runs
# sat on a card as sessions the user could click, resume and be blinked at, none of which he
# started. The command line is the only ground truth ("claude  -p --dangerously-skip-permissions"
# against the IDE's "...native-binary\claude.exe --output-format stream-json ..."), but reading it
# costs ~1.2s through CIM, which is three times the whole bridge's budget for one event.
#
# So the cheap half runs first: the exe PATH (10ms) of a session opened in the IDE is always the
# extension's own binary. Only when that contradicts the environment - the entrypoint claims the
# IDE while the binary is a standalone CLI - is the expensive half paid, and only that one session
# pays it. A session the user really opened never reaches the CIM call, and nothing is ever hidden
# on the cheap signal alone: precision over coverage (hooks/README.md).
function Test-PrintModeRun {
    if (-not $env:CLAUDE_PID) { return $false }
    if ($env:CLAUDE_CODE_ENTRYPOINT -ne 'claude-vscode') { return $false }   # sdk-* classifies itself
    try { $path = (Get-Process -Id $env:CLAUDE_PID -ErrorAction Stop).Path } catch { return $false }
    if (-not $path) { return $false }
    if ($path -match '(?i)extensions\\anthropic\.claude-code') { return $false }
    $proc = Get-CimInstance Win32_Process -Filter "ProcessId = $($env:CLAUDE_PID)" -ErrorAction SilentlyContinue
    return [bool]($proc.CommandLine -match '(^|\s)--?p(rint)?(\s|$)')
}

# WHICH VSCode instance this session is running in, and it is the only certain answer there is.
# Several instances can have the same folder open, so a window cannot be told from a tab label:
# two live sessions were genuinely titled "execute item #3.0", one in the green window and one
# in the purple, and the deck's label correlation put the green one in purple (05-09-2026).
#
# CLAUDE_SECURESTORAGE_CONFIG_DIR is what binds a window to a Claude account, so it is set by
# exactly one instance's launcher and inherited by every session started in it. Its ABSENCE is a
# value, not a missing reading: it means the default instance, which has no group. Only the
# directory NAME is read - never a file inside it, which is where the credentials live.
# The same mapping, for a different purpose, is in ~\.claude\hooks\session-account-tag.ps1.
function Get-SessionGroup {
    $dir = $env:CLAUDE_SECURESTORAGE_CONFIG_DIR
    if (-not $dir) { return $null }
    switch -Regex ((Split-Path $dir -Leaf)) {
        '^\.claude-mgmt$'  { return 'purple' }
        '^\.claude-mgmt2$' { return 'green'  }
        '^\.claude-mgmt3$' { return 'orange' }
        default            { return $null }
    }
}

$cliArgs = $null
switch ($HookEvent) {
    'SessionStart' {
        $cliArgs = @('session', 'start', '--id', $sid, '--workspace', $payload.cwd)
        if ($payload.source)          { $cliArgs += @('--source', $payload.source) }
        $group = Get-SessionGroup
        if ($group)                   { $cliArgs += @('--group', $group) }
    }
    'UserPromptSubmit' {
        $cliArgs = @('session', 'status', '--id', $sid, '--state', 'working')
        $prompt = Get-Trimmed $payload.prompt
        if ($prompt)                  { $cliArgs += @('--detail', $prompt) }
    }
    'Notification' {
        $cliArgs = @('session', 'status', '--id', $sid, '--state', 'waiting')
        $message = Get-Trimmed $payload.message
        if ($message)                 { $cliArgs += @('--detail', $message) }
    }
    # The official permission hook — fires the moment the approval dialog opens, in the
    # VSCode UI too (verified 2026-08-04, Claude Code 2.1.220), where Notification does
    # not. It carries no "resolved" counterpart, so --permission-dialog hands the clearing
    # to the transcript scanner (see MainWindow.SetSessionStatus).
    'PermissionRequest' {
        $cliArgs = @('session', 'status', '--id', $sid, '--state', 'waiting')
        $detail = Get-Trimmed (Get-PermissionSubject $payload)
        if (-not $detail)             { $detail = 'Waiting for permission' }
        $cliArgs += @('--detail', $detail, '--permission-dialog')
    }
    # A turn can end while the session is not free at all: subagents launched with
    # run_in_background keep running, report back on their own and resume it. The Stop
    # payload carries the live registry in background_tasks, so the count is a snapshot
    # and not a start/stop tally (one background agent produced four SubagentStart /
    # SubagentStop pairs in a controlled run - it stops and resumes whenever it waits).
    # Only 'subagent' entries are counted: a background shell (a dev server, a long
    # build) also sits in that list but never wakes anyone, and counting it would pin
    # the card blue forever. Always sent, including 0, so the next turn clears it.
    'Stop' {
        $cliArgs = @('session', 'status', '--id', $sid, '--state', 'done')
        $agents = @($payload.background_tasks | Where-Object { $_.type -eq 'subagent' })
        $cliArgs += @('--agents', $agents.Count)
    }
    # The turn died on an API error. Until this event existed SessionDeck had no hook for
    # its 'error' state at all and the card just went quiet.
    'StopFailure' {
        $cliArgs = @('session', 'status', '--id', $sid, '--state', 'error')
        $reason = Get-Trimmed ($payload.error, $payload.reason, $payload.message | Where-Object { $_ } | Select-Object -First 1)
        if ($reason)                  { $cliArgs += @('--detail', $reason) }
    }
    # An MCP server is asking the user for input — a real block that produces no tool_use,
    # so the transcript scanner cannot see it. Unlike PermissionRequest this one does have
    # a resolved event, so it clears itself.
    'Elicitation' {
        $cliArgs = @('session', 'status', '--id', $sid, '--state', 'waiting')
        $detail = Get-Trimmed ($payload.message, $payload.prompt | Where-Object { $_ } | Select-Object -First 1)
        if (-not $detail)             { $detail = 'An MCP server is waiting for input' }
        $cliArgs += @('--detail', $detail)
    }
    'ElicitationResult' {
        $cliArgs = @('session', 'status', '--id', $sid, '--state', 'working')
    }
    # Question forms (AskUserQuestion) and plan approvals (ExitPlanMode) don't fire
    # Notification — without these the deck keeps showing "working" while Claude is
    # actually waiting for the user (issue 2026-07-19). Registered with a matcher so
    # the script only runs for these tools.
    'PreToolUse' {
        if ($payload.tool_name -notin @('AskUserQuestion', 'ExitPlanMode')) { exit 0 }
        $cliArgs = @('session', 'status', '--id', $sid, '--state', 'waiting')
        $detail = if ($payload.tool_name -eq 'ExitPlanMode') { 'Waiting for plan approval' }
                  else { Get-Trimmed $payload.tool_input.questions[0].question }
        if (-not $detail)             { $detail = 'Waiting for an answer to a question' }
        $cliArgs += @('--detail', $detail)
    }
    # Also the LEADING edge of the background-agent count. Stop's background_tasks is the
    # authoritative snapshot, but it only arrives when the turn ENDS - so a session that
    # announces "I dispatched 3 agents" and keeps working shows nothing on its card until
    # then, which reads as the feature being broken (Shay, 18-08-2026). A background Agent
    # call answers in ~25ms with status 'async_launched', and that is the moment to count it.
    # Foreground agents are excluded by that same check: their PostToolUse fires when the
    # agent has FINISHED, so counting one would add an agent that no longer exists.
    'PostToolUse' {
        if ($payload.tool_name -eq 'Agent') {
            if ($payload.tool_response.status -ne 'async_launched') { exit 0 }
            $cliArgs = @('session', 'agents', '--id', $sid, '--launched')
        }
        elseif ($payload.tool_name -in @('AskUserQuestion', 'ExitPlanMode')) {
            $cliArgs = @('session', 'status', '--id', $sid, '--state', 'working')
        }
        else { exit 0 }
    }
    'SessionEnd' {
        $cliArgs = @('session', 'end', '--id', $sid)
        if ($payload.reason)          { $cliArgs += @('--reason', $payload.reason) }
    }
}
if (-not $cliArgs) { exit 0 }

# Asked on the three events that can CREATE a session record, not on every one: SessionStart
# normally, and UserPromptSubmit / Stop because a session whose SessionStart the deck missed (it
# was closed, restarting, or being upgraded mid-run) is recreated from whichever event arrives
# next - and a recreated print run that was never asked reappears as a session the user seems to
# have opened, which is the whole bug. Everything else skips it, and a session opened in the IDE
# answers in 10ms without ever reading a command line.
if ($HookEvent -in @('SessionStart', 'UserPromptSubmit', 'Stop') -and (Test-PrintModeRun)) {
    $cliArgs += '--print-mode'
}

# Common payload fields, forwarded on every event that carries them.
# cwd goes on EVERY event so SessionDeck can recreate a session it no longer knows
# (e.g. after its workspace was removed from the deck) — self-healing safety net.
if ($payload.cwd -and $HookEvent -ne 'SessionStart') { $cliArgs += @('--workspace', $payload.cwd) }
# On the other two events that can CREATE a session record, for the same self-healing reason as
# cwd above: a session whose SessionStart the deck missed (it was down, restarting, or being
# upgraded) is recreated from whichever event lands next, and it must not be recreated without
# knowing which window it is in.
if ($HookEvent -in @('UserPromptSubmit', 'Stop')) {
    $group = Get-SessionGroup
    if ($group)                   { $cliArgs += @('--group', $group) }
}
if ($payload.transcript_path)  { $cliArgs += @('--transcript', $payload.transcript_path) }
if ($payload.permission_mode)  { $cliArgs += @('--mode', $payload.permission_mode) }
# Who started this session: claude-vscode for the IDE, sdk-cli for a headless
# `claude --print` run (a scheduled task, a runner). It is NOT in the hook payload — measured
# 14-08-2026 across SessionStart/UserPromptSubmit/Stop/SessionEnd — so it is read off the
# hook's own environment, which Claude Code sets on the process it spawns. Sent on every
# event, not only SessionStart, so a session that predates this version gets classified on
# its next one instead of staying unknown until it restarts.
if ($env:CLAUDE_CODE_ENTRYPOINT) { $cliArgs += @('--entrypoint', $env:CLAUDE_CODE_ENTRYPOINT) }
# Which session launched this one as a headless run. Nothing in the payload or the process tree
# can answer that - a wave is fired through a hidden wscript launcher whose own parent has exited
# by the time anyone looks - so the launcher stamps it into the environment it hands the child,
# under a name Claude Code does not overwrite (it DOES overwrite CLAUDE_CODE_SESSION_ID with the
# child's own id, measured 18-08-2026). Sent on every event so a run whose SessionStart the deck
# missed still gets attributed to its launcher.
if ($env:SESSIONDECK_DISPATCHER) { $cliArgs += @('--dispatcher', $env:SESSIONDECK_DISPATCHER) }

& $exe @cliArgs | Out-Null
exit 0
