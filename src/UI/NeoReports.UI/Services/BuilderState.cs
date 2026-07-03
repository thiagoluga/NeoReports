using NeoReports.UI.Models;

namespace NeoReports.UI.Services;

/// <summary>
/// Shared state for the Builder wizard, alive for the Blazor circuit.
/// Registered as Scoped in Program.cs. The 5 steps read/write here.
/// </summary>
public sealed class BuilderState
{
    public string Query { get; set; } = "";
    public Dictionary<string, string> Parameters { get; set; } = [];
    public HashSet<string> Formats { get; set; } = ["csv", "xlsx"];

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

    // Resilience — mirrors the engine's own defaults (RetryOptions/FailureStrategyBuilder), so an
    // untouched wizard produces the same policy as omitting "resilience" from the config entirely.

    /// <summary>Total attempts per batch, including the first.</summary>
    public int RetryMaxAttempts { get; set; } = 1;

    /// <summary>Backoff shape: "Constant" or "Exponential".</summary>
    public string RetryBackoff { get; set; } = "Constant";

    /// <summary>Base delay, in seconds, used for the first retry.</summary>
    public double RetryBaseDelaySeconds { get; set; } = 1;

    /// <summary>Whether to add randomized jitter to retry delays.</summary>
    public bool RetryJitter { get; set; }

    /// <summary>What happens after a batch exhausts its retries: "abort" or "skip-and-log".</summary>
    public string FailureStrategy { get; set; } = "abort";

    /// <summary>
    /// True once step 1 has confirmed the engine API is reachable and reports at least one
    /// registered capability. While false, the wizard stays browsable (SampleData) but Save is
    /// disabled — there is nothing real to compile the report against.
    /// </summary>
    public bool EngineAvailable { get; set; }

    /// <summary>Reset everything (when starting a new report).</summary>
    public void Reset()
    {
        Query = "";
        Parameters.Clear();
        Formats = ["csv", "xlsx"];
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
        RetryMaxAttempts = 1;
        RetryBackoff = "Constant";
        RetryBaseDelaySeconds = 1;
        RetryJitter = false;
        FailureStrategy = "abort";
        EngineAvailable = false;
    }
}
