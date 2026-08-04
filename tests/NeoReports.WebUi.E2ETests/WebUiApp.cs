using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NeoReports.Abstractions;
using NeoReports.AspNetCore;
using NeoReports.AspNetCore.DependencyInjection;
using NeoReports.Core.DependencyInjection;
using NeoReports.Destinations.Local;
using NeoReports.Formats.Csv;
using NeoReports.Formats.Xlsx;
using NeoReports.Jobs.DependencyInjection;
using NeoReports.Samples.Shared;
using NeoReports.UI;

namespace NeoReports.WebUi.E2ETests;

/// <summary>
/// Boots the product the way <c>samples/09-web-ui-live</c> does — the Blazor UI and the engine in one
/// host — on a real Kestrel port, so a browser can drive it.
/// <para>
/// A real listening socket is the point: the UI is Blazor <b>Server</b>, so every click travels over a
/// SignalR circuit. <c>TestServer</c> has no port and cannot carry one, which is why these tests don't
/// use <c>WebApplicationFactory</c>.
/// </para>
/// <para>
/// Every piece of state (report configs, artifacts, the local destination's output) is redirected to a
/// per-instance temp directory, so a run leaves nothing behind and two runs can't collide.
/// </para>
/// </summary>
public sealed class WebUiApp : IAsyncDisposable
{
    private readonly WebApplication _app;

    private WebUiApp(WebApplication app, string baseUrl, string root)
    {
        _app = app;
        BaseUrl = baseUrl;
        Root = root;
    }

    /// <summary>Origin the app is listening on, e.g. <c>http://127.0.0.1:53123</c>.</summary>
    public string BaseUrl { get; }

    /// <summary>Per-instance temp directory holding configs, artifacts and destination output.</summary>
    public string Root { get; }

    /// <summary>Base path the UI is mounted at (matches the sample's default).</summary>
    public string UiPath => NeoReportsUIExtensions.DefaultBasePath;

    /// <summary>Absolute URL of a UI page, e.g. <c>Ui("/reports")</c>.</summary>
    public string Ui(string relative = "") => BaseUrl + UiPath + relative;

    /// <summary>Starts the host on a free port and returns once it is accepting requests.</summary>
    public static async Task<WebUiApp> StartAsync()
    {
        string root = Path.Join(Path.GetTempPath(), "nr-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        // ApplicationName must be THIS assembly, not the default (the entry assembly, which under
        // `dotnet test` is testhost). MVC discovers a Razor Class Library's compiled pages through the
        // named application's dependency context, so leaving it as testhost hides the UI's own _Host
        // page and every UI route 500s with "Cannot find the fallback endpoint { page: /_Host }".
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(WebUiApp).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // The default builder wires a Razor Class Library's static assets (_content/...) only in the
        // Development environment; this host runs as Production, so without this every stylesheet and
        // font the UI ships 404s. The page still renders — which is the trap: unstyled, Blazor's own
        // error overlay is no longer hidden by CSS and looks like a fault that isn't one.
        builder.WebHost.UseStaticWebAssets();

        // Port 0 lets the OS pick a free one — no fixed port to collide with a parallel run.
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddNeoReportsUI();

        // The same engine wiring as samples/09-web-ui-live, with every path pointed at `root`.
        builder.Services.AddDynamicReports(o => o.Directory = Path.Join(root, "configs"));
        builder.Services.AddSingleton<IConfigSourceProvider, InMemorySalesSourceProvider>();
        builder.Services.AddSingleton<IWriterFactory>(new CsvWriterFactory(new CsvOptions()));
        builder.Services.AddSingleton<IWriterFactory>(new XlsxWriterFactory(new XlsxOptions()));
        builder.Services.AddSingleton<IDestinationFactory>(
            new LocalDestinationFactory(Path.Join(root, "out", "{name}-{date:yyyy-MM-dd}.{ext}")));
        builder.Services.AddNeoReportsInMemoryJobs();
        builder.Services.AddNeoReportsArtifacts(Path.Join(root, "artifacts"));
        builder.Services.AddInMemoryJobEvents();
        builder.Services.AddInMemoryScheduling();
        builder.Services.AddInMemorySourceRegistry();

        WebApplication app = builder.Build();
        app.MapNeoReports("/api");
        app.UseNeoReportsUI(NeoReportsUIExtensions.DefaultBasePath);
        app.MapGet("/", () => Results.Redirect(NeoReportsUIExtensions.DefaultBasePath));

        await app.StartAsync();

        string baseUrl = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();

        return new WebUiApp(app, baseUrl.TrimEnd('/'), root);
    }

    /// <summary>Stops the host and deletes its temp directory.</summary>
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();

        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A file the host still holds must not fail the test run.
        }
    }
}
