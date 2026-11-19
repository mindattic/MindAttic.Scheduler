using System.Windows;
using MindAttic.Scheduler.Wpf.ViewModels;

namespace MindAttic.Scheduler.Wpf;

/// <summary>The plain-English/cron schedule editor dialog (M.SCHD-T38).</summary>
public partial class ScheduleEditorWindow : Window
{
    private readonly ScheduleEditorViewModel viewModel;

    public ScheduleEditorWindow(ScheduleEditorViewModel viewModel)
    {
        this.viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // Stay open (SaveError is shown) rather than save an entry install.ps1 would reject.
        if (viewModel.TrySave()) DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
