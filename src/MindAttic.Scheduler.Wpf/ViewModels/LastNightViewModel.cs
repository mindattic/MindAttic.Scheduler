using System.Collections.ObjectModel;
using System.Windows.Input;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Scheduler.Core;

namespace MindAttic.Scheduler.Wpf.ViewModels;

/// <summary>One overnight job's row in the Last night view.</summary>
/// <param name="historyKnown">False when the run log couldn't be read: no run doesn't mean missed then.</param>
public sealed class NightRowViewModel(NightRow row, DateOnly night, bool historyKnown = true)
{
    public string Name => row.Name;
    public string Planned => row.Planned ?? "—";
    public string ActualStart => row.Run is { } r ? Time(r.StartedAt) : "";
    public string ActualEnd => row.Run is { } r ? (r.EndedAt is { } e ? Time(e) : "—") : "";
    public string LockWait => row.Run is { } r ? Display.Duration(r.Wait) : "";
    public string Outcome => historyKnown || row.Run is not null ? row.Display : "unknown (no run history)";

    /// <summary>Missed, failed, abandoned or overran: the rows that answer "no, it didn't all work".</summary>
    public bool IsProblem => historyKnown && IsBad(row);

    internal static bool IsBad(NightRow r) => r.Overran || r.Outcome is "missed" or "failed" or "abandoned";

    // HH:mm on the night itself; a run that lands on another date shows its date too.
    private string Time(DateTimeOffset at)
    {
        var local = at.LocalDateTime;
        return DateOnly.FromDateTime(local) == night ? local.ToString("HH:mm") : local.ToString("yyyy-MM-dd HH:mm");
    }
}

/// <summary>
/// The Last night view (M.SCHD-T42): each arranged overnight job's planned slot next to what
/// actually ran, for one night at a time. Everything is read off the UI thread.
/// </summary>
public sealed class LastNightViewModel : ObservableObject
{
    private DateOnly night = NightReport.DefaultNight(DateTimeOffset.Now);
    private string status = "";
    private bool isLoading;
    private int generation;
    private RelayCommand? previousCommand;
    private RelayCommand? nextCommand;
    private RelayCommand? refreshCommand;

    public LastNightViewModel() => _ = LoadAsync();

    public ObservableCollection<NightRowViewModel> Rows { get; } = [];

    public DateOnly Night
    {
        get => night;
        private set
        {
            if (!Set(ref night, value)) return;
            Raise(nameof(NightText));
            Raise(nameof(CanGoNext));
            _ = LoadAsync();
        }
    }

    /// <summary>"Night of Thu 2026-11-19 (00:00 to 08:00)".</summary>
    public string NightText => $"Night of {Night:ddd yyyy-MM-dd} (00:00 to 08:00)";

    public bool CanGoNext => Night < NightReport.DefaultNight(DateTimeOffset.Now);

    /// <summary>A one-line summary of the night, or why it couldn't be read.</summary>
    public string Status { get => status; private set => Set(ref status, value); }

    public bool IsLoading { get => isLoading; private set => Set(ref isLoading, value); }

    public ICommand PreviousCommand => previousCommand ??= new RelayCommand(() => Night = Night.AddDays(-1));
    public ICommand NextCommand => nextCommand ??= new RelayCommand(() => Night = Night.AddDays(1), () => CanGoNext);
    public ICommand RefreshCommand => refreshCommand ??= new RelayCommand(() => _ = LoadAsync(), () => !IsLoading);

    private async Task LoadAsync()
    {
        var mine = ++generation;
        var forNight = Night;
        IsLoading = true;
        Status = "Loading…";
        Rows.Clear();
        try
        {
            var (rows, dbNote) = await Task.Run(() => Build(forNight));
            if (mine != generation) return;
            foreach (var row in rows) Rows.Add(new NightRowViewModel(row, forNight, historyKnown: dbNote is null));
            Status = dbNote ?? Summarize(rows);
        }
        catch (Exception ex)
        {
            if (mine != generation) return;
            Status = $"Couldn't read the night: {Display.Reason(ex)}";
        }
        finally
        {
            if (mine == generation) IsLoading = false;
        }
    }

    // The arranged jobs and their slots always come from the local files; the runs need the
    // database, and without it every slot still shows (as pending or missed) with a note why.
    private static (IReadOnlyList<NightRow> Rows, string? DbNote) Build(DateOnly night)
    {
        var autoJobs = ManifestJob.Load(SchedulerPaths.Manifest).Where(j => j.IsAuto).Select(j => j.Name).ToList();
        IReadOnlyList<JobRunRecord> runs = [];
        string? note = null;
        var db = SchedulerDb.FromEnvironment();
        try
        {
            runs = new RunLog(db).Runs(since: NightReport.Span(night).From, limit: 1000);
        }
        catch (Exception ex)
        {
            note = $"Run history unavailable ({db.Describe()}): {Display.Reason(ex)}";
        }
        var rows = NightReport.Build(night, autoJobs, Arrangement.Load(), runs, DateTimeOffset.Now);
        return (rows, note);
    }

    private static string Summarize(IReadOnlyList<NightRow> rows)
    {
        if (rows.Count == 0) return "No overnight (auto-arranged) jobs.";
        var bad = rows.Count(NightRowViewModel.IsBad);
        var ok = rows.Count(r => r.Outcome == "succeeded" && !r.Overran);
        return bad == 0
            ? $"{ok} of {rows.Count} succeeded; nothing missed, failed or overran."
            : $"{bad} of {rows.Count} missed, failed or overran; {ok} succeeded.";
    }
}
