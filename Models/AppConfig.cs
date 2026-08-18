namespace SessionDeck.Models;

// Order matters: ZoneModeCombo items are mapped by index cast (persistence is by name).
public enum ZoneMode { Off, QuarterLeft, HalfLeft, HalfRight, QuarterRight, Full, CustomLeft, CustomRight }
public enum StageMode { Full, HalfLeft, HalfRight, Rect }

/// <summary>Order of the workspace cards on the deck (Shay, 09-08-2026 — with the ⚡ filter
/// off the whole deck comes back and A→Z alone is not a useful way through it). Live cards
/// float to the top in every mode (decision 16); this decides the order below them.</summary>
public enum DeckSort { Alphabetical, Recent, Frequency }

public static class ModeNames
{
    /// <summary>
    /// Does this zone take work area away from the monitor (the AppBar reservation), or does
    /// it only place the window?
    ///
    /// Reserving exists so a MAXIMIZED window stays out of the deck's strip and lands in the
    /// rest of the monitor instead. `Full` has no rest of the monitor, so the reservation has
    /// nothing left to protect and only takes: measured 10-08-2026 on Shay's second display,
    /// Windows reported a work area of 13x816 on a 1536x864 screen, which is not a strip, it
    /// is a monitor no window can be opened, restored or maximized on any more. The deck then
    /// looked like it was pinned over everything while the pin was off — the window was never
    /// topmost (verified: pushed to the bottom of the z-order it stayed there), the monitor
    /// simply had nowhere else for a window to be. Full now sizes the deck to the monitor and
    /// reserves nothing, so other windows use the screen normally and land above or below the
    /// deck exactly as the 📌 pin says.
    /// </summary>
    public static bool ReservesWorkArea(ZoneMode m) => m is not (ZoneMode.Off or ZoneMode.Full);

    /// <summary>
    /// Does the deck hold a place of its own on screen, so a session going orange or green is
    /// visible where the user already looks and needs no OS notification?
    ///
    /// A separate question from ReservesWorkArea, and it must stay separate: every zone but
    /// `Off` gives the deck a fixed place, while only some of them do it by reserving work
    /// area. The notification gate used the reservation as its proxy for "on screen", which
    /// held until Full stopped reserving on 10-08-2026 — the same hour, a deck that had been
    /// quiet for weeks in Full started balloon-ing every finished session, about sixty an hour
    /// with headless waves running (Shay, 10-08-2026). Full still fills its monitor; nothing
    /// about how visible it is changed that day.
    /// </summary>
    public static bool HasOwnPlace(ZoneMode m) => m is not ZoneMode.Off;

    public static string ToName(ZoneMode m) => m switch
    {
        ZoneMode.Off => "off",
        ZoneMode.QuarterLeft => "quarter-left",
        ZoneMode.HalfLeft => "half-left",
        ZoneMode.HalfRight => "half-right",
        ZoneMode.QuarterRight => "quarter-right",
        ZoneMode.Full => "full",
        ZoneMode.CustomLeft => "custom-left",
        ZoneMode.CustomRight => "custom-right",
        _ => "off",
    };

    public static bool TryParseZone(string s, out ZoneMode m)
    {
        m = s switch
        {
            "off" => ZoneMode.Off,
            "quarter-left" => ZoneMode.QuarterLeft,
            "half-left" => ZoneMode.HalfLeft,
            "half-right" => ZoneMode.HalfRight,
            "quarter-right" => ZoneMode.QuarterRight,
            "full" => ZoneMode.Full,
            "custom-left" => ZoneMode.CustomLeft,
            "custom-right" => ZoneMode.CustomRight,
            _ => (ZoneMode)(-1),
        };
        return (int)m >= 0;
    }

    public static string ToName(DeckSort s) => s switch
    {
        DeckSort.Recent => "recent",
        DeckSort.Frequency => "frequency",
        _ => "abc",
    };

    public static bool TryParseDeckSort(string s, out DeckSort sort)
    {
        sort = s switch
        {
            "abc" => DeckSort.Alphabetical,
            "recent" => DeckSort.Recent,
            "frequency" => DeckSort.Frequency,
            _ => (DeckSort)(-1),
        };
        return (int)sort >= 0;
    }

