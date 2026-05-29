namespace NeoReports.Abstractions;

/// <summary>Base exception for all NeoReports errors. Carries a stable machine-readable code.</summary>
public class NeoReportsException : Exception
{
    public NeoReportsException(string code, string message, Exception? inner = null)
        : base(message, inner) => Code = code;

    /// <summary>Stable error code, e.g. "NR-BATCH-001".</summary>
    public string Code { get; }
}

/// <summary>A batch failed after exhausting its retries.</summary>
public sealed class BatchFailedException : NeoReportsException
{
    public BatchFailedException(int pageNumber, int attemptsExhausted, string message, Exception? inner = null)
        : base("NR-BATCH-001", message, inner)
    {
        PageNumber = pageNumber;
        AttemptsExhausted = attemptsExhausted;
    }

    public int PageNumber { get; }
    public int AttemptsExhausted { get; }
}

/// <summary>The source could not be initialized or connected to.</summary>
public sealed class SourceFailedException : NeoReportsException
{
    public SourceFailedException(string message, Exception? inner = null)
        : base("NR-SOURCE-001", message, inner) { }
}

/// <summary>A failure threshold (consecutive/total/ratio) was exceeded; the report was aborted.</summary>
public sealed class ThresholdExceededException : NeoReportsException
{
    public ThresholdExceededException(string message, Exception? inner = null)
        : base("NR-THRESHOLD-001", message, inner) { }
}

/// <summary>A report was registered or configured incorrectly.</summary>
public sealed class ConfigurationException : NeoReportsException
{
    public ConfigurationException(string message, Exception? inner = null)
        : base("NR-CONFIG-001", message, inner) { }
}
