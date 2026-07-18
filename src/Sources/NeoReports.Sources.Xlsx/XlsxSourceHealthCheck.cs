using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Files.Common;

namespace NeoReports.Sources.Xlsx;

/// <summary>
/// On-demand health check for a registered XLSX source (ADR D42/D59, <c>type: "xlsx"</c>). The check
/// itself is shared across every file-based source — see <see cref="FileSourceHealth"/>.
/// </summary>
public sealed class XlsxSourceHealthCheck : ISourceHealthCheck
{
    /// <inheritdoc />
    public string Type => "xlsx";

    /// <inheritdoc />
    public Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken) =>
        FileSourceHealth.CheckAsync(definition, services, cancellationToken);
}
