using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SessionDeck.ViewModels;

namespace SessionDeck;

/// <summary>One task card (T-0116). All actions delegate to MainWindow — same
/// code-behind pattern as WorkspaceCardView.</summary>
public partial class TaskItemView : UserControl
{
    private TaskItemViewModel? Vm => DataContext as TaskItemViewModel;
    private MainWindow? Owner => Window.GetWindow(this) as MainWindow;

    public TaskItemView() => InitializeComponent();

    private void Target_Click(object sender, RoutedEventArgs e)
    {
        if (Vm != null) Owner?.HandleTaskActivate(Vm, TargetButton);
    }

    /// <summary>Anywhere on the card does the card's one obvious thing. Clicks that land on a
    /// button never reach here — ButtonBase marks the mouse-up handled — so the buttons keep
    /// their own behavior.
    ///
    /// A card with somewhere to run (a workspace or sessions) starts a session. A card with
    /// only a link is a container, not a unit of work — a producer uses those to group tasks —
    /// so it opens its link instead of reporting that it has nothing to run.</summary>
    private void Card_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Vm == null) return;
        if (!Vm.HasTarget && Vm.HasUrl) Owner?.OpenTaskUrl(Vm);
        else Owner?.HandleTaskActivate(Vm, this);
    }

    private void Url_Click(object sender, RoutedEventArgs e)
    {
        if (Vm != null) Owner?.OpenTaskUrl(Vm);
    }
}
