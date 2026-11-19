using System.Windows.Input;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Scheduler.Core;

namespace MindAttic.Scheduler.Wpf.ViewModels;

/// <summary>One proposed slot in the Optimize preview.</summary>
public sealed class PlannedSlotRow(PlannedSlot slot)
{
    public string Name => slot.Name;
    public string CurrentStart => slot.CurrentStart is { } c ? OvernightOptimizer.Format(c) : "—";
    public string ProposedStart => slot.StartText;
    public bool Moves => slot.CurrentStart != slot.Start;
    public string Duration => Display.Duration(slot.Duration);
    public string Source => slot.DurationSource;
    public string PredictedEnd => slot.EndText;
    public bool Overruns => slot.Overruns;
    public string OverrunText => slot.Overruns ? "overruns" : "";
}

/// <summary>
/// The Optimize preview (M.SCHD-T42): the from-scratch plan next to today's slots. Apply saves
/// arrangement.json and runs install.ps1 so Task Scheduler takes the new times; Cancel changes nothing.
/// </summary>
public sealed class OptimizePreviewViewModel : ObservableObject
{
    private readonly Arranger arranger;
    private readonly ArrangeResult result;
    private string applyError = "";
    private bool isApplying;
    private AsyncRelayCommand? applyCommand;

    public OptimizePreviewViewModel(Arranger arranger, ArrangeResult result)
    {
        this.arranger = arranger;
        this.result = result;
        Slots = result.Plan.Slots.Select(s => new PlannedSlotRow(s)).ToList();
        Pinned = result.Pinned
            .OrderBy(p => p.Start)
            .Select(p => $"{p.Name} at {OvernightOptimizer.Format(p.Start)} (about {Display.Duration(p.Duration)})")
            .ToList();
    }

    public IReadOnlyList<PlannedSlotRow> Slots { get; }

    /// <summary>The pinned jobs inside the window the plan went around, as text.</summary>
    public IReadOnlyList<string> Pinned { get; }

    public bool HasPinned => Pinned.Count > 0;
    public bool HasNoPinned => Pinned.Count == 0;
    public bool HasNoSlots => Slots.Count == 0;

    public bool Fits => result.Plan.Fits;

    /// <summary>"Everything finishes by 08:00 (predicted finish 05:42)." or how far over it runs.</summary>
    public string Summary
    {
        get
        {
            var plan = result.Plan;
            var end = OvernightOptimizer.Format(plan.WindowEnd);
            if (plan.Slots.Count == 0) return "There are no overnight (auto-arranged) jobs to place.";
            var finish = OvernightOptimizer.Format(plan.PredictedFinish);
            return plan.Fits
                ? $"Everything finishes by {end} (predicted finish {finish})."
                : $"Doesn't fit: runs {Display.Duration(plan.Overrun)} past {end} (predicted finish {finish}).";
        }
    }

    public string GapText => $"{result.Plan.GapMinutes} min between jobs; heaviest first from {OvernightOptimizer.Format(arranger.WindowStart)}.";

    /// <summary>Why measured durations weren't used, or "".</summary>
    public string DatabaseNote => result.DatabaseNote ?? "";
    public bool HasDatabaseNote => !string.IsNullOrEmpty(result.DatabaseNote);

    /// <summary>install.ps1's stderr (or the save's error) when Apply failed, or "".</summary>
    public string ApplyError
    {
        get => applyError;
        private set { if (Set(ref applyError, value)) Raise(nameof(HasApplyError)); }
    }

    public bool HasApplyError => !string.IsNullOrEmpty(ApplyError);

    /// <summary>Raised once Apply has saved and installed; the window closes with OK.</summary>
    public event EventHandler? Applied;

    public ICommand ApplyCommand => applyCommand ??= new AsyncRelayCommand(ApplyAsync,
        onError: ex => ApplyError = $"Couldn't apply: {ex.Message}");

    /// <summary>True while Apply is saving and running install.ps1.</summary>
    public bool IsApplying { get => isApplying; private set => Set(ref isApplying, value); }

    private async Task ApplyAsync()
    {
        ApplyError = "";
        IsApplying = true;
        try
        {
            var install = await Task.Run(() =>
            {
                arranger.Save(result, DateTimeOffset.Now);
                return Arranger.Install();
            });
            if (install.ExitCode != 0)
            {
                var detail = install.StandardError.Trim();
                ApplyError = $"arrangement.json was saved, but install.ps1 failed (exit {install.ExitCode}):\n" +
                             (detail.Length > 0 ? detail : install.StandardOutput.Trim());
                return;
            }
            Applied?.Invoke(this, EventArgs.Empty);
        }
        finally { IsApplying = false; }
    }
}