    public static string ToName(StageMode m) => m switch
    {
        StageMode.Full => "full",
        StageMode.HalfLeft => "half-left",
        StageMode.HalfRight => "half-right",
        StageMode.Rect => "rect",
        _ => "full",
    };

    public static bool TryParseStage(string s, out StageMode m)
    {
        m = s switch
        {
            "full" => StageMode.Full,
            "half-left" => StageMode.HalfLeft,
            "half-right" => StageMode.HalfRight,
            "rect" => StageMode.Rect,
            _ => (StageMode)(-1),
        };
        return (int)m >= 0;
    }
}

/// <summary>Parses a custom zone width: "2/7" (fraction), "40%" (percent) or "0.4" (ratio).
/// Valid range is 5%..100% of the monitor width.</summary>
public static class ZoneSizeParser
{
    public static bool TryParse(string? s, out double fraction)
    {
        fraction = 0;
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return false;
        double f;
        int slash = s.IndexOf('/');
        if (slash > 0)
        {
            if (!int.TryParse(s[..slash].Trim(), out int num) ||
                !int.TryParse(s[(slash + 1)..].Trim(), out int den) || den <= 0 || num <= 0)
                return false;
            f = (double)num / den;
        }
        else if (s.EndsWith('%'))
        {
            if (!double.TryParse(s[..^1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double pct)) return false;
            f = pct / 100.0;
        }
        else
        {
            if (!double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out f)) return false;
        }
        if (f < 0.05 || f > 1.0) return false;
        fraction = f;
        return true;
    }
}

/// <summary>Legacy stage A/B generic tile — carried through the config untouched so
/// pre-cards data is never lost, but no longer shown in the UI (decision 15).</summary>
public class TileConfig
{
    public int Id { get; set; }
    public string ProcessName { get; set; } = "";
    public string TitlePattern { get; set; } = "";
    public string Title { get; set; } = "";
    public bool ManualTitle { get; set; }
    public string Description { get; set; } = "";
    public string Color { get; set; } = "gray";
    public string? AltColor { get; set; }
    public int BlinkIntervalMs { get; set; } = 500;
}

/// <summary>A VSCode workspace on the deck — a persistent entity; the OS window
/// is only its live binding.</summary>
public class WorkspaceConfig
{
    public int Id { get; set; }
    public string Path { get; set; } = "";           // folder path; may be empty for drag-in adds until a hook reports cwd
    public string Name { get; set; } = "";           // project name (folder leaf by default)
    public string? CustomTitle { get; set; }         // null = show Name
    public string Description { get; set; } = "";
    public string? CustomColor { get; set; }         // null = auto (Peacock / default)
    public bool Hidden { get; set; }
    public string? TranscriptDir { get; set; }       // learned from hooks (stage D)
    /// <summary>Last time a session on this card reported anything, or the user opened it
    /// from the deck — the key behind the "last used" order. Persisted because the sessions
    /// it was derived from are pruned by retention, so the deck would otherwise forget that
    /// a card was busy last week the moment its 21st session closed.</summary>
    public DateTime? LastUsedAt { get; set; }
    /// <summary>How many sessions have ever been opened on this card — the "most used"
    /// order. Counting the sessions still on the card would top out at the retention limit
    /// and rank every heavily-used workspace the same.</summary>
    public int UseCount { get; set; }
    public List<SessionConfig> Sessions { get; set; } = new();
}

