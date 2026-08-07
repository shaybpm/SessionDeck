using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SessionDeck.Models;
using SessionDeck.Services;

namespace SessionDeck.ViewModels;

/// <summary>
/// State of the tasks feature (T-0116): the parsed task list (pinned first, then file
/// order — the producer owns the order), the visible error/warning state, and the page
/// navigation flags. Fully opt-in: Enabled is false until a file path is configured, and
/// nothing tasks-related renders while it is.
/// </summary>
public sealed class TasksPanelViewModel : INotifyPropertyChanged
{
    // The file as loaded. The two bound collections below hold what SURVIVES the filter,
    // which is why the unfiltered lists have to be kept: a filter is re-applied against the
    // file, never against the previously filtered result.
    private readonly List<TaskItemViewModel> _allPinned = new();
    private readonly List<TaskItemViewModel> _allOther = new();

    /// <summary>Pinned tasks, in file order — always listed first with a separator.</summary>
    public ObservableCollection<TaskItemViewModel> PinnedTasks { get; } = new();
    /// <summary>The rest, strictly in file order.</summary>
    public ObservableCollection<TaskItemViewModel> OtherTasks { get; } = new();

    /// <summary>Every task in the file, filter or no filter — the workspace-card links are
    /// matched against the real list, not against what a search happens to leave on screen.</summary>
    public IEnumerable<TaskItemViewModel> AllTasks => _allPinned.Concat(_allOther);

    private bool _enabled;
    /// <summary>A tasks file path is configured — the strip (and everything else) exists.</summary>
    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled != value) { _enabled = value; Raise(); } }
    }

    private string _errorText = "";
    /// <summary>File-level error (missing / broken JSON / bad version) — shown INSTEAD of
    /// the list (T-0116 decision: visible errors beat a stale list).</summary>
    public string ErrorText
    {
        get => _errorText;
        set { if (_errorText != value) { _errorText = value; Raise(); Raise(nameof(HasError)); } }
    }

    public bool HasError => _errorText.Length > 0;

    private string _warningText = "";
    /// <summary>Record-level warnings (skipped entries) — shown ALONGSIDE the list.</summary>
    public string WarningText
    {
        get => _warningText;
        set { if (_warningText != value) { _warningText = value; Raise(); Raise(nameof(HasWarning)); } }
    }

    public bool HasWarning => _warningText.Length > 0;

    private bool _showSeparator;
    public bool ShowSeparator
    {
        get => _showSeparator;
        set { if (_showSeparator != value) { _showSeparator = value; Raise(); } }
    }

    private bool _pageOpen;
    /// <summary>The tasks page replaces the deck's central area while open.</summary>
    public bool PageOpen
    {
        get => _pageOpen;
        set { if (_pageOpen != value) { _pageOpen = value; Raise(); } }
    }

    /// <summary>newSessionPrompt template from the file's envelope (may be null).</summary>
    public string? NewSessionPrompt { get; private set; }

    private string _generatedText = "";
    public string GeneratedText
    {
        get => _generatedText;
        set { if (_generatedText != value) { _generatedText = value; Raise(); } }
    }

    private string _filter = "";
    /// <summary>The live search query while the tasks page is the visible panel. Pinned cards
    /// are exempt: they are the producer's navigation (back, the project header, the
    /// finished-tasks toggle), and filtering them away would strand the view with no way
    /// out.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            value = value?.Trim() ?? "";
            if (_filter == value) return;
            _filter = value;
            Raise();
            Rebuild();
        }
    }

    public const double MinFontScale = 0.8;
    public const double MaxFontScale = 2.0;

    private double _fontScale = 1.0;
    /// <summary>Multiplier on every font size of a card, driven by the toolbar's A+ / A− and
    /// persisted. It started life scoped to the task cards on 07-08-2026 and Shay widened it
    /// to the workspace and session cards the next day, so despite living here it now drives
    /// the whole deck. `WorkspaceCardView` binds this same property. It stays on this view
    /// model, and the config key stays `TaskFontScale`, so the size already chosen survives
    /// the change instead of silently resetting to 1.0.</summary>
    public double FontScale
    {
        get => _fontScale;
        set
        {
            value = Math.Clamp(value, MinFontScale, MaxFontScale);
            if (Math.Abs(_fontScale - value) < 0.001) return;
            _fontScale = value;
            Raise();
        }
    }

    private string _filterNote = "";
    /// <summary>"3 of 29" while filtering — without it an over-narrow query looks like a
    /// panel that lost its content.</summary>
    public string FilterNote
    {
        get => _filterNote;
        private set { if (_filterNote != value) { _filterNote = value; Raise(); Raise(nameof(HasFilterNote)); } }
    }

    public bool HasFilterNote => _filterNote.Length > 0;

    /// <summary>Apply a load result. A file-level error keeps nothing — the error state
    /// replaces the list entirely.</summary>
    public void Apply(TasksLoadResult result)
    {
        _allPinned.Clear();
        _allOther.Clear();
        if (result.Document is not { } doc)
        {
            Rebuild();
            ErrorText = result.FileError ?? "Unknown error";
            WarningText = "";
            GeneratedText = "";
            NewSessionPrompt = null;
            return;
        }
        ErrorText = "";
        NewSessionPrompt = string.IsNullOrWhiteSpace(doc.NewSessionPrompt) ? null : doc.NewSessionPrompt;
        GeneratedText = FormatGenerated(doc.Generated);
        foreach (var entry in doc.Tasks)
        {
            var item = TaskItemViewModel.From(entry, doc.StatusColors);
            (item.Pinned ? _allPinned : _allOther).Add(item);
        }
        Rebuild();
        WarningText = result.RecordWarnings.Count == 0 ? ""
            : $"{result.RecordWarnings.Count} records were not loaded: " + string.Join("; ", result.RecordWarnings);
    }

    /// <summary>Re-populate the bound collections from the file through the current filter.
    /// Called on every load and on every keystroke — the lists are short (one level of one
    /// project), so a rebuild is cheaper than per-item change notifications.</summary>
    private void Rebuild()
    {
        PinnedTasks.Clear();
        OtherTasks.Clear();
        foreach (var t in _allPinned) PinnedTasks.Add(t);
        foreach (var t in _allOther)
            if (t.Matches(_filter)) OtherTasks.Add(t);
        ShowSeparator = PinnedTasks.Count > 0 && OtherTasks.Count > 0;
        FilterNote = _filter.Length == 0 || _allOther.Count == 0
            ? "" : $"{OtherTasks.Count} of {_allOther.Count}";
    }

    public void Clear()
    {
        _allPinned.Clear();
        _allOther.Clear();
        Rebuild();
        ErrorText = "";
        WarningText = "";
        GeneratedText = "";
        NewSessionPrompt = null;
    }

    private static string FormatGenerated(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "";
        return DateTimeOffset.TryParse(iso, out var dt) ? $"updated {dt.LocalDateTime:HH:mm dd.MM}" : "";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
