namespace NeoReports.Web.Models;

/// <summary>Health of a source or connection.</summary>
public enum Health { Ok, Warn, Error }

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
    string PrimaryIcon = "player-play",
    bool IsPipeline = false
);

/// <summary>A registered data source.</summary>
public record SourceSummary(
    string Id,
    string Name,
    string Type,
    string Connection,
    Health Health,
    string Pagination,
    string P95,
    int UsedIn,
    string LastQuery,
    string CatKind,
    string CatIcon
);

/// <summary>A single column exposed by a source (source explorer).</summary>
public record ColumnDef(string Name, string Type, bool Selected, string? MetaLabel = null, string? MetaValue = null);

/// <summary>An output format option (builder step 3).</summary>
public record FormatOption(string Id, string Name, string Ext, string Kind, string Icon, string Description);

/// <summary>A destination option (builder step 4).</summary>
public record DestinationOption(string Id, string Name, string Kind, string Icon, string Description);

/// <summary>A plugin entry (settings/plugins). Health: "ok" | "update" | "license-error".</summary>
public record PluginInfo(string Group, string Name, string Kind, string Icon, string Origin, string Description, string Version, int UsedIn, string Health);

/// <summary>Static maps from JobStatus/Health to badge variant + icon + label.</summary>
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

    public static string Dot(Health h) => h switch
    {
        Health.Ok => "success",
        Health.Warn => "warn",
        _ => "danger",
    };

    public static (string Variant, string Icon, string Label) HealthBadge(Health h) => h switch
    {
        Health.Ok => ("success", "check", "healthy"),
        Health.Warn => ("warn", "clock", "high latency"),
        _ => ("danger", "alert-triangle", "timeout"),
    };
}
