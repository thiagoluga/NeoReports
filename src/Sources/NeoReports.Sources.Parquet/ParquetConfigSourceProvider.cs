using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using NeoReports.Sources.Files.Common;

namespace NeoReports.Sources.Parquet;

/// <summary>
/// Config-driven Parquet source for the dynamic path (<c>type: "parquet"</c>, ADR D60). Reads its
/// settings from the source <c>properties</c>: either <c>path</c> (local file) or <c>bucket</c>+
/// <c>key</c> (S3). No format-specific property is needed — Parquet is self-describing (no header/sheet
/// concept, unlike CSV/XLSX). Produces positional <see cref="ReportRecord"/>s matched against the
/// report's own declared schema by column name (case-insensitive), one row group at a time.
/// </summary>
public sealed class ParquetConfigSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc />
    public string Type => "parquet";

    /// <inheritdoc />
    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(schema);

        Func<CancellationToken, Task<Stream>> openStream =
            FileSourceProperties.ResolveStreamFactory("Parquet", source.Properties, services);

        var streaming = new ParquetStreamingSource<ReportRecord>(
            openStream, schema, ParquetRowGroups.RecordReader(schema));

        return new StreamingToBatchSource<ReportRecord>(streaming);
    }
}
