namespace NeoReports.Core.Preview;

/// <summary>
/// The closed set of comparisons a structured preview filter may use (ADR D45) — never a free-form
/// expression. This is a deliberately narrow, non-frozen concept that lives in <c>NeoReports.Core</c>
/// (not <c>Abstractions</c>): previews are an engine-hosting-layer feature, like job events (D38).
/// </summary>
public enum PreviewFilterOperator
{
    /// <summary>Column value equals the filter value.</summary>
    Equals,

    /// <summary>Column value does not equal the filter value.</summary>
    NotEquals,

    /// <summary>Column value is greater than the filter value.</summary>
    GreaterThan,

    /// <summary>Column value is greater than or equal to the filter value.</summary>
    GreaterThanOrEqual,

    /// <summary>Column value is less than the filter value.</summary>
    LessThan,

    /// <summary>Column value is less than or equal to the filter value.</summary>
    LessThanOrEqual,

    /// <summary>Column value contains the filter value as a substring.</summary>
    Contains,

    /// <summary>Column value starts with the filter value.</summary>
    StartsWith,
}

/// <summary>
/// One structured preview filter row (ADR D45): a report column, a closed-enum operator, and a
/// value. Ephemeral — never persisted; applies for a single preview or run request only.
/// </summary>
/// <param name="Column">Must be one of the report's declared columns.</param>
/// <param name="Operator">The comparison to apply.</param>
/// <param name="Value">The value to compare against.</param>
public sealed record PreviewFilter(string Column, PreviewFilterOperator Operator, object? Value);
