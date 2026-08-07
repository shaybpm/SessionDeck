# SessionDeck installer.
# Run from the extracted release zip. No admin rights required - everything is per-user.
# Upgrading = re-running this same script over a newer zip; every step is idempotent.
# PowerShell 5.1 compatible.
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\SessionDeck"
)

$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot

# SessionDeck.exe is a GUI-subsystem binary: PowerShell 5.1 neither waits for it nor
# captures its output via `&`. Start-Process -Wait with redirected streams does both.
function Invoke-SessionDeck([string]$ExePath, [string]$Arguments) {
    $outFile = [IO.Path]::GetTempFileName()
    $errFile = [IO.Path]::GetTempFileName()
    try {
        $p = Start-Process -FilePath $ExePath -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput $outFile -RedirectStandardError $errFile
        $text = @()
        foreach ($f in $outFile, $errFile) {
            if ((Get-Item $f).Length -gt 0) { $text += (Get-Content $f -Encoding UTF8) }
        }
        return @{ ExitCode = $p.ExitCode; Output = $text }
    } finally {
        Remove-Item $outFile, $errFile -ErrorAction SilentlyContinue
    }
}

# True when the destination already holds byte-identical content. Length first because it
# settles almost every mismatch for free; the hash is what makes the check trustworthy, since
# `dotnet publish` re-stamps the write time of every runtime assembly on every run while the
# bytes stay identical - comparing timestamps would rewrite the entire ~150MB payload each time.
function Test-SameContent([string]$Source, [string]$Destination) {
    if (-not (Test-Path $Destination)) { return $false }
    if ((Get-Item $Source).Length -ne (Get-Item $Destination).Length) { return $false }
    return (Get-FileHash $Source -Algorithm SHA256).Hash -eq (Get-FileHash $Destination -Algorithm SHA256).Hash
}

if (-not (Test-Path (Join-Path $src 'SessionDeck.exe'))) {
    Write-Error "SessionDeck.exe not found next to install.ps1 - run this script from the extracted release zip."
}

# --- 1. Stop a running instance (clean quit first: a forced kill skips the AppBar
#        release and leaves the Windows work area shrunken) ---
if (Get-Process SessionDeck -ErrorAction SilentlyContinue) {
    Write-Host "Stopping the running SessionDeck instance..."
    Invoke-SessionDeck (Join-Path $src 'SessionDeck.exe') 'quit' | Out-Null
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $deadline -and (Get-Process SessionDeck -ErrorAction SilentlyContinue)) {
        Start-Sleep -Milliseconds 250
    }
    if (Get-Process SessionDeck -ErrorAction SilentlyContinue) {
        Write-Warning "SessionDeck did not exit within 5s - forcing (the reserved zone may need the app restarted to reset)."
        Get-Process SessionDeck -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }
}

