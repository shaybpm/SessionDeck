using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SessionDeck.Models;
using SessionDeck.Services;
using SessionDeck.ViewModels;

namespace SessionDeck;

/// <summary>
/// Tasks-panel feature (T-0116): the external tasks file, its watcher, the tasks page
/// navigation and the task→workspace/session click flows. Everything here is inert until
/// a file path is configured (strict opt-in).
/// </summary>
public partial class MainWindow
{
    private TasksFileWatcher? _tasksWatcher;

    /// <summary>(Re)apply the configured path: tear down the old watcher, reset all task
    /// state, and start watching the new file. null/empty turns the feature off.
    /// Public — shared by the ⚙ dialog and the `tasks` CLI command.</summary>
    public void ApplyTasksFile(string? path)
    {
        _tasksWatcher?.Dispose();
        _tasksWatcher = null;
        Vm.TasksFilePath = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        bool enabled = Vm.TasksFilePath != null;
        Vm.TasksPanel.Clear();
        Vm.TasksPanel.Enabled = enabled;
        RefreshWorkspaceTaskLinks();
        if (!enabled)
        {
            CloseTasksPage();
            return;
        }
        _tasksWatcher = new TasksFileWatcher(Vm.TasksFilePath!, OnTasksLoaded);
    }

    private void OnTasksLoaded(TasksLoadResult result)
    {
        Vm.TasksPanel.Apply(result);
        RefreshWorkspaceTaskLinks();
        if (result.FileError is { } err)
        {
            LogService.Info("tasks", $"load failed: {err}");
            SetStatus("Tasks file: " + err);
        }
        else
        {
            int count = Vm.TasksPanel.PinnedTasks.Count + Vm.TasksPanel.OtherTasks.Count;
            LogService.Debug("tasks", $"loaded {count} tasks, {result.RecordWarnings.Count} skipped");
        }
    }

    /// <summary>Match tasks to workspace cards by path. Also keeps TasksEnabled in sync on
    /// cards added after the feature was configured (called from CollectionChanged).</summary>
    private void RefreshWorkspaceTaskLinks()
    {
        bool enabled = Vm.TasksPanel.Enabled;
        var byPath = Vm.TasksPanel.AllTasks
            .Where(t => t.HasWorkspace)
            .GroupBy(t => WorkspaceMetadata.NormalizePath(t.WorkspacePath))
            .ToDictionary(g => g.Key, g => g.ToList());
        foreach (var ws in Vm.Workspaces)
        {
            ws.TasksEnabled = enabled;
            ws.WorkspaceTasks.Clear();
            if (!enabled || ws.Path.Length == 0) { ws.TasksExpanded = false; continue; }
            if (byPath.TryGetValue(WorkspaceMetadata.NormalizePath(ws.Path), out var tasks))
                foreach (var t in tasks) ws.WorkspaceTasks.Add(t);
            if (ws.WorkspaceTasks.Count == 0) ws.TasksExpanded = false;
        }
    }

    // ---- page navigation ----

    public void ShowTasksPage()
    {
        if (!Vm.TasksPanel.Enabled) return;
        Vm.TasksPanel.PageOpen = true;
        DeckView.Visibility = Visibility.Collapsed;
        TasksPage.Visibility = Visibility.Visible;
        // The permanent search row now follows the page instead of being locked out by it
        // (the T-0116 mutual exclusion was removed 07-08-2026).
        UpdateSearchScope();
    }

    public void CloseTasksPage()
    {
        Vm.TasksPanel.PageOpen = false;
        TasksPage.Visibility = Visibility.Collapsed;
        DeckView.Visibility = Visibility.Visible;
        UpdateSearchScope();
    }

