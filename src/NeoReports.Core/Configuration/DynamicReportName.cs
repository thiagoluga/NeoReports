using System.Text.RegularExpressions;

namespace NeoReports.Core.Configuration;

/// <summary>
/// Validates names used for runtime-registered (dynamic) reports. A dynamic report name becomes a
/// file name on disk (<see cref="FileReportConfigStore"/>) and a URL segment (the AspNetCore
/// endpoints), so an invalid name is always rejected — never sanitized — to keep path traversal
/// structurally impossible.
/// </summary>
public static partial class DynamicReportName
{
    /// <summary>The validation pattern: starts with a letter, then up to 99 letters/digits/underscore/hyphen.</summary>
    /// <remarks>
    /// Anchored with <c>\z</c>, not <c>$</c>. In .NET <c>$</c> also matches immediately BEFORE a
    /// trailing newline, so <c>^…$</c> accepted a name ending in one — and this validator is the only
    /// thing standing in front of it. The name reaches a file name on disk, a URL segment, a job
    /// record and every log line about a run; in a plain-text log sink the newline lets the name forge
    /// entries (CodeQL cs/log-forging on ReportJobWorker/InMemoryJobScheduler). <c>\z</c> means end
    /// of input and nothing else.
    /// </remarks>
    public const string Pattern = @"^[a-zA-Z][a-zA-Z0-9_-]{0,99}\z";

    /// <summary>True when <paramref name="name"/> matches <see cref="Pattern"/>.</summary>
    /// <param name="name">The candidate report name.</param>
    public static bool IsValid(string? name) => name is not null && NamePattern().IsMatch(name);

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
