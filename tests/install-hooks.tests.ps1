# Tests for `sessiondeck install-hooks` / `uninstall-hooks`.
# Runs the built exe against temp settings files via --settings - no app instance needed.
# Usage: powershell -NoProfile -File tests\install-hooks.tests.ps1 [-Exe <path>]
[CmdletBinding()]
param(
    [string]$Exe
)

$ErrorActionPreference = 'Stop'
# $PSScriptRoot is not available in param defaults under PowerShell 5.1 - resolve here.
if (-not $Exe) { $Exe = Join-Path (Split-Path $PSScriptRoot -Parent) 'bin\Debug\net10.0-windows\SessionDeck.exe' }
if (-not (Test-Path $Exe)) { throw "exe not found: $Exe - run 'dotnet build' first" }

$workDir = Join-Path $env:TEMP ("sessiondeck-hooks-tests-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workDir | Out-Null
$script:failed = 0
$script:passed = 0

function Assert($condition, [string]$name) {
    if ($condition) { $script:passed++; Write-Host "  PASS  $name" }
    else            { $script:failed++; Write-Host "  FAIL  $name" -ForegroundColor Red }
}

# SessionDeck.exe is a GUI-subsystem binary: PowerShell 5.1 neither waits for it nor
# captures its output via `&`. Start-Process -Wait with redirected streams does both.
function Invoke-Hooks([string]$command, [string]$settings, [string[]]$extra = @()) {
    $outFile = Join-Path $workDir 'stdout.txt'
    $errFile = Join-Path $workDir 'stderr.txt'
    $argStr = $command + ' --settings "' + $settings + '"'
    if ($extra.Count -gt 0) { $argStr += ' ' + ($extra -join ' ') }
    $p = Start-Process -FilePath $Exe -ArgumentList $argStr -Wait -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $outFile -RedirectStandardError $errFile
    $text = ''
    foreach ($f in $outFile, $errFile) {
        if ((Get-Item $f).Length -gt 0) { $text += (Get-Content $f -Raw -Encoding UTF8) }
    }
    return @{ ExitCode = $p.ExitCode; Output = $text }
}

function Read-Json([string]$path) { Get-Content $path -Raw | ConvertFrom-Json }

$events = 'SessionStart', 'UserPromptSubmit', 'Notification', 'PermissionRequest', 'Stop', 'StopFailure',
          'SessionEnd', 'PreToolUse', 'PostToolUse', 'Elicitation', 'ElicitationResult'

# --- Case 1: settings.json does not exist -> created (including the directory) ---
Write-Host "Case 1: missing file (and directory) is created"
$s = Join-Path $workDir 'newdir\settings.json'
$r = Invoke-Hooks 'install-hooks' $s
Assert ($r.ExitCode -eq 0) "exit code 0"
Assert (Test-Path $s) "file created"
$json = Read-Json $s
$missing = $events | Where-Object { -not $json.hooks.$_ }
Assert (-not $missing) "all $($events.Count) events present"
Assert ($json.hooks.PermissionRequest[0].PSObject.Properties.Name -notcontains 'matcher') "PermissionRequest fires for every tool"
Assert ($json.hooks.PreToolUse[0].matcher -eq 'AskUserQuestion|ExitPlanMode') "PreToolUse matcher"
Assert ($json.hooks.PostToolUse[0].matcher -eq 'AskUserQuestion|ExitPlanMode') "PostToolUse matcher"
Assert ($json.hooks.SessionStart[0].PSObject.Properties.Name -notcontains 'matcher') "SessionStart has no matcher"
$cmd = $json.hooks.Stop[0].hooks[0].command
Assert ($cmd -match '^powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ".+sessiondeck-hook\.ps1" Stop$') "command format ($cmd)"
$bytes = [IO.File]::ReadAllBytes($s)
Assert (-not ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB)) "written without BOM"

# --- Case 2: empty file / {} -> hooks key added ---
Write-Host "Case 2: empty file and bare {}"
$s = Join-Path $workDir 'empty.json'
Set-Content -Path $s -Value '' -NoNewline
$r = Invoke-Hooks 'install-hooks' $s
Assert ($r.ExitCode -eq 0) "empty file: exit 0"
Assert ((Read-Json $s).hooks.Stop.Count -eq 1) "empty file: hooks added"
$s = Join-Path $workDir 'braces.json'
Set-Content -Path $s -Value '{}'
$r = Invoke-Hooks 'install-hooks' $s
Assert ($r.ExitCode -eq 0) "{}: exit 0"
Assert ((Read-Json $s).hooks.Stop.Count -eq 1) "{}: hooks added"

# --- Case 3: existing SessionDeck hooks from an OLD path -> replaced, no duplicates ---
Write-Host "Case 3: old-path SessionDeck hooks are replaced"
$s = Join-Path $workDir 'oldpath.json'
@'
{
  "hooks": {
    "Stop": [
      { "hooks": [ { "type": "command", "command": "powershell -NoProfile -ExecutionPolicy Bypass -File \"C:\\OLD\\PLACE\\hooks\\sessiondeck-hook.ps1\" Stop" } ] }
    ]
  }
}
'@ | Set-Content -Path $s
$r = Invoke-Hooks 'install-hooks' $s
$json = Read-Json $s
Assert ($json.hooks.Stop.Count -eq 1) "exactly one Stop group"
Assert ($json.hooks.Stop[0].hooks[0].command -notmatch 'OLD') "old path gone"

