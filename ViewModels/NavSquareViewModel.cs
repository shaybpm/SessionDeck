using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using SessionDeck.Models;
using SessionDeck.Services;

namespace SessionDeck.ViewModels;

/// <summary>
/// One square of the tasks page's navigation grid (#4.13.19). The grid is the map the
/// one-level-at-a-time panel cannot show on its own: column A is the top level, column B is
/// the selected top-level item's direct children.
///
/// A square says two things at once, so it uses both of the things a box has: the FILL is
/// structure (a parent that can be opened, versus a unit of work), the BORDER is status, in
/// the very colours the cards already use. Giving both to one channel would have meant
/// dropping one of them.
/// </summary>
public sealed class NavSquareViewModel : INotifyPropertyChanged
{
    public required string Label { get; init; }
    public required string Number { get; init; }
    public required string Name { get; init; }
    public string Status { get; init; } = "";
    public bool IsParent { get; init; }
    public string Url { get; init; } = "";
    public Brush StatusBrush { get; init; } = TaskItemViewModel.NeutralBrush;
    public IReadOnlyList<NavSquareViewModel> Children { get; init; } = Array.Empty<NavSquareViewModel>();

    private bool _selected;
    /// <summary>Column A: the previewed root. Column B: the level currently on screen.</summary>
    public bool Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; Raise(); Raise(nameof(FillBrush)); } }
    }

    public Brush FillBrush => _selected ? SelectedFill : IsParent ? ParentFill : LeafFill;

    private static readonly Brush ParentFill = SessionViewModel.MakeBrush("#576076");
    private static readonly Brush LeafFill = SessionViewModel.MakeBrush("#1A1A1A");
    private static readonly Brush SelectedFill = SessionViewModel.MakeBrush("#8FA0C0");

    /// <summary>Name and full number, nothing else — the card already prints the rest, and a
    /// tooltip the size of a card is what makes a grid unreadable (Shay, 07-08-2026). Two
    /// lines rather than one so the Hebrew name and the LTR number never share a line.</summary>
    public string TooltipText => Name + Environment.NewLine + Number;

    public static NavSquareViewModel From(NavEntry entry, IReadOnlyDictionary<string, string> statusColors)
    {
        string status = entry.Status?.Trim() ?? "";
        Brush brush = TaskItemViewModel.NeutralBrush;
        if (status.Length > 0 && statusColors.TryGetValue(status, out var colorName) &&
            ColorUtil.TryParse(colorName, out _))
            brush = SessionViewModel.MakeBrush(colorName);
        string number = entry.Number?.Trim() ?? "";
        return new NavSquareViewModel
        {
            Label = entry.Label?.Trim() is { Length: > 0 } label ? label : number,
            Number = number,
            Name = entry.Name?.Trim() ?? "",
            Status = status,
            IsParent = entry.IsParent,
            Url = entry.Url?.Trim() ?? "",
            StatusBrush = brush,
            Children = entry.Children.Select(c => From(c, statusColors)).ToList(),
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
