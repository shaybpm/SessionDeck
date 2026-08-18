using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using SessionDeck.Models;
using SessionDeck.Services;

namespace SessionDeck.ViewModels;

public enum SessionStatus { Idle, Working, Waiting, Done, Error, He }

public static class SessionStatusNames
{
    public static string ToName(SessionStatus s) => s switch
    {
        SessionStatus.Idle => "idle",
        SessionStatus.Working => "working",
        SessionStatus.Waiting => "waiting",
        SessionStatus.Done => "done",
        SessionStatus.Error => "error",
        SessionStatus.He => "he",
        _ => "idle",
    };

    /// <summary>The word the card shows. Only Done differs from the wire name: it marks the end
    /// of a turn, not the end of the work, and "done" reads as the second — it was taken for
    /// "this task is finished" on a session that was mid-task and just waiting for a reply.
    /// The wire name is deliberately left alone, so hooks, the config and the CLI keep matching.</summary>
    public static string ToDisplay(SessionStatus s) => s switch
    {
        SessionStatus.Done => "your turn",
        _ => ToName(s),
    };

    public static bool TryParse(string s, out SessionStatus status)
    {
        status = s.ToLowerInvariant() switch
        {
            "idle" => SessionStatus.Idle,
            "working" => SessionStatus.Working,
            "waiting" => SessionStatus.Waiting,
            "done" => SessionStatus.Done,
            "error" => SessionStatus.Error,
            "he" => SessionStatus.He,
            _ => (SessionStatus)(-1),
        };
        return (int)status >= 0;
    }
}

/// <summary>
/// A Claude Code session card: status-colored border driven by the hooks,
/// blink until acknowledge for done/error/waiting. No thumbnail by design.
/// </summary>
public sealed class SessionViewModel : INotifyPropertyChanged, IBlinkable
{
    public string SessionId { get; init; } = "";

    private string? _customTitle;
    public string? CustomTitle
    {
        get => _customTitle;
        set { if (_customTitle != value) { _customTitle = value; Raise(); Raise(nameof(DisplayTitle)); Raise(nameof(SubText)); } }
    }

    private string? _tabTitle;
    /// <summary>The VSCode tab label (last "ai-title" transcript entry). Primary title and
    /// the session↔tab correlation key (issues 2026-07-19).</summary>
    public string? TabTitle
    {
        get => _tabTitle;
        set { if (_tabTitle != value) { _tabTitle = value; Raise(); Raise(nameof(DisplayTitle)); Raise(nameof(SubTitle)); Raise(nameof(SubText)); } }
    }

    private string? _autoTitle;
    /// <summary>Heuristic session title from the transcript (summary / first prompt).</summary>
    public string? AutoTitle
    {
        get => _autoTitle;
        set { if (_autoTitle != value) { _autoTitle = value; Raise(); Raise(nameof(DisplayTitle)); Raise(nameof(SubTitle)); Raise(nameof(SubText)); } }
    }

    /// <summary>Transcript mtime already scanned for titles (not persisted).</summary>
    public DateTime TranscriptScannedAt { get; set; }

    /// <summary>Strings VSCode could be showing as this session's tab label — titles plus
    /// recent prompts. Correlation matches the label against all of them, because which one
    /// Claude Code picked isn't knowable from here (issue 2026-07-20). Runtime only.</summary>
    public IReadOnlyList<string> LabelCandidates { get; set; } = Array.Empty<string>();

    /// <summary>The "waiting" status may only be cleared by the transcript scanner, when
    /// the answer shows up. Set for waits the scanner inferred itself (issue 2026-07-20)
    /// and for the PermissionRequest hook, which has no "dialog closed" counterpart to
    /// resolve it (T-0318). Waits from any other hook are cleared by their own hook.
    /// Runtime only.</summary>
    public bool WaitingFromTranscript { get; set; }

    /// <summary>Set while a PermissionRequest hook's dialog is unresolved, to the value
    /// TranscriptScannedAt had when the hook arrived. Non-null both blocks the scanner
    /// from clearing the wait off a PendingCall it hasn't read yet, and marks the call as
    /// blocking without ageing. The stored mark is what bounds the hold: the moment
    /// TranscriptScannedAt moves off it, a scan has read the file since the dialog opened
    /// and PendingCall can be trusted. Runtime only (T-0318).</summary>
    public DateTime? PermissionDialogScanMark { get; set; }

