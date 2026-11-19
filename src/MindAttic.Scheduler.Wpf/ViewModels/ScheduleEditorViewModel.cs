using System.Text.RegularExpressions;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Scheduler.Core;

namespace MindAttic.Scheduler.Wpf.ViewModels;

/// <summary>
/// The schedule editor dialog's view model (M.SCHD-T38): a plain-English builder (every/daily
/// with optional weekdays/at logon) kept in sync, in both directions, with a free-text cron
/// field - typing a recognized cron fills in the builder; changing the builder updates the cron
/// preview. A cron shape this scheduler's triggers can't represent shows a plain explanation
/// instead of guessing.
/// </summary>
public sealed class ScheduleEditorViewModel : ObservableObject
{
    private ScheduleKind kind;
    private int everyValue = 5;
    private string everyUnit = "Minutes";
    private string dailyTime = "09:00";
    private bool mon = true, tue = true, wed = true, thu = true, fri = true, sat = true, sun = true;
    private string cronText = "";
    private string cronError = "";
    private string previewHuman = "";
    private bool suppressBuilderToCron;
    private string saveError = "";

    // The editor edits a job's first schedule entry; any further entries (a job can fire at
    // several daily times) are kept exactly as they were, never silently dropped on save.
    private readonly IReadOnlyList<ScheduleEntry> otherEntries;

    public ScheduleEditorViewModel(IReadOnlyList<ScheduleEntry> initial)
    {
        otherEntries = initial.Skip(1).ToList();
        var first = initial.FirstOrDefault() ?? new ScheduleEntry { Daily = "09:00" };
        ApplyToBuilder(first);
        UpdatePreviewFromBuilder();
    }

    public string[] EveryUnits { get; } = ["Minutes", "Hours"];

    /// <summary>The finished schedule, set by <see cref="Save"/> once the dialog's OK is pressed.</summary>
    public IReadOnlyList<ScheduleEntry>? Result { get; private set; }

    public ScheduleKind Kind
    {
        get => kind;
        set { if (Set(ref kind, value)) { RaiseKindFlags(); UpdatePreviewFromBuilder(); } }
    }

    public bool IsEvery => Kind == ScheduleKind.Every;
    public bool IsDaily => Kind == ScheduleKind.Daily;
    public bool IsOvernight => Kind == ScheduleKind.Overnight;
    public bool IsAtLogon => Kind == ScheduleKind.AtLogon;

    public int EveryValue { get => everyValue; set { if (Set(ref everyValue, value)) UpdatePreviewFromBuilder(); } }
    public string EveryUnit { get => everyUnit; set { if (Set(ref everyUnit, value)) UpdatePreviewFromBuilder(); } }
    public string DailyTime { get => dailyTime; set { if (Set(ref dailyTime, value)) UpdatePreviewFromBuilder(); } }

    public bool Mon { get => mon; set { if (Set(ref mon, value)) UpdatePreviewFromBuilder(); } }
    public bool Tue { get => tue; set { if (Set(ref tue, value)) UpdatePreviewFromBuilder(); } }
    public bool Wed { get => wed; set { if (Set(ref wed, value)) UpdatePreviewFromBuilder(); } }
    public bool Thu { get => thu; set { if (Set(ref thu, value)) UpdatePreviewFromBuilder(); } }
    public bool Fri { get => fri; set { if (Set(ref fri, value)) UpdatePreviewFromBuilder(); } }
    public bool Sat { get => sat; set { if (Set(ref sat, value)) UpdatePreviewFromBuilder(); } }
    public bool Sun { get => sun; set { if (Set(ref sun, value)) UpdatePreviewFromBuilder(); } }

    public string CronText
    {
        get => cronText;
        set
        {
            if (!Set(ref cronText, value)) return;
            if (suppressBuilderToCron) return;

            if (string.IsNullOrWhiteSpace(value)) { CronError = ""; PreviewHuman = ""; return; }

            PreviewHuman = CronTranslator.ToHumanText(value);
            if (CronTranslator.TryParse(value, out var schedule, out var error))
            {
                CronError = "";
                ApplyToBuilder(schedule[0]);
            }
            else CronError = error ?? "Not a recognized cron expression.";
        }
    }

    public string CronError
    {
        get => cronError;
        private set
        {
            if (!Set(ref cronError, value)) return;
            Raise(nameof(HasCronError));
        }
    }

    public bool HasCronError => !string.IsNullOrEmpty(CronError);

    public string PreviewHuman { get => previewHuman; private set => Set(ref previewHuman, value); }

    /// <summary>Why the last OK was refused (install.ps1 would reject the entry), or "".</summary>
    public string SaveError
    {
        get => saveError;
        private set { if (Set(ref saveError, value)) Raise(nameof(HasSaveError)); }
    }

    public bool HasSaveError => !string.IsNullOrEmpty(SaveError);

    /// <summary>"This job also runs at ..." for a job with more than one schedule entry, or "".</summary>
    public string OtherEntriesNote => otherEntries.Count == 0 ? "" :
        $"This job has {otherEntries.Count} more schedule entr{(otherEntries.Count == 1 ? "y" : "ies")} " +
        "(only the first is edited here); they are kept as they are.";

    public bool HasOtherEntries => otherEntries.Count > 0;

