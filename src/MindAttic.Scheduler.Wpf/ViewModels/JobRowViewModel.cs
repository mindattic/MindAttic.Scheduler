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
