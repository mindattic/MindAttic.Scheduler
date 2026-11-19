namespace MindAttic.Scheduler.Wpf.ViewModels;

/// <summary>The schedule shapes the plain-English builder offers (M.SCHD-T38; Overnight, M.SCHD-T42).</summary>
public enum ScheduleKind
{
    Every,
    Daily,
    /// <summary><c>"daily": "auto"</c>: an overnight slot the arranger picks.</summary>
    Overnight,
    AtLogon,
}