# --- Case 4: another tool's hooks on the same event -> preserved, ours added beside ---
Write-Host "Case 4: foreign hooks are preserved"
$s = Join-Path $workDir 'foreign.json'
@'
{
  "model": "opus",
  "customTopLevel": { "keep": [1, 2, 3] },
  "hooks": {
    "Stop": [
      { "hooks": [ { "type": "command", "command": "other-tool.exe notify" } ] }
    ],
    "PreCompact": [
      { "hooks": [ { "type": "command", "command": "other-tool.exe compact" } ] }
    ]
  }
}
'@ | Set-Content -Path $s
$r = Invoke-Hooks 'install-hooks' $s
$json = Read-Json $s
Assert ($json.hooks.Stop.Count -eq 2) "foreign Stop group + ours"
Assert (@($json.hooks.Stop | Where-Object { $_.hooks[0].command -eq 'other-tool.exe notify' }).Count -eq 1) "foreign Stop command intact"
Assert ($json.hooks.PreCompact.Count -eq 1) "unrelated event untouched"
Assert ($json.model -eq 'opus') "unknown top-level scalar preserved"
Assert ($json.customTopLevel.keep.Count -eq 3) "unknown top-level object preserved"

# --- Case 5: corrupt JSON -> fail without writing ---
Write-Host "Case 5: corrupt JSON fails without writing"
$s = Join-Path $workDir 'corrupt.json'
Set-Content -Path $s -Value '{ "hooks": '
$before = Get-Content $s -Raw
$r = Invoke-Hooks 'install-hooks' $s
Assert ($r.ExitCode -ne 0) "non-zero exit"
Assert ((Get-Content $s -Raw) -eq $before) "file untouched"
Assert (-not (Get-ChildItem "$s.sessiondeck-backup-*" -ErrorAction SilentlyContinue)) "no backup written"

# --- Case 6: second run in a row -> content unchanged (only a new backup) ---
Write-Host "Case 6: idempotent re-run"
$s = Join-Path $workDir 'idem.json'
Invoke-Hooks 'install-hooks' $s | Out-Null
$first = Get-Content $s -Raw
Start-Sleep -Seconds 1   # distinct backup timestamp
$r = Invoke-Hooks 'install-hooks' $s
Assert ($r.ExitCode -eq 0) "exit 0"
Assert ((Get-Content $s -Raw) -eq $first) "content identical"
Assert ((Get-ChildItem "$s.sessiondeck-backup-*").Count -eq 1) "backup created on the run that had a file"

# --- Case 7: uninstall returns the file to its original state ---
Write-Host "Case 7: uninstall restores the original shape"
$s = Join-Path $workDir 'roundtrip.json'
$original = @'
{
  "model": "opus",
  "hooks": {
    "Stop": [
      { "hooks": [ { "type": "command", "command": "other-tool.exe notify" } ] }
    ]
  }
}
'@
$original | Set-Content -Path $s
Invoke-Hooks 'install-hooks' $s | Out-Null
$r = Invoke-Hooks 'uninstall-hooks' $s
Assert ($r.ExitCode -eq 0) "uninstall exit 0"
$json = Read-Json $s
Assert ($json.model -eq 'opus') "top-level field kept"
Assert ($json.hooks.Stop.Count -eq 1) "foreign Stop group kept"
Assert ($json.hooks.Stop[0].hooks[0].command -eq 'other-tool.exe notify') "foreign command kept"
$sdLeft = $events | Where-Object { $json.hooks.$_ } | Where-Object {
    ($json.hooks.$_ | ForEach-Object { $_.hooks } | ForEach-Object { $_.command }) -match 'sessiondeck-hook'
}
Assert (-not $sdLeft) "no SessionDeck commands remain"
Assert ($json.hooks.PSObject.Properties.Name -notcontains 'SessionStart') "emptied event keys removed"

# --- Case 7b: uninstall on a file where ONLY SessionDeck hooks existed -> hooks key removed ---
Write-Host "Case 7b: hooks key removed when nothing remains"
$s = Join-Path $workDir 'onlyours.json'
Set-Content -Path $s -Value '{}'
Invoke-Hooks 'install-hooks' $s | Out-Null
Invoke-Hooks 'uninstall-hooks' $s | Out-Null
$json = Read-Json $s
Assert ($json.PSObject.Properties.Name -notcontains 'hooks') "hooks key gone"

# --- Case 8: --dry-run writes nothing ---
Write-Host "Case 8: dry-run"
$s = Join-Path $workDir 'dryrun.json'
Set-Content -Path $s -Value '{}'
$before = Get-Content $s -Raw
$r = Invoke-Hooks 'install-hooks' $s @('--dry-run')
Assert ($r.ExitCode -eq 0) "exit 0"
Assert ((Get-Content $s -Raw) -eq $before) "file untouched"
Assert ($r.Output -match 'SessionStart') "planned result printed"

# --- Case 9: SessionDeck command co-mingled inside a group with a foreign command ---
Write-Host "Case 9: shared group - only our entry is removed"
$s = Join-Path $workDir 'shared.json'
@'
{
  "hooks": {
    "Stop": [
      { "hooks": [
          { "type": "command", "command": "other-tool.exe notify" },
          { "type": "command", "command": "powershell -File \"C:\\OLD\\hooks\\sessiondeck-hook.ps1\" Stop" }
      ] }
    ]
  }
}
'@ | Set-Content -Path $s
Invoke-Hooks 'install-hooks' $s | Out-Null
$json = Read-Json $s
$sharedGroup = $json.hooks.Stop | Where-Object { $_.hooks.command -contains 'other-tool.exe notify' }
Assert ($sharedGroup.hooks.Count -eq 1) "foreign entry survives alone in its group"
Assert ($json.hooks.Stop.Count -eq 2) "our group added separately"

Write-Host ""
Write-Host "==== $script:passed passed, $script:failed failed ===="
Remove-Item -Path $workDir -Recurse -Force -ErrorAction SilentlyContinue
if ($script:failed -gt 0) { exit 1 }
exit 0
