namespace NeoReports.UI.Services;

/// <summary>
/// Shared state for the Builder wizard, alive for the Blazor circuit.
/// Registered as Scoped in Program.cs. The 5 steps read/write here.
/// </summary>
public sealed class BuilderState
{
    public HashSet<string> Formats { get; set; } = ["csv", "xlsx"];

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

    // Abort-when thresholds (ADR D37) — only meaningful, and only sent, when FailureStrategy is
    // "skip-and-log"; each pair is an independent on/off switch, sent as an OR of whichever are on.

    /// <summary>Escalate to abort after this many consecutive batch failures, when enabled.</summary>
    public bool AbortOnConsecutiveFailures { get; set; }
    public int AbortConsecutiveFailures { get; set; } = 3;

    /// <summary>Escalate to abort after this many total batch failures, when enabled.</summary>
    public bool AbortOnTotalFailures { get; set; }
    public int AbortTotalFailures { get; set; } = 10;

    /// <summary>Escalate to abort once the failure rate (percent) reaches this value, when enabled.</summary>
    public bool AbortOnFailureRate { get; set; }
    public double AbortFailureRatePercent { get; set; } = 50;

    /// <summary>
    /// True once step 1 has confirmed the engine API is reachable and reports at least one
    /// registered capability. While false, the wizard stays browsable (SampleData) but Save is
    /// disabled — there is nothing real to compile the report against.
    /// </summary>
    public bool EngineAvailable { get; set; }

    /// <summary>Reset everything (when starting a new report).</summary>
    public void Reset()
    {
        Formats = ["csv", "xlsx"];

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
        AbortOnConsecutiveFailures = false;
        AbortConsecutiveFailures = 3;
        AbortOnTotalFailures = false;
        AbortTotalFailures = 10;
        AbortOnFailureRate = false;
        AbortFailureRatePercent = 50;
        EngineAvailable = false;
    }
}
