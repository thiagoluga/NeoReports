using NeoReports.Abstractions;
using Parquet;
using Parquet.Serialization;

namespace NeoReports.Sources.Parquet;

/// <summary>
/// Builds the "read one row group" delegates <see cref="ParquetStreamingSource{T}"/> is parameterized
/// on (ADR D60). Named to avoid colliding with <c>Parquet.ParquetRowGroupReader</c> (the library's own
/// low-level type). The typed and dynamic paths diverge here — the one place Parquet's asymmetry
/// lives — and everything else in the source is shared:
/// <list type="bullet">
/// <item>the <b>typed</b> path lets <see cref="ParquetSerializer"/> map each row group straight to
/// <c>T</c>, so no hand-rolled reflection materializer (the CSV/XLSX
/// <c>ReflectedRowShape&lt;T&gt;</c> equivalent) is needed;</item>
/// <item>the <b>dynamic</b> path reads each row group untyped (a list of column-name-keyed
/// dictionaries) and materializes positional <see cref="ReportRecord"/>s against the report's declared
/// schema — Parquet's self-describing logical types mean the values arrive already correctly typed.</item>
/// </list>
/// </summary>
internal static class ParquetRowGroups
{
    // The typed path matches Parquet column names to T's properties case-insensitively, keeping the
    // whole file family's "columns match by name, not case" behavior (CSV/XLSX headers do the same).
    // PropertyNameCaseInsensitive only affects the typed T-mapping path — DeserializeUntypedAsync's
    // dictionary-keyed output is unaffected by it (ParquetReportRecordMaterializer does its own
    // case-insensitive column-name resolution instead), so the untyped path needs no options at all.
    private static readonly ParquetOptions TypedOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// A delegate that deserializes the row group at the given index directly into
    /// <typeparamref name="T"/> instances. <typeparamref name="T"/> must have a public parameterless
    /// constructor and settable/init properties — <see cref="ParquetSerializer"/>'s own requirement
    /// (see the capability note in ADR D60); a positional record is therefore not usable here, unlike
    /// the CSV/XLSX typed paths.
    /// </summary>
    /// <typeparam name="T">The row type produced.</typeparam>
    public static Func<Stream, int, CancellationToken, Task<IReadOnlyList<T>>> TypedReader<T>()
        where T : class, new()
    {
        return async (stream, rowGroupIndex, cancellationToken) =>
        {
            stream.Position = 0;
            DeserializationResult<T> result = await ParquetSerializer
                .DeserializeAsync<T>(stream, TypedOptions, rowGroupIndex, cancellationToken)
                .ConfigureAwait(false);
            return result.Data as IReadOnlyList<T> ?? new List<T>(result.Data);
        };
    }

    /// <summary>
    /// A delegate that reads the row group at the given index untyped and materializes one
    /// <see cref="ReportRecord"/> per row against <paramref name="schema"/>, matching declared column
    /// names to the file's columns case-insensitively.
    /// </summary>
    /// <param name="schema">The report's declared output schema.</param>
    public static Func<Stream, int, CancellationToken, Task<IReadOnlyList<ReportRecord>>> RecordReader(ReportSchema schema)
    {
        return async (stream, rowGroupIndex, cancellationToken) =>
        {
            stream.Position = 0;
            DeserializationResult<Dictionary<string, object>> result = await ParquetSerializer
                .DeserializeUntypedAsync(stream, new ParquetOptions(), rowGroupIndex, cancellationToken)
                .ConfigureAwait(false);
            return ParquetReportRecordMaterializer.MaterializeRowGroup(result.Data, result.Schema, schema);
        };
    }
}
