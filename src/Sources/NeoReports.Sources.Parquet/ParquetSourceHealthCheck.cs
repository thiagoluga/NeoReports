using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Files.Common;

namespace NeoReports.Sources.Parquet;

/// <summary>
/// On-demand health check for a registered Parquet source (ADR D42/D60, <c>type: "parquet"</c>). The
/// check itself is shared across every file-based source — see <see cref="FileSourceHealth"/>.
/// </summary>
public sealed class ParquetSourceHealthCheck : ISourceHealthCheck
{
    /// <inheritdoc />
    public string Type => "parquet";

    /// <inheritdoc />
    public Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken) =>
        FileSourceHealth.CheckAsync(definition, services, cancellationToken);
}
