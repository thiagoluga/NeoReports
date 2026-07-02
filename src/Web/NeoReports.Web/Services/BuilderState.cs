using NeoReports.Web.Models;

namespace NeoReports.Web.Services;

/// <summary>
/// Shared state for the Builder wizard, alive for the Blazor circuit.
/// Registered as Scoped in Program.cs. The 5 steps read/write here.
/// </summary>
public sealed class BuilderState
{
    public string? SourceId { get; set; }
    public string Query { get; set; } = "";
    public Dictionary<string, string> Parameters { get; set; } = [];
    public HashSet<string> Formats { get; set; } = ["csv", "xlsx"];
    public HashSet<string> Destinations { get; set; } = ["sharepoint"];

    public string ScheduleMode { get; set; } = "recurring"; // none | once | recurring
    public HashSet<int> Weekdays { get; set; } = [1];  // 0=Sun .. 6=Sat
    public bool SaveAsTemplate { get; set; } = true;

    /// <summary>Reset everything (when starting a new report).</summary>
    public void Reset()
    {
        SourceId = null;
        Query = "";
        Parameters.Clear();
        Formats = ["csv", "xlsx"];
        Destinations = ["sharepoint"];
        ScheduleMode = "recurring";
        Weekdays = [1];
        SaveAsTemplate = true;
    }
}
