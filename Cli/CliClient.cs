using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using SessionDeck.Interop;
using SessionDeck.Services;

namespace SessionDeck.Cli;

/// <summary>
/// CLI mode: same exe launched with arguments. Attaches to the parent console
/// (WinExe has none of its own), forwards argv to the running instance's pipe, prints the
/// response and returns its exit code. Target run time &lt;100ms — critical for hooks.
/// </summary>
public static class CliClient
{
    private const int ConnectTimeoutMs = 3000;

    public static int Run(string[] args)
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);

        if (args[0] is "help" or "--help" or "-h" or "/?")
        {
            Console.Out.WriteLine(HelpText);
            return 0;
        }

        var response = Send(args);
        if (response == null)
        {
            Console.Error.WriteLine("sessiondeck: no running SessionDeck instance — start the app first.");
            return 2;
        }

        if (response.Output.Length > 0)
        {
            if (response.ExitCode == 0) Console.Out.WriteLine(response.Output);
            else Console.Error.WriteLine(response.Output);
        }
        return response.ExitCode;
    }

    public static void TrySendActivate() => Send(new[] { "activate" });

    private static PipeResponse? Send(string[] argv)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeServer.PipeName, PipeDirection.InOut);
            client.Connect(ConnectTimeoutMs);

            using var reader = new StreamReader(client, leaveOpen: true);
            using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(new PipeRequest { Argv = argv }));

            string? line = reader.ReadLine();
            if (line == null) return new PipeResponse(1, "no response from SessionDeck");
            return JsonSerializer.Deserialize<PipeResponse>(line) ?? new PipeResponse(1, "bad response");
        }
        catch
        {
            return null;
        }
    }

    private static string HelpText => $"""
        SessionDeck CLI (v{typeof(CliClient).Assembly.GetName().Version?.ToString(3)})

        sessiondeck list [--all]                  workspaces + sessions (--all includes closed)
        sessiondeck add <folder path>             add a workspace to the deck
        sessiondeck remove <target>
        sessiondeck remove --ghosts [--apply]     cards no session of a person's ever ran in; lists them unless --apply
        sessiondeck set <target> [--title "..."] [--desc "..."] [--color <c>]   empty value = auto
        sessiondeck focus <target>                activate the workspace's window in place
        sessiondeck pin <target>                  move the window to the Stage + activate
        sessiondeck stage --monitor <n> --half left|right | --full | --rect x,y,w,h
        sessiondeck zone --monitor <n> --half left|right | --quarter left|right | --custom left|right [--size 2/7|40%|0.4] | --full | --off
        sessiondeck status                        app state: version, zone, stage, counts
        sessiondeck reconcile                     close sessions whose tab or window is gone, now
                                                  (the deck's ↻ button; the automatic sweep is slower on purpose)
        sessiondeck log [--debug on|off]          diagnostic log dir + toggle debug level (%APPDATA%\SessionDeck\logs)
        sessiondeck quit                          close the running app cleanly (saves config, releases the zone)
        sessiondeck install-hooks [--settings <path>] [--dry-run]     register the Claude Code hooks
        sessiondeck uninstall-hooks [--settings <path>]               remove them (runs locally, app not needed)
        sessiondeck toggle list | get <id> | set <id> on|off    user-defined flags;
                                                  state is mirrored to %APPDATA%\SessionDeck\toggles\<id>
        sessiondeck tasks [--file "<path>.json" | --off | --page on|off]   external tasks panel (T-0116):
                                                  no args = show state; --file sets the JSON, --off disables,
                                                  --page opens/closes the full page
        sessiondeck groups                        the VSCode instances a new session can be aimed at
                                                  (one per Claude account) and whether each is reachable
        sessiondeck help

        session commands (called by the Claude Code hooks):
        sessiondeck session start  --id <sid> --workspace <cwd path or name> [--title "..."] [--source <s>]
        sessiondeck session status --id <sid> --state working|waiting|done|error|idle [--detail "..."]
                                   [--agents <n>]  subagents still running in the background; a
                                                   done with n>0 lands on working, not "your turn"
        sessiondeck session agents --id <sid> --launched   one background subagent was just
                                                   dispatched; +1 to the card's 🤖 chip until
                                                   the next Stop replaces it with the snapshot
        sessiondeck session end    --id <sid> [--reason <r>]
        sessiondeck session open   --id <sid>          focus VSCode + open/resume the session's tab
        sessiondeck session new    <target> [--prompt "..."] [--group <id>]
                                                       open a NEW Claude conversation tab in the workspace;
                                                       --group picks WHICH VSCode instance, i.e. which
                                                       Claude account (see: sessiondeck groups)
        sessiondeck session list   [--workspace <name>] [--all]
        all session commands also accept: --transcript <path> --mode <permission_mode>
                                          --dispatcher <sid>  the session that launched this one
                                          as a headless run; counted on ITS card, not shown here

        <target> = workspace id, or --match "<regex>" on the workspace name/title
        colors   = red, green, orange, blue, gray, yellow, purple, cyan, magenta, white, black, or #RRGGBB
        monitors = 1-based index; --rect is in virtual-screen pixels
        """;
}