    /// <summary>Escape, in one place. It has two jobs now that the search row is permanent,
    /// and the order matters: clear a query first, close the page only on an empty box — so a
    /// mistyped search never costs the user the view they were searching in. Both live here
    /// rather than on the TextBox because a window-level PreviewKeyDown tunnels down BEFORE
    /// the box's own KeyDown ever bubbles, and would otherwise always win.</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (SearchBox.Text.Length > 0)
        {
            SearchBox.Clear();
            e.Handled = true;
        }
        else if (Vm.TasksPanel.PageOpen)
        {
            CloseTasksPage();
            e.Handled = true;
        }
    }

    private void TasksFileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TasksFileDialog(Vm.TasksFilePath) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        ApplyTasksFile(dialog.PathText);
        QueueSave();
        SetStatus(Vm.TasksFilePath == null ? "The tasks panel is off" : $"Tasks file: {Vm.TasksFilePath}");
    }

    // ---- task activation (T-0116 click behavior) ----

    /// <summary>Click on a task square / its sessions button: no sessions → straight to a
    /// new session in its workspace; otherwise a dropdown of "new session" + the task's
    /// sessions (live → focus, dead → real resume).
    ///
    /// <paramref name="fast"/> only ever reaches a NEW session — resuming an existing one
    /// re-enters a conversation that already chose its own opening.
    ///
    /// The modifier is read HERE rather than at each call site, and the coordinator check
    /// lives here too. Every way into a task — its card, the strip, the nav grid, the Run box
    /// — funnels through this method, so this is the only place where a modifier is guaranteed
    /// to be seen. Asking each call site to pass it is what made Ctrl work in the Run box and
    /// nowhere else (Shay, 13-08-2026).
    ///
    /// Since 04-09-2026 the modifier picks the VSCode instance — the Claude ACCOUNT — the new
    /// session opens in (<see cref="MainWindow.GroupForModifiers"/>). It used to ask for the
    /// short form of a coordinator session; that meaning moved onto the word "fast"/"מהיר",
    /// which is the only way in now, because Shay never used the modifier for it and wanted it
    /// for the account instead.</summary>
    public void HandleTaskActivate(TaskItemViewModel task, FrameworkElement anchor, bool fast = false,
                                   SessionGroupConfig? group = null)
    {
        bool requested = fast;
        bool honored = requested && IsCoordinatorNumber(task.Id);
        // A group named in the Run box wins over the keys, because it was typed on purpose;
        // with neither, the card's no-modifier group (if it has one) is what a plain click means.
        group ??= GroupForTask(task);

        if (!task.HasTarget)
        {
            SetStatus($"\"{task.Name}\" — the task has no workspace and no sessions");
            return;
        }
        if (task.Sessions.Count == 0)
        {
            StartTaskNewSession(task, honored, group);
            WarnIfFastIgnored(task, requested, honored);
            return;
        }

        // LTR menu with English items; each item's own direction still follows its header
        // (App.xaml MenuItem style), so a Hebrew session title renders RTL inside it.
        var menu = new ContextMenu();
        string groupSuffix = group == null ? "" : $" · {group.Name}";
        var newItem = new MenuItem
        {
            Header = (honored ? "+ New session (fast)" : "+ New session") + groupSuffix,
        };
        // The modifier counts whether it was held when the menu opened or when the item is
        // picked: the menu is a pause in the middle of one gesture, and either end of it
        // states the intent.
        newItem.Click += (_, _) => StartTaskNewSession(task, honored, GroupForTask(task) ?? group);
        newItem.IsEnabled = task.HasWorkspace;
        menu.Items.Add(newItem);
        menu.Items.Add(new Separator());
        foreach (string sid in task.Sessions)
        {
            var found = Vm.FindSession(sid);
            var session = found?.Item2;
            bool live = session is { Closed: false, Phantom: false };
            string title = session?.DisplayTitle
                           ?? "session " + (sid.Length > 8 ? sid[..8] : sid);
            var item = new MenuItem
            {
                Header = (live ? "🟢 " : "▶ ") + title,
                ToolTip = live ? "Live session — switch to it" : "Session not running — reopen it (resume)",
            };
            string capturedSid = sid;
            item.Click += (_, _) => OpenTaskSession(task, capturedSid);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
        WarnIfFastIgnored(task, requested, honored);
    }

    /// <summary>Say when a fast request was dropped. It still opens the task — a stray Ctrl
    /// must not cost a launch — but silence would leave the modifier looking broken, which is
    /// exactly how this reached Shay in the first place.</summary>
    private void WarnIfFastIgnored(TaskItemViewModel task, bool requested, bool honored)
    {
        if (requested && !honored)
            SetStatus($"{task.Id} is not a coordinator — opened normally; the fast form is a coordinator's only");
    }

    /// <summary>A click on a column-B square: do what clicking that item's CARD would do.
    ///
    /// A parent opens its own level, exactly like the card's drill-in link. A leaf has no
    /// level of its own, so if its card is on screen it gets the card's click (a session);
    /// otherwise the click walks to the level the card lives on, which is the only sense in
    /// which a leaf can be "entered" at all.</summary>
    public void OpenNavTarget(NavSquareViewModel square)
    {
        if (!square.IsParent &&
            Vm.TasksPanel.AllTasks.FirstOrDefault(t => t.Id == square.Number) is { } onScreen)
        {
            HandleTaskActivate(onScreen, TasksPage);
            return;
        }
        if (square.Url.Length == 0)
        {
            SetStatus($"{square.Number} — nowhere to navigate to");
            return;
        }
        OpenUrl(square.Url, square.Number);
    }

    public void OpenTaskUrl(TaskItemViewModel task)
    {
        if (task.HasUrl) OpenUrl(task.Url, task.Name);
    }

    private void OpenUrl(string url, string what)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"Opening the link failed ({what}): {ex.Message}");
        }
    }

    /// <summary>Open a session from a task: on the deck → the regular click flow (focus /
    /// resume); unknown to the deck → real resume by id, launching VSCode on the task's
    /// workspace if needed and parking the open request until the connector appears.</summary>
    private void OpenTaskSession(TaskItemViewModel task, string sessionId)
    {
        if (Vm.FindSession(sessionId) is { } found)
        {
            HandleSessionClick(found.Item1, found.Item2);
            return;
        }
        if (!task.HasWorkspace)
        {
            SetStatus($"\"{task.Name}\" — unknown session, and the task has no workspace to open it in");
            return;
        }
        if (Vm.FindByPath(task.WorkspacePath) is { } ws)
        {
            FocusWorkspace(ws);
            var conn = FindConnector(ws);
            if (conn == null)
                _pendingOpens[WorkspaceMetadata.NormalizePath(ws.Path)] = (sessionId, null, null, DateTime.Now);
            else if (!conn.TrySend(new { Cmd = "openSession", SessionId = sessionId, Maximize = Vm.OpenSessionMaximized }))
                _connectors.Remove(conn);
            SetStatus($"Opening a session of \"{task.Name}\" in VSCode…");
            return;
        }
        if (!Directory.Exists(task.WorkspacePath))
        {
            SetStatus($"\"{task.Name}\" — the workspace folder does not exist: {task.WorkspacePath}");
            return;
        }
        // Workspace isn't on the deck — launch VSCode directly; the extension connects and
        // the parked request resumes the session (no card is auto-added).
        if (WindowActions.LaunchVsCode(task.WorkspacePath))
        {
            _pendingOpens[WorkspaceMetadata.NormalizePath(task.WorkspacePath)] = (sessionId, null, null, DateTime.Now);
            SetStatus($"Launching VSCode for \"{task.Name}\" — the session will start once the connector is up");
        }
        else
            SetStatus($"\"{task.Name}\" — launching VSCode failed");
    }

    /// <summary>The session group the keys held right now ask for, on the card this task
    /// would open in. Null on every card that has no groups, which is all of them but the
    /// .claude management card.</summary>
    private SessionGroupConfig? GroupForTask(TaskItemViewModel task)
        => task.HasWorkspace && Vm.FindByPath(task.WorkspacePath) is { } ws
               ? GroupForModifiers(ws) : null;

    private void StartTaskNewSession(TaskItemViewModel task, bool fast = false,
                                     SessionGroupConfig? group = null)
    {
        if (!task.HasWorkspace)
        {
            SetStatus($"\"{task.Name}\" — the task has no workspace to open a session in");
            return;
        }
        string? prompt = BuildNewSessionPrompt(task, fast);
        if (Vm.FindByPath(task.WorkspacePath) is { } ws)
        {
            NewSessionInVscode(ws, prompt, group);
            return;
        }
        if (!Directory.Exists(task.WorkspacePath))
        {
            SetStatus($"\"{task.Name}\" — the workspace folder does not exist: {task.WorkspacePath}");
            return;
        }
        if (WindowActions.LaunchVsCode(task.WorkspacePath))
        {
            _pendingOpens[WorkspaceMetadata.NormalizePath(task.WorkspacePath)] = (null, prompt, null, DateTime.Now);
            SetStatus($"Launching VSCode for \"{task.Name}\" — a new session will open once the connector is up");
        }
        else
            SetStatus($"\"{task.Name}\" — launching VSCode failed");
    }

    /// <summary>newSessionPrompt from the file's envelope with &lt;id&gt;/&lt;name&gt;
    /// filled in; null (no template) = the session opens empty. Applies ONLY to new
    /// sessions opened from a task (T-0116).
    ///
    /// <paramref name="fast"/> asks for the short form of a coordinator session. The producer
    /// owns that wording too, so we prefer its newSessionPromptFast; without one we suffix the
    /// normal template, which keeps the modifier working on a tasks file written before the
    /// field existed (Shay, 13-08-2026).</summary>
    private string? BuildNewSessionPrompt(TaskItemViewModel task, bool fast = false)
    {
        // The card's own phrase wins. A document can hold cards of different KINDS — the
        // holdings view's cards are packages, which open with "holding session <slug>" — and
        // one template per document cannot serve both without opening a held item by number,
        // which the holding-session skill forbids outright.
        string? template = task.SessionPrompt.Length > 0
            ? task.SessionPrompt
            : Vm.TasksPanel.NewSessionPrompt;
        if (fast && task.SessionPrompt.Length == 0)
            template = Vm.TasksPanel.NewSessionPromptFast
                       ?? (template == null ? null : template + " --fast");
        return template?
            .Replace("<id>", task.Id, StringComparison.OrdinalIgnoreCase)
            .Replace("<name>", task.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Is this task number a coordinator's — the structural mark being a trailing
    /// ".0", or the bare "0" of the coordinator above all trees. The fast form is a property
    /// of how a COORDINATOR session opens, so it is refused on any other number rather than
    /// silently sent to a session that has no shorter mode to enter (Shay, 13-08-2026).</summary>
    public static bool IsCoordinatorNumber(string number)
    {
        number = number.Trim().TrimStart('#');
        return number == "0" || number.EndsWith(".0", StringComparison.Ordinal);
    }
}
