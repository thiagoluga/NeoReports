using System.Net;
using NeoReports.Abstractions;
using NeoReports.Core.Pipeline;

namespace NeoReports.Sources.Http.Common;

/// <summary>
/// A request against an HTTP-family source failed (ADR D61): a non-2xx response, or the configured
/// records path/mapping didn't match the response shape. Implements <see cref="IRetryDelayHint"/>
/// so a <c>Retry-After</c> response header is honored by the engine's existing batch-level
/// resilience pipeline instead of a second, HTTP-specific retry mechanism. 4xx and 5xx are not
/// distinguished (D37 already rejected per-exception-type retry filtering) — every failure is
/// retried uniformly, up to the report's configured attempts.
/// </summary>
public sealed class HttpSourceException : NeoReportsException, IRetryDelayHint
{
    /// <summary>Creates the exception.</summary>
    /// <param name="statusCode">The response's status code, when the failure came from a non-2xx response.</param>
    /// <param name="retryAfter">The response's <c>Retry-After</c> value, when present.</param>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="inner">Optional underlying exception.</param>
    public HttpSourceException(HttpStatusCode? statusCode, TimeSpan? retryAfter, string message, Exception? inner = null)
        : base("NR-HTTP-001", message, inner)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    /// <summary>The response's status code, when the failure came from a non-2xx response.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <inheritdoc />
    public TimeSpan? RetryAfter { get; }
}
