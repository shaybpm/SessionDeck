using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SessionDeck.ViewModels;

namespace SessionDeck;

/// <summary>The tasks page (T-0116); actions delegate to MainWindow.</summary>
public partial class TasksPageView : UserControl
{
    private MainWindow? Owner => Window.GetWindow(this) as MainWindow;

    public TasksPageView() => InitializeComponent();

    private void Back_Click(object sender, RoutedEventArgs e) => Owner?.CloseTasksPage();

    private void Workspace_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkspaceViewModel ws })
        {
            Owner?.FocusWorkspace(ws);
            e.Handled = true;
        }
    }

    /// <summary>Every square of the navigation grid navigates — the home square to the list of
    /// all projects, a top-level square into that project, a child square into itself (Shay,
    /// 10-08-2026: "so I can move quickly between the task pages"). Column A used to only
    /// preview, lighting column B without moving the list; he asked for the grid to be how he
    /// gets around, so the asymmetry is gone.</summary>
    private void NavSquare_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NavSquareViewModel square })
        {
            Owner?.OpenNavTarget(square);
            e.Handled = true;
        }
    }

    private void Session_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SessionViewModel session } &&
            Owner is { } owner && owner.Vm.FindSession(session.SessionId) is { } found)
        {
            owner.HandleSessionClick(found.Item1, found.Item2);
            e.Handled = true;
        }
    }
}
