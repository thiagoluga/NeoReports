using NeoReports.Abstractions;

namespace NeoReports.Core.Preview;

/// <summary>The result of previewing one page of a report (ADR D45).</summary>
/// <param name="Rows">The sample rows, schema-projected (same <c>object?[]</c> shape a writer consumes).</param>
/// <param name="Schema">The schema the rows are aligned to.</param>
/// <param name="FiltersApplied">
/// <c>true</c> when the supplied filters were actually translated and applied server-side;
/// <c>false</c> when there were none, or the source type has no registered <see cref="IFilterTranslator"/>
/// (filters are then ignored, honestly reported as such — never silently dropped without saying so).
/// </param>
/// <param name="HasMore">Whether more pages exist beyond this sample.</param>
public sealed record PreviewResult(
    IReadOnlyList<object?[]> Rows,
    ReportSchema Schema,
    bool FiltersApplied,
    bool HasMore);
