using System.Windows;
using MindAttic.Scheduler.Core;
using MindAttic.Scheduler.Wpf.ViewModels;

namespace MindAttic.Scheduler.Wpf;

/// <summary>The Scheduler jobs dashboard's main window (M.SCHD-T38).</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(new LocalTaskSchedulerProvider());
    }
}
