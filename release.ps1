# SessionDeck release script - one command from committed code to a published GitHub release.
# Usage:
#   .\release.ps1            # full release of the version in SessionDeck.csproj
#   .\release.ps1 -DryRun    # everything except the sync-commit and the actual release
#
# The version in SessionDeck.csproj is the single source of truth. The script:
#   guards (clean tree, main, unreleased version) -> syncs the hook-script version header
#   -> publishes self-contained -> runs the install-hooks tests against the published exe
#   -> packages the vsix only if the extension changed -> zips -> gh release create.
# PowerShell 5.1 compatible.
[CmdletBinding()]
param(
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
Set-Location $repo
$gh = "C:\Program Files\GitHub CLI\gh.exe"
if (-not (Test-Path $gh)) { $gh = 'gh' }   # fall back to PATH on other machines

function Fail([string]$msg) { Write-Host "RELEASE BLOCKED: $msg" -ForegroundColor Red; exit 1 }
function Step([string]$msg) { Write-Host "`n== $msg" -ForegroundColor Cyan }

# --- Guards -------------------------------------------------------------------
Step "Preflight checks"

if (git status --porcelain) { Fail "working tree is not clean - commit or stash first." }
$branch = git rev-parse --abbrev-ref HEAD
if ($branch -ne 'main') { Fail "on branch '$branch' - releases are cut from main." }

$csproj = Get-Content (Join-Path $repo 'SessionDeck.csproj') -Raw
if ($csproj -notmatch '<Version>([0-9]+\.[0-9]+\.[0-9]+)</Version>') { Fail "no <Version> in SessionDeck.csproj" }
$ver = $Matches[1]
$tag = "v$ver"

# --prune-tags keeps local tags in sync with releases replaced/deleted on GitHub.
git fetch --tags --prune --prune-tags --force --quiet
if (git tag --list $tag) { Fail "tag $tag already exists - bump <Version> in SessionDeck.csproj first." }

# Release policy: ONE release per major.minor line. A patch bump REPLACES the
# line's existing release (old release + tag are deleted); a new major.minor
# opens a new release. Only the highest version overall is marked "latest".
$verObj = [version]$ver
$releases = @(& $gh release list --json tagName --jq '.[].tagName' 2>$null)
$sameLine = @($releases | Where-Object { $_ -match "^v$($verObj.Major)\.$($verObj.Minor)\.[0-9]+$" })
$others = @($releases | Where-Object { $sameLine -notcontains $_ })

foreach ($old in $sameLine) {
    if ([version]($old.TrimStart('v')) -ge $verObj) { Fail "$old is already released and is not older than $ver." }
}
$isLatest = -not ($others | Where-Object { [version]($_.TrimStart('v')) -gt $verObj })

# Notes baseline: when replacing, the new release covers the WHOLE minor line,
# so diff from the line's oldest release; otherwise from the newest release.
$prevTag = $null
if ($sameLine) {
    $prevTag = $sameLine | Sort-Object { [version]($_.TrimStart('v')) } | Select-Object -First 1
} elseif ($releases) {
    $prevTag = $releases | Sort-Object { [version]($_.TrimStart('v')) } | Select-Object -Last 1
}

Write-Host "  version: $ver  (notes baseline: $(if ($prevTag) { $prevTag } else { 'none' }))"
if ($sameLine) { Write-Host "  will replace: $($sameLine -join ', ')" }
Write-Host "  will be marked latest: $isLatest"

# --- Sync the hook script's version header (BOM must survive - PS 5.1 + Hebrew) ---
Step "Hook script version header"
$hookPath = Join-Path $repo 'hooks\sessiondeck-hook.ps1'
$hookText = [IO.File]::ReadAllText($hookPath)
if ($hookText -notmatch '(?m)^# Version: (\S+)') { Fail "no '# Version:' header in the hook script." }
$hookVer = $Matches[1]
if ($hookVer -ne $ver) {
    if ($DryRun) {
        Write-Host "  would sync $hookVer -> $ver (+ commit)"
    } else {
        $hookText = $hookText -replace '(?m)^# Version: \S+', "# Version: $ver"
        [IO.File]::WriteAllText($hookPath, $hookText, [Text.UTF8Encoding]::new($true))
        $bytes = [IO.File]::ReadAllBytes($hookPath)
        if (-not ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) { Fail "BOM lost while syncing the hook header." }
        git add $hookPath
        git commit -m "chore: sync hook script version header to $ver" --quiet
        Write-Host "  synced $hookVer -> $ver and committed."
    }
} else {
    Write-Host "  already $ver."
}

# --- Build & test ---------------------------------------------------------------
Step "Publish (self-contained, one file per assembly)"
$pubDir = Join-Path $repo 'bin\Release\net10.0-windows\win-x64\publish'
# Incremental publish silently drops Content files (hooks\) from the output dir
# once they were published before (MSBuild up-to-date tracking) - always start fresh.
if (Test-Path $pubDir) { Remove-Item $pubDir -Recurse -Force }
# Deliberately NOT PublishSingleFile. Bundling the runtime produced a 140MB SessionDeck.exe,
# and every install rewrote all of it: explorer.exe then burned a whole core on 200,000+ soft
# page faults per second and stalled the machine for about a minute, measured on eight installs
# (agenda item 4.13.18). Spread over ~200 files the same upgrade rewrites only the assemblies
# that actually changed - typically SessionDeck.dll alone - and install.ps1 skips the rest by
# content hash. Still self-contained, so the zip needs no .NET runtime on the target machine.
dotnet publish -c Release -r win-x64 --self-contained --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed." }
$pubExe = Join-Path $pubDir 'SessionDeck.exe'

$exeVer = ((Get-Item $pubExe).VersionInfo.ProductVersion -split '\+')[0]
if ($exeVer -ne $ver) { Fail "published exe reports $exeVer, expected $ver." }
$bytes = [IO.File]::ReadAllBytes((Join-Path $pubDir 'hooks\sessiondeck-hook.ps1'))
if (-not ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) { Fail "published hook script lost its BOM." }

Step "install-hooks tests against the published exe"
powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'tests\install-hooks.tests.ps1') -Exe $pubExe
if ($LASTEXITCODE -ne 0) { Fail "tests failed." }

