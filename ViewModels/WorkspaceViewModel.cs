using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using SessionDeck.Services;

namespace SessionDeck.ViewModels;

public enum BindState { Connected, Disconnected }

/// <summary>
/// A workspace card: a persistent entity representing a VSCode workspace.
/// The OS window is only its live binding — the card survives window close/reopen.
/// </summary>
public sealed class WorkspaceViewModel : INotifyPropertyChanged
{
    public int Id { get; init; }

    private string _path = "";
    /// <summary>Folder path; empty for drag-in adds until a hook reports cwd (decision 21).</summary>
    public string Path
    {
        get => _path;
        set { if (_path != value) { _path = value; Raise(); Raise(nameof(TitleTooltip)); } }
    }

    private string _name = "";
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; Raise(); Raise(nameof(DisplayTitle)); Raise(nameof(TitleTooltip)); } }
    }

    private string? _customTitle;
    public string? CustomTitle
    {
        get => _customTitle;
        set { if (_customTitle != value) { _customTitle = value; Raise(); Raise(nameof(DisplayTitle)); Raise(nameof(TitleTooltip)); } }
    }

    public string DisplayTitle => !string.IsNullOrEmpty(_customTitle) ? _customTitle : _name;

    /// <summary>Card-header tooltip: the full title (the header trims long ones) + path.</summary>
    public string TitleTooltip => Path.Length > 0 ? DisplayTitle + Environment.NewLine + Path : DisplayTitle;

    private string _description = "";
    public string Description
    {
        get => _description;
        set { if (_description != value) { _description = value; Raise(); } }
    }

    // ---- card color (decision 18): manual override > Peacock > default ----

    private string? _customColor;
    public string? CustomColor
    {
        get => _customColor;
        set { if (_customColor != value) { _customColor = value; RaiseColor(); } }
    }

    private string? _peacockColor;
    public string? PeacockColor
    {
        get => _peacockColor;
        set { if (_peacockColor != value) { _peacockColor = value; RaiseColor(); } }
    }

    public string EffectiveColor => _customColor ?? _peacockColor ?? "#4A4A4A";

    public Brush CardBrush
    {
        get
        {
            var brush = new SolidColorBrush(ColorUtil.TryParse(EffectiveColor, out var c)
                ? c : Color.FromRgb(0x4A, 0x4A, 0x4A));
            brush.Freeze();
            return brush;
        }
    }

    private void RaiseColor()
    {
        Raise(nameof(EffectiveColor));
        Raise(nameof(CardBrush));
    }

    // ---- git branch (decision 17) ----

    private string _branch = "";
    public string Branch
    {
        get => _branch;
        set { if (_branch != value) { _branch = value; Raise(); Raise(nameof(HasBranch)); } }
    }

    public bool HasBranch => _branch.Length > 0;

    // ---- open Claude Code tabs, reported by the VSCode extension (stage D) ----

    private List<string> _claudeTabLabels = new();
    public IReadOnlyList<string> ClaudeTabLabels => _claudeTabLabels;
    public int ClaudeTabCount => _claudeTabLabels.Count;
    public bool HasClaudeTabs => _claudeTabLabels.Count > 0;
    public string ClaudeTabsTooltip => "Open Claude Code tabs:" + Environment.NewLine +
                                       string.Join(Environment.NewLine, _claudeTabLabels);

    public void SetClaudeTabs(List<string> labels)
    {
        if (_claudeTabLabels.SequenceEqual(labels)) return;
        _claudeTabLabels = labels;
        Raise(nameof(ClaudeTabLabels));
        Raise(nameof(ClaudeTabCount));
        Raise(nameof(HasClaudeTabs));
        Raise(nameof(ClaudeTabsTooltip));
    }

    /// <summary>How long a reported active tab stays believable without a refresh. The
    /// extension heartbeats every 2s while focused, so three missed beats expire it.</summary>
    public static TimeSpan ActiveTabTtl { get; set; } = TimeSpan.FromSeconds(6);

    private string? _activeClaudeTabLabel;
    private DateTime _activeClaudeTabAt;

    /// <summary>Label of the active Claude tab while the VSCode window is focused; null
    /// otherwise. Runtime only — drives auto-acknowledge (issue 2026-07-19).
    ///
    /// Expires after <see cref="ActiveTabTtl"/>. This value is written only when a sync
    /// arrives, and it is what SUPPRESSES a blink — so a sync that never arrives (pipe
    /// down, a second VSCode window on the same workspace winning the last write) used to
    /// leave the deck silencing a session forever on the belief that the user was still
    /// looking at its tab. Blinking at a tab the user is on is recoverable; staying silent
    /// on one they left is not, so absence of fresh evidence now means "not looking"
    /// (recurring blink issue, second root cause 2026-07-20).</summary>
    public string? ActiveClaudeTabLabel
    {
        get => _activeClaudeTabLabel != null && DateTime.Now - _activeClaudeTabAt <= ActiveTabTtl
            ? _activeClaudeTabLabel : null;
        set { _activeClaudeTabLabel = value; _activeClaudeTabAt = DateTime.Now; }
    }

    /// <summary>Session whose tab was last active — a CHANGE of active tab bumps the new
    /// session to the top (activity sort, extreme mode — request 2026-07-19).</summary>
    public string? LastActiveSessionId { get; set; }

    /// <summary>The workspace's Claude Code transcripts folder, learned from the first hook
    /// that reports transcript_path. Used to list historical sessions (expanded view).</summary>
    public string? TranscriptDir { get; set; }

    // ---- live window binding (engine reuse from stage A/B) ----

    private IntPtr _hwnd;
    public IntPtr Hwnd
    {
        get => _hwnd;
        set { if (_hwnd != value) { _hwnd = value; Raise(); } }
    }

    private string _windowTitle = "";
    public string WindowTitle
    {
        get => _windowTitle;
        set { if (_windowTitle != value) { _windowTitle = value; Raise(); } }
    }

    private string _processName = "";
    public string ProcessName
    {
        get => _processName;
        set { if (_processName != value) { _processName = value; Raise(); } }
    }

    /// <summary>Regex matching this workspace's VSCode window title.</summary>
    public string TitlePattern => WorkspaceMetadata.BuildTitlePattern(_name);

    private BindState _state = BindState.Disconnected;
    public BindState State
    {
        get => _state;
        set { if (_state != value) { _state = value; Raise(); Raise(nameof(IsActive)); } }
    }

    // ---- deck management (decision 16) ----

    private bool _hidden;
    public bool Hidden
    {
        get => _hidden;
        set { if (_hidden != value) { _hidden = value; Raise(); Raise(nameof(IsActive)); } }
    }

    /// <summary>Set by the controller from Hidden + the global show-hidden toggle.</summary>
    private bool _visibleInDeck = true;
    public bool VisibleInDeck
    {
        get => _visibleInDeck;
        set { if (_visibleInDeck != value) { _visibleInDeck = value; Raise(); } }
    }

    private bool _expanded;
    /// <summary>Expanded card shows closed sessions too (decision 12).</summary>
    public bool Expanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            RefreshSessionVisibility();
            Raise();
        }
    }

    public ObservableCollection<SessionViewModel> Sessions { get; } = new();

    // ---- linked tasks from the external tasks file (T-0116); runtime only ----

    /// <summary>Tasks whose workspace path matches this card, pinned first then file
    /// order. Rebuilt by the controller on every tasks-file reload.</summary>
    public ObservableCollection<TaskItemViewModel> WorkspaceTasks { get; } = new();

    private bool _tasksEnabled;
    /// <summary>The tasks feature is on (a file path is configured) — shows the card's
    /// task-count button even at 0 (disabled).</summary>
    public bool TasksEnabled
    {
        get => _tasksEnabled;
        set { if (_tasksEnabled != value) { _tasksEnabled = value; Raise(); } }
    }

    private bool _tasksExpanded;
    /// <summary>The card's inline task list is open.</summary>
    public bool TasksExpanded
    {
        get => _tasksExpanded;
        set { if (_tasksExpanded != value) { _tasksExpanded = value; Raise(); } }
    }

    /// <summary>Phantom sessions don't count — they must not float the workspace up.</summary>
    public bool HasOpenSessions => Sessions.Any(s => !s.Closed && !s.Phantom);

    /// <summary>Active = bound window or a live session; actives sort to the top.</summary>
    public bool IsActive => _state == BindState.Connected || HasOpenSessions;

    // ---- search filter (feature 2026-07-19), set by the controller; runtime only ----

    /// <summary>Active search predicate for sessions; null = no search.</summary>
    public Func<SessionViewModel, bool>? SearchPredicate { get; set; }

    /// <summary>The workspace's own fields match the active search (true when no search).</summary>
    public bool SelfMatchesSearch { get; set; } = true;

    public void RefreshSessionVisibility()
    {
        foreach (var s in Sessions)
        {
            bool normal = (!s.Closed || _expanded) && !s.Phantom;
            // A matching session is surfaced even if closed; a matching workspace keeps
            // its normal view; otherwise the session is filtered out.
            s.Visible = SearchPredicate == null ? normal
                : !s.Phantom && (SearchPredicate(s) || (SelfMatchesSearch && normal));
        }
        Raise(nameof(HasOpenSessions));
        Raise(nameof(IsActive));
    }

    public SessionViewModel? FindSession(string sessionId)
        => Sessions.FirstOrDefault(s => s.SessionId == sessionId);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