    /// <summary>
    /// Call once the dialog's OK is pressed: fills <see cref="Result"/> and returns true, or
    /// returns false with <see cref="SaveError"/> set when the builder holds something install.ps1
    /// would reject (saving it would leave jobs.json unappliable).
    /// </summary>
    public bool TrySave()
    {
        var error = Validate();
        SaveError = error ?? "";
        if (error is not null) return false;
        Result = [BuildEntry(), .. otherEntries];
        return true;
    }

    private string? Validate() => Kind switch
    {
        ScheduleKind.Every when EveryValue < 1 => "The repeat interval must be at least 1.",
        ScheduleKind.Daily when !Regex.IsMatch(DailyTime ?? "", @"^([01]\d|2[0-3]):[0-5]\d$") =>
            "The time must be a 24-hour HH:mm time, e.g. 09:00 or 21:30.",
        ScheduleKind.Daily or ScheduleKind.Overnight when !(Mon || Tue || Wed || Thu || Fri || Sat || Sun) => "Pick at least one day.",
        _ => null,
    };

    private void RaiseKindFlags()
    {
        Raise(nameof(IsEvery));
        Raise(nameof(IsDaily));
        Raise(nameof(IsOvernight));
        Raise(nameof(IsAtLogon));
    }

    private ScheduleEntry BuildEntry() => Kind switch
    {
        ScheduleKind.Every => new ScheduleEntry
        {
            Every = TimeSpan.FromMinutes(EveryUnit == "Hours" ? EveryValue * 60 : EveryValue).ToString(),
        },
        ScheduleKind.AtLogon => new ScheduleEntry { AtLogon = true },
        ScheduleKind.Overnight => new ScheduleEntry { Daily = ManifestJob.Auto, DaysOfWeek = SelectedDays() },
        _ => new ScheduleEntry { Daily = DailyTime, DaysOfWeek = SelectedDays() },
    };

    private string[]? SelectedDays()
    {
        var days = new List<string>();
        if (Mon) days.Add("Mon");
        if (Tue) days.Add("Tue");
        if (Wed) days.Add("Wed");
        if (Thu) days.Add("Thu");
        if (Fri) days.Add("Fri");
        if (Sat) days.Add("Sat");
        if (Sun) days.Add("Sun");
        // Every day means "no filter", matching jobs.json's plain daily (none picked is refused by Validate).
        return days.Count is 0 or 7 ? null : [.. days];
    }

    private void ApplyToBuilder(ScheduleEntry entry)
    {
        if (entry.Every is { } every && TimeSpan.TryParse(every, out var span))
        {
            kind = ScheduleKind.Every;
            if (span.TotalHours >= 1 && span.Minutes == 0 && span.Seconds == 0) { everyValue = (int)span.TotalHours; everyUnit = "Hours"; }
            else { everyValue = Math.Max(1, (int)span.TotalMinutes); everyUnit = "Minutes"; }
        }
        else if (entry.AtLogon == true) { kind = ScheduleKind.AtLogon; }
        else
        {
            // "daily": "auto" is an arranged overnight slot, not a time; the time box keeps its last
            // real time so switching back to Daily doesn't show "auto".
            var overnight = ManifestJob.IsAutoEntry(entry);
            kind = overnight ? ScheduleKind.Overnight : ScheduleKind.Daily;
            if (!overnight) dailyTime = entry.Daily ?? dailyTime;
            var days = entry.DaysOfWeek;
            mon = days?.Contains("Mon") ?? true;
            tue = days?.Contains("Tue") ?? true;
            wed = days?.Contains("Wed") ?? true;
            thu = days?.Contains("Thu") ?? true;
            fri = days?.Contains("Fri") ?? true;
            sat = days?.Contains("Sat") ?? true;
            sun = days?.Contains("Sun") ?? true;
        }

        Raise(nameof(Kind)); RaiseKindFlags();
        Raise(nameof(EveryValue)); Raise(nameof(EveryUnit)); Raise(nameof(DailyTime));
        Raise(nameof(Mon)); Raise(nameof(Tue)); Raise(nameof(Wed)); Raise(nameof(Thu));
        Raise(nameof(Fri)); Raise(nameof(Sat)); Raise(nameof(Sun));
    }

    private void UpdatePreviewFromBuilder()
    {
        if (Kind == ScheduleKind.AtLogon)
        {
            suppressBuilderToCron = true;
            try { CronText = ""; } finally { suppressBuilderToCron = false; }
            PreviewHuman = "At logon.";
            CronError = "";
            return;
        }

        if (Kind == ScheduleKind.Overnight)
        {
            // An arranged slot moves night to night, so it has no cron text.
            suppressBuilderToCron = true;
            try { CronText = ""; } finally { suppressBuilderToCron = false; }
            var days = SelectedDays();
            PreviewHuman = "Overnight, at the slot the arranger picks (finished by 08:00)" +
                           (days is null ? ", every day." : $", on {string.Join(", ", days)}.");
            CronError = "";
            SaveError = "";
            return;
        }

        var cron = CronTranslator.ToCronText([BuildEntry()]);
        suppressBuilderToCron = true;
        try { CronText = cron; } finally { suppressBuilderToCron = false; }
        // An interval cron can't express exactly (e.g. every 90 minutes) has no cron text; describe
        // it directly rather than feeding "" to the cron describer, which reports it as invalid.
        PreviewHuman = cron.Length > 0 ? CronTranslator.ToHumanText(cron)
            : Kind == ScheduleKind.Every ? $"Every {EveryValue} {EveryUnit.ToLowerInvariant()} (no exact cron equivalent)."
            : "Enter a 24-hour HH:mm time.";
        CronError = "";
        SaveError = "";
    }
}
