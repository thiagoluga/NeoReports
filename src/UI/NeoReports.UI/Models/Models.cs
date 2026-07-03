namespace NeoReports.UI.Models;

/// <summary>Status of a report/job.</summary>
public enum JobStatus { Ok, Running, Failed, Paused, Queued, Never }

/// <summary>Semantic color family used by badges and tiles.</summary>
public enum Semantic { Success, Info, Danger, Warn, Purple, Gray }

/// <summary>A registered report template (minimal model for the starter).</summary>
public record ReportSummary(
    string Slug,
    string Name,
    string Description,
    string[] Tags,
    JobStatus Status,
    string Source,
    string[] Formats,
    string Destination,
    string ScheduleTitle,
    string ScheduleSub,
    string LastTitle,
    string LastSub,
    string CatKind,
    string CatIcon,
    string PrimaryAction = "Run",
    string PrimaryIcon = "player-play"
);

/// <summary>An output format option (builder step 3).</summary>
public record FormatOption(string Id, string Name, string Ext, string Kind, string Icon, string Description);

/// <summary>Static map from JobStatus to badge variant + icon + label.</summary>
public static class StatusMaps
{
    public static (string Variant, string Icon, string Label, bool Live) Badge(JobStatus s) => s switch
    {
        JobStatus.Running => ("info", "player-play", "Running", true),
        JobStatus.Ok => ("success", "check", "OK", false),
        JobStatus.Failed => ("danger", "alert-triangle", "Failed", false),
        JobStatus.Paused => ("warn", "player-pause", "Paused", false),
        JobStatus.Queued => ("gray", "clock", "Queued", false),
        _ => ("gray", "minus", "Never ran", false),
    };
}
