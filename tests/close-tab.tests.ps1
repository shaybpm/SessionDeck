# Deck-half test for `session close-tab` / `session end --close-tab` (app 0.9.104).
#
# Stands a FAKE VSCode connector on SessionDeck's named pipe so the whole deck path can be
# driven without touching any of Shay's real VSCode windows: the CLI verb, the session lookup,
# the connector lookup, the 0.6.16 capability gate, and the exact JSON pushed down the pipe.
# The extension half (closeClaudeTabById) is not reachable from here - it needs a real window.
#
# Two things this file works around, both of them SessionDeck's own shape:
#  * WinExe: the CLI writes to the PARENT's console through AttachConsole, so a redirected
#    stdout on a -NoNewWindow child comes back empty. It is run through cmd.exe, whose own
#    stdout is the file, so the attached console IS the file.
#  * One abandoned ReadAsync on the pipe swallows the NEXT push into a buffer nobody reads.
#    The pending read is therefore kept and reused rather than re-issued per call.

$ErrorActionPreference = 'Stop'
$exe  = "$env:LOCALAPPDATA\Programs\SessionDeck\SessionDeck.exe"
$ws   = Join-Path ([System.IO.Path]::GetTempPath()) 'sessiondeck-close-tab-fake-ws'
if (-not (Test-Path $ws)) { New-Item -ItemType Directory -Path $ws | Out-Null }

$pass = 0; $fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host "  PASS  $name" }
    else     { $script:fail++; Write-Host "  FAIL  $name -- $detail" }
}

function Deck([string[]]$deckArgs) {
    $o = [System.IO.Path]::GetTempFileName()
    $e = [System.IO.Path]::GetTempFileName()
    # Pre-quoted: -ArgumentList joins an array on spaces without quoting, so `--title Claude Code`
    # would arrive as two arguments. Errors go to stderr, successes to stdout - both are read.
    $quoted = $deckArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
    $p = Start-Process $exe -ArgumentList $quoted -Wait -PassThru `
                       -RedirectStandardOutput $o -RedirectStandardError $e -NoNewWindow
    $out = ((Get-Content $o -Raw -Encoding UTF8) ?? '') + ((Get-Content $e -Raw -Encoding UTF8) ?? '')
    Remove-Item $o, $e -Force
    [pscustomobject]@{ Code = $p.ExitCode; Out = ($out -replace "`r", '').Trim() }
}

