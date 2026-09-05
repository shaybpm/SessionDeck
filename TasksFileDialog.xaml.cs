using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SessionDeck;

/// <summary>
/// Path input for the external tasks file (T-0116). Empty = feature off. The path may
/// point at a file that doesn't exist yet — the watcher reports that as a visible error
/// and picks the file up when it appears — but a nonexistent FOLDER can't be watched, so
/// that case is blocked here.
/// </summary>
public partial class TasksFileDialog : Window
{
    public string PathText => PathBox.Text.Trim();

    /// <summary>The file contract, for the 📋 button — hand this to whatever produces the
    /// file (a script, Claude, ...). Keep in sync with TasksFileService/TasksDocument.</summary>
    private const string SpecText = """
        # The SessionDeck tasks file — the JSON contract (version 1)

        SessionDeck only reads the file (read-only) and refreshes automatically on every save.
        The producer (whatever writes the file) owns the content, the order and the colors.

        ## File structure (envelope)

        ```json
        {
          "version": 1,
          "generated": "2026-07-27T10:00:00+03:00",
          "statusColors": { "in-progress": "#4FC3F7", "ready": "green" },
          "newSessionPrompt": "Let's work on task <id> — <name>",
          "tasks": [
            {
              "id": "T-0042",
              "name": "Task name",
              "description": "Short description",
              "status": "in-progress",
              "pinned": true,
              "workspace": "D:\\BPM\\SessionDeck",
              "sessions": ["9d089f9a-058e-4afc-a60e-93979b772824"],
              "url": "obsidian://open?vault=taskdeck&file=..."
            }
          ]
        }
        ```

        ## Envelope fields

        - `version` — required, must be 1. Anything else = a visible error state.
        - `generated` — optional. ISO timestamp of when the file was produced; shown as a freshness hint.
        - `statusColors` — optional. status→color map: `#RRGGBB` or a name
          (red, green, orange, blue, gray, yellow, purple, cyan, magenta, white, black).
          A status with no color renders neutral. Color is data semantics — the producer's call.
        - `newSessionPrompt` — optional. Text template for a new session opened by clicking a task;
          `<id>` and `<name>` are replaced with the task's values. The text is left waiting in the
          input box (not submitted). Missing → a new session opens empty.

        ## Task fields

        - `id` — **required**. Unique identifier (string).
        - `name` — **required**. Task name.
        - `description` — optional.
        - `status` — optional. Free-form string; the color comes from statusColors.
        - `pinned` — optional (bool). Pinned tasks come first, with a marker and a separator line.
        - `workspace` — optional. **Full path** of the project folder; matching to a SessionDeck
          card is done by path. Without it there is no "open session" button.
        - `sessions` — optional. List of Claude Code session UUIDs (many-to-many).
        - `url` — optional. Link that opens the task (via ShellExecute, e.g. obsidian://...).
        - `sessionPrompt` — optional. This card's own launch phrase, same `<id>`/`<name>`
          placeholders, used instead of the envelope's `newSessionPrompt`. For a document whose
          cards are not all the same kind of thing (a list of packages rather than of tasks).

        ## Pointing SessionDeck at the file (CLI)

        ```
        SessionDeck.exe tasks --file "C:\path\to\tasks.json"   # set the path + turn the panel on
        SessionDeck.exe tasks                                  # show the current state
        SessionDeck.exe tasks --off                            # turn the panel off
        ```

        (or from the UI: ⚙ → "Tasks file (JSON)...")

        ## Behavior rules

        - An entry with no `id` or `name` is skipped with a visible warning; the rest still loads.
        - Unknown keys are ignored silently (forward-compat) — you may add fields of your own.
        - Display order = array order (after the pinned ones); sorting is the producer's job.
        - Broken JSON / missing file / unsupported version → a visible error state instead of a list.
        - Writing atomically (temp file + rename) is recommended; either way a locked file is retried briefly.
        """;

    public TasksFileDialog(string? current)
    {
        InitializeComponent();
        PathBox.Text = current ?? "";
        PathBox.Focus();
        PathBox.SelectAll();
        UpdatePreview();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select a tasks file (JSON)",
            Filter = "JSON|*.json|All files|*.*",
            CheckFileExists = false,
        };
        if (PathText.Length > 0)
        {
            try { dialog.InitialDirectory = Path.GetDirectoryName(PathText); } catch { }
        }
        if (dialog.ShowDialog(this) == true) PathBox.Text = dialog.FileName;
    }

    private void Path_Changed(object sender, TextChangedEventArgs e) => UpdatePreview();

    private (bool Ok, string Message) Validate()
    {
        string path = PathText;
        if (path.Length == 0) return (true, "The panel will be turned off");
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (dir == null || !Directory.Exists(dir))
                return (false, "The file's folder does not exist");
            return (true, File.Exists(path) ? "" : "The file does not exist yet — it will load once created");
        }
        catch
        {
            return (false, "Invalid path");
        }
    }

    private void UpdatePreview()
    {
        if (PreviewText == null || OkButton == null) return;   // during InitializeComponent
        var (ok, message) = Validate();
        PreviewText.Text = message;
        OkButton.IsEnabled = ok;
    }

    private void CopySpec_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(SpecText);
            PreviewText.Text = "Spec copied to the clipboard 📋";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            PreviewText.Text = "The clipboard is busy — try again";
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!Validate().Ok) return;
        DialogResult = true;
    }
}