# --- 2. Copy the zip content to the install dir ---
$resolvedSrc = (Resolve-Path $src).Path.TrimEnd('\')
if (Test-Path $InstallDir) {
    $resolvedDst = (Resolve-Path $InstallDir).Path.TrimEnd('\')
} else {
    $resolvedDst = $InstallDir.TrimEnd('\')
}
if ($resolvedSrc -ieq $resolvedDst) {
    Write-Host "Running from the install directory itself - skipping the copy step."
} else {
    # Write only what actually changed. An upgrade normally touches SessionDeck.dll and little
    # else; rewriting all ~200 files unconditionally is what used to stall the machine for about
    # a minute per install - explorer.exe at 200,000+ soft page faults per second (agenda item
    # 4.13.18). Files the source no longer carries are left in place on purpose: the .NET host
    # loads by deps.json, so a leftover assembly is inert, and deleting by pattern here would be
    # the one step in this script capable of destroying something it did not put there.
    Write-Host "Installing to $InstallDir ..."
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    $written = 0
    $skipped = 0
    $writtenBytes = 0
    foreach ($file in Get-ChildItem -Path $resolvedSrc -Recurse -File) {
        $relative = $file.FullName.Substring($resolvedSrc.Length + 1)
        $target = Join-Path $resolvedDst $relative
        if (Test-SameContent $file.FullName $target) { $skipped++; continue }
        $targetDir = Split-Path $target -Parent
        if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Force -Path $targetDir | Out-Null }
        Copy-Item -Path $file.FullName -Destination $target -Force
        $written++
        $writtenBytes += $file.Length
    }
    Write-Host ("  wrote {0} file(s), {1:N1} MB; {2} already up to date." -f $written, ($writtenBytes / 1MB), $skipped)
}
$exe = Join-Path $InstallDir 'SessionDeck.exe'

# --- 3. Add the install dir to the user PATH (no-op if already there) ---
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (-not $userPath) { $userPath = '' }
$onPath = ($userPath -split ';') | Where-Object { $_ -and ($_.TrimEnd('\') -ieq $InstallDir.TrimEnd('\')) }
if ($onPath) {
    $pathStatus = 'already on the user PATH'
} else {
    $newPath = if ($userPath) { $userPath.TrimEnd(';') + ';' + $InstallDir } else { $InstallDir }
    [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
    $pathStatus = 'added to the user PATH (open a new terminal to pick it up)'
}
if (-not (($env:Path -split ';') | Where-Object { $_ -and ($_.TrimEnd('\') -ieq $InstallDir.TrimEnd('\')) })) {
    $env:Path += ';' + $InstallDir
}

# --- 4. Install the VSCode extension (warning, not failure: the app works without it;
#        only tab activation and tab labels are lost) ---
$extVersion = $null
$vsix = Get-ChildItem -Path $src -Filter 'sessiondeck-connector-*.vsix' -ErrorAction SilentlyContinue |
    Sort-Object { [version]($_.BaseName -replace '^sessiondeck-connector-', '') } |
    Select-Object -Last 1
$codeCmd = Get-Command code -ErrorAction SilentlyContinue
if (-not $vsix) {
    Write-Warning "No sessiondeck-connector-*.vsix found in the zip - skipping the VSCode extension."
} elseif (-not $codeCmd) {
    Write-Warning "VSCode 'code' command not found on PATH - skipping the extension. Install it later with: code --install-extension `"$($vsix.FullName)`""
} else {
    Write-Host "Installing the VSCode extension ($($vsix.Name))..."
    & code --install-extension $vsix.FullName --force 2>&1 | Out-Null
    $listed = & code --list-extensions --show-versions 2>$null |
        Where-Object { $_ -match 'sessiondeck-connector@(.+)$' } | Select-Object -First 1
    if ($listed -match 'sessiondeck-connector@(.+)$') { $extVersion = $Matches[1] }
    if (-not $extVersion) { Write-Warning "Extension install did not verify - check 'code --list-extensions --show-versions'." }
}

# --- 5. Register the Claude Code hooks (writes the real install path into settings.json) ---
Write-Host "Registering the Claude Code hooks..."
$hooksResult = Invoke-SessionDeck $exe 'install-hooks'
$hooksOutput = $hooksResult.Output
$hooksOutput | ForEach-Object { Write-Host "  $_" }
if ($hooksResult.ExitCode -ne 0) {
    Write-Error "install-hooks failed - see the message above. Nothing was written to settings.json."
}
$backupLine = $hooksOutput | Where-Object { $_ -match 'backup:\s*(.+)$' } | Select-Object -First 1
$backupPath = if ($backupLine -match 'backup:\s*(.+)$') { $Matches[1].Trim() } else { '(none - settings.json did not exist)' }

# --- 6. Start the app ---
Write-Host "Starting SessionDeck..."
Start-Process -FilePath $exe

# --- 7. Summary: the three parts update independently and can drift apart -
#        printing all three versions makes a mismatch visible ---
$appVersion = ((Get-Item $exe).VersionInfo.ProductVersion -split '\+')[0]
$hookScript = Join-Path $InstallDir 'hooks\sessiondeck-hook.ps1'
$hooksVersion = $null
$verMatch = Select-String -Path $hookScript -Pattern '^#\s*Version:\s*([0-9][0-9.]*)' -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($verMatch) { $hooksVersion = $verMatch.Matches[0].Groups[1].Value }

Write-Host ""
Write-Host "=== SessionDeck installed ==="
Write-Host "  Location:       $InstallDir"
Write-Host "  PATH:           $pathStatus"
Write-Host "  Hooks backup:   $backupPath"
Write-Host "  Versions:"
Write-Host "    app:          $appVersion"
Write-Host "    extension:    $(if ($extVersion) { $extVersion } else { 'not installed' })"
Write-Host "    hooks:        $(if ($hooksVersion) { $hooksVersion } else { 'unknown' })"
Write-Host ""
Write-Host "New Claude Code sessions will now appear as cards in SessionDeck."
