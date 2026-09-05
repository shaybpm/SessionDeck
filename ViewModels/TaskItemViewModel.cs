using System.Windows.Media;
using SessionDeck.Models;
using SessionDeck.Services;

namespace SessionDeck.ViewModels;

/// <summary>
/// One task from the external tasks file (T-0116). Immutable snapshot — the whole list is
/// rebuilt on every file reload, so no change notifications are needed.
/// </summary>
public sealed class TaskItemViewModel
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public string Status { get; init; } = "";
    public bool Pinned { get; init; }
    public string WorkspacePath { get; init; } = "";
    public IReadOnlyList<string> Sessions { get; init; } = Array.Empty<string>();
    public string Url { get; init; } = "";
    public string UrlLabel { get; init; } = "";
    /// <summary>This card's own launch phrase, or "" to use the document's template.</summary>
    public string SessionPrompt { get; init; } = "";

    public bool HasDescription => Description.Length > 0;
    public bool HasStatus => Status.Length > 0;
    public bool HasUrl => Url.Length > 0;
    public bool HasWorkspace => WorkspacePath.Length > 0;

    /// <summary>Search hit. Everything printed on the card counts, id included: the id is the
    /// agenda number, which is how a task is usually referred to and therefore the most likely
    /// thing to be typed. An empty query matches everything (no filter).</summary>
    public bool Matches(string query)
        => query.Length == 0
           || Id.Contains(query, StringComparison.OrdinalIgnoreCase)
           || Name.Contains(query, StringComparison.OrdinalIgnoreCase)
           || Status.Contains(query, StringComparison.OrdinalIgnoreCase)
           || Description.Contains(query, StringComparison.OrdinalIgnoreCase);
    /// <summary>The workspace/sessions button exists when there is anywhere to go.</summary>
    public bool HasTarget => HasWorkspace || Sessions.Count > 0;

    public string TargetButtonText => Sessions.Count > 0 ? $"▶ Sessions ({Sessions.Count})" : "▶ New session";

    /// <summary>What the url button says. The producer's own wording wins, because only it
    /// knows whether the link is a document or a level of the tree.</summary>
    public string UrlButtonText => UrlLabel.Length > 0 ? UrlLabel : "🔗 Open task";

    /// <summary>Status color resolved from the document's statusColors map at build time;
    /// a status without a color (or no status) renders neutral.</summary>
    public Brush StatusBrush { get; init; } = NeutralBrush;

    public static readonly Brush NeutralBrush = SessionViewModel.MakeBrush("#6E6E6E");

    public string TooltipText
    {
        get
        {
            var lines = new List<string> { $"‏{Id} — {Name}" };
            if (HasStatus) lines.Add($"Status: {Status}");
            if (HasDescription) lines.Add(Description);
            if (Pinned) lines.Add("📌 Pinned");
            return string.Join(Environment.NewLine, lines);
        }
    }

    public static TaskItemViewModel From(TaskEntry entry, IReadOnlyDictionary<string, string> statusColors)
    {
        string status = entry.Status?.Trim() ?? "";
        Brush brush = NeutralBrush;
        if (status.Length > 0 && statusColors.TryGetValue(status, out var colorName) &&
            ColorUtil.TryParse(colorName, out _))
            brush = SessionViewModel.MakeBrush(colorName);
        return new TaskItemViewModel
        {
            Id = entry.Id!.Trim(),
            Name = entry.Name!.Trim(),
            Description = entry.Description?.Trim() ?? "",
            Status = status,
            Pinned = entry.Pinned,
            WorkspacePath = entry.Workspace?.Trim() ?? "",
            Sessions = entry.Sessions.Where(s => !string.IsNullOrWhiteSpace(s)).ToList(),
            Url = entry.Url?.Trim() ?? "",
            UrlLabel = entry.UrlLabel?.Trim() ?? "",
            SessionPrompt = entry.SessionPrompt?.Trim() ?? "",
            StatusBrush = brush,
        };
    }
}
