using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NeoReports.Abstractions;
using NeoReports.Core.Registry;

namespace NeoReports.Core.Configuration;

/// <summary>
/// Forces the config-driven reports to compile at startup instead of on the first request. Config
/// reports (<c>AddReportFromConfig</c>/<c>File</c>/<c>Directory</c>) are parsed at registration but
/// compiled lazily when <see cref="IReportRegistry"/> is first resolved (ADR D33), so a malformed
/// document — an unknown source type, a missing column, an invalid filter — otherwise surfaces on
/// whichever request first touches the registry rather than at boot. Registering this service
/// (via <c>AddNeoReportsStartupValidation()</c>) resolves the registry in <see cref="StartAsync"/>,
/// so any compilation error fails the host at startup, matching the fail-fast the typed
/// <c>AddReport&lt;T&gt;</c> path already gives.
/// </summary>
public sealed class StartupValidationHostedService : IHostedService
{
    private readonly IReportRegistry _registry;
    private readonly ILogger<StartupValidationHostedService> _logger;

    /// <summary>Creates the hosted service.</summary>
    /// <param name="registry">
    /// The report registry. Injecting it already forces the lazy compile/hydrate; resolving it is
    /// the validation.
    /// </param>
    /// <param name="logger">Logger.</param>
    public StartupValidationHostedService(IReportRegistry registry, ILogger<StartupValidationHostedService> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolving _registry (constructor injection) already ran the config compile; a malformed
        // document has thrown before reaching here, failing host startup. This is just the
        // confirmation log.
        _logger.LogInformation(
            "NeoReports startup validation succeeded: {ReportCount} report(s) compiled.", _registry.Reports.Count);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
