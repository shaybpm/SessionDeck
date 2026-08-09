using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    /// sessions (live → focus, dead → real resume).</summary>
    public void HandleTaskActivate(TaskItemViewModel task, FrameworkElement anchor)
    {
        if (!task.HasTarget)
        {
            SetStatus($"\"{task.Name}\" — the task has no workspace and no sessions");
            return;
        }
        if (task.Sessions.Count == 0)
        {
            StartTaskNewSession(task);
            return;
        }

        // LTR menu with English items; each item's own direction still follows its header
        // (App.xaml MenuItem style), so a Hebrew session title renders RTL inside it.
        var menu = new ContextMenu();
        var newItem = new MenuItem { Header = "+ New session" };
        newItem.Click += (_, _) => StartTaskNewSession(task);
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
                _pendingOpens[WorkspaceMetadata.NormalizePath(ws.Path)] = (sessionId, null, DateTime.Now);
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
            _pendingOpens[WorkspaceMetadata.NormalizePath(task.WorkspacePath)] = (sessionId, null, DateTime.Now);
            SetStatus($"Launching VSCode for \"{task.Name}\" — the session will start once the connector is up");
        }
        else
            SetStatus($"\"{task.Name}\" — launching VSCode failed");
    }

    private void StartTaskNewSession(TaskItemViewModel task)
    {
        if (!task.HasWorkspace)
        {
            SetStatus($"\"{task.Name}\" — the task has no workspace to open a session in");
            return;
        }
        string? prompt = BuildNewSessionPrompt(task);
        if (Vm.FindByPath(task.WorkspacePath) is { } ws)
        {
            NewSessionInVscode(ws, prompt);
            return;
        }
        if (!Directory.Exists(task.WorkspacePath))
        {
            SetStatus($"\"{task.Name}\" — the workspace folder does not exist: {task.WorkspacePath}");
            return;
        }
        if (WindowActions.LaunchVsCode(task.WorkspacePath))
        {
            _pendingOpens[WorkspaceMetadata.NormalizePath(task.WorkspacePath)] = (null, prompt, DateTime.Now);
            SetStatus($"Launching VSCode for \"{task.Name}\" — a new session will open once the connector is up");
        }
        else
            SetStatus($"\"{task.Name}\" — launching VSCode failed");
    }

    /// <summary>newSessionPrompt from the file's envelope with &lt;id&gt;/&lt;name&gt;
    /// filled in; null (no template) = the session opens empty. Applies ONLY to new
    /// sessions opened from a task (T-0116).</summary>
    private string? BuildNewSessionPrompt(TaskItemViewModel task)
        => Vm.TasksPanel.NewSessionPrompt?
            .Replace("<id>", task.Id, StringComparison.OrdinalIgnoreCase)
            .Replace("<name>", task.Name, StringComparison.OrdinalIgnoreCase);
}
