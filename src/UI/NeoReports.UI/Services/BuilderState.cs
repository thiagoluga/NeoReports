using NeoReports.UI.Models;

namespace NeoReports.UI.Services;

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

    // Real, engine-backed fields (Epic D / D6) — everything above this line stays cosmetic
    // (no ReportConfig equivalent) and is never sent to the engine.

    /// <summary>Unique report name; also the URL identifier once saved.</summary>
    public string ReportName { get; set; } = "";

    /// <summary>Source type id (e.g. "sql"), matched against a capability the host has registered.</summary>
    public string SourceType { get; set; } = "sql";

    /// <summary>
    /// Name of an environment variable holding the connection string — never the raw secret
    /// itself. Serialized as <c>${NAME}</c>, resolved by the engine at compile time.
    /// </summary>
    public string ConnectionStringVariable { get; set; } = "";

    /// <summary>SQL query text for the source.</summary>
    public string SqlQuery { get; set; } = "";

    /// <summary>Keyset pagination key column.</summary>
    public string KeyColumn { get; set; } = "Id";

    /// <summary>Rows read per page.</summary>
    public int PageSize { get; set; } = 1000;

    /// <summary>Comma-separated output column names.</summary>
    public string ColumnNames { get; set; } = "Id";

    /// <summary>Destination type id (e.g. "local", "s3"); empty means no destination configured.</summary>
    public string DestinationType { get; set; } = "";

    /// <summary>Destination-specific path/key template.</summary>
    public string DestinationPath { get; set; } = "";

    /// <summary>
    /// True once step 1 has confirmed the engine API is reachable and reports at least one
    /// registered capability. While false, the wizard stays browsable (SampleData) but Save is
    /// disabled — there is nothing real to compile the report against.
    /// </summary>
    public bool EngineAvailable { get; set; }

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

        ReportName = "";
        SourceType = "sql";
        ConnectionStringVariable = "";
        SqlQuery = "";
        KeyColumn = "Id";
        PageSize = 1000;
        ColumnNames = "Id";
        DestinationType = "";
        DestinationPath = "";
        EngineAvailable = false;
    }
}
