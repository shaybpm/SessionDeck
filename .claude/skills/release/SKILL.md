---
name: release
description: Cut and publish a SessionDeck release — the one-release-per-major.minor policy, what release.ps1 guards and does, and how to recover when it blocks. Trigger when asked to release, publish, ship, cut a version, tag a version, or when a release run fails.
---

# Releasing SessionDeck

`release.ps1` takes committed code to a published GitHub release in one command. The
`<Version>` in `SessionDeck.csproj` is the **single source of truth** — the script derives
the tag, the zip name and the release title from it.

```powershell
.\release.ps1 -DryRun    # everything except the sync-commit and the actual release
.\release.ps1            # the real thing
```

Always do a `-DryRun` first. It exercises every guard, the publish, the tests and the
packaging, so a failure surfaces before anything is pushed.

## The release policy

**One release per `major.minor` line.**

- A patch bump (`0.9.0` → `0.9.1`) **replaces** that line's release on GitHub: the old
  release and tag are deleted, and the new notes cover the whole line.
- A minor bump (`0.9` → `0.10`) opens a new release.
- Only the highest version overall is marked *latest* — patching an old line never steals
  the latest flag.

Because a replacement deletes the old tag, notes for a replacing release are diffed from
the **oldest** release of that line, not the newest. The script works this out itself.

## What the script guards before touching anything

It refuses to run, with `RELEASE BLOCKED`, when:

| Guard | Fix |
|---|---|
| Working tree not clean | Commit or stash first. |
| Not on `main` | Releases are cut from `main`. Merge the branch first. |
| No `<Version>` in the csproj | Restore the element. |
| The tag already exists | Bump `<Version>`. This also catches a tag left behind by a deleted release — the script runs `git fetch --tags --prune --prune-tags` first, so a stale local tag is not the cause. |

Then, after building, it fails if:

- the **published exe reports a different version** than the csproj, or
- the **published hook script lost its UTF-8 BOM**.

Both are real historical failure modes. The BOM check exists because an incremental
publish silently drops `Content` files; the script deletes the publish directory and
starts fresh for the same reason.

## What a full run does, in order

1. Preflight guards (above).
2. Syncs the `# Version:` header in `hooks/sessiondeck-hook.ps1` from the csproj, and
   commits that sync if it changed anything.
3. `dotnet publish -c Release -r win-x64 --self-contained` into a freshly deleted publish
   directory. **Self-contained but not single-file, deliberately**. See Notes.
4. Verifies the published exe's version and the hook script's BOM.
5. Runs `tests/install-hooks.tests.ps1` **against the published exe** — not the debug
   build. 38 cases; any failure blocks the release.
6. Packages the `.vsix` **only if `vscode-extension/` changed** since the notes baseline;
   otherwise it reuses the existing one. The vsix is never committed — it is a release
   artifact (`.gitignore`).
7. Zips the publish output (including `hooks\`) plus the vsix, `install.ps1` and
   `uninstall.ps1` into `SessionDeck-<version>-win-x64.zip`.
8. Writes the notes, deletes the release+tag being replaced if any, and runs
   `gh release create`.

## Notes

- `gh` is expected at `C:\Program Files\GitHub CLI\gh.exe`, falling back to PATH. If it
  isn't recognised, open a **new** terminal — PATH is read once per window.
- The three versions (app / extension / hooks) move independently. `install.ps1` prints
  all three at the end of an install so a mismatch is visible rather than mysterious.
- Packaging is deliberately self-contained (~150MB): it must work on a machine with no
  .NET installed. It is just as deliberately **not** `PublishSingleFile`. Bundling the
  runtime made a 140MB `SessionDeck.exe`, and since `install.ps1` rewrote it whole every
  time, each install stalled the machine for about a minute: `explorer.exe` burned a core
  on 200,000+ soft page faults per second, measured across eight installs (agenda item
  4.13.18). Spread over ~200 files, an upgrade rewrites only what changed, usually
  `SessionDeck.dll` alone, because `install.ps1` compares content hashes before writing.
  If single-file is ever restored, that installer optimisation dies with it.
- There is no auto-update and no update notification. That is a conscious choice for a
  tool used by a handful of people — telling people about a new release is manual.

## After publishing

Verify the artifact the way a user would meet it: download the zip from the Releases page
onto a machine **without** the .NET SDK, run `install.ps1`, and confirm a fresh Claude Code
session produces a card. That end-to-end check is the only one that proves the packaging
works; nothing in the script can substitute for it.