/// <summary>A Claude Code session reported by the hooks.</summary>
public class SessionConfig
{
    public string SessionId { get; set; } = "";
    public string? CustomTitle { get; set; }
    public string Description { get; set; } = "";
    public string Status { get; set; } = "idle";     // idle|working|waiting|done|error
    public bool Acknowledged { get; set; }
    public bool Closed { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    // Everything the Claude Code hook payload provides (v0.4 — decision: keep it all):
    public string Detail { get; set; } = "";         // last prompt / notification message
    public string? TranscriptPath { get; set; }
    public string? Source { get; set; }              // SessionStart source: startup|resume|clear|compact
    public string? PermissionMode { get; set; }
    /// <summary>CLAUDE_CODE_ENTRYPOINT as the hook saw it: claude-vscode for a session in the
    /// IDE, sdk-cli for a headless `claude --print` run. Persisted so a restart doesn't
    /// un-hide every automated session until its next hook event.</summary>
    public string? Entrypoint { get; set; }
    /// <summary>The session is a `claude -p` run (hook-proven from its command line, not from
    /// the inherited environment). Persisted for the same reason as Entrypoint: a restart must
    /// not un-hide a wave of automated runs that is still going.</summary>
    public bool PrintMode { get; set; }
    public string? EndReason { get; set; }
    public DateTime? LastEventAt { get; set; }
    public string? AutoTitle { get; set; }           // derived from the transcript (stage D)
    public string? TabTitle { get; set; }            // VSCode tab label (last ai-title entry)
}

/// <summary>Session status → border style. Lives in config so the mapping can change
/// without touching hooks or code (decision 11).</summary>
public class StatusStyle
{
    public string Color { get; set; } = "gray";
    public string? AltColor { get; set; }            // non-null = blinking
    public int BlinkIntervalMs { get; set; } = 500;
    public bool UntilAcknowledge { get; set; }       // blink stops (solid Color) after user click
}

/// <summary>A user-defined toolbar toggle (feature 2026-07-19). SessionDeck knows nothing
/// about what a toggle controls — it only owns the flag: the current state is written to
/// %APPDATA%\SessionDeck\toggles\&lt;id&gt; as "1"/"0" for any external process to read.
/// No toggles defined = no UI.</summary>
public class CustomToggleConfig
{
    /// <summary>Immutable identity and the flag file's name. Set once when the toggle is
    /// created and never editable afterwards — renaming a toggle must not move the flag
    /// path out from under the external processes reading it (redesign 2026-07-20).</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";           // display name; free to change any time
    public string Icon { get; set; } = "🔘";         // toolbar button content (emoji)
    public bool DefaultOn { get; set; } = true;      // initial state when no flag file exists yet
}

public class ZoneConfig
{
    public int Monitor { get; set; }          // 0-based
    public string Mode { get; set; } = "off";
    public string Size { get; set; } = "1/3"; // custom-mode width: "2/7", "40%" or "0.4"
}

public class StageConfig
{
    public int Monitor { get; set; }          // 0-based
    public string Mode { get; set; } = "half-right";
    public string? Rect { get; set; }         // "x,y,w,h" in virtual-screen device px (mode=rect)
}

public class WindowBounds
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
}

