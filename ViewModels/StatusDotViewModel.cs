using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using SessionDeck.Services;

namespace SessionDeck.ViewModels;

/// <summary>
/// One dot in the status-bar summary (feature 2026-07-19): open sessions grouped by
/// (status, blinking), colored by the same StatusStyles mapping as the session borders.
/// Blinking groups share the BlinkEngine, so dots stay in phase with the card borders.
/// </summary>
public sealed class StatusDotViewModel : INotifyPropertyChanged, IBlinkable
{
    public SessionStatus Status { get; init; }
    public bool Blinking { get; init; }
    public int Count { get; init; }

    public string Tooltip =>
        $"{Count} sessions in status {SessionStatusNames.ToDisplay(Status)}" +
        (Blinking ? " (waiting to be acknowledged)" : "");

    // ---- IBlinkable ----

    public bool BlinkActive => Blinking;

    public int BlinkIntervalMs => SessionViewModel.ResolveStyle(Status).BlinkIntervalMs;

    private bool _altPhase;
    public bool AltPhase
    {
        get => _altPhase;
        set { if (_altPhase != value) { _altPhase = value; Raise(nameof(DotBrush)); } }
    }

    public Brush DotBrush
    {
        get
        {
            var style = SessionViewModel.ResolveStyle(Status);
            string color = Blinking && _altPhase ? style.AltColor ?? "black" : style.Color;
            return SessionViewModel.MakeBrush(color);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
