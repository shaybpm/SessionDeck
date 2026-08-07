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

    private string? _endReason;
    public string? EndReason
    {
        get => _endReason;
        set { if (_endReason != value) { _endReason = value; Raise(nameof(TooltipText)); Raise(nameof(StatusText)); } }
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
            lines.Add($"started: {StartedAt:HH:mm:ss}");
            if (LastEventAt is { } le) lines.Add($"last event: {le:HH:mm:ss}");
            if (EndedAt is { } ea) lines.Add($"ended: {ea:HH:mm:ss}" + (_endReason != null ? $" ({_endReason})" : ""));
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

    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }

    /// <summary>Set by the parent workspace: closed sessions are shown only when expanded.</summary>
    private bool _visible = true;
    public bool Visible
    {
        get => _visible;
        set { if (_visible != value) { _visible = value; Raise(); } }
    }

    public string StatusText => Closed
        ? "closed" + (_endReason is { Length: > 0 } r ? $" ({r})" : "")
        : SessionStatusNames.ToName(_status);

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

    /// <summary>
    /// The session is running or asking for something: working, waiting or error. What the
    /// toolbar's "active only" filter keeps on screen (Shay, 08-08-2026). `done`, `idle` and
    /// `he` are deliberately out — they are sessions that finished and want nothing, which is
    /// exactly the noise the filter exists to remove.
    /// </summary>
    public bool IsLive => !_closed && !Phantom &&
        _status is SessionStatus.Working or SessionStatus.Waiting or SessionStatus.Error;

    private void RaiseVisuals()
    {
        Raise(nameof(Status));
        Raise(nameof(StatusText));
        Raise(nameof(BorderBrush));
        Raise(nameof(Closed));
        Raise(nameof(TooltipText));
        Raise(nameof(IsLive));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
