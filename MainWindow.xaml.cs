using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using SessionDeck.Cli;
using SessionDeck.Interop;
using SessionDeck.Models;
using SessionDeck.Services;
using SessionDeck.ViewModels;

namespace SessionDeck;

/// <summary>
/// Main controller: owns the view-model, services, workspace/session engine,
/// zone/stage orchestration and the operations shared by UI and CLI.
/// </summary>
public partial class MainWindow : Window
{
    public MainViewModel Vm { get; } = new();

    private readonly ConfigStore _configStore;
    private readonly WindowTracker _tracker = new();
    private readonly AppBarService _appBar = new();
    private readonly AttentionNotifier _notifier = new();
    private readonly BlinkEngine _blink;

    // Sessions that have already produced a balloon, so a steady "waiting" does not
    // re-notify on every unrelated refresh. Entries drop out when the session stops
    // needing attention, which re-arms it for next time.
    private readonly HashSet<string> _notifiedSessions = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _metadataTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private PipeServer? _pipe;
    private CommandExecutor? _executor;
    private List<MonitorEntry> _monitors;
    private bool _initializing = true;
    private bool _syncingUi;
    private bool _zoneSizePrompted;   // suppresses the DropDownClosed re-prompt after SelectionChanged already asked

    // Legacy pre-cards tile data — round-tripped so nothing is lost (decision 15).
    private List<TileConfig> _legacyTiles = new();
    private int _legacyNextTileId = 1;
    private bool _legacyAutoRemove;

    private Dictionary<string, StatusStyle> _statusStyles = AppConfig.DefaultStatusStyles();
    // Custom-toggle definitions are config-only (no UI editor) — round-tripped on save.
    private List<CustomToggleConfig> _customToggleConfigs = new();
    /// <summary>Which VSCode instance a new session is aimed at, by modifier. See
    /// <see cref="SessionGroupConfig"/>; empty = the routing the deck always had.</summary>
    private List<SessionGroupConfig> _sessionGroups = new();

    // Live VSCode-extension connections (stage D). UI thread only (handlers are dispatched).
    private readonly List<VscodeConnection> _connectors = new();
    // A session click with no connector yet (VSCode still launching) parks here until the
    // extension's first sync for that workspace, then the open command is flushed to it.
    // SessionId == null means "open a NEW session" (the + New Session button / a task);
    // Prompt rides along for new sessions opened from a task (T-0116).
    private readonly Dictionary<string, (string? SessionId, string? Prompt, string? Group, DateTime At)> _pendingOpens = new();
    private static readonly TimeSpan PendingOpenTtl = TimeSpan.FromSeconds(90);
    /// <summary>A session parked for a group the deck had to START waits this long.</summary>
    private static readonly TimeSpan GroupLaunchTtl = TimeSpan.FromMinutes(3);
    // ▶ clicked with no bound window: VSCode was launched, and the stage is applied
    // when the window binds (the launched window ignores clicks made before it existed).
    private readonly Dictionary<int, DateTime> _pendingPins = new();
    // Sync paths already reported as unroutable — dedup so the heartbeat can't flood the log.
    private readonly HashSet<string> _loggedUnroutedSyncs = new(StringComparer.OrdinalIgnoreCase);
    private bool _titleScanRunning;

    public int MonitorCount => _monitors.Count;

    public MainWindow()
    {
        var config = ConfigStore.Load();
        InitializeComponent();
        DataContext = Vm;
        _configStore = new ConfigStore(BuildConfig);
        _blink = new BlinkEngine(() => Vm.AllSessions().Cast<IBlinkable>().Concat(Vm.StatusSummary));
        _monitors = MonitorService.GetMonitors();

        LoadFromConfig(config);
        LogService.CleanOldLogs();
        LogService.Info("app", $"start v{GetType().Assembly.GetName().Version?.ToString(3)}" +
                               $" debug={(LogService.DebugEnabled ? "on" : "off")}");
        PopulateCombos();
        RefreshBlinkAndSummary();

        Vm.Workspaces.CollectionChanged += (_, _) =>
        {
            UpdateEmptyHint();
            RefreshBlinkAndSummary();
            RefreshWorkspaceTaskLinks();   // a new card may match tasks (T-0116)
            QueueSave();
        };
        PreviewKeyDown += Window_PreviewKeyDown;   // Esc closes the tasks page
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => UpdateEmptyHint();
        Closing += OnClosing;
        LocationChanged += (_, _) => { if (Vm.ZoneMode == ZoneMode.Off) QueueSave(); };
        SizeChanged += (_, _) => { if (Vm.ZoneMode == ZoneMode.Off) QueueSave(); };
        _metadataTimer.Tick += (_, _) => RefreshAllMetadata();
        _metadataTimer.Start();
        // Focus decides whether attention has to leave the window, so both edges re-evaluate.
        Activated += (_, _) => UpdateAttentionEscalation();
        Deactivated += (_, _) => UpdateAttentionEscalation();

        _initializing = false;

        // Sessions restored from config are already blinking; that is history, not news.
        foreach (var s in Vm.AllSessions())
            if (s.BlinkActive) _notifiedSessions.Add(s.SessionId);
    }

    // ---- startup / shutdown ----

    private void LoadFromConfig(AppConfig config)
    {
        _legacyTiles = config.Tiles;
        _legacyNextTileId = config.NextTileId;
        _legacyAutoRemove = config.AutoRemoveDisconnected;

        // Status→style mapping (decision 11): config overrides on top of defaults.
        // Schema 3 recoloured `done` (green→purple). Every config ever saved carries the
        // full map, including the old default, which would silently win over the new one —
        // so drop that one entry, and only when it is still byte-for-byte the old default,
        // leaving a colour the user actually chose alone.
        if (config.SchemaVersion < 3 &&
            config.StatusStyles.GetValueOrDefault("done") is { } oldDone &&
            oldDone.Color.Equals("green", StringComparison.OrdinalIgnoreCase) &&
            oldDone.AltColor is "black" && oldDone.BlinkIntervalMs == 500 && oldDone.UntilAcknowledge)
        {
            config.StatusStyles.Remove("done");
            LogService.Info("config", "schema<3: dropped the stale default `done` style so it recolours to purple");
        }
        _statusStyles = AppConfig.DefaultStatusStyles();
        foreach (var (key, style) in config.StatusStyles)
            _statusStyles[key.ToLowerInvariant()] = style;
        SessionViewModel.ResolveStyle = status =>
            _statusStyles.GetValueOrDefault(SessionStatusNames.ToName(status)) ?? new StatusStyle();

        Vm.NextWorkspaceId = Math.Max(1, config.NextWorkspaceId);
        Vm.ClosedSessionRetention = Math.Max(0, config.ClosedSessionRetention);
        Vm.OpenSessionMaximized = config.OpenSessionMaximized;
        Vm.PermissionWaitToolSeconds = new Dictionary<string, int>(config.PermissionWaitToolSeconds, StringComparer.Ordinal);
        Vm.ShowHidden = config.ShowHidden;
        Vm.ActiveOnly = config.ActiveSessionsOnly;
        Vm.ShowHeadless = config.ShowHeadlessSessions;
        if (ModeNames.TryParseDeckSort(config.DeckSort, out var deckSort)) Vm.Sort = deckSort;
        // A config written before this field existed deserializes to the property default,
        // but a hand-edited 0 would clamp to the minimum and look like a shrunk panel with
        // no cause. Anything non-positive means "not set".
        Vm.TasksPanel.FontScale = config.TaskFontScale > 0 ? config.TaskFontScale : 1.0;
        Vm.AlwaysOnTop = config.AlwaysOnTop;
        Vm.WindowsNotifications = config.WindowsNotifications;
        Vm.ShowTasksStrip = config.ShowTasksStrip;
        LogService.DebugEnabled = config.DebugLogging;
        Topmost = config.AlwaysOnTop;

        _customToggleConfigs = config.CustomToggles;
        // Schema 5 seeds the three management instances once. A config saved before the field
        // existed deserializes to an empty list, which is indistinguishable from "the user
        // emptied it" — so the schema number, not the emptiness, is what decides.
        _sessionGroups = config.SessionGroups;
        if (config.SchemaVersion < 5 && _sessionGroups.Count == 0)
        {
            _sessionGroups = AppConfig.DefaultSessionGroups();
            LogService.Info("config", "schema<5: seeded " + _sessionGroups.Count +
                " session groups (" + string.Join(", ", _sessionGroups.Select(g => g.Id)) + ")");
        }
        // Schema 6: a group seeded by schema 5 knows no launcher, and the deck's own
        // Code.exe command line brought the window up on the WRONG Claude account.
        if (config.SchemaVersion < 6 && AppConfig.FillMissingLaunchers(_sessionGroups) is > 0 and var filled)
            LogService.Info("config", $"schema<6: filled the launcher on {filled} session group(s)");
        LoadCustomToggles();

        int usageRebuilt = 0;
        foreach (var wc in config.Workspaces)
        {
            var ws = new WorkspaceViewModel
            {
                Id = wc.Id,
                Path = wc.Path,
                Name = wc.Name,
                CustomTitle = wc.CustomTitle,
                Description = wc.Description,
                CustomColor = wc.CustomColor,
                Hidden = wc.Hidden,
                TranscriptDir = wc.TranscriptDir,
                State = BindState.Disconnected,
            };
            foreach (var sc in wc.Sessions)
            {
                if (!SessionStatusNames.TryParse(sc.Status, out var status)) status = SessionStatus.Idle;
                var svm = new SessionViewModel
                {
                    SessionId = sc.SessionId,
                    CustomTitle = sc.CustomTitle,
                    Description = sc.Description,
                    Status = status,
                    Acknowledged = sc.Acknowledged,
                    Closed = sc.Closed,
                    StartedAt = sc.StartedAt,
                    EndedAt = sc.EndedAt,
                    Detail = sc.Detail,
                    TranscriptPath = sc.TranscriptPath,
                    Source = sc.Source,
                    PermissionMode = sc.PermissionMode,
                    Entrypoint = sc.Entrypoint,
                    PrintMode = sc.PrintMode,
                    // Already in the card's persisted UseCount; see TouchUsage.
                    CountedForUsage = true,
                    DispatchedBy = sc.DispatchedBy,
                    EndReason = sc.EndReason,
                    LastEventAt = sc.LastEventAt,
                    AutoTitle = sc.AutoTitle,
                    TabTitle = sc.TabTitle,
                    BackgroundAgents = sc.BackgroundAgents,
                    // Restored waiting must be re-provable from the transcript, or the
                    // first scan clears it (T-0313: a fork-phantom orange otherwise
                    // survives restarts — the persisted status said waiting and the
                    // runtime-only flag no longer allowed clearing it). A genuine block
                    // is always re-confirmed by its still-pending call, hook or not.
                    WaitingFromTranscript = status == SessionStatus.Waiting,
                };
                // Same restart rule for working: hooks were down with the app, so a
                // restored "working" whose transcript went quiet has no Stop coming —
                // it would stay blue forever (T-0313 follow-up). A genuinely running
                // session re-proves itself within one turn.
                // ...but a session with background agents out is the one case where a quiet
                // transcript proves nothing: the agents write their OWN files while the main
                // one waits, and they wake the session themselves when they finish. Dropping
                // it to idle here showed a card as idle while five agents were still running
                // (Shay, 21-08-2026). If they really died with the previous process, the
                // stopped-agent notification scan turns the card red instead.
                if (svm.Status == SessionStatus.Working && !svm.Closed && svm.BackgroundAgents == 0 &&
                    !TranscriptActiveWithin(svm, RecentTranscriptActivity))
                {
                    svm.Status = SessionStatus.Idle;
                    svm.Detail = "";
                }
                // Purge archived warmup sessions persisted before this fix — closed,
                // titleless, transcript never written (issue 2026-07-26).
                if (svm.Closed && NeverMaterialized(svm)) continue;
                ws.Sessions.Add(svm);
            }
            // Seeded from what is already on the card, so the first "last used" / "most used"
            // sort is meaningful on a config written before these two fields existed — and
            // REBUILT from it once, at schema 4, because everything written before then counted
            // the machine's own activity as use (see TouchUsage): folders a scheduled runner
            // passes through held the top of both orders, and 230 of 297 cards carried a stamp
            // no session of theirs could explain. Rebuilt rather than adjusted, because there
            // is no way to tell how much of a given number was real.
            //
            // What the rebuild costs, knowingly: the count is capped by what retention kept
            // (decision 12), so heavily-used cards land flat at first and separate again as
            // real sessions accumulate. That is the trade this comment used to argue against
            // — it is taken here only because the persisted alternative was measurably worse
            // than flat, and only once. The stamp loses nothing: for a card holding any real
            // session, its newest one IS the last time the card was used, and a card holding
            // none has no honest stamp to keep.
            var realSessions = ws.Sessions.Where(s => !s.IsHeadless).ToList();
            DateTime? lastRealEvent = realSessions
                .Select(s => (DateTime?)(s.LastEventAt ?? s.EndedAt ?? s.StartedAt))
                .Max();
            if (config.SchemaVersion < 4)
            {
                if (wc.LastUsedAt != lastRealEvent || wc.UseCount != realSessions.Count) usageRebuilt++;
                ws.UseCount = realSessions.Count;
                ws.LastUsedAt = lastRealEvent;
            }
            else
            {
                ws.UseCount = wc.UseCount > 0 ? wc.UseCount : realSessions.Count;
                ws.LastUsedAt = wc.LastUsedAt ?? lastRealEvent;
            }
            foreach (var s in ws.Sessions) RefreshPhantom(s);
            ws.RefreshSessionVisibility();
            SortSessions(ws);
            RefreshMetadata(ws);
            Vm.Workspaces.Add(ws);
        }
        if (usageRebuilt > 0)
            LogService.Info("config", $"schema<4: rebuilt the usage stamp on {usageRebuilt} of " +
                $"{config.Workspaces.Count} cards — headless and never-materialized sessions no " +
                "longer count as use");
        RehomeMisfiledSessions();
        ApplyDeckVisibility();
        SortWorkspaces();

        if (ModeNames.TryParseZone(config.Zone.Mode, out var zm)) Vm.ZoneMode = zm;
        Vm.ZoneMonitor = Math.Clamp(config.Zone.Monitor, 0, _monitors.Count - 1);
        if (ZoneSizeParser.TryParse(config.Zone.Size, out _)) Vm.ZoneSize = config.Zone.Size.Trim();
        if (ModeNames.TryParseStage(config.Stage.Mode, out var sm)) Vm.StageMode = sm;
        Vm.StageMonitor = Math.Clamp(config.Stage.Monitor, 0, _monitors.Count - 1);
        Vm.StageRect = ParseRect(config.Stage.Rect);
        if (Vm.StageMode == StageMode.Rect && Vm.StageRect == null) Vm.StageMode = StageMode.HalfRight;

        if (config.Window is { } wb && wb.W > 100 && wb.H > 100)
        {
            Left = wb.X; Top = wb.Y; Width = wb.W; Height = wb.H;
        }

        ApplyTasksFile(config.TasksFilePath);   // after workspaces, so task links resolve
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source) return;