    /// <summary>StartedAtUtc of the pending call a PermissionRequest hook was matched to.
    /// The ageing thresholds are skipped for that call — the hook already proved it is a
    /// dialog — but only for it: without this the privilege leaked to whatever call came
    /// next and pinned the card orange for the rest of the turn. Runtime only.</summary>
    public DateTime? PermissionDialogCallAt { get; set; }

    /// <summary>Last unanswered tool call seen in the transcript, kept between scans so a
    /// permission dialog can be aged past the threshold. The transcript stops changing
    /// while a dialog is open, so re-reading the file would never notice — the clock has
    /// to run against the stored call instead. Runtime only.</summary>
    public PendingCall? PendingCall { get; set; }

    /// <summary>When the orphan sweep first saw this open session with no living host —
    /// workspace disconnected, or connected but no tab answers to its titles. The close
    /// fires only after the condition has held a full TTL, so one stale sync or a
    /// title-drift window can't kill a live session. Reset by any hook event
    /// (ApplyHookInfo) and whenever the condition clears. Runtime only.</summary>
    public DateTime? OrphanSince { get; set; }

    /// <summary>Discovered from the transcripts folder (expanded view) — not persisted.</summary>
    public bool Historical { get; init; }

    private bool _phantom;
    /// <summary>An idle session whose transcript file was never created — an empty
    /// conversation VSCode spins up on window load (SessionStart source=startup). Hidden
    /// until it shows real life; auto-closed after a while (issue 2026-07-19).</summary>
    public bool Phantom
    {
        get => _phantom;
        set { if (_phantom != value) { _phantom = value; Raise(); } }
    }

    private string? _matchedTabLabel;
    /// <summary>The candidate string that actually matched this session's VSCode tab label
    /// — i.e. what the tab is really showing, in full rather than truncated. Correlation
    /// already has to determine this, so using it as the title keeps the card and the tab
    /// in sync by construction instead of by re-deriving Claude Code's labelling rule
    /// (request 2026-07-20). Kept after the tab closes so the title doesn't jump; runtime
    /// only, re-derived on the next sync. Never fed back into matching — that would be
    /// circular.</summary>
    public string? MatchedTabLabel
    {
        get => _matchedTabLabel;
        set
        {
            if (_matchedTabLabel == value) return;
            _matchedTabLabel = value;
            Raise();
            Raise(nameof(DisplayTitle));
            Raise(nameof(SubTitle));
            Raise(nameof(SubText));
        }
    }

    public string DisplayTitle =>
        !string.IsNullOrEmpty(_customTitle) ? _customTitle
        : !string.IsNullOrEmpty(_matchedTabLabel) ? _matchedTabLabel
        : !string.IsNullOrEmpty(_tabTitle) ? _tabTitle
        : !string.IsNullOrEmpty(_autoTitle) ? _autoTitle
        : SessionId.Length > 8 ? "session " + SessionId[..8] : "session " + SessionId;

    /// <summary>Secondary title: what the session is about, shown whenever the primary
    /// title is something else (a tab label or an ai-title) so the card doesn't lose it.</summary>
    public string SubTitle =>
        string.IsNullOrEmpty(_customTitle) && !string.IsNullOrEmpty(_autoTitle) &&
        _autoTitle != DisplayTitle
            ? _autoTitle : "";

    private string _description = "";
    public string Description
    {
        get => _description;
        set { if (_description != value) { _description = value; Raise(); Raise(nameof(SubText)); } }
    }

    // ---- hook payload data (everything Claude Code provides) ----

    private string _detail = "";
    /// <summary>Last prompt (working) or notification message (waiting) from the hooks.</summary>
    public string Detail
    {
        get => _detail;
        set { if (_detail != value) { _detail = value; Raise(); Raise(nameof(SubText)); Raise(nameof(TooltipText)); } }
    }

    /// <summary>Card subtitle: a manual description wins; otherwise the live hook detail.
    /// Suppressed when it just repeats the title — e.g. a tab labelled by the same last
    /// prompt the hook reported as Detail (issue 2026-07-26).</summary>
    public string SubText
    {
        get
        {
            string text = _description.Length > 0 ? _description : _detail;
            return text == DisplayTitle ? "" : text;
        }
    }

    public string? TranscriptPath { get; set; }

    private string? _source;
    public string? Source
    {
        get => _source;
        set { if (_source != value) { _source = value; Raise(); } }
    }

    private string? _permissionMode;
    public string? PermissionMode
    {
        get => _permissionMode;
        set { if (_permissionMode != value) { _permissionMode = value; Raise(); } }
    }

