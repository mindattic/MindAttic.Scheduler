using System.ComponentModel;
using System.Windows;
using MindAttic.Scheduler.Wpf.ViewModels;

namespace MindAttic.Scheduler.Wpf;

/// <summary>The Optimize preview dialog (M.SCHD-T42): closes with OK once Apply has saved and installed.</summary>
public partial class OptimizePreviewWindow : Window
{
    private readonly OptimizePreviewViewModel viewModel;

    public OptimizePreviewWindow(OptimizePreviewViewModel viewModel)
    {
        this.viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.Applied += (_, _) => DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    // Closing mid-Apply would leave install.ps1 running unseen; wait for it to finish.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (viewModel.IsApplying && DialogResult != true) e.Cancel = true;
        base.OnClosing(e);
    }
}
