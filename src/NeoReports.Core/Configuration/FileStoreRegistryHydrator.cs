using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Registry;

namespace NeoReports.Core.Configuration;

/// <summary>
/// Loads every document from the registered <see cref="IReportConfigStore"/>, compiles it, and
/// registers it — the rehydration step that makes runtime-registered reports (ADR D33) survive a
/// restart. A document that fails to parse or compile is logged and skipped rather than crashing
/// the host; a name collision with a report registered earlier in the same pass (e.g. a code-first
/// report of the same name) is likewise logged and skipped, so code-first always wins.
/// </summary>
internal sealed class FileStoreRegistryHydrator : IRegistryHydrator
{
    /// <inheritdoc />
    public void Hydrate(IMutableReportRegistry registry, IServiceProvider services)
    {
        var store = services.GetRequiredService<IReportConfigStore>();
        ILogger logger = services.GetService<ILoggerFactory>()
            ?.CreateLogger("NeoReports.Core.Configuration.FileStoreRegistryHydrator")
            ?? NullLogger.Instance;

        // Rehydration runs synchronously inside the IReportRegistry factory delegate (no async
        // overload exists for AddSingleton<TService>(Func<IServiceProvider,TService>)); it only
        // ever runs once, at the first resolution of IReportRegistry, so blocking here does not
        // contend with any request-processing work.
        IReadOnlyList<(string Name, string Document)> documents = store
            .ListAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var parser = new JsonReportConfigParser();
        foreach ((string name, string document) in documents)
        {
            try
            {
                ReportConfig config = parser.Parse(document);
                ReportConfig substituted = ReportConfigEnvironment.Substitute(config);
                CompiledReport compiled = ReportConfigCompiler.Compile(substituted, services);
                registry.Register(compiled);
            }
            catch (ConfigurationException ex)
            {
                logger.LogError(ex, "Skipping dynamic report '{Name}': {Message}", Sanitize(name), ex.Message);
            }
            catch (IOException ex)
            {
                logger.LogError(ex, "Skipping dynamic report '{Name}': {Message}", Sanitize(name), ex.Message);
            }
        }
    }

    private static string Sanitize(string value) => value.Replace('\r', '_').Replace('\n', '_');
}