    private string? _entrypoint;
    /// <summary>CLAUDE_CODE_ENTRYPOINT, forwarded by the hook. Not in the hook payload —
    /// the hook reads it off its own environment (see hooks/README.md).</summary>
    public string? Entrypoint
    {
        get => _entrypoint;
        set { if (_entrypoint != value) { _entrypoint = value; Raise(); Raise(nameof(IsHeadless)); } }
    }

    /// <summary>Nobody opened this session by hand: a scheduled task or a runner fired
    /// `claude --print`, and its hooks are indistinguishable from a real session's.
    ///
    /// Deliberately a blacklist of the SDK entrypoints (`sdk-cli`, `sdk-ts`, `sdk-py`) rather
    /// than a whitelist of the interactive ones. The two failure directions are not equal:
    /// an unknown entrypoint treated as interactive shows a card the user may not want, which
    /// is merely today's behaviour, while an unknown entrypoint treated as headless would
    /// silently swallow a session he is waiting on. Precision over coverage.
    ///
    /// The inherited-variable hole this used to call "harmless" was not: a `claude --print`
    /// launched from inside an IDE session reports claude-vscode, and a wave of them lands on the
    /// card as four sessions the user can click, resume and be blinked at, none of which he
    /// started (reported 18-08-2026). <see cref="PrintMode"/> closes it from the other side —
    /// the process's own command line — and is ORed in here.</summary>
    public bool IsHeadless =>
        _printMode || (_entrypoint != null && _entrypoint.StartsWith("sdk", StringComparison.OrdinalIgnoreCase));

    private bool _printMode;
    /// <summary>This session is a `claude -p` run: nobody opened it, and there is nothing to
    /// interact with. Proven by the hook from the claude process's own command line, not from
    /// the environment, which a spawned run inherits from whoever spawned it. Sticky and
    /// persisted: a process cannot stop being a print run, and a restart must not un-hide a
    /// wave that is still going.</summary>
    public bool PrintMode
    {
        get => _printMode;
        set { if (_printMode != value) { _printMode = value; Raise(); Raise(nameof(IsHeadless)); } }
    }

    private string? _endReason;
    public string? EndReason
    {
        get => _endReason;
        set { if (_endReason != value) { _endReason = value; Raise(nameof(TooltipText)); Raise(nameof(StatusText)); Raise(nameof(StatusDisplay)); } }
    }

    public DateTime? LastEventAt { get; set; }

    private bool _openAsTab;
    /// <summary>Best-effort: a Claude tab with a matching label is open in VSCode (stage D).
    /// Matched by title, so it may lag until the transcript title is scanned.</summary>
    public bool OpenAsTab
    {
        get => _openAsTab;
        set { if (_openAsTab != value) { _openAsTab = value; Raise(); } }
    }

    // Trimmed by request 2026-07-19: previously also showed SessionId, source,
    // permission mode, transcript path and the open-as-tab flag — restore here if needed.
    public string TooltipText
    {
        get
        {
            var lines = new List<string>();
            if (_detail.Length > 0) lines.Add(_detail);
            if (_backgroundAgents > 0) lines.Add(BackgroundAgentsTip);
            if (_lostAgents > 0) lines.Add(LostAgentsTip);
            // Date added 2026-08-11: a bare clock reads as "today" even when the event
            // was yesterday. Seconds dropped — the minute is the useful resolution here.
            lines.Add($"started: {StartedAt:HH:mm d'/'M}");
            if (LastEventAt is { } le) lines.Add($"last event: {le:HH:mm d'/'M}");
            if (EndedAt is { } ea) lines.Add($"ended: {ea:HH:mm d'/'M}" + (_endReason != null ? $" ({_endReason})" : ""));
            if (_endedTabOpen) lines.Add("its VSCode tab is still open — nothing is running behind it");
            return string.Join(Environment.NewLine, lines);
        }
    }

