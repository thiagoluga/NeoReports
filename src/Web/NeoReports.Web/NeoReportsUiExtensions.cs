using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.Web.Services;

namespace NeoReports.Web;

/// <summary>
/// Mounts the NeoReports web UI (a Razor Class Library) into a host application.
/// </summary>
/// <example>
/// <code>
/// builder.Services.AddNeoReportsUi();
/// ...
/// app.UseNeoReportsUi("/neoreports");
/// </code>
/// </example>
public static class NeoReportsUiExtensions
{
    /// <summary>Default base path the UI is served under when none is given.</summary>
    public const string DefaultBasePath = "/neoreports";

    /// <summary>
    /// Registers the services the NeoReports UI needs (Razor Pages, Blazor Server and
    /// the builder-wizard state). Safe to call alongside a host that already uses them.
    /// </summary>
    public static IServiceCollection AddNeoReportsUi(this IServiceCollection services)
    {
        services.AddRazorPages();
        services.AddServerSideBlazor();
        services.AddScoped<BuilderState>();
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
    public static IApplicationBuilder UseNeoReportsUi(this IApplicationBuilder app, string basePath = DefaultBasePath)
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
