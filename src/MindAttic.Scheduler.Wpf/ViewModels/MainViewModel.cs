using System.Collections.ObjectModel;
using System.Windows.Input;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Scheduler.Core;

namespace MindAttic.Scheduler.Wpf.ViewModels;

/// <summary>The dashboard's main view model (M.SCHD-T38): the job grid, refresh, and opening the schedule editor.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly IJobProvider provider;
    private string? error;
    private bool isRefreshing;
    private RelayCommand? refreshCommand;
    private RelayCommand? editScheduleCommand;

    public MainViewModel(IJobProvider provider)
    {
        this.provider = provider;
        Jobs = [];
        Refresh();
    }

    public ObservableCollection<JobRowViewModel> Jobs { get; }
    public string SourceName => provider.DisplayName;

    public string? Error
    {
        get => error;
        private set
        {
            if (!Set(ref error, value)) return;
            Raise(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool IsRefreshing { get => isRefreshing; private set => Set(ref isRefreshing, value); }

    public ICommand RefreshCommand => refreshCommand ??= new RelayCommand(Refresh, () => !IsRefreshing);

    public ICommand EditScheduleCommand => editScheduleCommand ??= new RelayCommand(p =>
    {
        if (p is JobRowViewModel row) OpenScheduleEditor(row);
    });

    /// <summary>Re-reads every job from the provider and rebuilds the grid.</summary>
    public void Refresh()
    {
        IsRefreshing = true;
        Error = null;
        try
        {
            var statuses = provider.List().OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase);
            Jobs.Clear();
            foreach (var status in statuses) Jobs.Add(new JobRowViewModel(status, provider));
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsRefreshing = false; }
    }

    private void OpenScheduleEditor(JobRowViewModel row)
    {
        var editorViewModel = new ScheduleEditorViewModel(row.Status.ScheduleRaw);
        var window = new ScheduleEditorWindow(editorViewModel) { Owner = System.Windows.Application.Current.MainWindow };
        if (window.ShowDialog() != true || editorViewModel.Result is not { } schedule) return;

        try
        {
            provider.SetSchedule(row.Status, schedule);
            Refresh();
        }
        catch (Exception ex) { Error = ex.Message; }
    }
}
