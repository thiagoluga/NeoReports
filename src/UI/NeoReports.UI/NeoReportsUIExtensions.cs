using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.UI.Services;

namespace NeoReports.UI;

/// <summary>
/// Mounts the NeoReports web UI (a Razor Class Library) into a host application.
/// </summary>
/// <example>
/// <code>
/// builder.Services.AddNeoReportsUI();
/// ...
/// app.UseNeoReportsUI("/neoreports");
/// </code>
/// </example>
public static class NeoReportsUIExtensions
{
    /// <summary>Default base path the UI is served under when none is given.</summary>
    public const string DefaultBasePath = "/neoreports";

    /// <summary>
    /// Registers the services the NeoReports UI needs (Razor Pages, Blazor Server, the
    /// builder-wizard state, and the engine API client). Safe to call alongside a host that
    /// already uses them.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configureApi">
    /// Optional callback to customize <see cref="NeoReportsApiOptions"/> (e.g. a non-default
    /// <c>ApiPrefix</c> when the host maps <c>MapNeoReports</c> somewhere other than <c>/api</c>).
    /// </param>
    public static IServiceCollection AddNeoReportsUI(
        this IServiceCollection services, Action<NeoReportsApiOptions>? configureApi = null)
    {
        services.AddRazorPages();
        services.AddServerSideBlazor();
        services.AddScoped<BuilderState>();
        services.AddOptions<NeoReportsApiOptions>();
        if (configureApi is not null)
            services.Configure(configureApi);
        services.AddHttpClient<INeoReportsApiClient, NeoReportsApiClient>();
        return services;
    }

    /// <summary>
    /// Serves the NeoReports UI under <paramref name="basePath"/> (default
    /// <c>/neoreports</c>). The path is a branch: static assets, the Blazor hub and
    /// every UI route live below it, so the host's own routes are untouched.
    /// </summary>
    /// <param name="app">The host application pipeline.</param>
    /// <param name="basePath">
    /// Base path to mount the UI at, starting with '/' (e.g. <c>"/reports-admin"</c>).
    /// </param>
    public static IApplicationBuilder UseNeoReportsUI(this IApplicationBuilder app, string basePath = DefaultBasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        if (!basePath.StartsWith('/') || basePath.Length < 2)
        {
            throw new ArgumentException(
                $"The UI base path must start with '/' and not be the root (got '{basePath}').", nameof(basePath));
        }

        app.Map(basePath.TrimEnd('/'), ui =>
        {
            ui.UseStaticFiles();
            ui.UseRouting();
            ui.UseEndpoints(endpoints =>
            {
                endpoints.MapBlazorHub();
                endpoints.MapFallbackToPage("/_Host");
            });
        });
        return app;
    }
}