$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'sessiondeck',
    [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
$pipe.Connect(5000)
$writer = New-Object System.IO.StreamWriter($pipe)
$writer.AutoFlush = $true
$script:buf = New-Object byte[] 16384
$script:read = $null

function Send-Sync([string]$version) {
    $msg = @{
        Type = 'vscode-sync'; Workspace = $ws; Branch = 'fake'; Pid = $PID; Focused = $false
        Version = $version
        Tabs = @(@{ Label = 'Claude Code'; Active = $false }, @{ Label = 'Claude Code'; Active = $false })
    } | ConvertTo-Json -Depth 5 -Compress
    $writer.WriteLine($msg)
    Start-Sleep -Milliseconds 400
}

function Read-Pushed([int]$ms = 2000) {
    if ($null -eq $script:read) { $script:read = $pipe.ReadAsync($script:buf, 0, $script:buf.Length) }
    if (-not $script:read.Wait($ms)) { return '' }
    $text = [Text.Encoding]::UTF8.GetString($script:buf, 0, $script:read.Result)
    $script:read = $null
    return $text.Trim()
}

$idA = [guid]::NewGuid().ToString()
$idB = [guid]::NewGuid().ToString()
$idC = [guid]::NewGuid().ToString()
$idD = [guid]::NewGuid().ToString()

try {
    Write-Host "Case 0: the CLI verb exists and validates its input"
    $r = Deck @('session', 'close-tab')
    Check 'close-tab with no --id is rejected' ($r.Code -ne 0 -and $r.Out -match 'requires --id') "[$($r.Code)] $($r.Out)"
    $r = Deck @('session', 'close-tab', '--id', [guid]::NewGuid().ToString())
    Check 'an unknown session id is rejected' ($r.Code -ne 0 -and $r.Out -match 'unknown session id') "[$($r.Code)] $($r.Out)"

    Write-Host "Case 1: an extension too old to close by id is refused, and nothing is pushed"
    Send-Sync '0.6.15'
    Deck @('session', 'start', '--id', $idA, '--workspace', $ws, '--title', 'Claude Code') | Out-Null
    Deck @('session', 'start', '--id', $idB, '--workspace', $ws, '--title', 'Claude Code') | Out-Null
    $r = Deck @('session', 'close-tab', '--id', $idA)
    Check 'refused on a 0.6.15 window' ($r.Code -ne 0 -and $r.Out -match '0\.6\.15' -and $r.Out -match 'by session id') "[$($r.Code)] $($r.Out)"
    Check 'nothing was pushed to the old window' ((Read-Pushed 1200) -eq '')

    Write-Host "Case 2: a 0.6.16 window is asked, by id"
    Send-Sync '0.6.16'
    $r = Deck @('session', 'close-tab', '--id', $idA)
    Check 'accepted' ($r.Code -eq 0 -and $r.Out -match 'close the tab of session') "[$($r.Code)] $($r.Out)"
    $pushed = Read-Pushed 2500
    $cmd = if ($pushed) { $pushed.Split("`n")[0] | ConvertFrom-Json } else { $null }
    Check 'a closeSession command arrived' ($null -ne $cmd) "got: $pushed"
    if ($cmd) {
        Check 'Cmd=closeSession'  ($cmd.Cmd -eq 'closeSession') $cmd.Cmd
        Check 'SessionId is the one asked for' ($cmd.SessionId -eq $idA) $cmd.SessionId
        Check 'ById is set' ($cmd.ById -eq $true) "$($cmd.ById)"
        Check 'the labels ride along' ($cmd.Labels -contains 'Claude Code') ($cmd.Labels -join '|')
    }

    Write-Host "Case 3: session end --close-tab asks for the tab as well"
    $r = Deck @('session', 'end', '--id', $idB, '--close-tab')
    Check 'end reports both halves' ($r.Code -eq 0 -and $r.Out -match 'ended' -and $r.Out -match 'closing its VSCode tab') "[$($r.Code)] $($r.Out)"
    $pushed = Read-Pushed 2500
    $cmd = if ($pushed) { $pushed.Split("`n")[0] | ConvertFrom-Json } else { $null }
    Check 'the tab of the ENDED session was asked for' ($null -ne $cmd -and $cmd.SessionId -eq $idB -and $cmd.ById -eq $true) "got: $pushed"

    Write-Host "Case 4: a plain session end still touches no tab"
    Deck @('session', 'start', '--id', $idC, '--workspace', $ws, '--title', 'Claude Code') | Out-Null
    $r = Deck @('session', 'end', '--id', $idC)
    Check 'end alone says nothing about a tab' ($r.Code -eq 0 -and $r.Out -notmatch 'VSCode tab') "[$($r.Code)] $($r.Out)"
    Check 'nothing pushed on a plain end' ((Read-Pushed 1200) -eq '')

    Write-Host "Case 5: end --close-tab on a window that cannot do it says so and still ends"
    Send-Sync '0.6.15'
    Deck @('session', 'start', '--id', $idD, '--workspace', $ws, '--title', 'Claude Code') | Out-Null
    $r = Deck @('session', 'end', '--id', $idD, '--close-tab')
    Check 'the session ends and the tab is reported left open' ($r.Code -eq 0 -and $r.Out -match 'ended' -and $r.Out -match 'left open') "[$($r.Code)] $($r.Out)"

    Write-Host "Case 6: a session that already ENDED is refused - revealing it would resume it"
    Send-Sync '0.6.16'
    $r = Deck @('session', 'close-tab', '--id', $idD)
    Check 'refused, and it says why' ($r.Code -ne 0 -and $r.Out -match 'already ended' -and $r.Out -match 'resume it') "[$($r.Code)] $($r.Out)"
    Check 'nothing was pushed for an ended session' ((Read-Pushed 1200) -eq '')
}
finally {
    Deck @('session', 'end', '--id', $idA) | Out-Null
    $writer.Dispose(); $pipe.Dispose()
    Start-Sleep -Milliseconds 900
    Deck @('remove', '--match', 'fake-ws') | Out-Null
}

Write-Host ""
Write-Host "==== $pass passed, $fail failed ===="
if ($fail -gt 0) { exit 1 }
