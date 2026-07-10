using Cronos;
using NeoReports.Abstractions;

namespace NeoReports.Core.Scheduling;

/// <summary>
/// Shared cron validation used by both the typed builder (<c>ReportBuilder&lt;T&gt;.Schedule</c>)
/// and the dynamic path (<c>ReportConfigCompiler</c>), so an invalid expression is rejected
/// identically regardless of origin (ADR D41).
/// </summary>
public static class CronValidation
{
    /// <summary>Parses <paramref name="cron"/>, throwing a <see cref="ConfigurationException"/> on an invalid expression.</summary>
    /// <param name="cron">A 5-field cron expression, evaluated in UTC.</param>
    /// <returns>The parsed expression, ready for <c>GetNextOccurrence</c> queries.</returns>
    public static CronExpression Validate(string cron)
    {
        if (string.IsNullOrWhiteSpace(cron))
            throw new ConfigurationException("Schedule cron expression must be provided.");

        try
        {
            return CronExpression.Parse(cron);
        }
        catch (CronFormatException ex)
        {
            throw new ConfigurationException($"'{cron}' is not a valid cron expression: {ex.Message}");
        }
    }
}
