namespace NeoReports.Abstractions;

/// <summary>Base exception for all NeoReports errors. Carries a stable machine-readable code.</summary>
public class NeoReportsException : Exception
{
    /// <summary>Creates a NeoReports exception with a stable code.</summary>
    /// <param name="code">Stable, machine-readable error code (e.g. "NR-BATCH-001").</param>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="inner">Optional underlying exception.</param>
    public NeoReportsException(string code, string message, Exception? inner = null)
        : base(message, inner) => Code = code;

    /// <summary>Stable error code, e.g. "NR-BATCH-001".</summary>
    public string Code { get; }
}

// NOTE: BatchFailedException / SourceFailedException / ThresholdExceededException were removed
// (2026-07-30, next major — see CHANGELOG). They were never thrown: the pipeline reports a
// batch/source/threshold failure through ReportRunResult.Status + its error string and the
// IFailureStrategy decision, not by throwing — so they were dead surface in a frozen ABI (rule 7).

/// <summary>A report was registered or configured incorrectly.</summary>
public sealed class ConfigurationException : NeoReportsException
{
    /// <summary>Creates an exception describing an invalid report configuration.</summary>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="inner">Optional underlying exception.</param>
    public ConfigurationException(string message, Exception? inner = null)
        : base("NR-CONFIG-001", message, inner) { }
}
