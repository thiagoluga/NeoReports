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

    /// <summary>Source type id (e.g. "sql"), matched against a capability the host has registered.
    /// Ignored (and not sent) when <see cref="SourceRef"/> is set — the type then comes from the
    /// registered source definition (ADR D42).</summary>
    public string SourceType { get; set; } = "sql";

    /// <summary>
    /// Name of a registered source (ADR D42) to reference via <c>source.ref</c> instead of an
    /// inline connection; empty means the inline path (<see cref="SourceType"/> +
    /// <see cref="ConnectionStringVariable"/>). Query/key/page-size stay report-local either way.
    /// </summary>
    public string SourceRef { get; set; } = "";

    /// <summary>
    /// Name of an environment variable holding the connection string — never the raw secret
    /// itself. Serialized as <c>${NAME}</c>, resolved by the engine at compile time. Not used
    /// when <see cref="SourceRef"/> is set.
    /// </summary>
    public string ConnectionStringVariable { get; set; } = "";

    /// <summary>
    /// The exact placeholder the engine returned for the stored <c>connectionString</c>, or <c>null</c>
    /// when it was not held back. Kept verbatim rather than as a flag because a placeholder carries the
    /// address it came from (ADR D86) and has to go back exactly as it arrived. The connection is kept
    /// on save unless <see cref="ConnectionStringVariable"/> is filled in to replace it — so editing a
    /// page size does not cost the user their connection.
    /// </summary>
    public string? ConnectionStringSentinel { get; set; }

    /// <summary>Whether the stored connection string is one the engine held back.</summary>
    public bool ConnectionStringRedacted => ConnectionStringSentinel is not null;

    /// <summary>
    /// Whether the hidden connection string is actually still in play. Pointing the report at a
    /// different source discards it: restoring the old connection into the new source is the one
    /// outcome nobody asked for, and it would happen invisibly.
    /// </summary>
    public bool ConnectionStringKept =>
        ConnectionStringRedacted && string.Equals(LoadedSourceIdentity, SourceIdentity, StringComparison.Ordinal);

    /// <summary>
    /// One line describing the connection, for the recap and review summaries. A kept-but-hidden
    /// connection has to read differently from no connection at all — "no connection string set" on
    /// a report that has one is the kind of wrong that gets acted on.
    /// </summary>
    public string ConnectionSummary
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ConnectionStringVariable))
                return $"${{{ConnectionStringVariable}}}";

            return ConnectionStringKept ? "connection kept · not shown" : "no connection string set";
        }
    }

    /// <summary>
    /// Source type ids that ride the ADO/keyset source family (<c>AdoKeysetSource</c>,
    /// <c>NeoReports.Sources.Common</c>'s <c>AdoConfigProperties</c>) and therefore use the
    /// dedicated <see cref="SqlQuery"/>/<see cref="KeyColumn"/> fields below, instead of the
    /// generic <see cref="SourceProperties"/> editor every other engine source type uses. An
    /// honest, manually-synced list — the engine's <c>GET /api/capabilities</c> reports only
    /// type-id strings, no shape metadata — that needs updating if a new ADO-family provider ships.
    /// </summary>
    public static readonly IReadOnlySet<string> AdoSqlShapeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "sql", "mysql", "postgres", "oracle", "sqlite", "redshift", "snowflake",
    };

    /// <summary>Whether <see cref="SourceType"/> is one of the <see cref="AdoSqlShapeTypes"/>.</summary>
    public bool UsesAdoSqlShape => AdoSqlShapeTypes.Contains(SourceType);

    /// <summary>SQL query text for the source (<see cref="UsesAdoSqlShape"/> types only).</summary>
    public string SqlQuery { get; set; } = "";

    /// <summary>Keyset pagination key column (<see cref="UsesAdoSqlShape"/> types only).</summary>
    public string KeyColumn { get; set; } = "Id";

    /// <summary>
    /// Generic key/value source properties (ADR D42's property-bag pattern), used for every engine
    /// source type EXCEPT the <see cref="AdoSqlShapeTypes"/> family, which uses
    /// <see cref="SqlQuery"/>/<see cref="KeyColumn"/> instead. Sent as an overlay alongside
    /// <see cref="SourceRef"/> too, mirroring how the SQL family's query/key stay report-local even
    /// for a registered source.
    /// </summary>
    public List<PropertyRow> SourceProperties { get; set; } = [];

    /// <summary>Rows read per page.</summary>
    public int PageSize { get; set; } = 1000;

    /// <summary>
    /// Whether the engine counts the source's total rows once before each run, enabling a real
    /// completion percentage on the running-job page (ADR D47). Enabled by default, matching the
    /// typed builder's own default; unchecking it trades that percentage for an indeterminate bar.
    /// </summary>
    public bool TrackProgress { get; set; } = true;

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
    /// Recurring-run cron expression (ADR D41), evaluated in UTC; empty means no schedule. Only
    /// sent to <c>POST /api/reports</c> when non-blank — omitting "schedule" entirely from the
    /// config document is the untouched-wizard default, matching the engine's own "no schedule".
    /// </summary>
    public string ScheduleCron { get; set; } = "";

    /// <summary>
    /// True once step 1 has confirmed the engine API is reachable and reports at least one
    /// registered capability. While false, the wizard stays browsable (SampleData) but Save is
    /// disabled — there is nothing real to compile the report against.
    /// </summary>
    public bool EngineAvailable { get; set; }

    /// <summary>
    /// True when the wizard was opened from an existing report's "Edit" button (found missing
    /// during a 2026-07 UI audit) rather than "New report". Saving then deletes
    /// <see cref="EditingOriginalName"/> and re-creates it under the (possibly unchanged) new
    /// config — there is no <c>PUT /api/reports/{name}</c>, only create + delete.
    /// </summary>
    public bool IsEditing { get; set; }

    /// <summary>The report name being edited, captured before <see cref="ReportName"/> can be
    /// changed on the Review step — the name actually replaced on save. Empty outside edit mode.</summary>
    public string EditingOriginalName { get; set; } = "";

    /// <summary>
    /// The report's stored configuration document as <c>GET /api/reports/{name}/config</c> returned
    /// it (ADR D86), or <c>null</c> when creating. Saving an edit **patches** this document rather
    /// than regenerating one from the fields below, so everything the wizard has no editor for —
    /// a JsonLogic filter, per-output properties or sections, a column's format/culture, a second
    /// destination — survives an edit instead of being silently dropped by a form that never knew
    /// about it.
    /// </summary>
    public string? OriginalDocument { get; set; }

    /// <summary>
    /// Identifies the source the loaded document described, so the patch can tell "the user changed
    /// the page size" from "the user pointed this report at a different source". Properties from the
    /// stored document are only carried over while this still matches <see cref="SourceIdentity"/>;
    /// an HTTP source's <c>url</c> has no business surviving a switch to Postgres.
    /// </summary>
    public string LoadedSourceIdentity { get; set; } = "";

    /// <summary>
    /// How many destinations beyond the first the loaded document declared. The wizard edits only
    /// the first; the rest ride along untouched, and this is what lets the Destination step say so
    /// rather than present the report as having exactly one.
    /// </summary>
    public int AdditionalDestinationCount { get; set; }

    /// <summary>
    /// How many outputs the loaded document declared beyond one per distinct format. The Format step
    /// is a set of checkboxes and cannot express "two csv outputs with different writer options";
    /// all of them are kept on save, and this is what lets the step say so.
    /// </summary>
    public int AdditionalOutputCount { get; set; }

    /// <summary>The source currently selected, in the same shape as <see cref="LoadedSourceIdentity"/>.</summary>
    public string SourceIdentity =>
        string.IsNullOrWhiteSpace(SourceRef) ? $"type:{SourceType}" : $"ref:{SourceRef.Trim()}";

    /// <summary>Reset everything (when starting a new report).</summary>
    public void Reset()
    {
        Formats = ["csv", "xlsx"];

        ReportName = "";
        SourceType = "sql";
        SourceRef = "";
        ConnectionStringVariable = "";
        ConnectionStringSentinel = null;
        SqlQuery = "";
        KeyColumn = "Id";
        SourceProperties = [];
        PageSize = 1000;
        TrackProgress = true;
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
        ScheduleCron = "";
        EngineAvailable = false;
        IsEditing = false;
        EditingOriginalName = "";
        OriginalDocument = null;
        LoadedSourceIdentity = "";
        AdditionalDestinationCount = 0;
        AdditionalOutputCount = 0;
    }
}