    private SessionStatus _status = SessionStatus.Idle;
    public SessionStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            _acknowledged = false;   // a new status restarts its blink cycle
            RaiseVisuals();
        }
    }

    private int _backgroundAgents;
    /// <summary>Subagents still running in the background. Two sources, in this order: the
    /// PostToolUse on each dispatched Agent counts UP as it is launched (v0.9.44 — the
    /// leading edge, so the chip appears with the agents rather than at the end of the turn),
    /// and the Stop hook's <c>background_tasks</c> snapshot then overwrites the tally with the
    /// truth. Hooks are the only signal there is: a background Agent call returns its id in
    /// milliseconds, so the transcript shows a finished tool call and the scanner sees nothing
    /// to wait for. While it is non-zero the card stays "working" instead of claiming the
    /// user's turn. Not persisted — after a deck restart the session's next Stop refills
    /// it.</summary>
    public int BackgroundAgents
    {
        get => _backgroundAgents;
        set
        {
            if (_backgroundAgents == value) return;
            _backgroundAgents = value;
            Raise();
            Raise(nameof(HasBackgroundAgents));
            Raise(nameof(BackgroundAgentsText));
            Raise(nameof(BackgroundAgentsTip));
            Raise(nameof(TooltipText));
        }
    }

    private string? _dispatchedBy;
    /// <summary>The session that launched this one as a headless `claude -p` run — a wave. The
    /// process tree cannot say it: a wave is fired through a hidden `wscript` launcher whose own
    /// parent has exited by the time anyone looks, so the launching session is nowhere in the
    /// child's ancestry (measured 18-08-2026). The launcher stamps it instead, and the hook
    /// forwards it. Persisted, so a deck restart mid-wave does not lose the link.</summary>
    public string? DispatchedBy
    {
        get => _dispatchedBy;
        set { if (_dispatchedBy != value) { _dispatchedBy = value; Raise(); } }
    }

    private int _dispatchedRuns;
    /// <summary>How many headless runs this session launched are still going. Counted from the
    /// live session records rather than tallied, so nothing drifts: a run that ends, is closed,
    /// or is swept as an orphan leaves the count by itself. Deliberately does NOT hold the turn
    /// the way <see cref="BackgroundAgents"/> does — a wave reports back through the agenda, not
    /// into the session, so the session's turn really has ended and the card must keep saying
    /// so.</summary>
    public int DispatchedRuns
    {
        get => _dispatchedRuns;
        set
        {
            if (_dispatchedRuns == value) return;
            _dispatchedRuns = value;
            Raise();
            Raise(nameof(HasBackgroundAgents));
            Raise(nameof(BackgroundAgentsText));
            Raise(nameof(BackgroundAgentsTip));
            Raise(nameof(TooltipText));
        }
    }

    /// <summary>Everything this session has out in the world: its own subagents plus the
    /// headless runs it launched. One chip for both, because from the deck's side they are the
    /// same question — is anything of mine still running.</summary>
    private int OutstandingWork => _backgroundAgents + _dispatchedRuns;

    public bool HasBackgroundAgents => OutstandingWork > 0;

    /// <summary>The chip on the card: the icon alone for one, icon + count for more.</summary>
    public string BackgroundAgentsText => OutstandingWork > 1 ? $"🤖{OutstandingWork}" : "🤖";

    public string BackgroundAgentsTip
    {
        get
        {
            var parts = new List<string>();
            if (_backgroundAgents == 1)
                parts.Add("1 subagent is still running — the session resumes on its own when it reports back");
            else if (_backgroundAgents > 1)
                parts.Add($"{_backgroundAgents} subagents are still running — the session resumes on its own when they report back");
            if (_dispatchedRuns == 1)
                parts.Add("1 headless run it launched is still going — it reports back through the agenda, not into the session");
            else if (_dispatchedRuns > 1)
                parts.Add($"{_dispatchedRuns} headless runs it launched are still going — they report back through the agenda, not into the session");
            return string.Join(Environment.NewLine, parts);
        }
    }

    private int _lostAgents;
    private string _lostAgentsDetail = "";

    /// <summary>Background agents that died with this session's previous process, read from
    /// the task-notification in its transcript (no hook reports it). Cleared when the session
    /// does something again — the mark is about the gap, not a permanent scar.</summary>
    public int LostAgents => _lostAgents;

    /// <summary>The notification's own timestamp, so the 10-second scan reports it once.
    /// Runtime only.</summary>
    public DateTime? LostAgentsAt { get; private set; }

    public void SetLostAgents(int count, string detail, DateTime atUtc)
    {
        _lostAgents = count;
        _lostAgentsDetail = detail;
        LostAgentsAt = atUtc;
        RaiseLostVisuals();
    }

    public void ClearLostAgents()
    {
        if (_lostAgents == 0 && LostAgentsAt == null) return;
        _lostAgents = 0;
        _lostAgentsDetail = "";
        LostAgentsAt = null;
        RaiseLostVisuals();
    }

    private void RaiseLostVisuals()
    {
        Raise(nameof(LostAgents));
        Raise(nameof(HasLostAgents));
        Raise(nameof(LostAgentsText));
        Raise(nameof(LostAgentsTip));
        Raise(nameof(TooltipText));
    }

    public bool HasLostAgents => _lostAgents > 0;

    public string LostAgentsText => _lostAgents > 1 ? $"⚠{_lostAgents}" : "⚠";

    public string LostAgentsTip
    {
        get
        {
            string head = _lostAgents == 1
                ? "1 background agent was still running when this session's process exited"
                : $"{_lostAgents} background agents were still running when this session's process exited";
            string names = _lostAgentsDetail.Length > 0 ? Environment.NewLine + _lostAgentsDetail : "";
            return head + names + Environment.NewLine +
                   "Their transcripts are on disk — nothing was lost, but nothing finished either.";
        }
    }

    private bool _acknowledged;
    public bool Acknowledged
    {
        get => _acknowledged;
        set { if (_acknowledged != value) { _acknowledged = value; RaiseVisuals(); } }
    }

    private bool _closed;
    public bool Closed
    {
        get => _closed;
        set { if (_closed != value) { _closed = value; RaiseVisuals(); } }
    }

    private bool _endedTabOpen;
    /// <summary>This session is closed, VSCode still shows a tab that answers to it, and no
    /// live session does. The card stays in the normal view saying exactly that — see
    /// MainWindow.RefreshEndedTabs for why silence was the worse answer. Runtime only,
    /// re-derived on every sync.</summary>
    public bool EndedTabOpen
    {
        get => _endedTabOpen;
        set
        {
            if (_endedTabOpen == value) return;
            _endedTabOpen = value;
            Raise();
            Raise(nameof(StatusDisplay));
            Raise(nameof(TooltipText));
        }
    }

    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }

    /// <summary>Set by the parent workspace: closed sessions are shown only when expanded.</summary>
    private bool _visible = true;
    public bool Visible
    {
        get => _visible;
        set { if (_visible != value) { _visible = value; Raise(); } }
    }

    /// <summary>The protocol name — what the CLI prints and what a script may match on.</summary>
    public string StatusText => Closed ? ClosedLabel : SessionStatusNames.ToName(_status);

    /// <summary>What the card shows. Same states, friendlier words; see ToDisplay.
    /// "closed (other)" is the protocol wording and answers the wrong question for a card
    /// left standing only because its tab is still open — there, say what the user needs to
    /// decide: nothing is running behind that tab.</summary>
    public string StatusDisplay =>
        Closed ? (EndedTabOpen ? "ended · tab open" : ClosedLabel)
               : SessionStatusNames.ToDisplay(_status);

    private string ClosedLabel => "closed" + (_endReason is { Length: > 0 } r ? $" ({r})" : "");

    /// <summary>Status→style mapping resolver, injected once at startup from config.</summary>
    public static Func<SessionStatus, StatusStyle> ResolveStyle { get; set; } =
        _ => new StatusStyle();

    // ---- IBlinkable ----

    public bool BlinkActive
    {
        get
        {
            if (_closed) return false;
            var style = ResolveStyle(_status);
            if (style.AltColor == null) return false;
            return !style.UntilAcknowledge || !_acknowledged;
        }
    }

    public int BlinkIntervalMs => ResolveStyle(_status).BlinkIntervalMs;

    private bool _altPhase;
    public bool AltPhase
    {
        get => _altPhase;
        set { if (_altPhase != value) { _altPhase = value; Raise(nameof(BorderBrush)); } }
    }

    public Brush BorderBrush
    {
        get
        {
            if (_closed) return MakeBrush("#555555");
            var style = ResolveStyle(_status);
            string color = BlinkActive && _altPhase ? style.AltColor ?? "black" : style.Color;
            return MakeBrush(color);
        }
    }

    private static readonly Dictionary<string, Brush> BrushCache = new(StringComparer.OrdinalIgnoreCase);

    internal static Brush MakeBrush(string name)
    {
        if (BrushCache.TryGetValue(name, out var cached)) return cached;
        var brush = new SolidColorBrush(ColorUtil.TryParse(name, out var c) ? c : Colors.Gray);
        brush.Freeze();
        BrushCache[name] = brush;
        return brush;
    }

    private void RaiseVisuals()
    {
        Raise(nameof(Status));
        Raise(nameof(StatusText));
        Raise(nameof(StatusDisplay));
        Raise(nameof(BorderBrush));
        Raise(nameof(Closed));
        Raise(nameof(TooltipText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
