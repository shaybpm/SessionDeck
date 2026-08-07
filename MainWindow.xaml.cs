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

    // Live VSCode-extension connections (stage D). UI thread only (handlers are dispatched).
    private readonly List<VscodeConnection> _connectors = new();
    // A session click with no connector yet (VSCode still launching) parks here until the
    // extension's first sync for that workspace, then the open command is flushed to it.
    // SessionId == null means "open a NEW session" (the + New Session button / a task);
    // Prompt rides along for new sessions opened from a task (T-0116).
    private readonly Dictionary<string, (string? SessionId, string? Prompt, DateTime At)> _pendingOpens = new();
    private static readonly TimeSpan PendingOpenTtl = TimeSpan.FromSeconds(90);
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
        // A config written before this field existed deserializes to the property default,
        // but a hand-edited 0 would clamp to the minimum and look like a shrunk panel with
        // no cause. Anything non-positive means "not set".
        Vm.TasksPanel.FontScale = config.TaskFontScale > 0 ? config.TaskFontScale : 1.0;
        Vm.AlwaysOnTop = config.AlwaysOnTop;
        Vm.WindowsNotifications = config.WindowsNotifications;
        LogService.DebugEnabled = config.DebugLogging;
        Topmost = config.AlwaysOnTop;

        _customToggleConfigs = config.CustomToggles;
        LoadCustomToggles();

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
                    EndReason = sc.EndReason,
                    LastEventAt = sc.LastEventAt,
                    AutoTitle = sc.AutoTitle,
                    TabTitle = sc.TabTitle,
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
                if (svm.Status == SessionStatus.Working && !svm.Closed &&
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
            foreach (var s in ws.Sessions) RefreshPhantom(s);
            ws.RefreshSessionVisibility();
            SortSessions(ws);
            RefreshMetadata(ws);
            Vm.Workspaces.Add(ws);
        }
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
            TaskFontScale = Vm.TasksPanel.FontScale,
            AlwaysOnTop = Vm.AlwaysOnTop,
            WindowsNotifications = Vm.WindowsNotifications,
            DebugLogging = LogService.DebugEnabled,
            TasksFilePath = Vm.TasksFilePath,
            CustomToggles = _customToggleConfigs,
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
                    EndReason = s.EndReason,
                    LastEventAt = s.LastEventAt,
                    AutoTitle = s.AutoTitle,
                    TabTitle = s.TabTitle,
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

    /// <summary>A session the deck only ever heard about through events that carried no
    /// transcript path at all — hand-driven CLI calls (hooks/README.md) and the cwd safety
    /// net can both produce one. Nothing to scan, nothing to resume, and unreachable by
    /// every existing sweep: RefreshPhantom needs a declared path, NeverMaterialized
    /// returns false without one, and the orphan sweep can't correlate a titleless session
    /// while VSCode is open. Result: a card that outlived VSCode, restarts and the 15-minute
    /// orphan TTL (issue 2026-08-04, session "s1").
    /// Silence — not status — is the guard: while events keep arriving it stays, so the CLI
    /// sequence in hooks/README.md still drives a visible card.</summary>
    private static bool Ghost(SessionViewModel s)
        => !s.Closed && s.TranscriptPath is not { Length: > 0 }
           && string.IsNullOrEmpty(s.CustomTitle) && string.IsNullOrEmpty(s.TabTitle)
           && string.IsNullOrEmpty(s.AutoTitle) && s.Description.Length == 0
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

    /// <summary>Close sessions whose host died without a SessionEnd hook. Two shapes:
    /// (a) the workspace's VSCode window is gone — an update/crash kill skips the hooks
    /// entirely; (b) the window is up but no tab answers to the session's titles — a
    /// restored-then-closed tab was never a live session, so closing it fires nothing.
    /// Both are invisible to the phantom sweep (status isn't idle, the transcript exists).
    /// The close waits out OrphanSessionTtl on both the condition and total silence.</summary>
    private void RefreshOrphanSessions()
    {
        foreach (var ws in Vm.Workspaces.ToList())
        {
            bool connected = FindConnector(ws) != null;
            // Two windows on the same workspace race SetClaudeTabs (see extension.ts) —
            // the stored tab list reflects only one of them, so absence proves nothing.
            bool tabsAuthoritative = connected && ConnectorCount(ws) == 1;
            foreach (var s in ws.Sessions.Where(s => !s.Closed && !s.Phantom).ToList())
            {
                bool candidate = !connected || (tabsAuthoritative && Correlatable(s) && !s.OpenAsTab);
                if (!candidate) { s.OrphanSince = null; continue; }
                s.OrphanSince ??= DateTime.Now;
                if (DateTime.Now - s.OrphanSince < OrphanSessionTtl) continue;
                if (DateTime.Now - LastActivity(s) < OrphanSessionTtl) continue;
                EndSession(s.SessionId, new HookInfo(Reason: "orphaned"));
            }
        }
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

        bool blocked = call != null && (call.IsAsk || hookConfirmed || IsAgedPermissionDialog(call));

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
        => Vm.PermissionWaitToolSeconds.TryGetValue(call.ToolName, out int seconds)
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

    /// <summary>Actives (bound window / live session) float to the top (decision 16).
    /// Stable in-place sort via Move so DWM thumbnails survive.</summary>
    public void SortWorkspaces()
    {
        var desired = Vm.Workspaces
            .OrderByDescending(w => w.IsActive)
            .ThenBy(w => w.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        for (int target = 0; target < desired.Count; target++)
        {
            int current = Vm.Workspaces.IndexOf(desired[target]);
            if (current != target)
                Vm.Workspaces.Move(current, target);
        }
    }

    private void ApplyDeckVisibility()
    {
        bool searching = _searchQuery.Length > 0;
        foreach (var ws in Vm.Workspaces)
            ws.VisibleInDeck = (!ws.Hidden || Vm.ShowHidden)
                && (!searching || ws.SelfMatchesSearch || ws.Sessions.Any(SessionMatchesSearch));
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
                                  string? Mode = null, string? Reason = null, bool PermissionDialog = false)
    {
        public static readonly HookInfo Empty = new();
    }

    public (string, bool) StartSession(string sessionId, string workspaceArg, string? title, HookInfo info)
    {
        if (Vm.FindSession(sessionId) is { } found)
        {
            var (fw, fs) = found;
            fs.Closed = false;
            fs.Status = SessionStatus.Idle;
            fs.StartedAt = DateTime.Now;
            fs.EndedAt = null;
            if (!string.IsNullOrEmpty(title)) fs.CustomTitle = title;
            ApplyHookInfo(fs, info);
            LearnTranscriptDir(fw, info);
            RefreshPhantom(fs);
            fw.RefreshSessionVisibility();
            AfterSessionChange(fw);
            return ($"session {sessionId} restarted in \"{fw.DisplayTitle}\"", true);
        }

        var ws = ResolveOrCreateWorkspace(workspaceArg, out string? err);
        if (ws == null) return (err!, false);

        var session = new SessionViewModel
        {
            SessionId = sessionId,
            CustomTitle = string.IsNullOrEmpty(title) ? null : title,
            Status = SessionStatus.Idle,
            StartedAt = DateTime.Now,
        };
        ApplyHookInfo(session, info);
        LearnTranscriptDir(ws, info);
        RefreshPhantom(session);
        ws.Sessions.Insert(0, session);
        ws.RefreshSessionVisibility();
        AfterSessionChange(ws);
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
    }

    /// <summary>The workspace's transcripts folder, learned from any hook event that
    /// carries transcript_path — used to list historical sessions (stage D).</summary>
    private void LearnTranscriptDir(WorkspaceViewModel ws, HookInfo info)
    {
        if (info.Transcript is not { Length: > 0 } t) return;
        string? dir = Path.GetDirectoryName(t);
        if (dir != null && ws.TranscriptDir != dir)
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
    private WorkspaceViewModel? ResolveOrCreateWorkspace(string workspaceArg, out string? error)
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

    public (string, bool) SetSessionStatus(string sessionId, SessionStatus status, string workspaceArg, HookInfo info)
    {
        if (Vm.FindSession(sessionId) is not { } found)
        {
            // Self-healing (feedback 2026-07-19): the session may have been deleted with its
            // workspace. Every hook event carries cwd — recreate instead of dropping updates.
            if (workspaceArg.Length == 0)
                return ($"unknown session id {sessionId} (was 'session start' called?)", false);
            var host = ResolveOrCreateWorkspace(workspaceArg, out string? err);
            if (host == null) return (err!, false);
            var recreated = new SessionViewModel
            {
                SessionId = sessionId,
                Status = status,
                StartedAt = DateTime.Now,
            };
            ApplyHookInfo(recreated, info);
            LearnTranscriptDir(host, info);
            host.Sessions.Insert(0, recreated);
            AfterSessionChange(host);
            return ($"session {sessionId} recreated in \"{host.DisplayTitle}\" [{SessionStatusNames.ToName(status)}]", true);
        }
        var (ws, session) = found;
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
        var prev = session.Status;
        // `he` is the one status a hook may not overwrite with a quieter one. It is set from
        // inside the last turn of the session, so the Stop hook that ends that very turn
        // arrives right behind it and would put the card back to `done` a second later.
        // Real activity (working / waiting / error) does clear it — the session came back
        // to life, and the card has to say so.
        bool keepHe = prev == SessionStatus.He && status is SessionStatus.Done or SessionStatus.Idle;
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
        AfterSessionChange(ws);
        return keepHe
            ? ($"session {sessionId} stays he (ignored {SessionStatusNames.ToName(status)})", true)
            : ($"session {sessionId} → {SessionStatusNames.ToName(status)}", true);
    }

    /// <summary>A session whose transcript file was never written and that has no
    /// titles — an empty conversation VSCode spins up eagerly (issue 2026-07-26). Nothing
    /// to display or resume, so on close it is dropped rather than archived.</summary>
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
        ApplyHookInfo(session, info);
        LearnTranscriptDir(ws, info);
        if (NeverMaterialized(session))
        {
            LogService.Info("status", $"session={sessionId} ended (never materialized — removed)");
            ws.Sessions.Remove(session);
            AfterSessionChange(ws);
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
        AfterSessionChange(ws);
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

    private void AfterSessionChange(WorkspaceViewModel ws)
    {
        ws.RefreshSessionVisibility();
        SortSessions(ws);
        SortWorkspaces();
        RefreshBlinkAndSummary();
        QueueSave();
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
        FocusWorkspace(ws);

        // Resume-by-id only works when the transcript still exists under the workspace's
        // current project slug; otherwise Claude Code silently opens a NEW conversation
        // (issue 2026-07-19 — e.g. sessions from before a folder rename). Don't send.
        if (!CanResume(ws, session))
        {
            SetStatus($"\"{session.DisplayTitle}\" — session file not found (did the project move or get renamed?); opening it would start a new conversation, so it was cancelled");
            return;
        }
        var (sent, _) = OpenSessionInVscode(ws, session);
        if (sent) SetStatus($"Opening the session in VSCode: {session.DisplayTitle}");
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
        if (isNew)
            LogService.Info("vscode", $"connected pid={conn.Pid} ws=\"{conn.WorkspacePath}\"");
        if (conn.WorkspacePath.Length == 0) return;

        if (Vm.FindByPath(conn.WorkspacePath) is { } ws)
        {
            // The extension is the fresher branch source (event-driven vs our 10s poll).
            if (!string.IsNullOrEmpty(sync.Branch)) ws.Branch = sync.Branch;
            var labels = sync.Tabs.Select(t => t.Label).ToList();
            ws.SetClaudeTabs(labels);
            ws.ActiveClaudeTabLabel = sync.Focused ? sync.Tabs.FirstOrDefault(t => t.Active)?.Label : null;
            LogService.Debug("sync", $"ws=\"{ws.DisplayTitle}\" focused={sync.Focused}" +
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
            _pendingOpens.Remove(norm);
            if (DateTime.Now - pending.At < PendingOpenTtl)
                conn.TrySend(pending.SessionId is { } sid
                    ? new { Cmd = "openSession", SessionId = (string?)sid, Prompt = (string?)null, Maximize = Vm.OpenSessionMaximized }
                    : new { Cmd = "newSession", SessionId = (string?)null, Prompt = pending.Prompt, Maximize = Vm.OpenSessionMaximized });
        }
    }

    private void OnVscodeClosed(VscodeConnection conn)
    {
        LogService.Info("vscode", $"disconnected pid={conn.Pid} ws=\"{conn.WorkspacePath}\"");
        _connectors.Remove(conn);
        if (conn.WorkspacePath.Length > 0 &&
            Vm.FindByPath(conn.WorkspacePath) is { } ws && FindConnector(ws) == null)
        {
            ws.SetClaudeTabs(new List<string>());
            ws.ActiveClaudeTabLabel = null;
            foreach (var s in ws.Sessions) s.OpenAsTab = false;
        }
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
        foreach (var s in ws.Sessions)
        {
            string? matched = null;
            foreach (var label in ws.ClaudeTabLabels)
                if (MatchTabLabel(label, s) is { } m) { matched = m; break; }
            s.OpenAsTab = matched != null;
            // Adopt the tab's own text as the card title (request 2026-07-20). Kept when
            // the tab closes — a title that changes on tab close is worse than a stale one.
            if (matched != null) s.MatchedTabLabel = matched;
        }

        // Auto-acknowledge the session whose tab the user is looking at.
        if (ActiveTabSession(ws) is not { } target || target.Acknowledged) return false;
        string? active = ws.ActiveClaudeTabLabel;
        LogService.Info("ack", $"path=correlation session={target.SessionId} label=\"{active}\"" +
                               $" match=\"{(active != null ? MatchTabLabel(active, target) : null)}\"");
        target.Acknowledged = true;
        return true;
    }

    private VscodeConnection? FindConnector(WorkspaceViewModel ws)
    {
        if (ws.Path.Length == 0) return null;
        string norm = WorkspaceMetadata.NormalizePath(ws.Path);
        return _connectors.LastOrDefault(c => c.WorkspacePath.Length > 0 &&
            WorkspaceMetadata.NormalizePath(c.WorkspacePath) == norm);
    }

    private int ConnectorCount(WorkspaceViewModel ws)
    {
        if (ws.Path.Length == 0) return 0;
        string norm = WorkspaceMetadata.NormalizePath(ws.Path);
        return _connectors.Count(c => c.WorkspacePath.Length > 0 &&
            WorkspaceMetadata.NormalizePath(c.WorkspacePath) == norm);
    }

    /// <summary>Open/resume the session's tab in VSCode. Without a live connector the request
    /// is parked; it's flushed when the extension connects (VSCode may still be launching).</summary>
    public (bool, string) OpenSessionInVscode(WorkspaceViewModel ws, SessionViewModel session)
    {
        var conn = FindConnector(ws);
        if (conn == null)
        {
            if (ws.Path.Length > 0)
                _pendingOpens[WorkspaceMetadata.NormalizePath(ws.Path)] = (session.SessionId, null, DateTime.Now);
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
    public (bool, string) NewSessionInVscode(WorkspaceViewModel ws, string? prompt = null)
    {
        FocusWorkspace(ws);
        var conn = FindConnector(ws);
        if (conn == null)
        {
            if (ws.Path.Length > 0)
                _pendingOpens[WorkspaceMetadata.NormalizePath(ws.Path)] = (null, prompt, DateTime.Now);
            SetStatus("VSCode is starting — the new session will open once the connector is up");
            return (false, "no VSCode connector yet — request queued");
        }
        if (!conn.TrySend(new { Cmd = "newSession", Prompt = prompt, Maximize = Vm.OpenSessionMaximized }))
        {
            _connectors.Remove(conn);
            return (false, "connector connection lost");
        }
        SetStatus($"Opening a new session in \"{ws.DisplayTitle}\"");
        return (true, "");
    }

    // ---- focus / pin / stage ----

    public (bool, string) FocusWorkspace(WorkspaceViewModel ws)
    {
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

        bool buried = Vm.WindowsNotifications && !Vm.AlwaysOnTop && Vm.ZoneMode == ZoneMode.Off;
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
        SessionStatus.Done => "finished",
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
        ShowHiddenToggle.IsChecked = Vm.ShowHidden;
        PinTopToggle.IsChecked = Vm.AlwaysOnTop;
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
