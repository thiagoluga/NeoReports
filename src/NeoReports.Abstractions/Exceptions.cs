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

/// <summary>A batch failed after exhausting its retries.</summary>
public sealed class BatchFailedException : NeoReportsException
{
    /// <summary>Creates an exception describing a batch that failed after all retries.</summary>
    /// <param name="pageNumber">Index of the page that failed.</param>
    /// <param name="attemptsExhausted">Number of attempts made before giving up.</param>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="inner">Optional underlying exception.</param>
    public BatchFailedException(int pageNumber, int attemptsExhausted, string message, Exception? inner = null)
        : base("NR-BATCH-001", message, inner)
    {
        PageNumber = pageNumber;
        AttemptsExhausted = attemptsExhausted;
    }

    /// <summary>Index of the page that failed.</summary>
    public int PageNumber { get; }

    /// <summary>Number of attempts made before giving up.</summary>
    public int AttemptsExhausted { get; }
}

/// <summary>The source could not be initialized or connected to.</summary>
public sealed class SourceFailedException : NeoReportsException
{
    /// <summary>Creates an exception describing a source that could not be used.</summary>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="inner">Optional underlying exception.</param>
    public SourceFailedException(string message, Exception? inner = null)
        : base("NR-SOURCE-001", message, inner) { }
}

/// <summary>A failure threshold (consecutive/total/ratio) was exceeded; the report was aborted.</summary>
public sealed class ThresholdExceededException : NeoReportsException
{
    /// <summary>Creates an exception describing an exceeded failure threshold.</summary>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="inner">Optional underlying exception.</param>
    public ThresholdExceededException(string message, Exception? inner = null)
        : base("NR-THRESHOLD-001", message, inner) { }
}

/// <summary>A report was registered or configured incorrectly.</summary>
public sealed class ConfigurationException : NeoReportsException
{
    /// <summary>Creates an exception describing an invalid report configuration.</summary>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="inner">Optional underlying exception.</param>
    public ConfigurationException(string message, Exception? inner = null)
        : base("NR-CONFIG-001", message, inner) { }
}
