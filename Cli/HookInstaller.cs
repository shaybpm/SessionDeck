using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using SessionDeck.Interop;

namespace SessionDeck.Cli;

/// <summary>
/// `sessiondeck install-hooks` / `uninstall-hooks`. Merges the eleven
/// SessionDeck hooks into ~/.claude/settings.json, pointing at the hook script that ships
/// next to the installed exe. Runs entirely in the CLI process — never through the pipe —
/// because it must work before the app has ever started.
/// </summary>
public static class HookInstaller
{
    /// <summary>Any hook command containing this marker belongs to SessionDeck — removing
    /// them before re-adding is what makes install idempotent and path-upgrade safe.</summary>
    private const string ScriptMarker = "sessiondeck-hook.ps1";

    // Must match hooks/README.md ("Installation") exactly, including the matchers.
    private static readonly (string Event, string? Matcher)[] HookTable =
    {
        ("SessionStart", null),
        ("UserPromptSubmit", null),
        ("Notification", null),
        ("PermissionRequest", null),
        ("Stop", null),
        ("StopFailure", null),
        ("SessionEnd", null),
        ("PreToolUse", "AskUserQuestion|ExitPlanMode"),
        ("PostToolUse", "AskUserQuestion|ExitPlanMode|Agent"),
        ("Elicitation", null),
        ("ElicitationResult", null),
    };

    public static int Run(string[] args)
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);

        bool install = args[0].Equals("install-hooks", StringComparison.OrdinalIgnoreCase);
        bool dryRun = false;
        string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--settings" when i + 1 < args.Length:
                    settingsPath = Path.GetFullPath(args[++i]);
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    return Fail($"unknown option '{args[i]}'. Usage: sessiondeck {args[0]} [--settings <path>] [--dry-run]");
            }
        }

        // AppContext.BaseDirectory is the exe's directory even under single-file publish.
        string scriptPath = Path.Combine(AppContext.BaseDirectory, "hooks", "sessiondeck-hook.ps1");
        if (install && !File.Exists(scriptPath))
            return Fail($"hook script not found at {scriptPath} - refusing to register hooks that point nowhere. Reinstall SessionDeck.");

        // Load. A corrupt file must fail without writing; a missing/empty one starts fresh.
        JsonObject root;
        bool fileExisted = File.Exists(settingsPath);
        if (fileExisted)
        {
            string text = File.ReadAllText(settingsPath);
            if (string.IsNullOrWhiteSpace(text))
            {
                root = new JsonObject();
            }
            else
            {
                try
                {
                    root = JsonNode.Parse(text) as JsonObject
                           ?? throw new JsonException("top-level value is not an object");
                }
                catch (JsonException ex)
                {
                    return Fail($"{settingsPath} is not valid JSON ({ex.Message}). Fix it manually - nothing was written.");
                }
            }
        }
        else
        {
            if (!install)
            {
                Console.Out.WriteLine($"{settingsPath} does not exist - nothing to uninstall.");
                return 0;
            }
            root = new JsonObject();
        }

        Merge(root, install, scriptPath);

        string output = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        if (dryRun)
        {
            Console.Out.WriteLine($"--dry-run: {settingsPath} would become:");
            Console.Out.WriteLine(output);
            return 0;
        }

        string? backupPath = null;
        if (fileExisted)
        {
            backupPath = $"{settingsPath}.sessiondeck-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(settingsPath, backupPath, overwrite: true);
        }

        // Atomic write (same pattern as ConfigStore): UTF-8 without BOM, tmp + move.
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        string tmp = settingsPath + ".tmp";
        File.WriteAllText(tmp, output);
        File.Move(tmp, settingsPath, overwrite: true);

        Console.Out.WriteLine(install
            ? $"SessionDeck hooks installed into {settingsPath}"
            : $"SessionDeck hooks removed from {settingsPath}");
        Console.Out.WriteLine($"  hook script: {scriptPath}");
        if (backupPath != null)
            Console.Out.WriteLine($"  backup: {backupPath}");
        return 0;
    }

    /// <summary>Applies the merge in place. Only touches
    /// SessionDeck's own groups — hooks of other tools are preserved verbatim.</summary>
    private static void Merge(JsonObject root, bool install, string scriptPath)
    {
        if (root["hooks"] is not JsonObject hooks)
        {
            if (!install) return;                       // no hooks key — nothing of ours to remove
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        // -WindowStyle Hidden is not cosmetic: without it every hook that fires flashes a console
        // window, and creating and destroying a window is shell work that explorer.exe pays for
        // (measured at 103% of a core, with the taskbar dropping out, 2026-08-06).
        // Must stay before -File: everything after -File belongs to the script.
        // What it does NOT buy back: Claude Code honours the matcher, so the two entries below
        // never run on an ordinary tool call. The bridge is a per-turn cost, not a per-call one.
        // Measured rates and the sampling that proves it: hooks/README.md, "What the bridge costs".
        string command = $"powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"";

        foreach (var (evt, matcher) in HookTable)
        {
            if (hooks[evt] is not JsonArray groups)
            {
                if (!install) continue;
                groups = new JsonArray();
                hooks[evt] = groups;
            }

            for (int i = groups.Count - 1; i >= 0; i--)
            {
                if (groups[i] is not JsonObject group || group["hooks"] is not JsonArray inner) continue;
                for (int j = inner.Count - 1; j >= 0; j--)
                {
                    if (inner[j] is JsonObject h &&
                        h["command"] is JsonValue v && v.TryGetValue(out string? cmd) &&
                        cmd?.Contains(ScriptMarker, StringComparison.OrdinalIgnoreCase) == true)
                        inner.RemoveAt(j);
                }
                if (inner.Count == 0)
                    groups.RemoveAt(i);
            }

            if (install)
            {
                var group = new JsonObject();
                if (matcher != null) group["matcher"] = matcher;
                group["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = $"{command} {evt}",
                });
                groups.Add(group);
            }
            else if (groups.Count == 0)
            {
                hooks.Remove(evt);
            }
        }

        if (hooks.Count == 0)
            root.Remove("hooks");
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("sessiondeck: " + message);
        return 1;
    }
}
