using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Scheduler.Core;

namespace MindAttic.Scheduler.Wpf.ViewModels;

/// <summary>One row of the jobs grid (M.SCHD-T38): a <see cref="JobStatus"/> plus the enabled
/// toggle's in-flight state and any error from the last attempt to change it.</summary>
public sealed class JobRowViewModel(JobStatus status, IJobProvider provider) : ObservableObject
{
    private JobStatus status = status;
    private bool isBusy;
    private string? error;
    private JobSummary? history;

    public JobStatus Status
    {
        get => status;
        private set
        {
            if (!Set(ref status, value)) return;
            Raise(nameof(Name)); Raise(nameof(Description)); Raise(nameof(ScheduleHuman));
            Raise(nameof(Enabled)); Raise(nameof(StateText)); Raise(nameof(NextRun)); Raise(nameof(LastRun));
            Raise(nameof(SourceText)); Raise(nameof(CanEditSchedule));
        }
    }

    public string Name => Status.Name;
    public string Description => Status.Description;
    public string ScheduleHuman => Status.ScheduleHuman;
    public string StateText => Status.State;
    public string SourceText => Status.IsManifest ? "Manifest" : $"Runtime ({Status.Owner})";
    public bool CanEditSchedule => Status.IsManifest;
    public string NextRun => FormatLocal(Status.NextRun);
    public string LastRun => FormatLocal(Status.LastRun);

    /// <summary>The job's run history (M.SCHD-T42), or null when it has none or the database couldn't be read.</summary>
    public JobSummary? History
    {
        get => history;
        set
        {
            if (!Set(ref history, value)) return;
            Raise(nameof(LastResult)); Raise(nameof(UsualDuration));
        }
    }

    /// <summary>"succeeded", "failed (exit N)", "running" or "abandoned"; blank with no runs.</summary>
    public string LastResult => History?.Last is { } last ? Display.Outcome(last) : "";

    /// <summary>The average of the last successful runs, e.g. "12m 30s"; blank with none.</summary>
    public string UsualDuration => History?.UsualDuration is { } d ? Display.Duration(d) : "";

    public bool IsBusy { get => isBusy; private set { if (Set(ref isBusy, value)) Raise(nameof(IsNotBusy)); } }
    public bool IsNotBusy => !IsBusy;
    public string? Error { get => error; private set => Set(ref error, value); }

    public bool Enabled
    {
        get => Status.Enabled;
        set
        {
            if (value == Status.Enabled || IsBusy) return;
            IsBusy = true;
            Error = null;
            try
            {
                provider.SetEnabled(Status, value);
                Status = Status with { Enabled = value };
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                Raise(nameof(Enabled));   // snap the toggle back; the change didn't take.
            }
            finally { IsBusy = false; }
        }
    }

    private static string FormatLocal(string? isoLocal) =>
        DateTime.TryParse(isoLocal, out var dt) ? dt.ToString("yyyy-MM-dd HH:mm") : "-";
}
