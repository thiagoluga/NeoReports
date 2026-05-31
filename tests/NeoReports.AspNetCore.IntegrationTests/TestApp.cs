using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NeoReports.Abstractions;
using NeoReports.AspNetCore;
using NeoReports.AspNetCore.DependencyInjection;
using NeoReports.Core.Building;
using NeoReports.Core.DependencyInjection;
using NeoReports.Jobs.DependencyInjection;
using static NeoReports.Core.Building.ReportColumns;
using static NeoReports.Formats.Csv.Format;

namespace NeoReports.AspNetCore.IntegrationTests;

/// <summary>Reference row type for the API tests.</summary>
public sealed record Venda(long Id, string Cliente);

/// <summary>In-memory batch source synthesizing a fixed number of rows, one page at a time.</summary>
public sealed class InMemorySource : IBatchSource<Venda>
{
    private readonly long _rows;
    private readonly int _pageSize;
    private readonly TimeSpan _delay;

    public InMemorySource(long rows, int pageSize, TimeSpan delay = default)
    {
        _rows = rows;
        _pageSize = pageSize;
        _delay = delay;
    }

    public ReportSchema Schema { get; } = new(new[] { new ReportColumn("Id", ColumnType.Integer) });

    public async Task<BatchResult<Venda>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        if (_delay > TimeSpan.Zero)
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);

        var last = context.Cursor is null ? 0L : long.Parse(context.Cursor, System.Globalization.CultureInfo.InvariantCulture);
        var start = last + 1;
        if (start > _rows)
            return BatchResult<Venda>.Empty;

        var end = Math.Min(start + _pageSize - 1, _rows);
        var rows = new List<Venda>();
        for (var id = start; id <= end; id++)
            rows.Add(new Venda(id, $"C{id}"));

        var hasMore = end < _rows;
        return new BatchResult<Venda>(rows, hasMore ? end.ToString(System.Globalization.CultureInfo.InvariantCulture) : null, hasMore);
    }
}

/// <summary>Builds a TestServer host wired with NeoReports endpoints, in-memory jobs, and artifacts.</summary>
public static class TestApp
{
    public static async Task<IHost> StartAsync(Action<IServiceCollection>? configureReports = null, [CallerMemberName] string testName = "")
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), "nr-api-tests", testName + "-" + Guid.NewGuid().ToString("N"));

        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddLogging();

                    if (configureReports is not null)
                    {
                        configureReports(services);
                    }
                    else
                    {
                        services.AddReport<Venda>("vendas", b => b
                            .From(new InMemorySource(rows: 30, pageSize: 10))
                            .Column(v => v.Id, "ID")
                            .Column(v => v.Cliente, "Cliente")
                            .To(Csv(o => o.Delimiter(';'))));
                    }

                    services.AddNeoReportsInMemoryJobs();
                    services.AddNeoReportsArtifacts(artifactsRoot);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapNeoReports("/api"));
                });
            })
            .StartAsync();

        return host;
    }
}
