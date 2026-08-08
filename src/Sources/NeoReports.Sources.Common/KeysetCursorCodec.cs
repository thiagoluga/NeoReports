using System.Globalization;
using NeoReports.Abstractions;

namespace NeoReports.Sources.Common;

/// <summary>
/// Serializes a keyset key value into the opaque string cursor round-tripped across pages
/// (ADR D43). The cursor is bound back as the <c>@cursor</c> string parameter on the next page, and
/// the report author's SQL casts it to the key column's type (e.g. <c>@cursor::uuid</c>,
/// <c>@cursor IS NULL OR Id &gt; @cursor</c>). The string must therefore reconstruct the <b>exact</b>
/// key regardless of the host's or database's locale — a lossy or culture-dependent encoding
/// silently duplicates or skips rows across the page boundary. Temporal keys are emitted in the
/// culture-invariant ISO-8601 round-trip form; the author's cast must match it (implicit on SQL
/// Server/PostgreSQL, but Oracle needs an explicit <c>TO_TIMESTAMP(@cursor, 'YYYY-MM-DD"T"HH24:MI:SS.FF7')</c>
/// format model and MySQL is sensitive to the <c>T</c> separator).
/// <para>
/// <b>A key column that carries a time zone needs the matching zoned cast</b> — <c>@cursor::timestamptz</c>
/// on PostgreSQL, <c>TO_TIMESTAMP_TZ(:cursor, 'YYYY-MM-DD"T"HH24:MI:SS.FF7TZH:TZM')</c> on Oracle.
/// The encoded cursor keeps the offset the driver reported (a trailing <c>Z</c> for Npgsql's Utc-kind
/// <c>timestamptz</c>, a <c>+hh:mm</c> for Oracle's <c>DateTimeOffset</c>), and a zone-less cast either
/// discards it — moving the page boundary by the session's offset and dropping the rows inside that
/// window — or, on Oracle, fails to parse it at all (ORA-01830). ADR D81 covers this for the generated
/// SQL the QueryBuilder emits; in the typed code-first path the cast is in the author's own SQL.
/// </para>
/// </summary>
internal static class KeysetCursorCodec
{
    /// <summary>
    /// Formats the last-read key value as the next page's cursor. <paramref name="value"/> is the
    /// non-null result of <c>DbDataReader.GetValue</c> for the key column.
    /// </summary>
    /// <param name="value">The key value read from the current page's last row.</param>
    /// <param name="keyColumn">The key column name, used only for a clear error message.</param>
    /// <returns>A round-trippable, culture-invariant string form of <paramref name="value"/>.</returns>
    /// <exception cref="ConfigurationException">
    /// The key type cannot be encoded as an opaque string cursor (e.g. a <c>byte[]</c>
    /// rowversion/timestamp).
    /// </exception>
    public static string FormatKey(object value, string keyColumn)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value switch
        {
            // The general date/time format Convert.ToString applies drops sub-second precision, so
            // two rows in the same whole second straddling a page boundary both re-satisfy
            // `key > @cursor` and are read twice. The round-trip ("O") form preserves full precision
            // and is the ISO-8601 shape every target database parses without a locale-dependent
            // style. A datetime2 column reads back as DateTimeKind.Unspecified (no trailing 'Z'), so
            // the emitted string casts cleanly; a datetimeoffset column reads as DateTimeOffset and
            // keeps its offset. A Utc-kind DateTime (e.g. PostgreSQL timestamptz via Npgsql) emits a
            // trailing 'Z', which its own tz-aware column casts back to the same instant.
            DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("O", CultureInfo.InvariantCulture),
            TimeOnly t => t.ToString("O", CultureInfo.InvariantCulture),

            // byte[] (a SQL Server rowversion/timestamp is the canonical example) has no
            // IConvertible, so Convert.ToString would return the literal "System.Byte[]" — a
            // constant cursor that never advances and can't be cast back to the binary key. Fail
            // with an actionable message rather than silently corrupt pagination.
            byte[] => throw new ConfigurationException(
                $"Keyset key column '{keyColumn}' is a byte[] value (for example a SQL Server " +
                "rowversion/timestamp), which cannot be encoded as an opaque string cursor. Order " +
                "the report on a monotonic scalar key — an integer, GUID, datetime2, or string " +
                "column — instead."),

            // Numbers, GUIDs, booleans and strings all round-trip through the invariant culture.
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
                ?? throw new ConfigurationException(
                    $"Keyset key column '{keyColumn}' produced no string cursor from a non-null " +
                    $"{value.GetType().Name} value. Order the report on a scalar key whose value " +
                    "round-trips as text (an integer, GUID, datetime2, or string column)."),
        };
    }
}
