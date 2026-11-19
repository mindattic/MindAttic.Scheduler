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

    public ScheduleEditorViewModel(IReadOnlyList<ScheduleEntry> initial)
    {
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

    /// <summary>Call once the dialog's OK is pressed; fills <see cref="Result"/>.</summary>
    public void Save() => Result = [BuildEntry()];

    private void RaiseKindFlags()
    {
        Raise(nameof(IsEvery));
        Raise(nameof(IsDaily));
        Raise(nameof(IsAtLogon));
    }

    private ScheduleEntry BuildEntry() => Kind switch
    {
        ScheduleKind.Every => new ScheduleEntry
        {
            Every = TimeSpan.FromMinutes(EveryUnit == "Hours" ? EveryValue * 60 : EveryValue).ToString(),
        },
        ScheduleKind.AtLogon => new ScheduleEntry { AtLogon = true },
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
        // Every day (or nothing picked yet) means "no filter", matching jobs.json's plain daily.
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
            kind = ScheduleKind.Daily;
            dailyTime = entry.Daily ?? dailyTime;
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

        var cron = CronTranslator.ToCronText([BuildEntry()]);
        suppressBuilderToCron = true;
        try { CronText = cron; } finally { suppressBuilderToCron = false; }
        PreviewHuman = CronTranslator.ToHumanText(cron);
        CronError = "";
    }
}
