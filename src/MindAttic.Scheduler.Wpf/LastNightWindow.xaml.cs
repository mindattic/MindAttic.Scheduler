using System.Windows;
using MindAttic.Scheduler.Wpf.ViewModels;

namespace MindAttic.Scheduler.Wpf;

/// <summary>The Last night view (M.SCHD-T42): did every overnight job run, and on time.</summary>
public partial class LastNightWindow : Window
{
    public LastNightWindow(LastNightViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
