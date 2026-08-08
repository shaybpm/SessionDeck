using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SessionDeck.Models;

namespace SessionDeck.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    public ObservableCollection<WorkspaceViewModel> Workspaces { get; } = new();

    /// <summary>User-defined toolbar toggles (config: customToggles); empty = no UI.</summary>
    public ObservableCollection<CustomToggleViewModel> CustomToggles { get; } = new();

    /// <summary>External tasks file state (T-0116); inert until a path is configured.</summary>
    public TasksPanelViewModel TasksPanel { get; } = new();

    /// <summary>Configured tasks-file path; null/empty = feature off (strict opt-in).</summary>
    public string? TasksFilePath { get; set; }

    public int NextWorkspaceId { get; set; } = 1;

    private bool _showHidden;
    public bool ShowHidden
    {
        get => _showHidden;
        set { if (_showHidden != value) { _showHidden = value; Raise(); } }
    }

    /// <summary>The width cards were designed at, and the narrowest they are ever drawn.
    /// It is also what the window's MinWidth is derived from (see MainWindow.xaml).</summary>
    public const double MinCardWidth = 430;

    private double _cardWidth = MinCardWidth;
    /// <summary>Width of one workspace card, recomputed from the deck viewport so a row fills
    /// it exactly (Shay, 08-08-2026: "the squares don't take up the space there is"). Runtime
    /// only: it follows the window and is never persisted.</summary>
    public double CardWidth
    {
        get => _cardWidth;
        set { if (Math.Abs(_cardWidth - value) > 0.5) { _cardWidth = value; Raise(); } }
    }

    private bool _activeOnly;
    /// <summary>Show only the workspace cards that are open right now: a bound VSCode window
    /// or at least one session that has not ended (`WorkspaceViewModel.IsActive`). It never
    /// hides a session inside a card that is shown (Shay, 08-08-2026).</summary>
    public bool ActiveOnly
    {
        get => _activeOnly;
        set { if (_activeOnly != value) { _activeOnly = value; Raise(); } }
    }

    public ZoneMode ZoneMode { get; set; } = ZoneMode.Off;
    public int ZoneMonitor { get; set; }
    /// <summary>Custom-mode width as the user typed it ("2/7", "40%", "0.4") — kept verbatim for display.</summary>
    public string ZoneSize { get; set; } = "1/3";
    public StageMode StageMode { get; set; } = StageMode.HalfRight;
    public int StageMonitor { get; set; }
    public Interop.RECT? StageRect { get; set; }       // used when StageMode == Rect
    public int ClosedSessionRetention { get; set; } = 20;
    public bool OpenSessionMaximized { get; set; } = true;   // stage D: collapse panels when opening a session

    /// <summary>Per-tool seconds before an unfinished call counts as an open permission
    /// dialog. Empty = heuristic off. See AppConfig.PermissionWaitToolSeconds.</summary>
    public Dictionary<string, int> PermissionWaitToolSeconds { get; set; } = new(StringComparer.Ordinal);
    public bool AlwaysOnTop { get; set; }                    // 📌 pin: deck window stays topmost
    public bool WindowsNotifications { get; set; } = true;   // ⚙ menu: OS-level attention escalation

    public WorkspaceViewModel? FindById(int id)
        => Workspaces.FirstOrDefault(w => w.Id == id);

    public WorkspaceViewModel? FindByHwnd(IntPtr hwnd)
        => hwnd == IntPtr.Zero ? null : Workspaces.FirstOrDefault(w => w.Hwnd == hwnd);

    public WorkspaceViewModel? FindByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string norm = Services.WorkspaceMetadata.NormalizePath(path);
        return Workspaces.FirstOrDefault(w =>
            w.Path.Length > 0 && Services.WorkspaceMetadata.NormalizePath(w.Path) == norm);
    }

    public (WorkspaceViewModel, SessionViewModel)? FindSession(string sessionId)
    {
        foreach (var w in Workspaces)
            if (w.FindSession(sessionId) is { } s)
                return (w, s);
        return null;
    }

    public IEnumerable<SessionViewModel> AllSessions()
        => Workspaces.SelectMany(w => w.Sessions);

    /// <summary>Status-bar summary dots (feature 2026-07-19): open sessions in visible
    /// workspaces, grouped by (status, blinking). Blinking (attention) groups first.</summary>
    public ObservableCollection<StatusDotViewModel> StatusSummary { get; } = new();

    // Attention-first display order; first item renders rightmost (RTL panel).
    // Also picks which status the taskbar overlay badge shows when several are blinking.
    public static int Severity(SessionStatus s) => s switch
    {
        SessionStatus.Error => 0,
        SessionStatus.Waiting => 1,
        SessionStatus.Done => 2,
        SessionStatus.He => 3,      // finished for real — the least urgent thing to look at
        SessionStatus.Working => 4,
        _ => 5,
    };

    public void RebuildStatusSummary()
    {
        var groups = Workspaces.Where(w => w.VisibleInDeck)
            .SelectMany(w => w.Sessions)
            .Where(s => !s.Closed && !s.Phantom)
            .GroupBy(s => (s.Status, s.BlinkActive))
            .Select(g => (g.Key.Status, Blinking: g.Key.BlinkActive, Count: g.Count()))
            .OrderBy(g => g.Blinking ? 0 : 1)
            .ThenBy(g => Severity(g.Status))
            .ToList();

        if (groups.SequenceEqual(StatusSummary.Select(d => (d.Status, d.Blinking, d.Count))))
            return;

        StatusSummary.Clear();
        foreach (var g in groups)
            StatusSummary.Add(new StatusDotViewModel { Status = g.Status, Blinking = g.Blinking, Count = g.Count });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
