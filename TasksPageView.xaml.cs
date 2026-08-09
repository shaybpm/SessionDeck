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

    /// <summary>Both columns of the navigation grid arrive here, told apart by the collection
    /// the square belongs to rather than by two near-identical templates.
    ///
    /// Column A previews and nothing more: it lights column B and leaves the task list where
    /// it is. Column B navigates. The asymmetry is the point — navigating regenerates the file
    /// and moves the list, so if merely LOOKING for something in the tree also moved it, every
    /// glance would cost the user the place they were standing in.</summary>
    private void NavSquare_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NavSquareViewModel square } ||
            Owner is not { } owner) return;
        if (owner.Vm.TasksPanel.NavRoots.Contains(square))
            owner.Vm.TasksPanel.PreviewRoot(square);
        else
            owner.OpenNavTarget(square);
        e.Handled = true;
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
