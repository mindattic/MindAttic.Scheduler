using System.Collections.ObjectModel;
using System.Windows.Input;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Scheduler.Core;

namespace MindAttic.Scheduler.Wpf.ViewModels;

/// <summary>
/// The dashboard's main view model (M.SCHD-T38): the job grid, refresh, and opening the schedule
/// editor; plus (M.SCHD-T42) each job's last result and usual duration from the run log, the
/// Optimize preview, and the Last night view. Every database read runs off the UI thread, and an
/// unreachable database only leaves those columns blank and says so in the status bar.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly IJobProvider provider;
    private string? error;
    private bool isRefreshing;
    private RelayCommand? refreshCommand;
    private RelayCommand? editScheduleCommand;
    private AsyncRelayCommand? optimizeCommand;
    private RelayCommand? lastNightCommand;
    private string historyStatus = "";
    private bool isPlanning;
    private int historyGeneration;
    private LastNightWindow? lastNightWindow;

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

    /// <summary>True while Optimize is planning (reading jobs.json and the run log) before its preview opens.</summary>
    public bool IsPlanning { get => isPlanning; private set => Set(ref isPlanning, value); }

    /// <summary>Where the run history comes from, or why it couldn't be read.</summary>
    public string HistoryStatus { get => historyStatus; private set => Set(ref historyStatus, value); }
    public bool IsRefreshing { get => isRefreshing; private set => Set(ref isRefreshing, value); }

    public ICommand RefreshCommand => refreshCommand ??= new RelayCommand(Refresh, () => !IsRefreshing);

    public ICommand EditScheduleCommand => editScheduleCommand ??= new RelayCommand(p =>
    {
        if (p is JobRowViewModel row) OpenScheduleEditor(row);
    });

    /// <summary>Plans the night from scratch and shows the plan for the owner to apply or cancel.</summary>
    public ICommand OptimizeCommand => optimizeCommand ??= new AsyncRelayCommand(OptimizeAsync,
        onError: ex => Error = $"Optimize failed: {Display.Reason(ex)}");

    /// <summary>Opens (or brings forward) the Last night view.</summary>
    public ICommand LastNightCommand => lastNightCommand ??= new RelayCommand(OpenLastNight);

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
        _ = LoadHistoryAsync();
    }

    /// <summary>Fills the Last result and Usual duration columns from the run log, off the UI thread.</summary>
    private async Task LoadHistoryAsync()
    {
        var generation = ++historyGeneration;
        HistoryStatus = "Run history: loading…";
        string where = "the run log";
        try
        {
            var (summaries, describe) = await Task.Run(() =>
            {
                var db = SchedulerDb.FromEnvironment();
                where = db.Describe();
                return (new RunLog(db).Summaries(), where);
            });
            if (generation != historyGeneration) return;   // a newer refresh owns the grid now.
            foreach (var row in Jobs) row.History = summaries.TryGetValue(row.Name, out var s) ? s : null;
            HistoryStatus = $"Run history: {describe}";
        }
        catch (Exception ex)
        {
            if (generation != historyGeneration) return;
            foreach (var row in Jobs) row.History = null;
            HistoryStatus = $"Run history unavailable ({where}): {Display.Reason(ex)}";
        }
    }

    private async Task OptimizeAsync()
    {
        Error = null;
        IsPlanning = true;
        Arranger arranger;
        ArrangeResult result;
        try
        {
            (arranger, result) = await Task.Run(() =>
            {
                var a = new Arranger(db: SchedulerDb.FromEnvironment());
                return (a, a.Plan(optimize: true));
            });
        }
        finally { IsPlanning = false; }

        var preview = new OptimizePreviewViewModel(arranger, result);
        var window = new OptimizePreviewWindow(preview) { Owner = System.Windows.Application.Current.MainWindow };
        if (window.ShowDialog() == true) Refresh();
    }

    private void OpenLastNight()
    {
        if (lastNightWindow is { IsLoaded: true })
        {
            lastNightWindow.Activate();
            return;
        }
        lastNightWindow = new LastNightWindow(new LastNightViewModel()) { Owner = System.Windows.Application.Current.MainWindow };
        lastNightWindow.Closed += (_, _) => lastNightWindow = null;
        lastNightWindow.Show();
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
