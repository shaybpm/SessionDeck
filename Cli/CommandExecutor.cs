using System.Text;
using System.Text.RegularExpressions;
using SessionDeck.Models;
using SessionDeck.Services;
using SessionDeck.ViewModels;

namespace SessionDeck.Cli;

/// <summary>
/// Executes CLI argv against the live app state. Always invoked on the UI thread
/// (the pipe handler dispatches here). Session commands are the hooks' entry point
/// — they must stay fast and atomic.
/// </summary>
public sealed class CommandExecutor
{
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "match", "desc", "color", "monitor", "half", "quarter", "custom", "size", "rect", "title",
        "id", "workspace", "state", "path",
        "detail", "transcript", "source", "mode", "reason", "debug", "file",
        "prompt", "page",
    };

    private readonly MainWindow _window;
    private MainViewModel Vm => _window.Vm;

    public CommandExecutor(MainWindow window)
    {
        _window = window;
    }

    public PipeResponse Execute(string[] argv)
    {
        try
        {
            var args = Parse(argv);
            return args.Command switch
            {
                "list" => List(args),
                "add" => Add(args),
                "remove" => Remove(args),
                "set" => Set(args),
                "focus" => Focus(args),
                "pin" => Pin(args),
                "zone" => Zone(args),
                "stage" => Stage(args),
                "session" => Session(args),
                "toggle" => Toggle(args),
                "tasks" => Tasks(args),
                "status" => Status(),
                "log" => LogCmd(args),
                "activate" => Activate(),
                "quit" => Quit(),
                "snapshot" => Snapshot(args),   // internal: render the WPF tree to PNG (debug aid)
                _ => Err($"unknown command '{args.Command}'. Available: list, add, remove, set, focus, pin, zone, stage, session, toggle, tasks, status, log, quit"),
            };
        }
        catch (Exception ex)
        {
            return Err("error: " + ex.Message);
        }
    }

    // ---- workspace commands ----

    private PipeResponse List(ParsedArgs a)
    {
        if (Vm.Workspaces.Count == 0) return Ok("(no workspaces)");
        var sb = new StringBuilder();
        foreach (var w in Vm.Workspaces)
        {
            string bind = w.State == BindState.Connected ? "connected" : "no window";
            string flags = w.Hidden ? " [hidden]" : "";
            string branch = w.HasBranch ? $" ({w.Branch})" : "";
            sb.AppendLine($"[{w.Id}] {w.DisplayTitle}{branch} — {bind}{flags}  {w.Path}");
            if (a.Flags.Contains("tabs"))
            {
                foreach (var label in w.ClaudeTabLabels)
                    sb.AppendLine($"     tab: \"{label}\"{(label == w.ActiveClaudeTabLabel ? "  [active+focused]" : "")}");
                if (w.ClaudeTabLabels.Count == 0) sb.AppendLine("     tab: (none reported)");
            }
            foreach (var s in w.Sessions.Where(s => (!s.Closed && !s.Phantom) || a.Flags.Contains("all")))
            {
                string ack = s.Acknowledged ? " ack" : "";
                sb.AppendLine($"     {s.SessionId}  {s.StatusText}{ack}  {s.DisplayTitle}" +
                              (s.Description.Length > 0 ? $"  — {s.Description}" : ""));
            }
        }
        return Ok(sb.ToString().TrimEnd());
    }

    private PipeResponse Add(ParsedArgs a)
    {
        string? path = a.Options.GetValueOrDefault("path") ??
                       (a.Positionals.Count > 0 ? a.Positionals[0] : null);
        if (path == null) return Err("add requires a folder path: sessiondeck add <path>");
        var (ws, err) = _window.AddWorkspaceFromPath(path);
        if (ws == null) return Err(err!);
        string bind = ws.State == BindState.Connected ? "connected" : "no open window yet";
        return Ok($"added workspace {ws.Id}: \"{ws.DisplayTitle}\" [{bind}]");
    }

    private PipeResponse Remove(ParsedArgs a)
    {
        var (ws, err) = ResolveTarget(a);
        if (ws == null) return Err(err!);
        _window.RemoveWorkspace(ws);
        return Ok($"removed workspace {ws.Id} (\"{ws.DisplayTitle}\")");
    }

    private PipeResponse Set(ParsedArgs a)
    {
        var (ws, err) = ResolveTarget(a);
        if (ws == null) return Err(err!);
        if (!a.Options.ContainsKey("title") && !a.Options.ContainsKey("desc") && !a.Options.ContainsKey("color"))
            return Err("set requires --title/--desc/--color (empty value reverts to auto)");

        var changes = new List<string>();
        if (a.Options.TryGetValue("title", out var title))
        {
            ws.CustomTitle = title.Length == 0 ? null : title;
            changes.Add(title.Length == 0 ? "title=auto" : $"title=\"{title}\"");
        }
        if (a.Options.TryGetValue("desc", out var desc))
        {
            ws.Description = desc;
            changes.Add($"desc=\"{desc}\"");
        }
        if (a.Options.TryGetValue("color", out var color))
        {
            if (color.Length == 0)
            {
                ws.CustomColor = null;
                changes.Add("color=auto");
            }
            else
            {
                if (!ColorUtil.TryParse(color, out _))
                    return Err($"unknown color '{color}'. Use {ColorUtil.KnownNames} or #RRGGBB");
                ws.CustomColor = color;
                changes.Add($"color={color}");
            }
        }
        _window.QueueSave();
        return Ok($"workspace {ws.Id}: {string.Join(", ", changes)}");
    }

    private PipeResponse Focus(ParsedArgs a)
    {
        var (ws, err) = ResolveTarget(a);
        if (ws == null) return Err(err!);
        var (ok, msg) = _window.FocusWorkspace(ws);
        return ok ? Ok($"focused workspace {ws.Id}") : Err(msg);
    }

    private PipeResponse Pin(ParsedArgs a)
    {
        var (ws, err) = ResolveTarget(a);
        if (ws == null) return Err(err!);
        var (ok, msg) = _window.PinWorkspace(ws);
        return ok ? Ok($"pinned workspace {ws.Id} to stage") : Err(msg);
    }

    // ---- session commands (called by the Claude Code hooks) ----

    private PipeResponse Session(ParsedArgs a)
    {
        string sub = a.Positionals.Count > 0 ? a.Positionals[0].ToLowerInvariant() : "";
        switch (sub)
        {
            case "start":
            {
                if (!a.Options.TryGetValue("id", out var id)) return Err("session start requires --id <session_id>");
                string workspace = a.Options.GetValueOrDefault("workspace", "");
                var (msg, ok) = _window.StartSession(id, workspace, a.Options.GetValueOrDefault("title"), HookInfoFrom(a));
                return ok ? Ok(msg) : Err(msg);
            }
            case "status":
            {
                if (!a.Options.TryGetValue("id", out var id)) return Err("session status requires --id <session_id>");
                if (!a.Options.TryGetValue("state", out var stateStr) ||
                    !SessionStatusNames.TryParse(stateStr, out var status))
                    return Err("session status requires --state working|waiting|done|he|error|idle");
                var (msg, ok) = _window.SetSessionStatus(id, status,
                    a.Options.GetValueOrDefault("workspace", ""), HookInfoFrom(a));
                return ok ? Ok(msg) : Err(msg);
            }
            case "end":
            {
                if (!a.Options.TryGetValue("id", out var id)) return Err("session end requires --id <session_id>");
                var (msg, ok) = _window.EndSession(id, HookInfoFrom(a));
                return ok ? Ok(msg) : Err(msg);
            }
            case "new":
            {
                // Target parsing shares the workspace resolver: sessiondeck session new <ws id | --match ...>
                var rest = new ParsedArgs { Command = "session" };
                foreach (var p in a.Positionals.Skip(1)) rest.Positionals.Add(p);
                foreach (var (k, v) in a.Options) rest.Options[k] = v;
                var (wsNew, errNew) = ResolveTarget(rest);
                if (wsNew == null) return Err(errNew!);
                // --prompt lands in the new session's input box, unsent, exactly as a
                // click on a tasks-panel card already does. Without it the CLI could
                // only ever open an EMPTY session, so anything outside the WPF panel
                // (a protocol handler, a script, another tool) had no way to say what
                // the session is for.
                a.Options.TryGetValue("prompt", out var promptNew);
                var (okNew, msgNew) = _window.NewSessionInVscode(wsNew, promptNew);
                return okNew ? Ok($"opening a new session in workspace {wsNew.Id}") : Err(msgNew);
            }
            case "open":
            {
                if (!a.Options.TryGetValue("id", out var id)) return Err("session open requires --id <session_id>");
                if (Vm.FindSession(id) is not { } found) return Err($"unknown session id {id}");
                var (ws, session) = found;
                _window.FocusWorkspace(ws);
                var (sent, msg) = _window.OpenSessionInVscode(ws, session);
                return sent ? Ok($"opening session {id} in VSCode") : Err(msg);
            }
            case "list":
            {
                var wanted = a.Options.GetValueOrDefault("workspace");
                bool all = a.Flags.Contains("all");
                var sb = new StringBuilder();
                foreach (var w in Vm.Workspaces)
                {
                    if (wanted != null && !string.Equals(w.Name, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var s in w.Sessions.Where(s => all || (!s.Closed && !s.Phantom)))
                        sb.AppendLine($"{s.SessionId}  {s.StatusText,-8} {w.DisplayTitle}  {s.DisplayTitle}");
                }
                return Ok(sb.Length > 0 ? sb.ToString().TrimEnd() : "(no sessions)");
            }
            default:
                return Err("session requires: start | status | end | open | new | list");
        }
    }

    // ---- custom toggles (feature 2026-07-19) ----

    private PipeResponse Toggle(ParsedArgs a)
    {
        string sub = a.Positionals.Count > 0 ? a.Positionals[0].ToLowerInvariant() : "list";
        if (sub == "list")
        {
            if (Vm.CustomToggles.Count == 0)
                return Ok("(no toggles — add them from the settings menu: Toggles (flags))");
            return Ok(string.Join(Environment.NewLine,
                Vm.CustomToggles.Select(t => $"{t.Id}  {(t.Enabled ? "on " : "off")}  {t.Name}")));
        }

        if (a.Positionals.Count < 2) return Err($"toggle {sub} requires a toggle id");
        var toggle = Vm.CustomToggles.FirstOrDefault(
            t => string.Equals(t.Id, a.Positionals[1], StringComparison.OrdinalIgnoreCase));
        if (toggle == null) return Err($"unknown toggle '{a.Positionals[1]}'");

        switch (sub)
        {
            case "get":
                return Ok(toggle.Enabled ? "on" : "off");
            case "set":
            {
                string value = a.Positionals.Count > 2 ? a.Positionals[2].ToLowerInvariant() : "";
                bool? enabled = value switch
                {
                    "on" or "1" or "true" => true,
                    "off" or "0" or "false" => false,
                    _ => null,
                };
                if (enabled == null) return Err("toggle set requires: <id> on|off");
                toggle.Enabled = enabled.Value;    // Changed handler writes the flag file
                return Ok($"toggle {toggle.Id}: {(toggle.Enabled ? "on" : "off")}");
            }
            default:
                return Err("toggle requires: list | get <id> | set <id> on|off");
        }
    }

    // ---- zone / stage / status ----

    private PipeResponse Stage(ParsedArgs a)
    {
        int monitor = Vm.StageMonitor;
        if (a.Options.TryGetValue("monitor", out var monStr))
        {
            if (!int.TryParse(monStr, out int mon1) || mon1 < 1 || mon1 > _window.MonitorCount)
                return Err($"--monitor must be 1..{_window.MonitorCount}");
            monitor = mon1 - 1;
        }

        if (a.Options.TryGetValue("rect", out var rectStr))
        {
            var rect = MainWindow.ParseRect(rectStr);
            if (rect == null) return Err("--rect must be x,y,w,h (virtual-screen px, w/h > 0)");
            _window.SetStage(monitor, StageMode.Rect, rect);
            return Ok($"stage: rect {rectStr}");
        }

        StageMode mode;
        if (a.Flags.Contains("full")) mode = StageMode.Full;
        else if (a.Options.TryGetValue("half", out var half))
        {
            if (half == "left") mode = StageMode.HalfLeft;
            else if (half == "right") mode = StageMode.HalfRight;
            else return Err("--half must be left or right");
        }
        else return Err("stage requires --half left|right, --full, or --rect x,y,w,h");

        _window.SetStage(monitor, mode, null);
        return Ok($"stage: {ModeNames.ToName(mode)} on monitor {monitor + 1}");
    }

    private PipeResponse Zone(ParsedArgs a)
    {
        ZoneMode mode;
        if (a.Flags.Contains("off")) mode = ZoneMode.Off;
        else if (a.Flags.Contains("full")) mode = ZoneMode.Full;
        else if (a.Options.TryGetValue("half", out var half))
        {
            if (half == "left") mode = ZoneMode.HalfLeft;
            else if (half == "right") mode = ZoneMode.HalfRight;
            else return Err("--half must be left or right");
        }
        else if (a.Options.TryGetValue("quarter", out var quarter))
        {
            if (quarter == "left") mode = ZoneMode.QuarterLeft;
            else if (quarter == "right") mode = ZoneMode.QuarterRight;
            else return Err("--quarter must be left or right");
        }
        else if (a.Options.TryGetValue("custom", out var custom))
        {
            if (custom == "left") mode = ZoneMode.CustomLeft;
            else if (custom == "right") mode = ZoneMode.CustomRight;
            else return Err("--custom must be left or right");
        }
        else return Err("zone requires --half left|right, --quarter left|right, --custom left|right [--size 2/7], --full, or --off");

        string? size = null;
        if (a.Options.TryGetValue("size", out var sizeStr))
        {
            if (mode is not (ZoneMode.CustomLeft or ZoneMode.CustomRight))
                return Err("--size only applies to --custom left|right");
            if (!ZoneSizeParser.TryParse(sizeStr, out _))
                return Err("--size must be a fraction like 2/7, a percent like 40%, or a decimal 0.05..1");
            size = sizeStr;
        }

        int monitor = Vm.ZoneMonitor;
        if (a.Options.TryGetValue("monitor", out var monStr))
        {
            if (!int.TryParse(monStr, out int mon1) || mon1 < 1 || mon1 > _window.MonitorCount)
                return Err($"--monitor must be 1..{_window.MonitorCount}");
            monitor = mon1 - 1;
        }

        _window.ApplyZone(monitor, mode, customSize: size);
        string sizeSuffix = mode is ZoneMode.CustomLeft or ZoneMode.CustomRight ? $" {Vm.ZoneSize}" : "";
        return Ok($"zone: {ModeNames.ToName(mode)}{sizeSuffix} on monitor {monitor + 1}");
    }

    private PipeResponse Status()
    {
        int connected = Vm.Workspaces.Count(w => w.State == BindState.Connected);
        int openSessions = Vm.AllSessions().Count(s => !s.Closed);
        string version = typeof(CommandExecutor).Assembly.GetName().Version?.ToString(3) ?? "?";
        string stage = Vm.StageMode == StageMode.Rect && Vm.StageRect is { } r
            ? $"rect {r.Left},{r.Top},{r.Width},{r.Height}"
            : $"{ModeNames.ToName(Vm.StageMode)} (monitor {Vm.StageMonitor + 1})";
        return Ok($"""
            SessionDeck {version}
            zone:  {ModeNames.ToName(Vm.ZoneMode)}{(Vm.ZoneMode is ZoneMode.CustomLeft or ZoneMode.CustomRight ? $" {Vm.ZoneSize}" : "")} (monitor {Vm.ZoneMonitor + 1})
            stage: {stage}
            workspaces: {Vm.Workspaces.Count} ({connected} with window, {Vm.Workspaces.Count(w => w.Hidden)} hidden)
            sessions: {openSessions} open
            log: debug={(LogService.DebugEnabled ? "on" : "off")}  {LogService.LogDir}
            """);
    }

    /// <summary>The external tasks file (T-0116): show / set / turn off from the CLI.
    /// Same semantics as the ⚙ dialog — the folder must exist, the file itself may not
    /// yet (the watcher picks it up when it appears).</summary>
    private PipeResponse Tasks(ParsedArgs a)
    {
        if (a.Options.TryGetValue("file", out var file) || a.Flags.Contains("off"))
        {
            string? path = a.Flags.Contains("off") ? null : file;
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    string? dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
                    if (dir == null || !System.IO.Directory.Exists(dir))
                        return Err($"the file's folder does not exist: {dir ?? path}");
                }
                catch (Exception ex)
                {
                    return Err("invalid path: " + ex.Message);
                }
            }
            _window.ApplyTasksFile(path);
            _window.QueueSave();
            return Ok(Vm.TasksFilePath == null ? "tasks: off" : $"tasks: file={Vm.TasksFilePath}");
        }

        if (Vm.TasksFilePath == null)
            return Ok("tasks: off  (enable: sessiondeck tasks --file \"<path>.json\")");

        // Open/close the page from the CLI. It exists for verification: the deck is a
        // singleton on a live desktop, so a change to the tasks page cannot be checked by
        // clicking a second copy, and `snapshot` renders nothing while the page is hidden.
        if (a.Options.TryGetValue("page", out var page))
        {
            if (page is not ("on" or "off")) return Err("--page must be on or off");
            if (page == "on") _window.ShowTasksPage(); else _window.CloseTasksPage();
        }
        var p = Vm.TasksPanel;
        string state = p.HasError ? $"ERROR: {p.ErrorText}"
            : $"{p.PinnedTasks.Count + p.OtherTasks.Count} tasks ({p.PinnedTasks.Count} pinned)"
              + (p.HasWarning ? $"  WARNING: {p.WarningText}" : "");
        return Ok($"tasks: file={Vm.TasksFilePath}\n{state}");
    }

    private PipeResponse LogCmd(ParsedArgs a)
    {
        if (a.Options.TryGetValue("debug", out var dbg))
        {
            if (dbg is not ("on" or "off")) return Err("--debug must be on or off");
            LogService.DebugEnabled = dbg == "on";
            LogService.Info("log", $"debug={dbg} (cli)");
            _window.QueueSave();
        }
        return Ok($"log: debug={(LogService.DebugEnabled ? "on" : "off")}  {LogService.LogDir}");
    }

    private PipeResponse Activate()
    {
        _window.ActivateFromCli();
        return Ok("");
    }

    /// <summary>Clean shutdown for the installer: Close() runs
    /// OnClosing, which saves config and releases the AppBar — a forced kill skips that
    /// and leaves the Windows work area permanently shrunken. The close is deferred so
    /// the pipe response reaches the client before the pipe server is disposed.</summary>
    private PipeResponse Quit()
    {
        _ = Task.Delay(150).ContinueWith(_ =>
            _window.Dispatcher.BeginInvoke(() => _window.Close()));
        return Ok("quitting");
    }

    /// <summary>Internal debug command: renders the window's WPF visual tree to a PNG.
    /// DWM thumbnails are composited by the OS and never appear here — chrome only.</summary>
    private PipeResponse Snapshot(ParsedArgs a)
    {
        if (a.Positionals.Count == 0) return Err("snapshot requires a target .png path");
        string path = a.Positionals[0];
        var root = (System.Windows.Media.Visual?)_window.Content;
        if (root == null || _window.ActualWidth < 1) return Err("window has no content");
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_window);
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(_window.ActualWidth * dpi.DpiScaleX), (int)(_window.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(root);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = System.IO.File.Create(path);
        encoder.Save(fs);
        return Ok($"saved {path}");
    }

    // ---- helpers ----

    private static MainWindow.HookInfo HookInfoFrom(ParsedArgs a) => new(
        Detail: a.Options.GetValueOrDefault("detail"),
        Transcript: a.Options.GetValueOrDefault("transcript"),
        Source: a.Options.GetValueOrDefault("source"),
        Mode: a.Options.GetValueOrDefault("mode"),
        Reason: a.Options.GetValueOrDefault("reason"),
        PermissionDialog: a.Flags.Contains("permission-dialog"));

    private (WorkspaceViewModel?, string?) ResolveTarget(ParsedArgs a)
    {
        if (a.Positionals.Count > 0)
        {
            if (!int.TryParse(a.Positionals[0], out int id))
                return (null, $"invalid workspace id '{a.Positionals[0]}'");
            var byId = Vm.FindById(id);
            return byId != null ? (byId, null) : (null, $"no workspace with id {id}");
        }
        if (a.Options.TryGetValue("match", out var pattern))
        {
            if (!TryRegex(pattern, out var rx, out var rxErr))
                return (null, rxErr);
            var ws = Vm.Workspaces.FirstOrDefault(w => rx!.IsMatch(w.DisplayTitle) || rx.IsMatch(w.Name));
            return ws != null ? (ws, null) : (null, $"no workspace matches /{pattern}/");
        }
        return (null, "target required: <workspace id> or --match \"<regex>\"");
    }

    private static bool TryRegex(string pattern, out Regex? rx, out string? error)
    {
        try
        {
            rx = new Regex(pattern);
            error = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            rx = null;
            error = $"invalid regex: {ex.Message}";
            return false;
        }
    }

    private static PipeResponse Ok(string output) => new(0, output);
    private static PipeResponse Err(string output) => new(1, output);

    private sealed class ParsedArgs
    {
        public string Command = "";
        public List<string> Positionals { get; } = new();
        public Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static ParsedArgs Parse(string[] argv)
    {
        var result = new ParsedArgs { Command = argv[0].ToLowerInvariant() };
        for (int i = 1; i < argv.Length; i++)
        {
            string token = argv[i];
            if (token.StartsWith("--"))
            {
                string name = token[2..];
                if (ValueOptions.Contains(name) && i + 1 < argv.Length)
                    result.Options[name] = argv[++i];
                else
                    result.Flags.Add(name);
            }
            else
            {
                result.Positionals.Add(token);
            }
        }
        return result;
    }
}