        _appBar.Attach(source);
        _notifier.Attach(source);
        _notifier.Activated += () =>
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        };
        _tracker.TitleChanged += OnWindowTitleChanged;
        _tracker.WindowDestroyed += OnWindowDestroyed;
        _tracker.WindowAppeared += TryRebindWindow;
        _tracker.MoveSizeEnded += HandleDragIn;
        _tracker.Start();

        RebindAll();

        if (Vm.ZoneMode != ZoneMode.Off)
            ApplyZone(Vm.ZoneMonitor, Vm.ZoneMode, save: false);

        _executor = new CommandExecutor(this);
        _pipe = new PipeServer(
            argv => Dispatcher.Invoke(() => _executor.Execute(argv)),
            (sync, conn) => Dispatcher.BeginInvoke(() => OnVscodeSync(sync, conn)),
            conn => Dispatcher.BeginInvoke(() => OnVscodeClosed(conn)));
        _pipe.Start();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        LogService.Info("app", "quit");
        _configStore.SaveNow();
        _pipe?.Dispose();
        _tracker.Dispose();
        _appBar.Remove();
        _notifier.Dispose();
    }

    // ---- persistence ----

    private AppConfig BuildConfig()
    {
        var cfg = new AppConfig
        {
            NextTileId = _legacyNextTileId,
            Tiles = _legacyTiles,
            AutoRemoveDisconnected = _legacyAutoRemove,
            NextWorkspaceId = Vm.NextWorkspaceId,
            StatusStyles = _statusStyles,
            ClosedSessionRetention = Vm.ClosedSessionRetention,
            OpenSessionMaximized = Vm.OpenSessionMaximized,
            PermissionWaitToolSeconds = new Dictionary<string, int>(Vm.PermissionWaitToolSeconds),
            ShowHidden = Vm.ShowHidden,
            ActiveSessionsOnly = Vm.ActiveOnly,
            ShowHeadlessSessions = Vm.ShowHeadless,
            DeckSort = ModeNames.ToName(Vm.Sort),
            TaskFontScale = Vm.TasksPanel.FontScale,
            AlwaysOnTop = Vm.AlwaysOnTop,
            WindowsNotifications = Vm.WindowsNotifications,
            ShowTasksStrip = Vm.ShowTasksStrip,
            DebugLogging = LogService.DebugEnabled,
            TasksFilePath = Vm.TasksFilePath,
            CustomToggles = _customToggleConfigs,
            SessionGroups = _sessionGroups,
            Zone = new ZoneConfig { Monitor = Vm.ZoneMonitor, Mode = ModeNames.ToName(Vm.ZoneMode), Size = Vm.ZoneSize },
            Stage = new StageConfig
            {
                Monitor = Vm.StageMonitor,
                Mode = ModeNames.ToName(Vm.StageMode),
                Rect = Vm.StageRect is { } r ? $"{r.Left},{r.Top},{r.Width},{r.Height}" : null,
            },
        };
        foreach (var w in Vm.Workspaces)
        {
            var wc = new WorkspaceConfig
            {
                Id = w.Id,
                Path = w.Path,
                Name = w.Name,
                CustomTitle = w.CustomTitle,
                Description = w.Description,
                CustomColor = w.CustomColor,
                Hidden = w.Hidden,
                TranscriptDir = w.TranscriptDir,
                LastUsedAt = w.LastUsedAt,
                UseCount = w.UseCount,
            };
            // Historical sessions (discovered from the transcripts folder) are re-discovered
            // on expand — persisting them would bloat the config.
            foreach (var s in w.Sessions.Where(s => !s.Historical))
            {
                wc.Sessions.Add(new SessionConfig
                {
                    SessionId = s.SessionId,
                    CustomTitle = s.CustomTitle,
                    Description = s.Description,
                    Status = SessionStatusNames.ToName(s.Status),
                    Acknowledged = s.Acknowledged,
                    Closed = s.Closed,
                    StartedAt = s.StartedAt,
                    EndedAt = s.EndedAt,
                    Detail = s.Detail,
                    TranscriptPath = s.TranscriptPath,
                    Source = s.Source,
                    PermissionMode = s.PermissionMode,
                    Entrypoint = s.Entrypoint,
                    PrintMode = s.PrintMode,
                    DispatchedBy = s.DispatchedBy,
                    EndReason = s.EndReason,
                    LastEventAt = s.LastEventAt,
                    AutoTitle = s.AutoTitle,
                    TabTitle = s.TabTitle,
                    BackgroundAgents = s.BackgroundAgents,
                });
            }
            cfg.Workspaces.Add(wc);
        }
        if (Vm.ZoneMode == ZoneMode.Off && WindowState == WindowState.Normal)
            cfg.Window = new WindowBounds { X = Left, Y = Top, W = Width, H = Height };
        return cfg;
    }

    public void QueueSave()
    {
        if (!_initializing) _configStore.QueueSave();
    }

    // ---- workspaces: add / remove / metadata ----

    /// <summary>Primary add flow (decision 21.1): pick a project folder.</summary>
    public (WorkspaceViewModel?, string?) AddWorkspaceFromPath(string path)
    {
        if (!Directory.Exists(path))
            return (null, $"folder not found: {path}");
        if (Vm.FindByPath(path) is { } existing)
            return (null, $"workspace \"{existing.DisplayTitle}\" already on the deck (id {existing.Id})");

        var ws = new WorkspaceViewModel
        {
            Id = Vm.NextWorkspaceId++,
            Path = Path.GetFullPath(path),
            Name = WorkspaceMetadata.NameFromPath(path),
        };
        RefreshMetadata(ws);
        Vm.Workspaces.Add(ws);
        TryBindWorkspace(ws);
        ApplyDeckVisibility();
        SortWorkspaces();
        return (ws, null);
    }

    public void RemoveWorkspace(WorkspaceViewModel ws)
    {
        Vm.Workspaces.Remove(ws);
        UpdateEmptyHint();
        QueueSave();   // a removal that is not written is undone by the next restart
    }

    /// <summary>Cards nobody ever worked in: the residue of the hook's cwd route creating one
    /// per folder a runner happened to stand in (see ResolveOrCreateWorkspace). The gate there
    /// stops new ones; this clears what accumulated before it — 230 of 297 cards on Shay's
    /// deck, folders like `system32`, `chapters` and four separate `scratchpad`s.
    ///
    /// Every condition has to hold, and each one is there to protect a card that only LOOKS
    /// like residue: no usage stamp at all (after schema 4 that means no real session has ever
    /// run here), nothing but machine sessions on it, nothing the user typed or chose, no live
    /// window, and not hidden — hiding is a decision he made about that card, so a hidden card
    /// is left exactly where he put it. What this can still take is a card he added by picking
    /// a folder and then never opened a session in; that costs him picking the folder again,
    /// which is why the verb dry-runs by default and every removal is logged by name.</summary>
    public List<string> PruneGhostWorkspaces(bool apply)
    {
        var doomed = Vm.Workspaces.Where(w =>
            !w.Hidden &&
            w.LastUsedAt == null && w.UseCount == 0 &&
            !w.Sessions.Any(s => !IsMachineSession(s)) &&
            !w.HasOpenSessions &&
            w.State != BindState.Connected &&
            w.CustomTitle == null && w.CustomColor == null && w.Description.Length == 0).ToList();
        if (!apply) return doomed.Select(w => w.DisplayTitle).ToList();
        foreach (var w in doomed)
        {
            LogService.Info("config", $"pruned ghost card \"{w.DisplayTitle}\" path=\"{w.Path}\" " +
                                      $"sessions={w.Sessions.Count}");
            Vm.Workspaces.Remove(w);
        }
        UpdateEmptyHint();
        ApplyDeckVisibility();
        QueueSave();
        LogService.Info("config", $"pruned {doomed.Count} ghost card(s); {Vm.Workspaces.Count} left");
        return doomed.Select(w => w.DisplayTitle).ToList();
    }

    public void ToggleHideWorkspace(WorkspaceViewModel ws)
    {
        ws.Hidden = !ws.Hidden;
        ApplyDeckVisibility();
        SortWorkspaces();
        QueueSave();
        SetStatus(ws.Hidden ? $"\"{ws.DisplayTitle}\" hidden (👁 shows hidden ones)" : $"\"{ws.DisplayTitle}\" shown again");
    }

    public void EditWorkspace(WorkspaceViewModel ws)
    {
        var dialog = new EditCardDialog(ws) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            RefreshMetadata(ws);
            QueueSave();
        }
    }

    /// <summary>Branch + Peacock color straight from the folder (decisions 17-18).</summary>
    private static void RefreshMetadata(WorkspaceViewModel ws)
    {
        if (ws.Path.Length == 0) return;
        ws.Branch = WorkspaceMetadata.ReadBranch(ws.Path);
        ws.PeacockColor = WorkspaceMetadata.ReadPeacockColor(ws.Path);
    }

    private void RefreshAllMetadata()
    {
        foreach (var ws in Vm.Workspaces)
            RefreshMetadata(ws);
        RefreshTranscriptTitles();
        RefreshPhantomSessions();
        RefreshOrphanSessions();
        // A permission dialog freezes the transcript, so the scan above may find nothing
        // new — the threshold still has to be re-checked against the stored pending call.
        if (EvaluateAllPendingWaits())
        {
            RefreshBlinkAndSummary();
            QueueSave();
        }
    }

    // ---- phantom sessions (issue 2026-07-19) ----

    /// <summary>Auto-close a phantom that never came to life within this window.</summary>
    private static readonly TimeSpan PhantomSessionTtl = TimeSpan.FromMinutes(30);

    /// <summary>Phantom = open idle session whose declared transcript file was never
    /// written — an empty conversation VSCode starts eagerly on window load.</summary>
    private static void RefreshPhantom(SessionViewModel s)
    {
        bool phantom = false;
        if (!s.Closed && s.Status == SessionStatus.Idle && s.TranscriptPath is { Length: > 0 } path)
        {
            try { phantom = !File.Exists(path); } catch { }
        }
        s.Phantom = phantom;
    }

    /// <summary>A titleless session whose conversation was never written to disk, and that
    /// has since gone silent. Two shapes reach here and neither can be scanned, correlated
    /// or resumed: no transcript path at all — hand-driven CLI calls (hooks/README.md) and
    /// the cwd safety net — and a path that was declared but never written, which is what
    /// the Claude Code CLI leaves behind for the extra session ids it mints per launch
    /// (issue 2026-08-05, agent mode). Both are unreachable by every other sweep:
    /// RefreshPhantom only looks at idle sessions, so any hook that moves the status makes
    /// the card immortal; the orphan sweep can't correlate a titleless session while VSCode
    /// is open; and NeverMaterialized only runs on a SessionEnd that never comes.
    /// Result: a card — blinking orange in the reported case — that outlived VSCode,
    /// restarts and the 15-minute orphan TTL (issue 2026-08-04, session "s1").
    /// Silence — not status — is the guard: while events keep arriving it stays, so the CLI
    /// sequence in hooks/README.md still drives a visible card.</summary>
    private static bool Ghost(SessionViewModel s)
        => !s.Closed && NeverMaterialized(s)
           && DateTime.Now - (s.LastEventAt ?? s.StartedAt) > PhantomSessionTtl;

    private void RefreshPhantomSessions()
    {
        bool anyChanged = false;
        foreach (var ws in Vm.Workspaces)
        {
            bool changed = false;
            foreach (var s in ws.Sessions.Where(Ghost).ToList())
            {
                LogService.Info("status", $"session={s.SessionId} removed (no transcript, silent) ws=\"{ws.DisplayTitle}\"");
                ws.Sessions.Remove(s);
                changed = true;
            }
            foreach (var s in ws.Sessions.Where(s => s.Phantom).ToList())
            {
                RefreshPhantom(s);
                if (!s.Phantom)
                {
                    changed = true;   // came to life — show it
                    continue;
                }
                if (DateTime.Now - s.StartedAt > PhantomSessionTtl)
                {
                    // Removed outright, not stale-closed — a closed phantom used to leak
                    // into the session history as "session xxxx" (issue 2026-07-26).
                    if (NeverMaterialized(s))
                    {
                        ws.Sessions.Remove(s);
                    }
                    else
                    {
                        s.Closed = true;
                        s.EndedAt = DateTime.Now;
                        s.EndReason = "stale";
                        s.Phantom = false;
                    }
                    changed = true;
                }
            }
            if (changed)
            {
                ws.RefreshSessionVisibility();
                SortSessions(ws);
                anyChanged = true;
            }
        }
        if (anyChanged)
        {
            SortWorkspaces();
            RefreshBlinkAndSummary();
            QueueSave();
        }
    }

    // ---- orphan sessions (issue 2026-07-26: VSCode update killed the window, SessionEnd never fired) ----

    /// <summary>How long BOTH conditions must hold before an orphan is closed: the session
    /// has no living host, AND no hook event or transcript write has arrived. Generous on
    /// purpose (false alarms break trust); a wrong close is revived by the next status
    /// hook, see SetSessionStatus.</summary>
    private static readonly TimeSpan OrphanSessionTtl = TimeSpan.FromMinutes(15);

    /// <summary>How long a card whose LAST VSCode window closed waits before its sessions are
    /// declared over. Counted in SWEPT time, not wall time (see RefreshOrphanSessions), so a
    /// sleeping machine does not burn through it. Short on purpose: the extension reconnects
    /// every 5s, so this covers a pipe blip and a window reload many times over, and anything
    /// longer just leaves the deck showing sessions whose host has exited.</summary>
    private static readonly TimeSpan DeadWindowTtl = TimeSpan.FromMinutes(2);

    /// <summary>In a MANUAL reconcile, how recently a session must have spoken to be spared
    /// on the "window is up but no tab answers" shape. That shape rests on matching a tab
    /// LABEL, and the labels lag: a new session's tab already shows its ai-title while the
    /// deck still knows only the opening prompt, until the 10s transcript scan catches up.
    /// The automatic sweep rides out that lag on its 15-minute TTL; a button press has no TTL
    /// to ride, so it needs this instead. The dead-window shape deliberately gets no such
    /// guard — no label is being matched there, and recent noise is exactly what lies.</summary>
    private static readonly TimeSpan ManualReconcileGrace = TimeSpan.FromSeconds(60);

    /// <summary>Newest sign of life we can observe: hook events (LastEventAt) or the
    /// transcript file's own mtime — the transcript is authoritative when hooks are dead.</summary>
    private static DateTime LastActivity(SessionViewModel s)
    {
        var last = s.LastEventAt ?? s.StartedAt;
        if (s.TranscriptPath is { Length: > 0 } path)
            try { var m = File.GetLastWriteTime(path); if (m > last) last = m; } catch { }
        return last;
    }

    /// <summary>Has anything to match a VSCode tab label against — a session with no
    /// titles at all can never correlate, so "no tab matched" proves nothing for it.</summary>
    private static bool Correlatable(SessionViewModel s)
        => s.LabelCandidates.Count > 0 || !string.IsNullOrEmpty(s.CustomTitle)
           || !string.IsNullOrEmpty(s.TabTitle) || !string.IsNullOrEmpty(s.AutoTitle);

    /// <summary>Reconcile every card against what VSCode actually has open, right now, and
    /// close what no longer exists. The ↻ button and `sessiondeck reconcile` both land here.
    ///
    /// It exists because the automatic sweep is deliberately slow, and the situation that
    /// produces the most junk is one the user can see coming: running out of tokens and
    /// switching accounts restarts VSCode, every tab resumes under a new session id, and the
    /// old ones linger until each TTL matures. Waiting is right for a sweep nobody asked for;
    /// it is wrong when someone is looking at the deck and telling it that it is wrong.
    ///
    /// Re-reads the connectors and the tab correlation FIRST, so the sweep decides on this
    /// second's evidence rather than whatever the last sync left behind. Nothing here is
    /// destructive beyond the sweep's own close, and that close still revives on the next
    /// hook — so a wrong call costs a card until the session speaks again.</summary>
    public (string, bool) ReconcileNow()
    {
        foreach (var ws in Vm.Workspaces.ToList())
        {
            RefreshMetadata(ws);
            ApplyConnectorState(ws);
            ReapplyTabCorrelation(ws);
        }
        RefreshPhantomSessions();
        int closed = RefreshOrphanSessions(force: true);
        RefreshBlinkAndSummary();
        QueueSave();
        // The title scan is asynchronous and lands afterwards; it only ever ADDS a match, so
        // it cannot un-close anything this pass decided. Kicked off last so the next press
        // (or tick) judges on fresher labels.
        RefreshTranscriptTitles();
        int open = Vm.Workspaces.Sum(w => w.Sessions.Count(s => !s.Closed));
        LogService.Info("status", $"reconcile: closed {closed}, {open} session(s) still open");
        return (closed == 0
            ? $"Nothing to clean up — all {open} open session(s) match a live tab"
            : $"Cleaned up {closed} session(s) whose tab or window is gone — {open} still open", true);
    }

    /// <summary>Close sessions whose host died without a SessionEnd hook. Two shapes:
    /// (a) the workspace's VSCode window is gone — an update/crash kill skips the hooks
    /// entirely; (b) the window is up but no tab answers to the session's titles — a
    /// restored-then-closed tab was never a live session, so closing it fires nothing.
    /// Both are invisible to the phantom sweep (status isn't idle, the transcript exists).
    /// The close waits out OrphanSessionTtl on both the condition and total silence.
    ///
    /// <paramref name="force"/> is the ↻ button and `sessiondeck reconcile`: the user is
    /// looking at the deck saying it is wrong NOW, so the two shapes that carry evidence stop
    /// waiting. Returns how many sessions were closed, which is what the button reports.</summary>
    private int RefreshOrphanSessions(bool force = false)
    {
        int closed = 0;
        foreach (var ws in Vm.Workspaces.ToList())
        {
            bool connected = ConnectorCount(ws) > 0;
            // The tab list is the union over every window with this folder open (see
            // ApplyConnectorState), so absence from it means absent from all of them.
            // It used to be one window's list overwriting another's, which forced this
            // sweep off for any folder open twice — and a card that never sweeps keeps
            // dead sessions forever, which is what "Open only" then showed (21-08-2026).
            bool tabsAuthoritative = connected;
            // Shape (a) again, but with the card's own history: a window this card
            // DEMONSTRABLY had and that is now gone is stronger evidence than silence, because
            // the process hosting those sessions has exited. The silence guard actively worked
            // against the truth here — closing a VSCode window fires a SessionStart resume for a
            // tab it never ran (measured three seconds before the disconnect, 17:16 03-09-2026),
            // which un-closed a session AND restarted its clock, so the card kept a dead session
            // for a further quarter hour. A card that never had a window keeps the slow path:
            // there, no connector proves nothing at all (a terminal session, a headless run).
            bool windowDied = !connected && ws.WindowGoneAt != null;
            foreach (var s in ws.Sessions.Where(s => !s.Closed && !s.Phantom).ToList())
            {
                bool candidate = !connected || (tabsAuthoritative && Correlatable(s) && !s.OpenAsTab);
                if (!candidate) { s.OrphanSince = null; continue; }
                // A manual reconcile skips the wait on the two shapes that have evidence. The
                // third — a card that never had a VSCode window at all — has none, so its guard
                // stands even here: a terminal session or a headless run must not be swept away
                // by a button press. ManualReconcileGrace covers the label-lag on the first.
                bool skipWait = force && (windowDied ||
                                          (connected && DateTime.Now - LastActivity(s) >= ManualReconcileGrace));
                if (!skipWait)
                {
                    // OrphanSince is set by this sweep, so it only advances while the deck is
                    // RUNNING AND AWAKE — which is what makes the short TTL safe. Sleeping the
                    // machine drops every connector at once (measured twice, 18:45 and 19:16
                    // on 03-09-2026), and the extensions take ~5s after the wake to come back.
                    // Reading the wall clock alone would have closed every session on every
                    // card inside that window — a mass false close on nothing but a lid.
                    // Counting from a sweep costs one extra 10s tick and cannot be fooled by a
                    // clock that jumped.
                    s.OrphanSince ??= DateTime.Now;
                    if (DateTime.Now - s.OrphanSince < (windowDied ? DeadWindowTtl : OrphanSessionTtl)) continue;
                    // The silence guard is the part the dead-window shape drops: a resume fired
                    // on the way out makes recent noise a LIAR about whether anyone is home.
                    if (!windowDied && DateTime.Now - LastActivity(s) < OrphanSessionTtl) continue;
                }
                // Which of the two shapes fired, and against what — "ended (orphaned)" alone
                // can't tell a dead window from a tab label we failed to match (issue 2026-08-16).
                LogService.Info("status", $"session={s.SessionId} orphan close ws=\"{ws.DisplayTitle}\" " +
                    (connected ? $"no tab matched tabs=[{string.Join(" | ", ws.ClaudeTabLabels)}]"
                     : windowDied ? $"its VSCode window closed at {ws.WindowGoneAt:HH:mm:ss}"
                     : "no VSCode window"));
                EndSession(s.SessionId, new HookInfo(Reason: "orphaned"));
                closed++;
            }
        }
        return closed;
    }

    /// <summary>Background scan of session transcripts for titles (stage D): the tab title
    /// (ai-title) + the heuristic session title. Only files whose mtime changed are re-read.</summary>
    private void RefreshTranscriptTitles()
    {
        if (_titleScanRunning) return;
        var stale = new List<(WorkspaceViewModel Ws, SessionViewModel Session, string Path, DateTime Mtime)>();
        foreach (var ws in Vm.Workspaces)
        foreach (var s in ws.Sessions)
        {
            if (s.TranscriptPath is not { Length: > 0 } path) continue;
            try
            {
                DateTime mtime = File.GetLastWriteTimeUtc(path);
                if (mtime != s.TranscriptScannedAt) stale.Add((ws, s, path, mtime));
            }
            catch { }
        }
        if (stale.Count == 0) return;

        _titleScanRunning = true;
        Task.Run(() =>
        {
            var results = stale.Select(x => (x.Ws, x.Session, Info: TranscriptReader.ReadInfo(x.Path), x.Mtime)).ToList();
            Dispatcher.BeginInvoke(() =>
            {
                _titleScanRunning = false;
                bool changed = false;
                var retitled = new HashSet<WorkspaceViewModel>();
                foreach (var (ws, session, tInfo, mtime) in results)
                {
                    session.TranscriptScannedAt = mtime;
                    if (tInfo.TabTitle != null && session.TabTitle != tInfo.TabTitle)
                    {
                        LogService.Info("title", $"session={session.SessionId} tab=\"{tInfo.TabTitle}\"");
                        session.TabTitle = tInfo.TabTitle;
                        retitled.Add(ws);
                        changed = true;
                    }
                    if (tInfo.AutoTitle != null && session.AutoTitle != tInfo.AutoTitle)
                    {
                        session.AutoTitle = tInfo.AutoTitle;
                        changed = true;
                    }
                    if (tInfo.LabelCandidates is { } cands &&
                        !cands.SequenceEqual(session.LabelCandidates))
                    {
                        // A new prompt renames the tab, so this is a correlation input
                        // just like TabTitle — re-correlate below (issue 2026-07-20).
                        session.LabelCandidates = cands;
                        retitled.Add(ws);
                    }
                    session.PendingCall = tInfo.Pending;
                    if (ApplyLostAgents(session, tInfo.Lost)) changed = true;
                }
                // Evaluate right after a scan too, so a question goes orange at once
                // instead of waiting for the next tick.
                if (EvaluateAllPendingWaits()) changed = true;
                // A fresh TabTitle can complete a match that failed while the title was
                // stale — re-correlate so auto-acknowledge isn't lost to title drift
                // (recurring blink issue, root-caused 2026-07-20).
                bool ackChanged = false;
                foreach (var ws in retitled)
                    ackChanged |= ReapplyTabCorrelation(ws);
                if (ackChanged || changed) RefreshBlinkAndSummary();
                if (changed) QueueSave();
            });
        });
    }

    /// <summary>
    /// Drive the "waiting" status straight from the transcript. Neither Notification nor
    /// PostToolUse fires in the VSCode extension's native UI (both do in the terminal), so
    /// a question form or a permission dialog left the card stuck on blue "working" while
    /// Claude was actually blocked on the user (issue 2026-07-20).
    ///
    /// Two confidence levels, because the transcript can't tell a pending permission
    /// dialog from a tool that is simply still running — both are just a tool_use with no
    /// tool_result yet:
    ///   • AskUserQuestion / ExitPlanMode — definitive, applied immediately.
    ///   • Any tool in PermissionWaitToolSeconds — only once pending for that tool's own
    ///     threshold (see the AppConfig note for the measured false-alarm rates).
    /// Runs on every metadata tick, not only after a re-scan: a transcript stops changing
    /// while a dialog is open, so the clock must run against the stored call.
    ///
    /// Only transcript-inferred waiting is cleared here — a real Notification hook's
    /// waiting state is left for its own hook to resolve.
    /// </summary>
    private bool EvaluatePendingWait(WorkspaceViewModel ws, SessionViewModel session)
    {
        if (session.Closed) return false;
        var call = session.PendingCall;

        // A PermissionRequest hook reported an open dialog. PendingCall may not show it
        // yet — the scan is driven by the transcript's mtime, and the transcript stops
        // growing exactly while a dialog is open, so the read can lag a tick or more.
        // Clearing on that stale null is what made v0.8.0 blink back to blue while the
        // user was still blocked. Hold — but only until a scan has actually read the file
        // since the dialog opened, otherwise a fast Deny (answered before the scanner
        // caught up) would leave the card orange for the rest of the turn.
        if (session.PermissionDialogScanMark is { } mark)
        {
            if (call != null)
            {
                // Corroborated. Pin the privilege to this call only.
                session.PermissionDialogCallAt = call.StartedAtUtc;
                session.PermissionDialogScanMark = null;
            }
            else if (session.TranscriptScannedAt == mark) return false;   // no scan yet — hold
            else
            {
                // A scan read the file and there is no pending call: answered before the
                // scanner caught up, or a subagent's call, which is filtered out
                // (isSidechain) and never appears. Release via the normal clear path.
                session.PermissionDialogScanMark = null;
                session.WaitingFromTranscript = true;
            }
        }

        // A hook-confirmed dialog needs no ageing — that guesswork is what the hook
        // replaces — but the privilege belongs to that one call. Letting it ride on the
        // session pinned the card orange onto every later call (v0.8.1).
        bool hookConfirmed = call != null && session.PermissionDialogCallAt == call.StartedAtUtc;
        if (!hookConfirmed) session.PermissionDialogCallAt = null;

        // A session running with permissions bypassed can never open a permission dialog,
        // so ageing an unfinished tool call into one is a guaranteed false alarm there.
        // AskUserQuestion / ExitPlanMode still block in that mode, and a hook that
        // actually reported a dialog is still believed — only the guess is dropped.
        bool guessAllowed = !string.Equals(session.PermissionMode, "bypassPermissions",
                                           StringComparison.OrdinalIgnoreCase);

        bool blocked = call != null &&
                       (call.IsAsk || hookConfirmed || (guessAllowed && IsAgedPermissionDialog(call)));

        if (blocked)
        {
            if (session.Status == SessionStatus.Waiting && session.WaitingFromTranscript) return false;
            session.WaitingFromTranscript = true;
            session.Detail = call!.Detail;
            session.Status = SessionStatus.Waiting;
            session.LastEventAt = DateTime.Now;
            LogService.Info("status", $"session={session.SessionId} →waiting (transcript: {call.ToolName})");
            // Don't blink at a dialog the user is already looking at.
            if (ActiveTabSession(ws) == session)
            {
                if (!session.Acknowledged)
                    LogService.Info("ack", $"path=pending-wait session={session.SessionId} label=\"{ws.ActiveClaudeTabLabel}\"");
                session.Acknowledged = true;
            }
            return true;
        }
        if (!session.WaitingFromTranscript) return false;
        session.WaitingFromTranscript = false;
        // Answered — Claude is running again, and the Stop hook takes it from here to
        // done. Unless the transcript is quiet: then nothing is running (a restored
        // fork-phantom, T-0313) and "working" would stick forever — land on idle.
        if (session.Status == SessionStatus.Waiting)
        {
            bool active = TranscriptActiveWithin(session, RecentTranscriptActivity);
            session.Status = active ? SessionStatus.Working : SessionStatus.Idle;
            if (!active) session.Detail = "";
            session.LastEventAt = DateTime.Now;
            LogService.Info("status",
                $"session={session.SessionId} waiting→{(active ? "working" : "idle")} (transcript)");
            return true;
        }
        return false;
    }

    /// <summary>How stale a lost-agents notification may be and still raise the card. The
    /// deck rescans every transcript on startup, so without a bound a restart would light up
    /// every session that ever lost an agent. An hour keeps the case this was built for — the
    /// agents died minutes ago and the user is looking at the deck now.</summary>
    private static readonly TimeSpan LostAgentsFreshness = TimeSpan.FromHours(1);

    /// <summary>Background agents that died with the session's previous process. The only
    /// witness is the transcript (no hook carries it — measured 2026-08-14), so this runs off
    /// the scan. Reported once per notification: its own timestamp is the identity.</summary>
    private static bool ApplyLostAgents(SessionViewModel session, LostAgents? lost)
    {
        if (lost == null || session.Closed) return false;
        if (session.LostAgentsAt == lost.AtUtc) return false;
        if (DateTime.UtcNow - lost.AtUtc > LostAgentsFreshness) return false;
        session.SetLostAgents(lost.Count, lost.Detail, lost.AtUtc);
        LogService.Info("status",
            $"session={session.SessionId} lost {lost.Count} background agent(s) (transcript)");
        // `waiting` is a live block on the user and outranks a post-mortem; `he` is a
        // deliberate close-out that only real activity may clear. Everything else goes red:
        // work was started and never finished, and nothing else on the card can say so.
        if (session.Status is SessionStatus.Waiting or SessionStatus.He) return true;
        session.Status = SessionStatus.Error;
        session.LastEventAt = DateTime.Now;
        return true;
    }

    /// <summary>How fresh a transcript write must be to count as "Claude is doing
    /// something right now". Generous: turns write every few seconds.</summary>
    private static readonly TimeSpan RecentTranscriptActivity = TimeSpan.FromMinutes(2);

    /// <summary>A transcript write within the window is the only hook-independent signal
    /// of live activity — used before claiming a session is working (T-0313).</summary>
    private static bool TranscriptActiveWithin(SessionViewModel session, TimeSpan window)
    {
        try
        {
            return session.TranscriptPath is { Length: > 0 } path &&
                   DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < window;
        }
        catch { return false; }
    }

    private bool IsAgedPermissionDialog(PendingCall call)
        => !call.HasOlderPending
           && Vm.PermissionWaitToolSeconds.TryGetValue(call.ToolName, out int seconds)
           && seconds > 0
           && (DateTime.UtcNow - call.StartedAtUtc).TotalSeconds >= seconds;

    private bool EvaluateAllPendingWaits()
    {
        bool changed = false;
        foreach (var ws in Vm.Workspaces)
        foreach (var s in ws.Sessions)
            changed |= EvaluatePendingWait(ws, s);
        return changed;
    }

    // ---- historical sessions (expanded view; issue 2026-07-19) ----

    private const int HistoricalSessionLimit = 15;

    /// <summary>Expanded view lists past sessions straight from the workspace's Claude Code
    /// transcripts folder — including ones SessionDeck never witnessed. Not persisted.</summary>
    public void DiscoverHistoricalSessions(WorkspaceViewModel ws)
    {
        string? dir = ws.TranscriptDir ?? DefaultTranscriptDir(ws.Path);
        if (dir == null || !Directory.Exists(dir)) return;
        var known = new HashSet<string>(ws.Sessions.Select(s => s.SessionId));

        Task.Run(() =>
        {
            var found = new List<(string Id, string Path, DateTime Created, DateTime Modified, TranscriptInfo Info)>();
            try
            {
                var files = new DirectoryInfo(dir).GetFiles("*.jsonl")
                    .OrderByDescending(f => f.LastWriteTime)
                    .Take(HistoricalSessionLimit);
                foreach (var f in files)
                {
                    string id = Path.GetFileNameWithoutExtension(f.Name);
                    if (known.Contains(id)) continue;
                    found.Add((id, f.FullName, f.CreationTime, f.LastWriteTime, TranscriptReader.ReadInfo(f.FullName)));
                }
            }
            catch { }
            if (found.Count == 0) return;

            Dispatcher.BeginInvoke(() =>
            {
                foreach (var h in found)
                {
                    if (ws.FindSession(h.Id) != null) continue;
                    ws.Sessions.Add(new SessionViewModel
                    {
                        SessionId = h.Id,
                        Historical = true,
                        Closed = true,
                        StartedAt = h.Created,
                        EndedAt = h.Modified,
                        LastEventAt = h.Modified,
                        TranscriptPath = h.Path,
                        TabTitle = h.Info.TabTitle,
                        AutoTitle = h.Info.AutoTitle,
                        TranscriptScannedAt = File.GetLastWriteTimeUtc(h.Path),
                        Acknowledged = true,
                    });
                }
                ws.RefreshSessionVisibility();
                SortSessions(ws);
            });
        });
    }

    /// <summary>The same slug with no existence check, for matching a transcripts folder
    /// back to the workspace that produced it. Every comparison against it is
    /// case-insensitive — the drive letter's case varies across Claude Code versions, and
    /// two spellings of one folder read as two workspaces otherwise.</summary>
    private static string? TranscriptSlug(string wsPath)
    {
        if (wsPath.Length == 0) return null;
        try
        {
            string full = Path.GetFullPath(wsPath).TrimEnd('\\');
            return string.Concat(full.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));
        }
        catch { return null; }
    }

    /// <summary>The workspace a session's transcript proves it belongs to, or null.
    /// The transcripts folder is derived from the directory the session STARTED in and
    /// never moves; the cwd a hook reports moves with the session. On 09-08-2026 a
    /// coordinator session walked through four folders in one day and its card followed
    /// the last one, so it sat on a workspace that had no window and no session of its own.
    /// Slug match first (it decodes the folder back to the directory that made it), then a
    /// folder some workspace has already learned.</summary>
    private WorkspaceViewModel? WorkspaceForTranscript(string? transcriptPath)
    {
        if (string.IsNullOrEmpty(transcriptPath)) return null;
        string? dir = Path.GetDirectoryName(transcriptPath);
        if (string.IsNullOrEmpty(dir)) return null;
        string folder = Path.GetFileName(dir.TrimEnd('\\'));
        if (folder.Length == 0) return null;

        return SlugOwner(folder)
            ?? Vm.Workspaces.FirstOrDefault(w => w.TranscriptDir is { Length: > 0 } d &&
                   string.Equals(Path.GetFileName(d.TrimEnd('\\')), folder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The one workspace whose path slugs to this transcripts folder name.</summary>
    private WorkspaceViewModel? SlugOwner(string folder)
        => Vm.Workspaces.FirstOrDefault(w =>
               string.Equals(TranscriptSlug(w.Path), folder, StringComparison.OrdinalIgnoreCase));

    /// <summary>Keep a session on the card its transcript proves it belongs to — on every
    /// hook event, not only when the record is first created. Creation is the one moment the
    /// placement used to be decided, and for a session already filed under the old cwd rule
    /// (or auto-closed and then revived, which re-uses the existing record) that moment has
    /// passed: it stayed on the wrong card for the rest of its life. Shay reported the same
    /// coordinator session sitting on a folder it never ran in twice (09-08-2026).
    /// Returns the workspace the session now lives on.</summary>
    private WorkspaceViewModel EnsureSessionHome(WorkspaceViewModel ws, SessionViewModel session, string? transcript)
    {
        var home = WorkspaceForTranscript(transcript ?? session.TranscriptPath);
        if (home == null || home == ws) return ws;
        ws.Sessions.Remove(session);
        home.Sessions.Insert(0, session);
        foreach (var w in new[] { ws, home }) { w.RefreshSessionVisibility(); SortSessions(w); }
        LogService.Info("status", $"session={session.SessionId} re-homed from \"{ws.DisplayTitle}\" to \"{home.DisplayTitle}\"");
        QueueSave();
        return home;
    }

    /// <summary>Repair at load for cards filed under the old cwd-only rule: a workspace
    /// that borrowed another's transcripts folder drops it, and every session whose
    /// transcript names a different workspace moves there. Closed sessions are moved too
    /// (they were exempt until 09-08-2026, on the reasoning that history should not be
    /// rewritten): a closed card on a folder the session never ran in is not history, it is
    /// wrong data, and it is exactly what the user sees and reports.</summary>
    private void RehomeMisfiledSessions()
    {
        foreach (var ws in Vm.Workspaces)
        {
            if (ws.TranscriptDir is not { Length: > 0 } d) continue;
            var owner = SlugOwner(Path.GetFileName(d.TrimEnd('\\')));
            if (owner == null || owner == ws) continue;
            LogService.Info("workspace", $"\"{ws.DisplayTitle}\" dropped a transcripts folder owned by \"{owner.DisplayTitle}\"");
            ws.TranscriptDir = null;
        }

        var moves = new List<(WorkspaceViewModel From, WorkspaceViewModel To, SessionViewModel Session)>();
        foreach (var ws in Vm.Workspaces)
            foreach (var s in ws.Sessions)
            {
                var home = WorkspaceForTranscript(s.TranscriptPath);
                if (home != null && home != ws) moves.Add((ws, home, s));
            }
        if (moves.Count == 0) return;

        foreach (var (from, to, session) in moves)
        {
            from.Sessions.Remove(session);
            to.Sessions.Insert(0, session);
            LogService.Info("status", $"session={session.SessionId} re-homed from \"{from.DisplayTitle}\" to \"{to.DisplayTitle}\"");
        }
        foreach (var ws in moves.SelectMany(m => new[] { m.From, m.To }).Distinct())
        {
            ws.RefreshSessionVisibility();
            SortSessions(ws);
        }
        QueueSave();
    }

    /// <summary>Claude Code's project-folder slug: non-ASCII-alphanumeric chars → '-'.
    /// The drive letter's case varies across versions — try both.</summary>
    private static string? DefaultTranscriptDir(string wsPath)
    {
        if (wsPath.Length == 0) return null;
        try
        {
            string full = Path.GetFullPath(wsPath).TrimEnd('\\');
            string slug = string.Concat(full.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
            foreach (var variant in new[] { slug, char.ToLowerInvariant(slug[0]) + slug[1..], char.ToUpperInvariant(slug[0]) + slug[1..] })
            {
                string candidate = Path.Combine(root, variant);
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Actives (bound window / live session) float to the top (decision 16), and
    /// below them the order the user picked: A→Z (the original and the default), last used,
    /// or most used. Active-first is kept in every mode, and it CAN disagree with recency: a
    /// card whose window is open but which has not been touched in a week still outranks one
    /// used ten minutes ago whose window is closed. Deliberate — what is on screen now is
    /// what the deck is for — and the recency order continues immediately below it.
    /// Stable in-place sort via Move so DWM thumbnails survive.</summary>
    public void SortWorkspaces()
    {
        var byTitle = Vm.Workspaces.OrderByDescending(w => w.IsActive)
            .ThenBy(w => w.DisplayTitle, StringComparer.CurrentCultureIgnoreCase);
        var desired = (Vm.Sort switch
        {
            // Never used at all sorts last rather than first: DateTime.MinValue would put
            // every card the deck knows nothing about above the ones it does.
            DeckSort.Recent => Vm.Workspaces.OrderByDescending(w => w.IsActive)
                .ThenByDescending(w => w.LastUsedAt ?? DateTime.MinValue)
                .ThenBy(w => w.DisplayTitle, StringComparer.CurrentCultureIgnoreCase),
            DeckSort.Frequency => Vm.Workspaces.OrderByDescending(w => w.IsActive)
                .ThenByDescending(w => w.UseCount)
                .ThenByDescending(w => w.LastUsedAt ?? DateTime.MinValue)
                .ThenBy(w => w.DisplayTitle, StringComparer.CurrentCultureIgnoreCase),
            _ => byTitle,
        }).ToList();
        for (int target = 0; target < desired.Count; target++)
        {
            int current = Vm.Workspaces.IndexOf(desired[target]);
            if (current != target)
                Vm.Workspaces.Move(current, target);
        }
    }

    /// <summary>
    /// Fit whole cards across the deck viewport. Cards used to be a fixed 430px inside a
    /// WrapPanel, so anything left over after the last card that fit was dead space. On a
    /// 1078px-wide deck that was two cards and a 148px gap (Shay, 08-08-2026).
    ///
    /// Take as many columns as fit at the design width, then share the viewport out between
    /// them: the cards only ever grow, never shrink below what they were drawn for, and the
    /// column count still changes at exactly the same widths it did before.
    /// </summary>
    private void CardsHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        const double cardMargin = 16;   // WorkspaceCardView Margin="8" on each side
        double available = e.NewSize.Width;
        if (double.IsNaN(available) || available <= 0) return;

        int columns = Math.Max(1, (int)(available / (MainViewModel.MinCardWidth + cardMargin)));
        Vm.CardWidth = Math.Max(MainViewModel.MinCardWidth,
                                Math.Floor(available / columns) - cardMargin);
    }

    private void ApplyDeckVisibility()
    {
        bool searching = _searchQuery.Length > 0;
        // "Open only" stands down while searching: a query is an explicit request to find
        // something, and a filter that quietly hides the hit is worse than no filter.
        bool openOnly = Vm.ActiveOnly && !searching;
        foreach (var ws in Vm.Workspaces)
        {
            // Push the global headless setting down first: the workspace counts its own
            // sessions, and a card whose only sessions just became hidden has to stop
            // reporting itself as open in this same pass — IsActive is read three lines down.
            if (ws.ShowHeadless != Vm.ShowHeadless)
            {
                ws.ShowHeadless = Vm.ShowHeadless;
                ws.RefreshSessionVisibility();
            }
            ws.VisibleInDeck = (!ws.Hidden || Vm.ShowHidden)
                && (!searching || ws.SelfMatchesSearch || ws.Sessions.Any(SessionMatchesSearch))
                // The filter hides CARDS, never sessions. A card is kept when it is open right
                // now: a bound VSCode window or at least one session that has not ended, which
                // is what WorkspaceViewModel.IsActive already means. Filtering by session STATUS
                // was the first attempt and it was wrong - it kept only `working` and buried the
                // `done` sessions, which are the ones that finished answering and are waiting to
                // be read. Shay had 12 open sessions across 5 windows and saw 4 (08-08-2026).
                && (!openOnly || ws.Expanded || ws.IsActive);
        }
        UpdateEmptyHint();
        RefreshBlinkAndSummary();   // hidden workspaces don't count in the summary dots
    }

    // ---- search / filter (feature 2026-07-19) ----

    private string _searchQuery = "";
    private bool _searchInContent;
    // Session ids whose transcript file contains the query (content search results).
    private readonly HashSet<string> _contentMatches = new();
    private CancellationTokenSource? _contentSearchCts;
    private DispatcherTimer? _searchDebounce;

    /// <summary>Point the one search box at whatever is on screen, and say so on its label.
    /// Called whenever the tasks page opens or closes.</summary>
    private void UpdateSearchScope()
    {
        bool tasks = Vm.TasksPanel.PageOpen;
        SearchScopeLabel.Text = tasks ? "🔍 Tasks" : "🔍 Sessions";
        SearchContentCheck.Visibility = tasks ? Visibility.Collapsed : Visibility.Visible;
        // A query belongs to the list it was typed against. Carrying "1284" out of the task
        // list and onto the deck would silently hide every workspace that does not contain
        // it, which reads as a deck that lost its cards.
        SearchBox.Clear();
        ApplySearch();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (_initializing) return;
        _searchDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchDebounce.Stop();
        _searchDebounce.Tick -= SearchDebounce_Tick;
        _searchDebounce.Tick += SearchDebounce_Tick;
        _searchDebounce.Start();
    }

    private void SearchDebounce_Tick(object? sender, EventArgs e)
    {
        _searchDebounce!.Stop();
        ApplySearch();
    }

    private void SearchContent_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        ApplySearch();
    }

    private void ApplySearch()
    {
        // The query drives EXACTLY ONE target: the one on screen. Feeding it to both looks
        // reasonable and is not — a task query matches no session, so the tasks page's own
        // live-sessions panel emptied itself the moment anything was typed (caught in a
        // window snapshot, 07-08-2026).
        string query = SearchBox.Text.Trim();
        bool tasks = Vm.TasksPanel.PageOpen;
        Vm.TasksPanel.Filter = tasks ? query : "";
        _searchQuery = tasks ? "" : query;
        LogService.Debug("search", $"q=\"{query}\" scope={(tasks ? "tasks" : "deck")} " +
                                   $"shown={Vm.TasksPanel.OtherTasks.Count}");
        // Transcript scanning is session-only and expensive — never start it for a query
        // typed at the task list.
        _searchInContent = SearchContentCheck.IsChecked == true && !tasks;
        if (_searchQuery.Length > 0 && _searchInContent)
        {
            StartContentSearch();            // async; re-applies visibility when done
        }
        else
        {
            _contentSearchCts?.Cancel();
            _contentMatches.Clear();
        }
        ApplySearchVisibility();
    }

    private void ApplySearchVisibility()
    {
        bool searching = _searchQuery.Length > 0;
        foreach (var ws in Vm.Workspaces)
        {
            ws.SearchPredicate = searching ? SessionMatchesSearch : null;
            ws.SelfMatchesSearch = !searching || WorkspaceMatchesSearch(ws);
            ws.RefreshSessionVisibility();
        }
        ApplyDeckVisibility();
    }

    private bool SessionMatchesSearch(SessionViewModel s)
        => Matches(s.DisplayTitle) || Matches(s.AutoTitle) || Matches(s.SubText)
           || Matches(s.SessionId) || _contentMatches.Contains(s.SessionId);

    private bool WorkspaceMatchesSearch(WorkspaceViewModel ws)
        => Matches(ws.DisplayTitle) || Matches(ws.Name) || Matches(ws.Path)
           || Matches(ws.Branch) || Matches(ws.Description);

    private bool Matches(string? text)
        => text != null && text.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase);

    /// <summary>Content search: scans every known session's transcript file for the query
    /// on a background thread; cancelled and restarted on each query change.</summary>
    private void StartContentSearch()
    {
        _contentSearchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _contentSearchCts = cts;
        string query = _searchQuery;

        // Snapshot (id, transcript path) on the UI thread.
        var targets = new List<(string Id, string Path)>();
        foreach (var ws in Vm.Workspaces)
        {
            string? dir = ws.TranscriptDir ?? DefaultTranscriptDir(ws.Path);
            foreach (var s in ws.Sessions)
            {
                string? p = s.TranscriptPath;
                if ((p == null || !File.Exists(p)) && dir != null)
                    p = Path.Combine(dir, s.SessionId + ".jsonl");
                if (p != null) targets.Add((s.SessionId, p));
            }
        }

        Task.Run(() =>
        {
            var matches = new HashSet<string>();
            foreach (var (id, path) in targets)
            {
                if (cts.Token.IsCancellationRequested) return;
                if (TranscriptReader.ContainsText(path, query)) matches.Add(id);
            }
            Dispatcher.BeginInvoke(() =>
            {
                if (cts != _contentSearchCts || _searchQuery != query) return;
                _contentMatches.Clear();
                foreach (var m in matches) _contentMatches.Add(m);
                ApplySearchVisibility();
                SetStatus($"Content search: {matches.Count} sessions contain \"{query}\"");
            });
        }, cts.Token);
    }

    // ---- window binding (engine reuse; VSCode-only per decision 13) ----

    private void RebindAll()
    {
        var candidates = WindowEnumerator.GetCandidates()
            .Where(c => WorkspaceMetadata.IsVsCodeProcess(c.ProcessName)).ToList();
        var used = new HashSet<IntPtr>(Vm.Workspaces.Where(w => w.Hwnd != IntPtr.Zero).Select(w => w.Hwnd));

        // ToList: Bind() re-sorts the collection, which must not happen mid-enumeration.
        foreach (var ws in Vm.Workspaces.Where(w => w.State == BindState.Disconnected).ToList())
        {
            var match = candidates.FirstOrDefault(c =>
                !used.Contains(c.Hwnd) && SafeIsMatch(c.Title, ws.TitlePattern));
            if (match == null) continue;
            used.Add(match.Hwnd);
            Bind(ws, match.Hwnd, match.Title, match.ProcessName);
        }
    }

    private void TryBindWorkspace(WorkspaceViewModel ws)
    {
        if (ws.State == BindState.Connected) return;
        var bound = new HashSet<IntPtr>(Vm.Workspaces.Where(w => w.Hwnd != IntPtr.Zero).Select(w => w.Hwnd));
        var match = WindowEnumerator.GetCandidates().FirstOrDefault(c =>
            !bound.Contains(c.Hwnd) &&
            WorkspaceMetadata.IsVsCodeProcess(c.ProcessName) &&
            SafeIsMatch(c.Title, ws.TitlePattern));
        if (match != null)
            Bind(ws, match.Hwnd, match.Title, match.ProcessName);
    }

    private void Bind(WorkspaceViewModel ws, IntPtr hwnd, string title, string process)
    {
        ws.Hwnd = hwnd;
        ws.WindowTitle = title;
        ws.ProcessName = process;
        ws.State = BindState.Connected;
        // ▶ that had to launch VSCode first parked its stage request here.
        if (_pendingPins.Remove(ws.Id, out var pinnedAt) && DateTime.Now - pinnedAt < PendingOpenTtl)
            PinWorkspace(ws);
        SortWorkspaces();
        QueueSave();
    }

    private void OnWindowTitleChanged(IntPtr hwnd, string newTitle)
    {
        var ws = Vm.FindByHwnd(hwnd);
        if (ws == null)
        {
            // A title change can make an unbound VSCode window match a workspace.
            if (newTitle.Length > 0) TryRebindWindow(hwnd);
            return;
        }
        if (newTitle.Length == 0) return;
        ws.WindowTitle = newTitle;
        if (SafeIsMatch(newTitle, ws.TitlePattern)) return;
        // Only trust settled VSCode titles; a partial mid-reload title must not
        // release the bind. A wrong release self-heals on the next title event.
        if (!newTitle.Contains("Visual Studio Code")) return;

        // Open Folder in the same window: same HWND, new workspace — release the
        // bind and offer the window to the card whose pattern matches the new title.
        ws.Hwnd = IntPtr.Zero;
        ws.State = BindState.Disconnected;
        SortWorkspaces();
        QueueSave();
        TryRebindWindow(hwnd);
    }

    private void OnWindowDestroyed(IntPtr hwnd)
    {
        var ws = Vm.FindByHwnd(hwnd);
        if (ws == null) return;
        ws.Hwnd = IntPtr.Zero;
        ws.State = BindState.Disconnected;
        SortWorkspaces();
        QueueSave();
    }

    /// <summary>Automatic re-bind: a new/renamed VSCode window that matches an unbound
    /// workspace's title pattern connects to it.</summary>
    private void TryRebindWindow(IntPtr hwnd)
    {
        if (Vm.FindByHwnd(hwnd) != null) return;
        if (!Vm.Workspaces.Any(w => w.State == BindState.Disconnected)) return;
        if (NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) != hwnd) return;
        if (!WindowEnumerator.IsEligible(hwnd, Environment.ProcessId)) return;

        string process = WindowEnumerator.GetProcessName(hwnd);
        if (!WorkspaceMetadata.IsVsCodeProcess(process)) return;
        string title = NativeMethods.GetWindowTextSafe(hwnd);

        var ws = Vm.Workspaces.FirstOrDefault(w =>
            w.State == BindState.Disconnected && SafeIsMatch(title, w.TitlePattern));
        if (ws == null) return;
        Bind(ws, hwnd, title, process);
        SetStatus($"\"{ws.DisplayTitle}\" bound to window: {title}");
    }

    /// <summary>Drag-in (decision 21.3, secondary channel): only VSCode windows,
    /// blocked when the workspace is already on the deck.</summary>
    private void HandleDragIn(IntPtr hwnd)
    {
        if (!IsVisible || !NativeMethods.GetCursorPos(out POINT pt)) return;
        if (PresentationSource.FromVisual(this) is not HwndSource source) return;
        if (!NativeMethods.GetWindowRect(source.Handle, out RECT self)) return;
        if (pt.X < self.Left || pt.X >= self.Right || pt.Y < self.Top || pt.Y >= self.Bottom) return;

        IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (Vm.FindByHwnd(root) != null)
        {
            SetStatus("That window is already bound to a workspace on the deck");
            return;
        }
        if (!WindowEnumerator.IsEligible(root, Environment.ProcessId)) return;

        string process = WindowEnumerator.GetProcessName(root);
        if (!WorkspaceMetadata.IsVsCodeProcess(process))
        {
            SetStatus("Only VSCode windows are supported on the deck (decision 13)");
            return;
        }

        TryRebindWindow(root);
        if (Vm.FindByHwnd(root) != null) return;   // connected to an existing workspace

        string title = NativeMethods.GetWindowTextSafe(root);
        string name = WorkspaceNameFromTitle(title);
        if (name.Length == 0)
        {
            SetStatus("No workspace name could be read from the window title");
            return;
        }
        var ws = new WorkspaceViewModel { Id = Vm.NextWorkspaceId++, Name = name };
        Vm.Workspaces.Add(ws);
        Bind(ws, root, title, process);
        ApplyDeckVisibility();
        SetStatus($"Workspace \"{name}\" added (drag-and-drop; the path fills in from the first hook)");
    }

    /// <summary>"file - {workspace} - Visual Studio Code" → workspace segment.</summary>
    private static string WorkspaceNameFromTitle(string title)
    {
        var parts = title.Split(" - ");
        int vsIdx = Array.FindIndex(parts, p => p.StartsWith("Visual Studio Code"));
        if (vsIdx > 0) return parts[vsIdx - 1].Trim();
        return parts.Length >= 2 ? parts[^2].Trim() : "";
    }

    private static bool SafeIsMatch(string input, string pattern)
    {
        try { return Regex.IsMatch(input, pattern); }
        catch { return false; }
    }

    // ---- sessions engine (driven by the hooks only) ----

    /// <summary>Extra hook-payload data attached to any session command (all optional).</summary>
    public sealed record HookInfo(string? Detail = null, string? Transcript = null, string? Source = null,
                                  string? Mode = null, string? Reason = null, bool PermissionDialog = false,
                                  int? Agents = null, string? Entrypoint = null,
                                  bool PrintMode = false, string? Dispatcher = null)
    {
        public static readonly HookInfo Empty = new();
    }

    public (string, bool) StartSession(string sessionId, string workspaceArg, string? title, HookInfo info)
    {
        if (Vm.FindSession(sessionId) is { } found)
        {
            var (fw, fs) = found;
            fw = EnsureSessionHome(fw, fs, info.Transcript);
            fs.Closed = false;
            // A SessionStart on a session the deck already knows is NOT always a fresh start,
            // and treating it as one erased live state: clicking a card makes the deck send an
            // open command, VSCode answers it with SessionStart source=resume, and the card
            // dropped from "working, 1 agent out" to idle — so looking at a session destroyed
            // the very information the user clicked to read (reported 14-08-2026, proven by a
            // subagent transcript still being written minutes later).
            //   resume / compact: the same conversation continues. Keep the status and the
            //     agent count; the session never stopped.
            //   startup / clear / anything else: genuinely new or wiped. Reset.
            // `he` is the exception either way: a SessionStart clears it by documented rule
            // (hooks/README.md), because the session is demonstrably back in use.
            bool continues = info.Source is "resume" or "compact" && fs.Status != SessionStatus.He;
            if (!continues)
            {
                fs.Status = SessionStatus.Idle;
                // Whatever agents the previous incarnation had are not knowable from here. The
                // lost-agents mark is cleared too and re-earned from the transcript: the
                // notification about them is written a few seconds AFTER this hook.
                fs.BackgroundAgents = 0;
                fs.ClearLostAgents();
            }
            fs.StartedAt = DateTime.Now;
            fs.EndedAt = null;
            if (!string.IsNullOrEmpty(title)) fs.CustomTitle = title;
            ApplyHookInfo(fs, info);
            LearnTranscriptDir(fw, info);
            RefreshPhantom(fs);
            fw.RefreshSessionVisibility();
            AfterSessionChange(fw, fs);
            // Logged because it was not: this path rewrote a card's status silently, so the
            // damage above was invisible in the diagnostic log and had to be reconstructed
            // from the config file and two transcripts.
            LogService.Info("status", $"session={sessionId} restarted (source={info.Source ?? "?"}) " +
                                      $"{(continues ? "kept" : "reset to idle")}: {SessionStatusNames.ToName(fs.Status)}");
            return ($"session {sessionId} restarted in \"{fw.DisplayTitle}\"", true);
        }

        // Built before the workspace is resolved, because what the session IS decides whether
        // it is allowed to bring a new card into existence (see ResolveOrCreateWorkspace).
        var session = new SessionViewModel
        {
            SessionId = sessionId,
            CustomTitle = string.IsNullOrEmpty(title) ? null : title,
            Status = SessionStatus.Idle,
            StartedAt = DateTime.Now,
        };
        ApplyHookInfo(session, info);

        // Transcript first, cwd second: the cwd in a hook payload is wherever the session
        // happens to be standing right now, which is not always where it lives.
        var ws = WorkspaceForTranscript(info.Transcript);
        if (ws == null)
        {
            ws = ResolveOrCreateWorkspace(workspaceArg, out string? err, !IsMachineSession(session));
            if (ws == null) return (err!, false);
        }

        LearnTranscriptDir(ws, info);
        RefreshPhantom(session);
        ws.Sessions.Insert(0, session);
        ws.RefreshSessionVisibility();
        AfterSessionChange(ws, session);
        return ($"session {sessionId} started in \"{ws.DisplayTitle}\" [idle]", true);
    }

    private static void ApplyHookInfo(SessionViewModel session, HookInfo info)
    {
        session.LastEventAt = DateTime.Now;
        session.OrphanSince = null;   // any hook event is proof of life — restart the orphan clock
        if (info.Detail != null) session.Detail = Sanitize(info.Detail);
        if (info.Transcript != null) session.TranscriptPath = info.Transcript;
        if (info.Source != null) session.Source = info.Source;
        if (info.Mode != null) session.PermissionMode = info.Mode;
        if (info.Reason != null) session.EndReason = info.Reason;
        if (info.Agents is int agents) session.BackgroundAgents = agents;
        if (info.Entrypoint != null) session.Entrypoint = info.Entrypoint;
        // One-way: proven once at SessionStart, and no later event can argue with it.
        if (info.PrintMode) session.PrintMode = true;
        if (info.Dispatcher != null) session.DispatchedBy = info.Dispatcher;
    }

    /// <summary>The workspace's transcripts folder, learned from any hook event that
    /// carries transcript_path — used to list historical sessions (stage D).</summary>
    private void LearnTranscriptDir(WorkspaceViewModel ws, HookInfo info)
    {
        if (info.Transcript is not { Length: > 0 } t) return;
        string? dir = Path.GetDirectoryName(t);
        if (dir == null) return;
        // Never let one workspace claim a folder that slugs to another. A session whose cwd
        // wandered used to stamp its own transcripts folder onto whatever card it had landed
        // on, and that stale value then pulled later sessions to the wrong card too.
        var owner = SlugOwner(Path.GetFileName(dir.TrimEnd('\\')));
        if (owner != null && owner != ws) return;
        if (!string.Equals(ws.TranscriptDir, dir, StringComparison.OrdinalIgnoreCase))
        {
            ws.TranscriptDir = dir;
            QueueSave();
        }
    }

    /// <summary>Hook details (prompts, messages) become one bounded display line.</summary>
    private static string Sanitize(string s)
    {
        string oneLine = Regex.Replace(s, @"\s+", " ").Trim();
        return oneLine.Length <= 300 ? oneLine : oneLine[..299] + "…";
    }

    /// <summary>Workspace resolution for hooks (decision 21.4 — cwd is the safety net):
    /// by path → by name (adopting the path into a pathless workspace) → auto-create.</summary>
    /// <param name="mayCreate">False when the session asking is the machine's own (see
    /// IsMachineSession): an EXISTING card still takes it — a wave dispatched into a repo the
    /// user has a card for belongs on that card — but it may no longer bring a new one into
    /// existence. Route 4 of decision 21 (the hook's cwd) is the only card-creating route with
    /// no human behind it, and it was creating one per folder a runner happened to stand in:
    /// `bpm-port-check` from a session that lived 35 seconds, four different `scratchpad`s,
    /// `system32` from the task that runs there twice an hour. 230 of 297 cards on Shay's deck
    /// (01-09-2026) came from that, and every one of them was a card whose sessions the deck
    /// then deleted as worthless. A genuine session in a genuinely new folder is not affected
    /// for long: it stops being a ghost on its first real event, and the recreate path in
    /// SetSessionStatus creates the card then, a second later.</param>
    private WorkspaceViewModel? ResolveOrCreateWorkspace(string workspaceArg, out string? error, bool mayCreate = true)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(workspaceArg))
        {
            error = "session start requires --workspace <path or name>";
            return null;
        }

        bool isPath = workspaceArg.Contains('\\') || workspaceArg.Contains('/');
        if (isPath)
        {
            if (Vm.FindByPath(workspaceArg) is { } byPath) return byPath;
            string leaf = WorkspaceMetadata.NameFromPath(workspaceArg);
            var byName = Vm.Workspaces.FirstOrDefault(w =>
                w.Path.Length == 0 && string.Equals(w.Name, leaf, StringComparison.OrdinalIgnoreCase));
            if (byName != null)
            {
                // A drag-in workspace learns its path from the first hook that reports cwd.
                byName.Path = workspaceArg;
                RefreshMetadata(byName);
                QueueSave();
                return byName;
            }
            if (!mayCreate)
            {
                error = $"no card for \"{workspaceArg}\" and this session cannot create one";
                return null;
            }
            var (created, err) = AddWorkspaceFromPath(workspaceArg);
            if (created == null) error = err;
            else SetStatus($"Workspace \"{created.DisplayTitle}\" created from a hook (cwd)");
            return created;
        }

        var named = Vm.Workspaces.FirstOrDefault(w =>
            string.Equals(w.Name, workspaceArg, StringComparison.OrdinalIgnoreCase));
        if (named == null) error = $"no workspace named \"{workspaceArg}\" (pass the folder path to auto-create)";
        return named;
    }

    /// <summary>One background subagent was just dispatched (PostToolUse on an Agent call that
    /// answered `async_launched`). The Stop payload's snapshot is still the authority on how many
    /// are out — but it only arrives when the turn ENDS, so a session that dispatches a wave and
    /// then works on for ten minutes showed an empty card for all of them, which reads as the
    /// count being broken (reported 18-08-2026, measured: 86 seconds from the launch to the first
    /// Stop, and the chip cleared 4 minutes later once they were done). Counting up here is a
    /// tally, so it can overcount for the rest of the turn if an agent finishes early; the next
    /// Stop overwrites it with the truth, and an agent finishing wakes the session, which produces
    /// exactly such a Stop. Status is deliberately untouched: this fires mid-turn.</summary>
    public (string, bool) NoteAgentLaunched(string sessionId, HookInfo info)
    {
        if (Vm.FindSession(sessionId) is not { } found)
            return ($"unknown session id {sessionId}", false);
        var (ws, session) = found;
        session.BackgroundAgents++;
        ApplyHookInfo(session, info);
        LearnTranscriptDir(ws, info);
        LogService.Info("status", $"session={sessionId} background agent launched " +
                                  $"({session.BackgroundAgents} out) ws=\"{ws.DisplayTitle}\"");
        return ($"session {sessionId}: {session.BackgroundAgents} background agents", true);
    }

    public (string, bool) SetSessionStatus(string sessionId, SessionStatus status, string workspaceArg, HookInfo info)
    {
        // A turn that ended while background subagents are still running is not the user's
        // turn — they resume the session themselves when they report back, and a wave of
        // them blinks "your turn" once per return at a user with nothing to answer
        // (measured: five done↔working flips in two minutes off a single agent). The card
        // says what is true instead: the session is working, just not by itself. The hook
        // counts subagents only; a background shell never wakes anything. Ahead of the
        // recreate branch on purpose — a session the deck has forgotten gets the same read.
        bool agentsHeldTurn = status == SessionStatus.Done && info.Agents > 0;
        if (agentsHeldTurn)
        {
            status = SessionStatus.Working;
            LogService.Info("status", $"session={sessionId} done→working ({info.Agents} background agents)");
        }
        if (Vm.FindSession(sessionId) is not { } found)
        {
            // Self-healing (feedback 2026-07-19): the session may have been deleted with its
            // workspace. Every hook event carries cwd — recreate instead of dropping updates.
            // Same order as StartSession: the transcript decides, cwd is the fallback.
            var host = WorkspaceForTranscript(info.Transcript);
            // Built before the card is resolved, for the reason StartSession builds its own
            // there: what the session IS decides whether it may create a card.
            var recreated = new SessionViewModel
            {
                SessionId = sessionId,
                Status = status,
                StartedAt = DateTime.Now,
            };
            ApplyHookInfo(recreated, info);
            if (host == null)
            {
                if (workspaceArg.Length == 0)
                    return ($"unknown session id {sessionId} (was 'session start' called?)", false);
                host = ResolveOrCreateWorkspace(workspaceArg, out string? err, !IsMachineSession(recreated));
                if (host == null) return (err!, false);
            }
            LearnTranscriptDir(host, info);
            host.Sessions.Insert(0, recreated);
            AfterSessionChange(host, recreated);
            return ($"session {sessionId} recreated in \"{host.DisplayTitle}\" [{SessionStatusNames.ToName(status)}]", true);
        }
        var (ws, session) = found;
        // Before anything is logged or acknowledged: the card this session belongs to may
        // have been decided under the old cwd rule, and every hook carries the proof.
        ws = EnsureSessionHome(ws, session, info.Transcript);
        if (session.Closed)
        {
            // An auto-closed session (orphan/stale sweep) that emits a hook is demonstrably
            // alive — the sweep guessed wrong; revive it. User/hook closes stay final.
            if (session.EndReason is "orphaned" or "stale")
            {
                session.Closed = false;
                session.EndedAt = null;
                session.EndReason = null;
                LogService.Info("status", $"session={sessionId} revived (was auto-closed) ws=\"{ws.DisplayTitle}\"");
            }
            else
                return ($"session {sessionId} is closed — status not changed", false);
        }
        // The session is doing something again, so the post-mortem has served its purpose.
        // Not for the `working` this method synthesises out of a `done` — that one is a turn
        // ENDING, and the mark has to outlive it to still be there when the user looks.
        if (status == SessionStatus.Working && !agentsHeldTurn) session.ClearLostAgents();
        var prev = session.Status;
        // `he` is the one status a hook may not overwrite with a quieter one. It is set from
        // inside the last turn of the session, so the Stop hook that ends that very turn
        // arrives right behind it and would put the card back to `done` a second later.
        // Real activity (working / waiting / error) does clear it — the session came back
        // to life, and the card has to say so.
        // A `working` this method produced itself out of a `done` is still that same Stop
        // hook, and has to be held off `he` exactly like the `done` it came from — otherwise
        // a session closed out while one of its agents is still in flight loses its green
        // mark to the very Stop that follows the `he` command, which is the bug keepHe was
        // written for. Real activity from a hook (a prompt, a wait, an error) still clears it.
        bool keepHe = prev == SessionStatus.He &&
                      (status is SessionStatus.Done or SessionStatus.Idle || agentsHeldTurn);
        if (!keepHe) session.Status = status;
        if (keepHe)
            LogService.Info("status", $"session={sessionId} kept he (ignored →{SessionStatusNames.ToName(status)})");
        else if (prev != status)
            LogService.Info("status", $"session={sessionId} {SessionStatusNames.ToName(prev)}→{SessionStatusNames.ToName(status)} ws=\"{ws.DisplayTitle}\"");
        // PermissionRequest fires when the dialog opens, but Claude Code has no matching
        // "resolved" event — so the clearing is handed to the transcript scanner, which
        // sees the tool_result arrive. Not WaitingFromTranscript directly: that let the
        // very next tick clear the wait off a PendingCall the scanner had not read yet
        // (v0.8.0 blinked orange→blue→orange). EvaluatePendingWait promotes this.
        if (status == SessionStatus.Waiting && info.PermissionDialog)
            session.PermissionDialogScanMark = session.TranscriptScannedAt;
        ApplyHookInfo(session, info);
        LearnTranscriptDir(ws, info);
        RefreshPhantom(session);
        // The user is already looking at this session's tab — don't start blinking at them.
        if (ActiveTabSession(ws) == session)
        {
            if (!session.Acknowledged)
                LogService.Info("ack", $"path=status-hook session={sessionId} label=\"{ws.ActiveClaudeTabLabel}\"");
            session.Acknowledged = true;
        }
        else if (!session.Acknowledged && ws.ActiveClaudeTabLabel != null)
            // A tab IS focused but the labels disagree — likely title drift (Claude renamed
            // the tab mid-turn and our TabTitle is stale). Rescan the transcript now; the
            // scan callback re-runs the correlation and acknowledges (issue 2026-07-20).
            RefreshTranscriptTitles();
        AfterSessionChange(ws, session);
        return keepHe
            ? ($"session {sessionId} stays he (ignored {SessionStatusNames.ToName(status)})", true)
            : ($"session {sessionId} → {SessionStatusNames.ToName(status)}", true);
    }

    /// <summary>A session whose transcript file was never written and that has no
    /// titles — an empty conversation VSCode spins up eagerly (issue 2026-07-26), or one of
    /// the spare session ids the CLI mints per launch. Nothing to display or resume, so on
    /// close it is dropped rather than archived, and once silent the Ghost sweep drops it
    /// even without a close.</summary>
    private static bool NeverMaterialized(SessionViewModel s)
    {
        if (!string.IsNullOrEmpty(s.CustomTitle) || !string.IsNullOrEmpty(s.TabTitle) ||
            !string.IsNullOrEmpty(s.AutoTitle) || s.Description.Length > 0) return false;
        // No path at all is the same verdict, not an exemption — it used to return false
        // here, which archived titleless ghosts as "session <id>" forever (issue 2026-08-04).
        if (s.TranscriptPath is not { Length: > 0 } path) return true;
        try { return !File.Exists(path); } catch { return false; }
    }

    public (string, bool) EndSession(string sessionId, HookInfo info)
    {
        if (Vm.FindSession(sessionId) is not { } found)
            return ($"unknown session id {sessionId}", false);
        var (ws, session) = found;
        ws = EnsureSessionHome(ws, session, info.Transcript);
        ApplyHookInfo(session, info);
        LearnTranscriptDir(ws, info);
        if (NeverMaterialized(session))
        {
            LogService.Info("status", $"session={sessionId} ended (never materialized — removed)");
            ws.Sessions.Remove(session);
            AfterSessionChange(ws, session);
            return ($"session {sessionId} ended (empty — removed)", true);
        }
        LogService.Info("status", $"session={sessionId} ended{(info.Reason is { Length: > 0 } r ? $" ({r})" : "")}");
        session.Closed = true;
        session.EndedAt = DateTime.Now;

        // Retention (decision 12): keep only the last N closed sessions per workspace.
        var closed = ws.Sessions.Where(s => s.Closed).OrderByDescending(s => s.EndedAt ?? DateTime.MinValue).ToList();
        foreach (var extra in closed.Skip(Math.Max(0, Vm.ClosedSessionRetention)))
            ws.Sessions.Remove(extra);

        ws.RefreshSessionVisibility();
        AfterSessionChange(ws, session);
        return ($"session {sessionId} ended", true);
    }

    /// <summary>Sessions order like workspaces: open before closed, most recent activity
    /// first within each group. Stable in-place sort via Move.</summary>
    private static void SortSessions(WorkspaceViewModel ws)
    {
        var desired = ws.Sessions
            .OrderBy(s => s.Closed ? 1 : 0)
            .ThenByDescending(s => s.LastEventAt ?? s.EndedAt ?? s.StartedAt)
            .ToList();
        for (int target = 0; target < desired.Count; target++)
        {
            int current = ws.Sessions.IndexOf(desired[target]);
            if (current != target)
                ws.Sessions.Move(current, target);
        }
    }

    /// <summary>Usage, for the deck's "last used" and "most used" orders — bumped only by a
    /// session a PERSON is sitting in front of.
    ///
    /// Every session event used to count, which put the machine's own activity at the top of
    /// both orders: a scheduled runner opens a session in whatever folder it happens to stand
    /// in, twice an hour, so `system32` ranked 6th by last-used and 2nd by most-used with 1202
    /// "uses" — above every real project, and 230 of the 297 cards carried a usage stamp no
    /// session of theirs could explain (Shay, 01-09-2026: the order "does not really match what
    /// actually happened"). Two exclusions, and both of them are sessions the deck already
    /// refuses to DISPLAY, which is what made the ranking unreadable rather than merely wrong:
    ///   - a headless run (`claude -p` / SDK), hidden by ShowHeadlessSessions;
    ///   - a session with no transcript and no title — a spare id VSCode mints per launch, or
    ///     a run that died before writing anything. Re-checked on every event rather than once
    ///     at `session start`, where nothing is written yet and a real session cannot be told
    ///     from a ghost: a genuine one earns its bump on its first real event, a second later.
    /// Clicking a card in the deck still counts (FocusWorkspace) — that is the user using it,
    /// with no session involved at all.</summary>
    /// <summary>The machine's own activity rather than the user's: a headless run, or a
    /// session that has written no transcript and carries no title — a spare id VSCode mints
    /// per launch, or a run that died before writing anything. Both classes are ones the deck
    /// already refuses to DISPLAY, which is what makes them safe to ignore elsewhere: they can
    /// neither stamp a card as used (TouchUsage) nor bring one into existence
    /// (ResolveOrCreateWorkspace). Re-checked per event rather than decided once: at
    /// `session start` nothing is written yet and a real session cannot be told from a ghost,
    /// so a genuine one stops matching this a second later, on its first real event.</summary>
    private static bool IsMachineSession(SessionViewModel s) => s.IsHeadless || NeverMaterialized(s);

    private static void TouchUsage(WorkspaceViewModel ws, SessionViewModel? s)
    {
        if (s == null || IsMachineSession(s)) return;
        ws.LastUsedAt = DateTime.Now;
        if (s.CountedForUsage) return;
        s.CountedForUsage = true;
        ws.UseCount++;
    }

    private void AfterSessionChange(WorkspaceViewModel ws, SessionViewModel? trigger)
    {
        TouchUsage(ws, trigger);
        ws.RefreshSessionVisibility();
        SortSessions(ws);
        SortWorkspaces();
        // Starting or ending a session is exactly what ⚡ ("open only") filters on, so the
        // filter has to be re-applied here. It used to run only when a toggle was flipped, a
        // search changed, or a workspace was added or removed — so a card kept whatever
        // visibility it happened to have at that moment. Both halves were wrong and both were
        // visible: a card whose last session closed stayed on the deck showing nothing (Shay,
        // on pybpm-server, 10-08-2026), and a card already filtered out did not come back when
        // a session opened on it. ApplyDeckVisibility ends in RefreshBlinkAndSummary, so it
        // replaces the call that was here instead of adding a second pass.
        ApplyDeckVisibility();
        RefreshDispatchedRuns();
        QueueSave();
    }

    /// <summary>Recount, for every session, how many headless runs it launched are still working.
    /// A walk over all the records rather than a tally kept on the launcher: a run can end, be
    /// closed by hand, or be swept as an orphan, and only some of those paths would remember to
    /// decrement. The walk covers a few hundred records and runs on session events only.
    ///
    /// Counted on `working`, not merely on "not closed", and that is the difference between a
    /// number that empties itself and one that only grows. A `claude -p` run has exactly one
    /// turn: working while it does the job, done when the turn ends, and then it exits - so
    /// `done` already means finished, whether or not its SessionEnd ever arrived. Measured while
    /// building this: of two runs launched, one closed cleanly and one was left sitting at done
    /// with no SessionEnd at all, and a "not closed" count would have pinned the launcher's card
    /// at one forever. A run woken by its own background agent is turned back to `working` by the
    /// Stop hook, so a genuinely busy run is never missed.</summary>
    private void RefreshDispatchedRuns()
    {
        var live = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var w in Vm.Workspaces)
            foreach (var s in w.Sessions)
                if (!s.Closed && s.Status == SessionStatus.Working &&
                    s.DispatchedBy is { Length: > 0 } owner)
                    live[owner] = live.GetValueOrDefault(owner) + 1;
        foreach (var w in Vm.Workspaces)
            foreach (var s in w.Sessions)
                s.DispatchedRuns = live.GetValueOrDefault(s.SessionId);
    }

    /// <summary>Click on a session card = acknowledge + focus the window + open/resume the
    /// session's tab in VSCode via the connector (stage D).</summary>
    public void HandleSessionClick(WorkspaceViewModel ws, SessionViewModel session)
    {
        if (!session.Acknowledged)
        {
            LogService.Info("ack", $"path=click session={session.SessionId}");
            session.Acknowledged = true;
            RefreshBlinkAndSummary();
            QueueSave();
        }
        // Decide the target window BEFORE focusing: with the folder open twice, the card's
        // bind (a title match, which can't tell two windows on one folder apart) is not
        // necessarily the window the session lives in.
        var target = FindConnector(ws, session);
        RebindToConnectorWindow(ws, target);
        FocusWorkspace(ws);

        // Resume-by-id only works when the transcript still exists under the workspace's
        // current project slug; otherwise Claude Code silently opens a NEW conversation
        // (issue 2026-07-19 — e.g. sessions from before a folder rename). Don't send.
        if (!CanResume(ws, session))
        {
            SetStatus($"\"{session.DisplayTitle}\" — session file not found (did the project move or get renamed?); opening it would start a new conversation, so it was cancelled");
            return;
        }
        // Say something either way. This used to be `if (sent)`, so every failure - no
        // connector for that window yet, or a connector whose pipe had died - discarded the
        // reason it had just been handed and left the click looking like it had not
        // registered at all (Shay, 10-08-2026: "I can't click it and get to the 4.0
        // session"). The log line matters for the same reason: nothing in this path wrote
        // one, so afterwards there was no way to tell a click that failed from a click that
        // never happened.
        var (sent, reason) = OpenSessionInVscode(ws, session, target);
        LogService.Info("open", $"click session={session.SessionId} ws=\"{ws.DisplayTitle}\" " +
                                (sent ? "sent" : $"FAILED: {reason}"));
        SetStatus(sent
            ? $"Opening the session in VSCode: {session.DisplayTitle}"
            : $"\"{session.DisplayTitle}\" — {reason}");
    }

    /// <summary>Resume looks the id up in the workspace's CURRENT transcripts folder — a
    /// transcript that only exists under an old slug (pre-rename) can't be resumed.</summary>
    private static bool CanResume(WorkspaceViewModel ws, SessionViewModel session)
    {
        try
        {
            string? dir = ws.TranscriptDir ?? DefaultTranscriptDir(ws.Path);
            if (dir == null)
                return session.TranscriptPath == null || File.Exists(session.TranscriptPath);
            return File.Exists(Path.Combine(dir, session.SessionId + ".jsonl"));
        }
        catch
        {
            return true;   // can't verify — best effort
        }
    }

    // ---- VSCode extension connector (stage D) ----

    private void OnVscodeSync(VscodeSyncMessage sync, VscodeConnection conn)
    {
        bool isNew = !_connectors.Contains(conn);
        if (isNew) _connectors.Add(conn);
        conn.Pid = sync.Pid;
        conn.WorkspacePath = sync.Workspace ?? "";
        conn.Tabs = sync.Tabs;
        conn.Focused = sync.Focused;
        if (sync.Focused) conn.LastFocusedAt = DateTime.Now;
        CorrelateConnectorWindow(conn);
        if (isNew)
        {
            conn.OwnerPid = NativeMethods.GetParentProcessId(conn.Pid);
            LogService.Info("vscode", $"connected pid={conn.Pid} window-pid={conn.OwnerPid} ws=\"{conn.WorkspacePath}\"");
        }
        if (conn.WorkspacePath.Length == 0) return;

        if (Vm.FindByPath(conn.WorkspacePath) is { } ws)
        {
            // The extension is the fresher branch source (event-driven vs our 10s poll).
            if (!string.IsNullOrEmpty(sync.Branch)) ws.Branch = sync.Branch;
            var labels = ApplyConnectorState(ws);
            LogService.Debug("sync", $"ws=\"{ws.DisplayTitle}\" pid={sync.Pid} focused={sync.Focused}" +
                $" windows={ConnectorCount(ws)}" +
                $" active=\"{ws.ActiveClaudeTabLabel}\" tabs=[{string.Join(" | ", labels)}]");

            if (ReapplyTabCorrelation(ws))
            {
                RefreshBlinkAndSummary();
                QueueSave();
            }

            // Extreme activity sort (request 2026-07-19): switching to a session's tab in
            // VSCode counts as activity — the session jumps to the top of its card.
            var activeSession = ActiveTabSession(ws);
            if (activeSession != null && ws.LastActiveSessionId != activeSession.SessionId)
            {
                ws.LastActiveSessionId = activeSession.SessionId;
                activeSession.LastEventAt = DateTime.Now;
                SortSessions(ws);
                QueueSave();
            }
        }
        else if (_loggedUnroutedSyncs.Add(WorkspaceMetadata.NormalizePath(conn.WorkspacePath)))
        {
            // Otherwise a silent black hole (issue 3, 2026-07-22): every sync for this
            // window is dropped and its card never shows tabs. Once per path per run —
            // the 2s heartbeat would repeat it forever.
            LogService.Info("sync", $"no workspace card matches \"{conn.WorkspacePath}\" — tab state dropped");
        }

        // A click that had to launch VSCode first parked its open request here.
        string norm = WorkspaceMetadata.NormalizePath(conn.WorkspacePath);
        if (_pendingOpens.TryGetValue(norm, out var pending))
        {
            // A request parked for a GROUP waits for that group. Any window of the folder may
            // connect first - an extension host restarting in another instance does it several
            // times an hour - and handing it the session would land the account exactly where
            // the group was meant to prevent. It stays parked until its own group arrives or
            // the TTL drops it.
            bool mine = pending.Group == null ||
                        GroupIdOf(conn, WindowEnumerator.GetCandidates()
                                      .Where(w => WorkspaceMetadata.IsVsCodeProcess(w.ProcessName)).ToList(),
                                  Vm.FindByPath(conn.WorkspacePath) is { } pw
                                      ? GroupsFor(pw) : new List<SessionGroupConfig>()) == pending.Group;
            // A group whose instance had to be launched from cold needs longer than a
            // window that was already up: 90s is a reconnect budget, not a boot one.
            bool expired = DateTime.Now - pending.At >=
                           (pending.Group == null ? PendingOpenTtl : GroupLaunchTtl);
            if (mine || expired) _pendingOpens.Remove(norm);
            if (mine && !expired)
                conn.TrySend(pending.SessionId is { } sid
                    ? new { Cmd = "openSession", SessionId = (string?)sid, Prompt = (string?)null, Maximize = Vm.OpenSessionMaximized }
                    : new { Cmd = "newSession", SessionId = (string?)null, Prompt = pending.Prompt, Maximize = Vm.OpenSessionMaximized });
            else if (!mine && !expired)
                LogService.Debug("group", $"pending session for \"{pending.Group}\" held — " +
                                          $"pid={conn.Pid} window-pid={conn.OwnerPid} is not it");
        }
    }

    private void OnVscodeClosed(VscodeConnection conn)
    {
        LogService.Info("vscode", $"disconnected pid={conn.Pid} ws=\"{conn.WorkspacePath}\"");
        _connectors.Remove(conn);
        if (conn.WorkspacePath.Length == 0 || Vm.FindByPath(conn.WorkspacePath) is not { } ws) return;
        if (ConnectorCount(ws) > 0)
        {
            // Another window still has this folder open — recompute from the survivors
            // instead of clearing, or closing one of two windows would blank the card.
            ApplyConnectorState(ws);
            if (ReapplyTabCorrelation(ws)) RefreshBlinkAndSummary();
            return;
        }
        ws.SetClaudeTabs(new List<string>());
        ws.ActiveClaudeTabLabel = null;
        ws.WindowGoneAt = DateTime.Now;
        foreach (var s in ws.Sessions) s.OpenAsTab = false;
    }

    /// <summary>VSCode truncates long tab labels with a trailing '…' (bug 2026-07-19) —
    /// a truncated label matches any title it prefixes.</summary>
    private static bool TabLabelMatches(string label, string title)
    {
        if (label == title) return true;
        return label.EndsWith('…') && label.Length > 1 &&
               title.StartsWith(label[..^1], StringComparison.Ordinal);
    }

    /// <summary>Does this VSCode tab label belong to this session, and if so which string
    /// is the tab showing (in full — the label itself is truncated)? Checked against every
    /// candidate, not just the primary title: a session whose transcript has no ai-title is
    /// labelled from a user prompt instead, which the title fields alone never reproduce
    /// (issue 2026-07-20, second report).
    ///
    /// Deliberately matches concrete fields rather than DisplayTitle: DisplayTitle now
    /// prefers the matched label, so feeding it back in would be circular.</summary>
    private static string? MatchTabLabel(string label, SessionViewModel session)
    {
        foreach (var candidate in session.LabelCandidates)
            if (TabLabelMatches(label, candidate)) return candidate;
        // Same rule as the candidate list: AutoTitle is a prompt/summary, and prompts
        // label only titleless sessions (T-0313 follow-up — see TranscriptReader).
        var fallbacks = session.TabTitle is { Length: > 0 }
            ? new[] { session.CustomTitle, session.TabTitle }
            : new[] { session.CustomTitle, session.TabTitle, session.AutoTitle };
        foreach (var title in fallbacks)
            if (title is { Length: > 0 } t && TabLabelMatches(label, t)) return t;
        return null;
    }

    private static bool TabLabelMatches(string label, SessionViewModel session)
        => MatchTabLabel(label, session) != null;

    /// <summary>The one open session the user is demonstrably looking at, or null.
    ///
    /// Prompts are part of the candidate set, so two sessions can answer to the same label
    /// (same opening prompt, /clear, resume). Acknowledging the wrong one silently hides a
    /// real alert, so an ambiguous label resolves to nothing — leaving a card blinking is
    /// the recoverable failure. Every auto-acknowledge path goes through here: the guard
    /// used to live in ReapplyTabCorrelation alone while the two status-driven paths
    /// matched bare, so the same label could be safe on one path and silence a session on
    /// another (issue 2026-07-20).</summary>
    private static SessionViewModel? ActiveTabSession(WorkspaceViewModel ws)
    {
        if (ws.ActiveClaudeTabLabel is not { } active) return null;
        var matches = ws.Sessions.Where(s => !s.Closed && TabLabelMatches(active, s)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Recompute tab↔session correlation (OpenAsTab + auto-acknowledge) from the
    /// workspace's last-known VSCode state. The two match inputs refresh on independent
    /// clocks — tab labels arrive event-driven from the extension while TabTitle lags
    /// behind the 10s transcript scan — so this must re-run whenever EITHER side changes,
    /// not only when a sync arrives (recurring blink issue, root-caused 2026-07-20).</summary>
    private static bool ReapplyTabCorrelation(WorkspaceViewModel ws)
    {
        // One tab, one open session. Several sessions routinely answer to the same label —
        // an account switch, a /clear or a resume leaves a dead session still carrying the
        // title the live one shows — and marking them ALL "open as a tab" is what kept the
        // orphan sweep from ever closing any of them: every duplicate was propped up by the
        // one surviving tab, so a card accumulated them for days (17 on ".claude",
        // 03-09-2026). Capacity is the number of tabs carrying the label, so two genuinely
        // live tabs with the same title still keep two sessions; only the surplus is left
        // uncorrelated, oldest first, for the sweep to close after its silence TTL.
        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var label in ws.ClaudeTabLabels)
            remaining[label] = remaining.TryGetValue(label, out int n) ? n + 1 : 1;

        foreach (var s in ws.Sessions.Where(s => !s.Closed && !s.Phantom).OrderByDescending(LastActivity))
        {
            string? matched = null;
            foreach (var label in ws.ClaudeTabLabels)
                if (remaining[label] > 0 && MatchTabLabel(label, s) is { } m)
                { matched = m; remaining[label]--; break; }
            s.OpenAsTab = matched != null;
            // Adopt the tab's own text as the card title (request 2026-07-20). Kept when
            // the tab closes — a title that changes on tab close is worse than a stale one.
            if (matched != null) s.MatchedTabLabel = matched;
        }

        // A closed or phantom session claims nothing: both are outside the orphan sweep's
        // reach, so a tab spent on one would starve a session the sweep CAN close — turning
        // a leftover into a false close of something live.
        foreach (var s in ws.Sessions.Where(s => s.Closed || s.Phantom))
        {
            string? matched = null;
            foreach (var label in ws.ClaudeTabLabels)
                if (MatchTabLabel(label, s) is { } m) { matched = m; break; }
            s.OpenAsTab = matched != null;
            if (matched != null) s.MatchedTabLabel = matched;
        }

        if (RefreshEndedTabs(ws)) ws.RefreshSessionVisibility();

        // Auto-acknowledge the session whose tab the user is looking at.
        if (ActiveTabSession(ws) is not { } target || target.Acknowledged) return false;
        string? active = ws.ActiveClaudeTabLabel;
        LogService.Info("ack", $"path=correlation session={target.SessionId} label=\"{active}\"" +
                               $" match=\"{(active != null ? MatchTabLabel(active, target) : null)}\"");
        target.Acknowledged = true;
        return true;
    }

    /// <summary>Mark the closed session behind a Claude tab that is still open, so the card
    /// says "the tab is a leftover" instead of showing nothing at all.
    ///
    /// Nothing at all is genuinely ambiguous: it reads the same whether the session ended or
    /// the deck failed to detect a live one, and Shay hit both on the same evening — the
    /// orphan-sweep false close on ws shimi-agent, and this, ws bpm-emailagent-mcp, where
    /// Claude Code really did send a SessionEnd six seconds after the answer while the tab
    /// stayed open (16-08-2026). The badge counted the tab either way, which is what made the
    /// card unreadable.
    ///
    /// One card per tab, the newest match only: every run of the same slash command shares a
    /// label, and that workspace holds fifteen closed sessions all called
    /// "/shimi-triage AAMk…" — showing them all would answer noise with more noise. A tab with
    /// a LIVE session marks nothing: that session's own card already answers the question.</summary>
    private static bool RefreshEndedTabs(WorkspaceViewModel ws)
    {
        var marked = new HashSet<SessionViewModel>();
        foreach (var label in ws.ClaudeTabLabels)
        {
            if (ws.Sessions.Any(s => !s.Closed && !s.Phantom && TabLabelMatches(label, s))) continue;
            var last = ws.Sessions.Where(s => s.Closed && TabLabelMatches(label, s))
                                  .OrderByDescending(s => s.EndedAt ?? s.StartedAt)
                                  .FirstOrDefault();
            if (last != null) marked.Add(last);
        }
        bool changed = false;
        foreach (var s in ws.Sessions)
        {
            bool want = marked.Contains(s);
            if (s.EndedTabOpen == want) continue;
            s.EndedTabOpen = want;
            changed = true;
        }
        return changed;
    }

    /// <summary>Every live VSCode window with this folder open, in connection order. More
    /// than one is normal: a second window on the same folder signed into another account
    /// is exactly what Shay runs (21-08-2026).</summary>
    private List<VscodeConnection> ConnectorsFor(WorkspaceViewModel ws)
    {
        if (ws.Path.Length == 0) return new List<VscodeConnection>();
        string norm = WorkspaceMetadata.NormalizePath(ws.Path);
        return _connectors.Where(c => c.WorkspacePath.Length > 0 &&
            WorkspaceMetadata.NormalizePath(c.WorkspacePath) == norm).ToList();
    }

    /// <summary>Which window a command for this workspace goes to.
    ///
    /// With one window there is no question. With several, "the one that connected last"
    /// (the old rule) is whichever window's extension host restarted most recently — it
    /// flips under the user with nothing on screen to explain it, which is how sessions
    /// opened from the deck kept landing in the wrong window. The order now is: the window
    /// that already shows this session's tab (revealing it there is the only answer that
    /// can't start a second copy of the same conversation), then the window the user was
    /// most recently working in, then the last to connect.</summary>
    private VscodeConnection? FindConnector(WorkspaceViewModel ws, SessionViewModel? session = null)
    {
        var conns = ConnectorsFor(ws);
        if (conns.Count <= 1) return conns.FirstOrDefault();

        if (session != null &&
            conns.LastOrDefault(c => c.Tabs.Any(t => TabLabelMatches(t.Label, session))) is { } holder)
        {
            LogService.Debug("route", $"session={session.SessionId} → pid={holder.Pid} (holds the tab)");
            return holder;
        }
        var focused = conns.Where(c => c.LastFocusedAt != default)
                           .OrderByDescending(c => c.LastFocusedAt).FirstOrDefault();
        var pick = focused ?? conns[^1];
        LogService.Debug("route", $"ws=\"{ws.DisplayTitle}\" → pid={pick.Pid} " +
            (focused != null ? "(last focused)" : "(last connected)") + $" of {conns.Count} windows");
        return pick;
    }

    private int ConnectorCount(WorkspaceViewModel ws) => ConnectorsFor(ws).Count;

    // ---- session groups: WHICH VSCode instance a new session opens in ----
    //
    // Shay runs three VSCode instances on C:\Users\Shay\.claude, each with its own
    // --user-data-dir so each can be signed into a different Claude account (04-09-2026).
    // They are one card - a card is a folder - and FindConnector above sends a new session to
    // whichever of them he focused last, which is invisible and flips under him. A group
    // makes the choice a gesture: no modifier, Ctrl or Alt picks the account.

    /// <summary>The groups that apply to this card: those pinned to its path, plus any that
    /// name no path at all. A card no group names has none, and every path below behaves
    /// exactly as it did before groups existed.</summary>
    private List<SessionGroupConfig> GroupsFor(WorkspaceViewModel ws)
    {
        if (_sessionGroups.Count == 0 || ws.Path.Length == 0) return new List<SessionGroupConfig>();
        string norm = WorkspaceMetadata.NormalizePath(ws.Path);
        return _sessionGroups.Where(g => g.Id.Length > 0 &&
            (g.WorkspacePath.Length == 0 ||
             WorkspaceMetadata.NormalizePath(g.WorkspacePath) == norm)).ToList();
    }

    /// <summary>The group the keys held right now ask for on this card, or null when the card
    /// has no groups. Called at the moment of the click: a modifier is only ever true while
    /// the gesture is happening.</summary>
    public SessionGroupConfig? GroupForModifiers(WorkspaceViewModel ws)
    {
        var groups = GroupsFor(ws);
        if (groups.Count == 0) return null;
        string held = HeldModifierName();
        return groups.FirstOrDefault(g => NormalizeModifier(g.Modifier) == held);
    }

    /// <summary>A group by its id, whatever card it belongs to (the CLI's --group).</summary>
    public SessionGroupConfig? GroupById(string id)
        => _sessionGroups.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<SessionGroupConfig> SessionGroups => _sessionGroups;

    /// <summary>Where a group stands right now, for `sessiondeck groups`: is its instance
    /// running, and is its connector up. Three states, because they need three answers — a
    /// session aimed at a group that is merely slow to connect is parked, one aimed at a group
    /// that is not running gets it launched.</summary>
    public string GroupStateText(SessionGroupConfig group)
    {
        var ws = group.WorkspacePath.Length > 0 ? Vm.FindByPath(group.WorkspacePath) : null;
        if (ws != null && ConnectorInGroup(ws, group) is { } conn)
            return $"connected (window-pid {conn.OwnerPid})";
        if (GroupWindowIsOpen(group)) return "open, connector not up";
        return group.Launcher.Length > 0 && File.Exists(group.Launcher)
                   ? "not running (the deck can start it)" : "not running";
    }

    private static string HeldModifierName()
    {
        var m = System.Windows.Input.Keyboard.Modifiers;
        var parts = new List<string>();
        if ((m & System.Windows.Input.ModifierKeys.Control) != 0) parts.Add("ctrl");
        if ((m & System.Windows.Input.ModifierKeys.Alt) != 0) parts.Add("alt");
        if ((m & System.Windows.Input.ModifierKeys.Shift) != 0) parts.Add("shift");
        return string.Join("+", parts);
    }

    /// <summary>"Ctrl", "control", "Alt+Ctrl" and "ctrl+alt" are all the same combination.</summary>
    private static string NormalizeModifier(string spec)
    {
        var parts = spec.Split(new[] { '+', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().ToLowerInvariant())
            .Select(p => p switch { "control" => "ctrl", "menu" => "alt", _ => p })
            .Where(p => p is "ctrl" or "alt" or "shift")
            .Distinct().ToList();
        // Fixed order, so the spec's own order never matters.
        return string.Join("+", new[] { "ctrl", "alt", "shift" }.Where(parts.Contains));
    }

    /// <summary>Which group a connector belongs to, by the marker in its window's title.
    ///
    /// The pid answers "which INSTANCE", and here that is exactly the question - each of these
    /// groups IS a separate instance, with its own Electron main process. But a pid is not
    /// something a config file can hold across a restart, so the durable name is the title
    /// marker, and the pid is how we get from a connector to the titles to test it against.</summary>
    private static string GroupIdOf(VscodeConnection conn, List<CandidateWindow> windows,
                                    List<SessionGroupConfig> groups)
    {
        var titles = windows
            .Where(w => (conn.Hwnd != IntPtr.Zero && w.Hwnd == conn.Hwnd) ||
                        (conn.OwnerPid != 0 && WindowEnumerator.GetProcessId(w.Hwnd) == conn.OwnerPid))
            .Select(w => w.Title).ToList();
        if (titles.Count == 0) return "";
        return groups.FirstOrDefault(g => g.TitleMarker.Length > 0 &&
                   titles.Any(t => t.Contains(g.TitleMarker, StringComparison.Ordinal)))?.Id ?? "";
    }

    /// <summary>This group's live connector, or null when the instance is not running (or is
    /// running with its extension host not yet connected).</summary>
    private VscodeConnection? ConnectorInGroup(WorkspaceViewModel ws, SessionGroupConfig group)
    {
        var conns = ConnectorsFor(ws);
        if (conns.Count == 0) return null;
        var windows = WindowEnumerator.GetCandidates()
            .Where(w => WorkspaceMetadata.IsVsCodeProcess(w.ProcessName)).ToList();
        var groups = GroupsFor(ws);
        return conns.LastOrDefault(c => GroupIdOf(c, windows, groups) == group.Id);
    }

    /// <summary>Is the group's instance on screen at all? Separates "not running" (launch it)
    /// from "running, its connector is not up yet" (wait for it) - launching over a window
    /// that already exists would hand him a fourth instance he never asked for.</summary>
    private static bool GroupWindowIsOpen(SessionGroupConfig group)
        => group.TitleMarker.Length > 0 && WindowEnumerator.GetCandidates().Any(w =>
               WorkspaceMetadata.IsVsCodeProcess(w.ProcessName) &&
               w.Title.Contains(group.TitleMarker, StringComparison.Ordinal));

    /// <summary>Start the group's instance by running ITS OWN launcher script.
    ///
    /// The deck does not assemble the command line, and that is the point. What binds a window
    /// to a Claude account is `CLAUDE_SECURESTORAGE_CONFIG_DIR` in its environment, not the
    /// `--user-data-dir` (which only forces a separate process so the variable can reach the
    /// extension at all): a window started without it comes up looking exactly right and spends
    /// the DEFAULT wallet. The start also has to go through `bin\code.cmd` rather than
    /// `Code.exe` - six attempts to bring wallet 2 up through `Code.exe` on 03-09-2026 produced
    /// no window whatsoever. Both facts, and a stuck-updater recovery, already live in
    /// `~/.claude/scripts/launch-dev-mgmt*-window.vbs`. A second copy here would be one more
    /// thing to keep in step, and being wrong about it costs an account.
    ///
    /// Every VSCODE_* and ELECTRON_* variable is stripped from the child anyway: inherited from
    /// a shell running under an extension host they change how the VSCode CLI behaves, and the
    /// deck IS started that way while it is being tested from a session.</summary>
    private static bool LaunchGroup(SessionGroupConfig group)
    {
        try
        {
            if (group.Launcher.Length == 0 || !File.Exists(group.Launcher))
            {
                LogService.Info("group", $"\"{group.Name}\" has no launcher on disk " +
                                         $"({(group.Launcher.Length == 0 ? "none configured" : group.Launcher)})");
                return false;
            }
            bool script = group.Launcher.EndsWith(".vbs", StringComparison.OrdinalIgnoreCase) ||
                          group.Launcher.EndsWith(".js", StringComparison.OrdinalIgnoreCase);
            var psi = new System.Diagnostics.ProcessStartInfo(
                script ? "wscript.exe" : group.Launcher)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(group.Launcher) ?? "",
            };
            if (script) psi.ArgumentList.Add(group.Launcher);
            foreach (string key in psi.Environment.Keys
                         .Where(k => k.StartsWith("VSCODE_", StringComparison.OrdinalIgnoreCase) ||
                                     k.StartsWith("ELECTRON_", StringComparison.OrdinalIgnoreCase))
                         .ToList())
                psi.Environment.Remove(key);
            System.Diagnostics.Process.Start(psi);
            LogService.Info("group", $"launched \"{group.Name}\" ({group.Id}) via {group.Launcher}");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Info("group", $"launching \"{group.Name}\" failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Point the card at the window this session lives in, before anything focuses
    /// it. For callers outside the click handler (the CLI's `session open`).</summary>
    public void PointCardAtSessionWindow(WorkspaceViewModel ws, SessionViewModel session)
        => RebindToConnectorWindow(ws, FindConnector(ws, session));

    /// <summary>Learn which OS window a connector lives in, at the one moment the two
    /// systems can be lined up: the extension reports that its window has focus, and Windows
    /// says which window that is.
    ///
    /// Nothing cheaper works. The pid cannot answer it - Electron creates every window in the
    /// MAIN process and the extension host is a utility child of that same main process, so
    /// all four of Shay's windows and all four of their hosts report pid 53380 (measured
    /// 22-08-2026). It only looked like a window id on 21-08 because the second window was a
    /// second VSCode INSTANCE with its own main process; one reboot put everything back into
    /// one instance and the answer collapsed to "the whole instance". The title cannot answer
    /// it either: two windows on one folder carry the same title, and a custom `window.title`
    /// matches no pattern at all.
    ///
    /// One API call per sync, and it self-corrects: whatever a window was thought to be, the
    /// next time the user works in it, it says so itself.</summary>
    private void CorrelateConnectorWindow(VscodeConnection conn)
    {
        if (!conn.Focused) return;
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == conn.Hwnd) return;
        // The foreground window has to BE VSCode, and this connector's VSCode when we know
        // which instance that is. Guards the one race in the protocol: a 2s heartbeat whose
        // focus flag was true a moment ago, while the user has already switched away, must
        // not stamp whatever is in front now onto this connector.
        int fgPid = WindowEnumerator.GetProcessId(fg);
        bool sameVscode = conn.OwnerPid != 0
            ? fgPid == conn.OwnerPid
            : WorkspaceMetadata.IsVsCodeProcess(WindowEnumerator.GetProcessName(fg));
        if (!sameVscode) return;
        // A window hosts one folder, so one connector. Whoever held this hwnd on older,
        // weaker evidence loses it.
        foreach (var other in _connectors)
            if (other != conn && other.Hwnd == fg) other.Hwnd = IntPtr.Zero;
        conn.Hwnd = fg;
        LogService.Info("vscode", $"window identified pid={conn.Pid} ws=\"{conn.WorkspacePath}\" " +
                                  $"hwnd=0x{fg.ToInt64():X} title=\"{NativeMethods.GetWindowTextSafe(fg)}\"");
    }

    /// <summary>Move the card's window bind onto the window a command was just routed to.
    ///
    /// Binding matches on the window TITLE, and the title fails in two different ways. With
    /// one folder open in two windows it binds whichever Windows enumerated first, so the
    /// deck raised one window while opening the session in the other (Shay, 21-08-2026). And
    /// a window with a custom `window.title` matches NOTHING, so its card never binds at all
    /// and every click launched yet another VSCode window on a folder that was already open
    /// (Shay, 22-08-2026).
    ///
    /// The connector's own window answers both, once focus correlation has identified it.
    /// Until then all the connector proves is which VSCode INSTANCE the folder is open in -
    /// the pid is instance-wide, not per window, which is why this whole path went inert the
    /// moment a reboot put both of Shay's windows in one instance (22-08-2026). Then the
    /// title is still the only discriminator, and the fallbacks below run weakest-last.</summary>
    private void RebindToConnectorWindow(WorkspaceViewModel ws, VscodeConnection? conn)
    {
        if (conn == null) return;

        // Identified: exact, and it needs no title at all.
        if (conn.Hwnd != IntPtr.Zero && NativeMethods.IsWindow(conn.Hwnd))
        {
            if (ws.Hwnd == conn.Hwnd) return;
            // A window belongs to one card. If some other card already holds it, leave both
            // alone rather than passing the window back and forth on every click.
            if (Vm.FindByHwnd(conn.Hwnd) is { } holder && holder != ws) return;
            string title = NativeMethods.GetWindowTextSafe(conn.Hwnd);
            LogService.Info("bind", $"ws=\"{ws.DisplayTitle}\" re-bound to its connector's own window " +
                                    $"(hwnd=0x{conn.Hwnd.ToInt64():X}, title=\"{title}\")");
            Bind(ws, conn.Hwnd, title, WindowEnumerator.GetProcessName(conn.Hwnd));
            return;
        }
        if (conn.OwnerPid == 0) return;
        // Not identified yet - the user has not worked in that window since it connected. A
        // bind the card's own title pattern agrees with beats anything guessed below.
        if (ws.State == BindState.Connected && NativeMethods.IsWindow(ws.Hwnd) &&
            SafeIsMatch(ws.WindowTitle, ws.TitlePattern)) return;

        var windows = WindowEnumerator.GetCandidates()
            .Where(c => WorkspaceMetadata.IsVsCodeProcess(c.ProcessName) &&
                        WindowEnumerator.GetProcessId(c.Hwnd) == conn.OwnerPid &&
                        (Vm.FindByHwnd(c.Hwnd) is not { } owner || owner == ws)).ToList();
        string leaf = WorkspaceMetadata.NameFromPath(ws.Path);
        // Weakest last: the card's own pattern, then any window that merely NAMES the folder
        // (a custom title still does - Shay's "DEV MGMT · .claude"), then a lone window in
        // that instance. Every one of them is a guess, and every one is replaced the moment
        // that window is focused and identifies itself for real.
        var match = windows.FirstOrDefault(c => SafeIsMatch(c.Title, ws.TitlePattern))
                 ?? (leaf.Length > 0
                        ? windows.FirstOrDefault(c => c.Title.Contains(leaf, StringComparison.OrdinalIgnoreCase))
                        : null)
                 ?? (windows.Count == 1 ? windows[0] : null);
        if (match == null)
        {
            LogService.Debug("bind", $"ws=\"{ws.DisplayTitle}\" no window of pid={conn.OwnerPid} " +
                                     $"identifies itself ({windows.Count} candidates) - bind left alone");
            return;
        }
        if (match.Hwnd == ws.Hwnd) return;

        LogService.Info("bind", $"ws=\"{ws.DisplayTitle}\" re-bound by title, its connector's window is " +
                                $"not identified yet (window-pid={conn.OwnerPid}, title=\"{match.Title}\")");
        Bind(ws, match.Hwnd, match.Title, match.ProcessName);
    }

    /// <summary>Recompute the card's VSCode state from ALL its windows: the tab list is
    /// their union (a card describes the folder, and a session's tab counts wherever it is
    /// open), and the active tab comes only from a window that currently has focus —
    /// an unfocused window's sync must not blank out what the focused one reported.
    /// Returns the union, for the caller's log line.</summary>
    private List<string> ApplyConnectorState(WorkspaceViewModel ws)
    {
        var conns = ConnectorsFor(ws);
        if (conns.Count > 0) ws.WindowGoneAt = null;
        // Duplicates are kept: two tabs with the same label are two tabs, and the
        // correlation above hands out one session per tab instance. Distinct() collapsed
        // them, which silently halved the capacity of every repeated title. Deduped by pid
        // instead, so a reconnect that left its old connection behind cannot double a
        // window's tabs.
        var labels = conns.GroupBy(c => c.Pid).Select(g => g.Last())
                          .SelectMany(c => c.Tabs).Select(t => t.Label).ToList();
        ws.SetClaudeTabs(labels);
        var focused = conns.Where(c => c.Focused).OrderByDescending(c => c.LastFocusedAt).FirstOrDefault();
        ws.ActiveClaudeTabLabel = focused?.Tabs.FirstOrDefault(t => t.Active)?.Label;
        return labels;
    }

    /// <summary>Open/resume the session's tab in VSCode. Without a live connector the request
    /// is parked; it's flushed when the extension connects (VSCode may still be launching).</summary>
    public (bool, string) OpenSessionInVscode(WorkspaceViewModel ws, SessionViewModel session,
                                              VscodeConnection? conn = null)
    {
        conn ??= FindConnector(ws, session);
        if (conn == null)
        {
            if (ws.Path.Length > 0)
                _pendingOpens[WorkspaceMetadata.NormalizePath(ws.Path)] = (session.SessionId, null, null, DateTime.Now);
            return (false, "no VSCode connector for this workspace yet — request queued");
        }
        if (!conn.TrySend(new { Cmd = "openSession", SessionId = session.SessionId, Maximize = Vm.OpenSessionMaximized }))
        {
            _connectors.Remove(conn);
            return (false, "connector connection lost");
        }
        return (true, "");
    }

    /// <summary>+ New Session (feedback 2026-07-19): open a fresh Claude conversation tab
    /// in the workspace's VSCode window; parks like openSession when VSCode is launching.
    /// An optional opening prompt (a task's newSessionPrompt, T-0116) rides along.</summary>
    /// <paramref name="group"/> pins the session to ONE VSCode instance (the modifier held at
    /// the click, or the CLI's --group). It is never approximated: a group whose instance is
    /// not up gets launched, or waited for, but the session does not quietly open in another
    /// account's window - which is the whole reason the groups exist.
    public (bool, string) NewSessionInVscode(WorkspaceViewModel ws, string? prompt = null,
                                             SessionGroupConfig? group = null)
    {
        var conn = group != null ? ConnectorInGroup(ws, group) : FindConnector(ws);
        if (group != null && conn == null) return QueueGroupSession(ws, prompt, group);
        RebindToConnectorWindow(ws, conn);      // same folder open twice: raise the right window
        FocusWorkspace(ws);
        if (conn == null)
        {
            if (ws.Path.Length > 0)
                _pendingOpens[WorkspaceMetadata.NormalizePath(ws.Path)] = (null, prompt, null, DateTime.Now);
            SetStatus("VSCode is starting — the new session will open once the connector is up");
            return (false, "no VSCode connector yet — request queued");
        }
        if (!conn.TrySend(new { Cmd = "newSession", Prompt = prompt, Maximize = Vm.OpenSessionMaximized }))
        {
            _connectors.Remove(conn);
            return (false, "connector connection lost");
        }
        SetStatus(group == null
            ? $"Opening a new session in \"{ws.DisplayTitle}\""
            : $"Opening a new session in {group.Name}");
        LogService.Info("group", $"new session → \"{group?.Name ?? ws.DisplayTitle}\" " +
                                 $"pid={conn.Pid} window-pid={conn.OwnerPid}");
        return (true, "");
    }

    /// <summary>The requested group has no connector. Start its instance when we know how and
    /// no window of it is already up, and park the request under the group's name so the
    /// FIRST connector to appear cannot claim it unless it is that group's.</summary>
    private (bool, string) QueueGroupSession(WorkspaceViewModel ws, string? prompt, SessionGroupConfig group)
    {
        if (ws.Path.Length == 0) return (false, $"{group.Name}: the card has no folder to open");
        _pendingOpens[WorkspaceMetadata.NormalizePath(ws.Path)] = (null, prompt, group.Id, DateTime.Now);
        if (GroupWindowIsOpen(group))
        {
            SetStatus($"{group.Name} is open but not connected yet — the session will start there when it is");
            return (false, $"{group.Id}: window up, connector not");
        }
        if (LaunchGroup(group))
        {
            SetStatus($"Starting {group.Name} — the session will open there once it is up");
            return (false, $"{group.Id}: launching");
        }
        _pendingOpens.Remove(WorkspaceMetadata.NormalizePath(ws.Path));
        SetStatus($"{group.Name} is not running, and the deck has no way to start it — nothing opened");
        return (false, $"{group.Id}: not running");
    }

    // ---- focus / pin / stage ----

    public (bool, string) FocusWorkspace(WorkspaceViewModel ws)
    {
        ws.LastUsedAt = DateTime.Now;   // opening a card from the deck is using it
        if (ws.State != BindState.Connected || !NativeMethods.IsWindow(ws.Hwnd))
        {
            // A live connector means the folder IS open in a window, whatever the title
            // match believes. Launching another one on top of it is the worst answer, and
            // it is what a card whose window can't be title-matched used to do on every
            // single click (Shay, 22-08-2026: "it opens the wrong place").
            RebindToConnectorWindow(ws, FindConnector(ws));
        }
        if (ws.State != BindState.Connected || !NativeMethods.IsWindow(ws.Hwnd))
        {
            // No bound window — open VSCode on the folder; auto-bind picks it up (feedback 2026-07-19).
            if (ws.Path.Length > 0 && Directory.Exists(ws.Path))
            {
                if (WindowActions.LaunchVsCode(ws.Path))
                {
                    SetStatus($"Launching VSCode for \"{ws.DisplayTitle}\"...");
                    return (true, $"launching VSCode for workspace {ws.Id}");
                }
                SetStatus($"\"{ws.DisplayTitle}\" — launching VSCode failed");
                return (false, $"failed to launch VSCode for workspace {ws.Id}");
            }
            SetStatus($"\"{ws.DisplayTitle}\" — no open window and no folder path");
            return (false, $"workspace {ws.Id} has no bound window and no path");
        }
        WindowActions.Focus(ws.Hwnd);
        return (true, "");
    }

    /// <summary>⋯ menu action (feedback 2026-07-19): close the workspace's VSCode window
    /// itself (graceful WM_CLOSE). The card stays on the deck as disconnected.</summary>
    public void CloseWorkspaceWindow(WorkspaceViewModel ws)
    {
        if (ws.State != BindState.Connected || !NativeMethods.IsWindow(ws.Hwnd))
        {
            SetStatus($"\"{ws.DisplayTitle}\" — no open window to close");
            return;
        }
        WindowActions.Close(ws.Hwnd);
        SetStatus($"Closing the VSCode window of \"{ws.DisplayTitle}\"...");
    }

    public (bool, string) PinWorkspace(WorkspaceViewModel ws)
    {
        if (ws.State != BindState.Connected || !NativeMethods.IsWindow(ws.Hwnd))
        {
            // No bound window — same launch fallback as Focus, and the stage is
            // applied automatically once the launched window binds (Bind()).
            var res = FocusWorkspace(ws);
            if (res.Item1) _pendingPins[ws.Id] = DateTime.Now;
            return res;
        }
        if (Vm.StageMode == StageMode.Full)
            WindowActions.MaximizeOn(ws.Hwnd, GetStageRect());
        else
            WindowActions.MoveTo(ws.Hwnd, GetStageRect());
        return (true, "");
    }

    /// <summary>Stage rect from the target monitor's work area — respects taskbar and our own zone.</summary>
    private RECT GetStageRect()
    {
        if (Vm.StageMode == StageMode.Rect && Vm.StageRect is { } custom)
            return custom;
        _monitors = MonitorService.GetMonitors();
        var mon = _monitors[Math.Clamp(Vm.StageMonitor, 0, _monitors.Count - 1)];
        RECT work = mon.WorkArea;
        return Vm.StageMode switch
        {
            StageMode.HalfLeft => new RECT { Left = work.Left, Top = work.Top, Right = work.Left + work.Width / 2, Bottom = work.Bottom },
            StageMode.HalfRight => new RECT { Left = work.Left + work.Width / 2, Top = work.Top, Right = work.Right, Bottom = work.Bottom },
            _ => work,
        };
    }

    public static RECT? ParseRect(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split(',');
        if (parts.Length != 4) return null;
        if (!int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y) ||
            !int.TryParse(parts[2], out int w) || !int.TryParse(parts[3], out int h) || w <= 0 || h <= 0)
            return null;
        return new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }

    /// <summary>Called by the CLI after changes that may start/stop blinking.</summary>
    public void RefreshBlink() => RefreshBlinkAndSummary();

    /// <summary>Recompute the status-bar summary dots, then start/stop the blink timer
    /// (the dots blink via the same engine as the session borders).</summary>
    private void RefreshBlinkAndSummary()
    {
        Vm.RebuildStatusSummary();
        _blink.Refresh();
        UpdateAttentionEscalation();
    }

    /// <summary>
    /// A blinking border is only a signal while the deck is on screen. With the 📌 pin off
    /// and no reserved zone the deck is an ordinary window that anything can cover, so
    /// attention has to leave it: a taskbar overlay badge for as long as something is
    /// pending, plus one balloon and a single taskbar flash per session that newly needs
    /// attention (feature 2026-07-20).
    ///
    /// Nothing escalates while the deck has focus — the user is looking straight at the
    /// blink, and a toast over the window you are already reading is the kind of false
    /// alarm that makes people mute the app. The ⚙ menu's "Windows notifications" switch turns
    /// the whole mechanism off regardless.
    /// </summary>
    private void UpdateAttentionEscalation()
    {
        if (_initializing) return;

        var attention = Vm.Workspaces.Where(w => w.VisibleInDeck)
            .SelectMany(w => w.Sessions.Select(s => (Ws: w, S: s)))
            .Where(p => !p.S.Closed && !p.S.Phantom && p.S.BlinkActive)
            .OrderBy(p => MainViewModel.Severity(p.S.Status))
            .ToList();

        // Anything that stopped needing attention is re-armed for its next event.
        _notifiedSessions.IntersectWith(attention.Select(p => p.S.SessionId));

        // "Buried" = the deck has no place of its own on screen, so a balloon is worth it.
        // Any zone gives it one; only a free-floating deck (Off) with the pin off can end up
        // somewhere the user never sees. This asks ModeNames.HasOwnPlace and not
        // ReservesWorkArea on purpose — see the note there.
        bool buried = Vm.WindowsNotifications && !Vm.AlwaysOnTop && !ModeNames.HasOwnPlace(Vm.ZoneMode);
        if (!buried || IsActive || attention.Count == 0)
        {
            // Seed instead of notify: a session that was already blinking when the deck was
            // pinned/zoned/focused or notifications were off is not news the moment that
            // condition goes away.
            foreach (var p in attention) _notifiedSessions.Add(p.S.SessionId);
            _notifier.Clear();
            return;
        }

        // Deliberately not withdrawn when only *some* of what it named is dealt with: the
        // balloon means "the deck needs you", which is still true while anything blinks, and
        // pulling it would leave the remaining session with a weaker signal than it had
        // (decision 2026-07-20). Its headline can name an already-answered session — a
        // cosmetic flaw on a transient toast, where the fix (pushing a fresh one) is noise.
        _notifier.SetBadge(BadgeColor(attention[0].S.Status), AttentionText(attention));

        var fresh = attention.Where(p => _notifiedSessions.Add(p.S.SessionId)).ToList();
        if (fresh.Count == 0) return;
        _notifier.Balloon("SessionDeck", AttentionText(fresh));
        _notifier.Flash();
    }

    private static string AttentionText(IReadOnlyList<(WorkspaceViewModel Ws, SessionViewModel S)> items)
    {
        var (ws, s) = items[0];
        // The line is an English frame around names that are often Hebrew. A leading LRM
        // pins the paragraph to LTR, so a Hebrew workspace title can't flip the whole
        // balloon line right-to-left in the shell; the name itself still renders RTL.
        string first = $"‎{ws.DisplayTitle} — {s.DisplayTitle}: {AttentionWord(s.Status)}";
        return items.Count == 1 ? first : $"{first}{Environment.NewLine}and {items.Count - 1} more";
    }

    /// <summary>Badge colour comes from the same StatusStyles map as the card border, so a
    /// config override moves both together.</summary>
    private static System.Windows.Media.Color BadgeColor(SessionStatus status)
        => ColorUtil.TryParse(SessionViewModel.ResolveStyle(status).Color, out var c)
            ? c : System.Windows.Media.Colors.Gray;

    private static string AttentionWord(SessionStatus status) => status switch
    {
        SessionStatus.Waiting => "waiting for you",
        SessionStatus.Done => "your turn",
        SessionStatus.He => "wrapped up (HE)",
        SessionStatus.Error => "error",
        _ => SessionStatusNames.ToName(status),
    };

    /// <summary>Stage definition from the CLI: monitor + full/half, or a custom rect.</summary>
    public void SetStage(int monitor, StageMode mode, RECT? rect)
    {
        Vm.StageMonitor = Math.Clamp(monitor, 0, _monitors.Count - 1);
        Vm.StageMode = mode;
        if (mode == StageMode.Rect) Vm.StageRect = rect;
        SyncCombosFromVm();
        QueueSave();
    }

    // ---- Reserved Zone ----

    public void ApplyZone(int monitor, ZoneMode mode, bool save = true, string? customSize = null)
    {
        if (customSize != null && ZoneSizeParser.TryParse(customSize, out _))
            Vm.ZoneSize = customSize.Trim();
        _monitors = MonitorService.GetMonitors();
        monitor = Math.Clamp(monitor, 0, _monitors.Count - 1);
        Vm.ZoneMonitor = monitor;
        Vm.ZoneMode = mode;
        // Zoned = locked in place; NoResize also removes the resize cursors on the borders.
        ResizeMode = mode == ZoneMode.Off ? ResizeMode.CanResize : ResizeMode.NoResize;
        double fraction = ZoneSizeParser.TryParse(Vm.ZoneSize, out double f) ? f : 1.0 / 3;
        _appBar.Apply(mode, _monitors[monitor], fraction);
        SyncCombosFromVm();
        UpdateAttentionEscalation();   // zone state is the other half of the escalation gate
        if (save) QueueSave();
    }

    // ---- UI: toolbar ----

    /// <summary>
    /// The toolbar dividers only make sense between neighbors that share a row —
    /// hide them when the responsive wrap moved a group to its own row. Uses
    /// Hidden (not Collapsed) so toggling never changes layout width, which would
    /// re-trigger the wrap and oscillate on borderline window sizes.
    /// </summary>
    private void ToolbarLayout_Changed(object sender, SizeChangedEventArgs e)
    {
        static bool SameRow(FrameworkElement a, FrameworkElement b, UIElement origin) =>
            Math.Abs(a.TranslatePoint(default, origin).Y - b.TranslatePoint(default, origin).Y) < 10;

        ToolbarDiv1.Visibility = SameRow(AddWorkspaceButton, ZoneGroup, ToolbarWrap)
            ? Visibility.Visible : Visibility.Hidden;
        ToolbarDiv2.Visibility = SameRow(ZoneGroup, StageGroup, ToolbarWrap)
            ? Visibility.Visible : Visibility.Hidden;

        // The right-docked icon strip is measured before the left controls, so on a
        // narrow window it would keep its single row and squeeze the combos off-screen.
        // Cap it to what the widest left group leaves free — the WrapPanel then wraps
        // the icons row-by-row instead. Inputs (window width, fixed group widths) do
        // not depend on the cap itself, so the layout converges without oscillating.
        double leftNeeded = Math.Max(AddGroup.ActualWidth,
            Math.Max(ZoneGroup.ActualWidth, StageGroup.ActualWidth));
        double free = ToolbarRoot.ActualWidth - leftNeeded - IconStrip.Margin.Left;
        IconStrip.MaxWidth = Math.Max(44, free);   // 44 ≈ one icon — never fully collapse
    }

    private void PopulateCombos()
    {
        _syncingUi = true;
        foreach (var combo in new[] { ZoneMonitorCombo, StageMonitorCombo })
        {
            combo.Items.Clear();
            foreach (var m in _monitors) combo.Items.Add(m.DisplayName);
        }
        ZoneModeCombo.Items.Clear();
        foreach (var name in new[] { "Off", "Left quarter", "Left half", "Right half", "Right quarter", "Full screen", "Custom left…", "Custom right…" }) ZoneModeCombo.Items.Add(name);
        StageModeCombo.Items.Clear();
        foreach (var name in new[] { "Full screen", "Left half", "Right half", "Rect (CLI)" }) StageModeCombo.Items.Add(name);
        StartupMenuItem.IsChecked = StartupService.IsEnabled();
        VersionMenuItem.Header = $"SessionDeck v{GetType().Assembly.GetName().Version?.ToString(3)}";
        MaximizeSessionMenuItem.IsChecked = Vm.OpenSessionMaximized;
        NotificationsMenuItem.IsChecked = Vm.WindowsNotifications;
        TasksStripMenuItem.IsChecked = Vm.ShowTasksStrip;
        HeadlessSessionsMenuItem.IsChecked = Vm.ShowHeadless;
        ShowHiddenToggle.IsChecked = Vm.ShowHidden;
        ActiveOnlyToggle.IsChecked = Vm.ActiveOnly;
        PinTopToggle.IsChecked = Vm.AlwaysOnTop;
        SyncSortMenu();
        _syncingUi = false;
        SyncCombosFromVm();
    }

    private void SyncCombosFromVm()
    {
        _syncingUi = true;
        ZoneMonitorCombo.SelectedIndex = Vm.ZoneMonitor;
        // The custom items display the active size; refresh their text before re-selecting.
        ZoneModeCombo.Items[(int)ZoneMode.CustomLeft] = $"Custom left ({Vm.ZoneSize})…";
        ZoneModeCombo.Items[(int)ZoneMode.CustomRight] = $"Custom right ({Vm.ZoneSize})…";
        ZoneModeCombo.SelectedIndex = (int)Vm.ZoneMode;
        StageMonitorCombo.SelectedIndex = Vm.StageMonitor;
        StageModeCombo.SelectedIndex = (int)Vm.StageMode;
        _syncingUi = false;
    }

    private void ZoneUi_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi || _initializing) return;
        if (ZoneMonitorCombo.SelectedIndex < 0 || ZoneModeCombo.SelectedIndex < 0) return;
        var mode = (ZoneMode)ZoneModeCombo.SelectedIndex;
        if (mode is ZoneMode.CustomLeft or ZoneMode.CustomRight && mode != Vm.ZoneMode)
        {
            _zoneSizePrompted = true;
            if (!PromptZoneSize()) { SyncCombosFromVm(); return; }   // canceled → revert selection
        }
        ApplyZone(ZoneMonitorCombo.SelectedIndex, mode);
    }

    /// <summary>Re-selecting the already-active custom item re-opens the size dialog
    /// (SelectionChanged doesn't fire when the selection is unchanged).</summary>
    private void ZoneModeCombo_DropDownClosed(object sender, EventArgs e)
    {
        bool alreadyPrompted = _zoneSizePrompted;
        _zoneSizePrompted = false;
        if (_syncingUi || _initializing || alreadyPrompted) return;
        var mode = (ZoneMode)ZoneModeCombo.SelectedIndex;
        if (mode == Vm.ZoneMode && mode is ZoneMode.CustomLeft or ZoneMode.CustomRight && PromptZoneSize())
            ApplyZone(Vm.ZoneMonitor, mode);
    }

    private bool PromptZoneSize()
    {
        var dlg = new ZoneSizeDialog(Vm.ZoneSize) { Owner = this };
        if (dlg.ShowDialog() != true) return false;
        Vm.ZoneSize = dlg.SizeText;
        return true;
    }

    private void StageUi_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi || _initializing) return;
        if (StageMonitorCombo.SelectedIndex < 0 || StageModeCombo.SelectedIndex < 0) return;
        var mode = (StageMode)StageModeCombo.SelectedIndex;
        if (mode == StageMode.Rect && Vm.StageRect == null)
        {
            // Custom rect can only be defined via CLI (sessiondeck stage --rect x,y,w,h).
            SetStatus("A custom rect can only be set from the CLI: sessiondeck stage --rect x,y,w,h");
            SyncCombosFromVm();
            return;
        }
        Vm.StageMonitor = StageMonitorCombo.SelectedIndex;
        Vm.StageMode = mode;
        QueueSave();
    }

    private void AddWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select a project folder (workspace)",
        };
        if (dialog.ShowDialog(this) != true) return;
        var (ws, err) = AddWorkspaceFromPath(dialog.FolderName);
        SetStatus(ws != null ? $"Workspace \"{ws.DisplayTitle}\" added" : err!);
    }

    // Task text size (Shay, 07-08-2026). A tenth per click: small enough that the step is
    // never jarring, large enough that four clicks are a visibly different size. The clamp
    // lives in the view model, so holding a button cannot walk the size off the card.
    private void FontBigger_Click(object sender, RoutedEventArgs e) => StepTaskFont(+0.1);

    private void FontSmaller_Click(object sender, RoutedEventArgs e) => StepTaskFont(-0.1);

    private void StepTaskFont(double delta)
    {
        Vm.TasksPanel.FontScale += delta;
        QueueSave();
        SetStatus($"Task text size: {Vm.TasksPanel.FontScale * 100:0}%");
    }

    private void ShowHidden_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingUi || _initializing) return;
        Vm.ShowHidden = ShowHiddenToggle.IsChecked == true;
        ApplyDeckVisibility();
        QueueSave();
    }

    private void ActiveOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingUi || _initializing) return;
        Vm.ActiveOnly = ActiveOnlyToggle.IsChecked == true;
        ApplyDeckVisibility();
        SetStatus(Vm.ActiveOnly ? "Showing open workspaces only" : "Showing all workspaces");
        QueueSave();
    }

    /// <summary>The ↻ button: reconcile the deck against VSCode and say what it cleaned.
    /// The count goes to the status line rather than a dialog — pressing it and being told
    /// "nothing to clean up" is a useful answer, and a dialog for that would be a punishment.
    /// </summary>
    private void Reconcile_Click(object sender, RoutedEventArgs e)
    {
        var (msg, _) = ReconcileNow();
        SetStatus(msg);
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        SortButton.ContextMenu.PlacementTarget = SortButton;
        SortButton.ContextMenu.IsOpen = true;
    }

    private void SortMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string name ||
            !ModeNames.TryParseDeckSort(name, out var sort)) return;
        Vm.Sort = sort;
        SyncSortMenu();
        SortWorkspaces();
        QueueSave();
        // Logged because it was not: a report that the order looked wrong (Shay, 01-09-2026)
        // could not be checked against which order was actually chosen.
        LogService.Info("config", $"deck sort → {ModeNames.ToName(sort)}");
        SetStatus(sort switch
        {
            DeckSort.Recent => "Cards ordered by last used",
            DeckSort.Frequency => "Cards ordered by how often they are used",
            _ => "Cards ordered A → Z",
        });
    }

    /// <summary>The three items are one choice, so the check marks are set from the view
    /// model rather than by the click: IsCheckable would otherwise leave two of them ticked.</summary>
    private void SyncSortMenu()
    {
        SortAbcMenuItem.IsChecked = Vm.Sort == DeckSort.Alphabetical;
        SortRecentMenuItem.IsChecked = Vm.Sort == DeckSort.Recent;
        SortFrequencyMenuItem.IsChecked = Vm.Sort == DeckSort.Frequency;
    }

    private void PinTop_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingUi || _initializing) return;
        Vm.AlwaysOnTop = PinTopToggle.IsChecked == true;
        Topmost = Vm.AlwaysOnTop;
        UpdateAttentionEscalation();   // pin state is half the escalation gate
        QueueSave();
    }

    // ---- settings ----

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        SettingsButton.ContextMenu.PlacementTarget = SettingsButton;
        SettingsButton.ContextMenu.IsOpen = true;
    }

    /// <summary>Custom toggles (feature 2026-07-19): rebuild the toolbar buttons from the
    /// definitions; current state comes from the flag files (they survive restarts and
    /// are what external processes read).</summary>
    private void LoadCustomToggles()
    {
        Vm.CustomToggles.Clear();
        foreach (var t in _customToggleConfigs.Where(t => t.Id.Length > 0))
        {
            var toggle = new CustomToggleViewModel
            {
                Id = t.Id,
                Icon = t.Icon,
                Name = t.Name,
                Enabled = ToggleStore.Read(t.Id, t.DefaultOn),
            };
            ToggleStore.Write(toggle.Id, toggle.Enabled);   // ensure the flag file exists
            toggle.Changed += tv => ToggleStore.Write(tv.Id, tv.Enabled);
            Vm.CustomToggles.Add(toggle);
        }
    }

    private void TogglesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TogglesEditorDialog(_customToggleConfigs) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _customToggleConfigs = dialog.Result;
        LoadCustomToggles();
        QueueSave();
        SetStatus($"Toggles updated ({_customToggleConfigs.Count})");
    }

    private void MaximizeSessionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Vm.OpenSessionMaximized = MaximizeSessionMenuItem.IsChecked;
        QueueSave();
    }

    private void NotificationsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Vm.WindowsNotifications = NotificationsMenuItem.IsChecked;
        UpdateAttentionEscalation();   // turning it off must drop the badge/tray icon now
        QueueSave();
    }

    private void TasksStripMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Vm.ShowTasksStrip = TasksStripMenuItem.IsChecked;
        QueueSave();
    }

    private void HeadlessSessionsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Vm.ShowHeadless = HeadlessSessionsMenuItem.IsChecked;
        // ApplyDeckVisibility pushes the flag into every workspace and re-filters their
        // sessions; the sort then follows, because a card that just stopped counting as
        // open must also leave the active block at the top.
        ApplyDeckVisibility();
        SortWorkspaces();
        SetStatus(Vm.ShowHeadless ? "Showing headless sessions too" : "Hiding headless sessions");
        QueueSave();
    }

    private void TasksPageButton_Click(object sender, RoutedEventArgs e) => ShowTasksPage();

    private void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    /// <summary>Run a task by its number: the same flow as its card's button, reached without
    /// finding the card first (Shay, 10-08-2026). The number resolves against the level on
    /// screen and then against the file's launch index, so a number from any part of the tree
    /// works — but only for tasks the producer recorded a directory for, because there is
    /// nowhere to open the others.</summary>
    private void RunTask_Click(object sender, RoutedEventArgs e) => RunTypedTask(false);

    /// <summary>Enter runs the typed number. Alt+Enter has to be read through SystemKey:
    /// WPF reports any Alt combination as Key.System and puts the real key in SystemKey, so
    /// the plain check saw nothing and the orange group could not be reached from the box.
    /// </summary>
    private void RunTaskBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key != System.Windows.Input.Key.Enter) return;
        RunTypedTask(false);
        e.Handled = true;
    }

    /// <summary>The words the box accepts AFTER a number to ask for the short form of a
    /// coordinator session. Since 04-09-2026 they are the ONLY way to ask for it: Ctrl used to
    /// mean the same thing and now picks which VSCode instance — which Claude account — the
    /// session opens in (Shay: he never used the fast form, and wanted the modifier for the
    /// account). Hebrew and English both, plus the flag itself for whoever types it out.</summary>
    private static bool IsFastWord(string word)
        => word.Equals("fast", StringComparison.OrdinalIgnoreCase)
           || word.Equals("--fast", StringComparison.OrdinalIgnoreCase)
           || word == "מהיר";

    private void RunTypedTask(bool fastRequested)
    {
        string typed = RunTaskBox.Text.Trim();
        if (typed.Length == 0)
        {
            SetStatus("Type a task number first, e.g. 4.13.19");
            return;
        }
        // Strip the trailing words before resolving: the number has to reach FindByNumber
        // exactly as the tasks file spells it. Two words are accepted, in any order —
        // "fast"/"מהיר" for a coordinator's short form, and a group's id or alias
        // ("green"/"ירוק") for the VSCode instance, i.e. the account, it opens in.
        string number = typed;
        SessionGroupConfig? group = null;
        while (true)
        {
            int space = number.LastIndexOfAny(new[] { ' ', '\t' });
            if (space <= 0) break;
            string word = number[(space + 1)..];
            if (IsFastWord(word)) fastRequested = true;
            else if (GroupByWord(word) is { } g) group = g;
            else break;
            number = number[..space].TrimEnd();
        }
        if (Vm.TasksPanel.FindByNumber(number) is not { } task)
        {
            SetStatus($"No task {number} in the tasks file — it may be closed, or have no directory recorded");
            return;
        }
        RunTaskBox.Clear();
        // Whether the number is a coordinator's, and what to say when it is not, is decided in
        // HandleTaskActivate so that every entry point answers the same way.
        HandleTaskActivate(task, RunTaskBox, fastRequested, group);
    }

    /// <summary>A session group named by its id or one of its aliases, or null.</summary>
    private SessionGroupConfig? GroupByWord(string word)
        => SessionGroups.FirstOrDefault(g =>
               string.Equals(g.Id, word, StringComparison.OrdinalIgnoreCase) ||
               g.Aliases.Any(a => string.Equals(a, word, StringComparison.OrdinalIgnoreCase)));

    private void StartupMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartupService.SetEnabled(StartupMenuItem.IsChecked);
        }
        catch (Exception ex)
        {
            SetStatus("Failed to update the startup entry: " + ex.Message);
            StartupMenuItem.IsChecked = StartupService.IsEnabled();
        }
    }

    private void SetStatus(string message) => StatusText.Text = message;

    private void UpdateEmptyHint()
        => EmptyHint.Visibility = Vm.Workspaces.Any(w => w.VisibleInDeck) ? Visibility.Collapsed : Visibility.Visible;

    // ---- CLI ----

    public void ActivateFromCli()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
    }
}