public class AppConfig
{
    public int SchemaVersion { get; set; } = 3;   // 3: `done` moved green→purple, `he` took green
    public int NextTileId { get; set; } = 1;
    public List<TileConfig> Tiles { get; set; } = new();      // legacy, round-tripped only
    public int NextWorkspaceId { get; set; } = 1;
    public List<WorkspaceConfig> Workspaces { get; set; } = new();
    public Dictionary<string, StatusStyle> StatusStyles { get; set; } = new();
    public int ClosedSessionRetention { get; set; } = 20;     // per workspace (decision 12)
    public bool OpenSessionMaximized { get; set; } = true;    // stage D: collapse VSCode panels on session open
    public bool ShowHidden { get; set; }
    /// <summary>Show only sessions that are running or asking for something, and drop the
    /// workspace cards left with none (feature 2026-08-08). Expanding a card shows all of it
    /// regardless, and a search switches the filter off for its duration.</summary>
    public bool ActiveSessionsOnly { get; set; }
    /// <summary>Show the sessions nobody opened by hand: scheduled tasks and runners firing
    /// `claude --print`, which produce hooks indistinguishable from a real session and so
    /// earn a card of their own (Shay, 14-08-2026). Default false — an existing config with
    /// no such key deserializes to false, which is the wanted state, so no migration.</summary>
    public bool ShowHeadlessSessions { get; set; }
    /// <summary>Card order: "abc" (default — what the deck always did), "recent" or
    /// "frequency". Default stays A→Z so an upgrade never silently rearranges the deck.</summary>
    public string DeckSort { get; set; } = "abc";
    /// <summary>Multiplier on the card font sizes, driven by A+ / A− on the toolbar
    /// (feature 2026-08-07, widened to the whole deck 2026-08-08). 1.0 = the sizes the cards
    /// were designed at; the view model clamps what is loaded, so an old or hand-edited config
    /// cannot set an unusable size. The key keeps its original name so the size people already
    /// chose survives the upgrade.</summary>
    public double TaskFontScale { get; set; } = 1.0;
    public bool AlwaysOnTop { get; set; }                     // 📌 pin toggle (feature 2026-07-19)
    /// <summary>Master switch for the OS-level attention escalation (feature 2026-07-20):
    /// balloon + taskbar overlay badge + one-shot flash. Only ever fires when the deck is
    /// neither pinned nor zoned — see MainWindow.UpdateAttentionEscalation.</summary>
    public bool WindowsNotifications { get; set; } = true;
    /// <summary>The narrow column of task squares at the right edge of the deck. Off by
    /// default (Shay, 10-08-2026): since the tasks panel became one level at a time the strip
    /// shows whichever level the page was last left on, which is rarely the one you want, and
    /// the toolbar's toolbar button opens the page anyway. The ⚙ menu brings it back.</summary>
    public bool ShowTasksStrip { get; set; }
    public List<CustomToggleConfig> CustomToggles { get; set; } = new();
    /// <summary>
    /// How long each tool may sit without a result before the deck reads it as an open
    /// permission dialog (issue 2026-07-20). The VSCode extension fires no Notification
    /// hook, so this is the only way to catch a permission prompt there — but a pending
    /// dialog and a running tool look identical in the transcript, so the threshold is the
    /// only thing separating them and it has to be set per tool.
    ///
    /// Only tools listed here are ever considered; an empty map turns the heuristic off
    /// entirely. Questions (AskUserQuestion/ExitPlanMode) are unaffected either way —
    /// those are detected with certainty and never wait for a threshold.
    ///
    /// Defaults come from measuring 11k real tool calls in this user's transcripts — the
    /// share that legitimately runs longer than the threshold, i.e. the false-alarm rate:
    ///   Read/Edit/Write @15s  → 0.04% / 0.08% / 0.12%   (effectively never)
    ///   Bash/PowerShell @120s → 1.03% / 0.53%           (~1 in 100-200 calls)
    /// Agent is deliberately absent: 37% of subagent runs exceed 120s and 65% exceed 30s,
    /// so no threshold short enough to be useful is quiet enough to be trustworthy.
    /// A false alarm is self-correcting — the card reverts to blue when the tool finishes.
    /// </summary>
    public Dictionary<string, int> PermissionWaitToolSeconds { get; set; } = new()
    {
        ["Read"] = 15, ["Edit"] = 15, ["Write"] = 15, ["NotebookEdit"] = 15,
        ["Grep"] = 15, ["Glob"] = 15, ["TodoWrite"] = 15,
        ["Bash"] = 120, ["PowerShell"] = 120,
    };

    /// <summary>Debug-level logging (full sync snapshots). Persisted so a hunt for a
    /// sporadic bug survives an app restart; toggled via `sessiondeck log --debug`.</summary>
    public bool DebugLogging { get; set; }

    /// <summary>External tasks file (T-0116). null/empty = the tasks feature is fully off:
    /// no watcher, no read, no UI (strict opt-in).</summary>
    public string? TasksFilePath { get; set; }

    public ZoneConfig Zone { get; set; } = new();
    public StageConfig Stage { get; set; } = new();
    public WindowBounds? Window { get; set; }
    public bool AutoRemoveDisconnected { get; set; }          // legacy tile option, unused since v0.4

    /// <summary>Default status→style mapping (decision 11). Missing entries are
    /// filled in on load, so a hand-edited config only needs the overrides.</summary>
    public static Dictionary<string, StatusStyle> DefaultStatusStyles() => new()
    {
        ["idle"] = new StatusStyle { Color = "gray" },
        ["working"] = new StatusStyle { Color = "blue" },
        ["waiting"] = new StatusStyle { Color = "orange", AltColor = "black", UntilAcknowledge = true },
        ["done"] = new StatusStyle { Color = "purple", AltColor = "black", UntilAcknowledge = true },
        ["error"] = new StatusStyle { Color = "red", AltColor = "black", UntilAcknowledge = true },
        // The session ran its full end-of-session protocol ("Happy Ending"). It ends the
        // session for real, so it takes the green `done` used to carry, and `done` — which
        // now only means "the turn stopped" — moves to purple.
        ["he"] = new StatusStyle { Color = "green", AltColor = "black", UntilAcknowledge = true },
    };
}
