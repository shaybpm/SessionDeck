# SessionDeck hook bridge for Claude Code.
# Version: 0.9.6  (parsed by install.ps1 — keep in sync with SessionDeck.csproj; release.ps1 syncs automatically)
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

$cliArgs = $null
switch ($HookEvent) {
    'SessionStart' {
        $cliArgs = @('session', 'start', '--id', $sid, '--workspace', $payload.cwd)
        if ($payload.source)          { $cliArgs += @('--source', $payload.source) }
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
    'Stop' {
        $cliArgs = @('session', 'status', '--id', $sid, '--state', 'done')
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
    'PostToolUse' {
        if ($payload.tool_name -notin @('AskUserQuestion', 'ExitPlanMode')) { exit 0 }
        $cliArgs = @('session', 'status', '--id', $sid, '--state', 'working')
    }
    'SessionEnd' {
        $cliArgs = @('session', 'end', '--id', $sid)
        if ($payload.reason)          { $cliArgs += @('--reason', $payload.reason) }
    }
}
if (-not $cliArgs) { exit 0 }

# Common payload fields, forwarded on every event that carries them.
# cwd goes on EVERY event so SessionDeck can recreate a session it no longer knows
# (e.g. after its workspace was removed from the deck) — self-healing safety net.
if ($payload.cwd -and $HookEvent -ne 'SessionStart') { $cliArgs += @('--workspace', $payload.cwd) }
if ($payload.transcript_path)  { $cliArgs += @('--transcript', $payload.transcript_path) }
if ($payload.permission_mode)  { $cliArgs += @('--mode', $payload.permission_mode) }

& $exe @cliArgs | Out-Null
exit 0