# --- VSCode extension ------------------------------------------------------------
Step "VSCode extension"
$extDir = Join-Path $repo 'vscode-extension'
$pkg = Get-Content (Join-Path $extDir 'package.json') -Raw | ConvertFrom-Json
$extVer = $pkg.version
$vsix = Join-Path $extDir "sessiondeck-connector-$extVer.vsix"

$extChanged = $true
if ($prevTag) {
    git diff --quiet $prevTag HEAD -- vscode-extension/src vscode-extension/package.json
    $extChanged = ($LASTEXITCODE -ne 0)
}
if ($extChanged -and $prevTag -and -not $DryRun) {
    # The extension changed but its version didn't -> the installed extension would look
    # identical to the old one. Block: bump vscode-extension/package.json first.
    $prevPkg = git show "${prevTag}:vscode-extension/package.json" | ConvertFrom-Json
    if ($prevPkg.version -eq $extVer) { Fail "vscode-extension changed since $prevTag but its package.json version is still $extVer - bump it." }
}
if ($extChanged -or -not (Test-Path $vsix)) {
    Write-Host "  packaging vsix $extVer..."
    Push-Location $extDir
    # npm/vsce write warnings to stderr; under EAP=Stop PS 5.1 turns a merged (2>&1)
    # native stderr line into a terminating NativeCommandError even on success.
    # The Test-Path below is the real success check.
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    npm install --silent 2>&1 | Out-Null
    npx @vscode/vsce package 2>&1 | Select-Object -Last 1
    $ErrorActionPreference = $prevEap
    Pop-Location
    if (-not (Test-Path $vsix)) { Fail "vsce did not produce $vsix" }
} else {
    Write-Host "  unchanged since $prevTag - reusing sessiondeck-connector-$extVer.vsix"
}

# --- Stage & zip -----------------------------------------------------------------
Step "Package zip"
$stage = Join-Path $repo "publish\SessionDeck-$ver-win-x64"
$zip = "$stage.zip"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
# The whole publish directory, not just the exe: without single-file the runtime assemblies
# and SessionDeck.dll sit beside it and the app does not start without them. hooks\ is in
# there too, as a Content item of the project.
Copy-Item (Join-Path $pubDir '*') $stage -Recurse
Copy-Item $vsix $stage
Copy-Item (Join-Path $repo 'install.ps1'), (Join-Path $repo 'uninstall.ps1') $stage
Compress-Archive -Path "$stage\*" -DestinationPath $zip -Force
Write-Host "  $zip ($([math]::Round((Get-Item $zip).Length/1MB)) MB)"

# --- Release notes + publish -------------------------------------------------------
Step "Release notes"
$notesFile = Join-Path $env:TEMP "sessiondeck-release-notes-$ver.md"
$changes = if ($prevTag) { git log "$prevTag..HEAD" --no-merges --format='- %s' } else { @('- initial packaged release') }
@"
**Install:** download the zip, extract, run ``install.ps1`` (no admin, no .NET needed). **Upgrade** from any previous version the same way. Details in the [README](https://github.com/eyalBPM/SessionDeck#getting-started).

Versions in this zip: app $ver | extension $extVer | hooks $ver

### Changes since $(if ($prevTag) { $prevTag } else { 'the beginning' })

$($changes -join "`n")
"@ | Set-Content -Path $notesFile -Encoding UTF8
Get-Content $notesFile | ForEach-Object { "  | $_" }

if ($DryRun) {
    Step "DryRun - stopping before: git push, replace [$($sameLine -join ', ')], gh release create $tag (latest: $isLatest)"
    exit 0
}

Step "Publish"
git push origin main
foreach ($old in $sameLine) {
    Write-Host "  replacing $old (release + tag deleted)"
    & $gh release delete $old --cleanup-tag --yes
    if ($LASTEXITCODE -ne 0) { Fail "failed to delete release $old." }
    # gh --cleanup-tag may already remove the local tag; under EAP=Stop a bare
    # `git tag -d` on a missing tag turns its stderr into a terminating error (v0.6.40 release incident).
    if (git tag --list $old) { git tag -d $old | Out-Null }
}
$latestFlag = if ($isLatest) { '--latest' } else { '--latest=false' }
& $gh release create $tag $zip --title $tag --notes-file $notesFile $latestFlag
if ($LASTEXITCODE -ne 0) { Fail "gh release create failed." }
Write-Host "`nDone." -ForegroundColor Green
