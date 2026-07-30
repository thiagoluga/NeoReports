using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NeoReports.AspNetCore;
using NeoReports.Core.Building;
using NeoReports.Core.DependencyInjection;
using Shouldly;
using Xunit;
using static NeoReports.Core.Building.ReportColumns;
using static NeoReports.Formats.Csv.Format;

namespace NeoReports.AspNetCore.IntegrationTests;

public class AuthWarningTests
{
    private static async Task<IReadOnlyCollection<string>> WarningsFromMapping(
        Action<IServiceCollection>? extraServices, Action<NeoReportsEndpointOptions>? options)
    {
        var capture = new CapturingLoggerProvider();
        using var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddLogging(b => b.AddProvider(capture));
                    services.AddReport<Sale>("sales", b => b
                        .From(new InMemorySource(rows: 1, pageSize: 10))
                        .Column(v => v.Id, "ID")
                        .To(Csv(o => o.Delimiter(';'))));
                    extraServices?.Invoke(services);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapNeoReports("/api", options));
                });
            })
            .StartAsync();

        return capture.Warnings;
    }

    [Fact]
    public async Task Warns_when_mapped_without_auth_and_no_authentication_configured()
    {
        var warnings = await WarningsFromMapping(extraServices: null, options: null);
        warnings.ShouldContain(w => w.Contains("reachable", StringComparison.Ordinal) && w.Contains("/api", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Does_not_warn_when_the_host_has_authentication_configured()
    {
        var warnings = await WarningsFromMapping(
            extraServices: s => s.AddAuthentication(), options: null);
        warnings.ShouldNotContain(w => w.Contains("reachable unauthenticated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Does_not_warn_when_authorization_is_required()
    {
        var warnings = await WarningsFromMapping(
            extraServices: s => s.AddAuthorizationBuilder(),
            options: o => o.RequireAuthorization = true);
        warnings.ShouldNotContain(w => w.Contains("reachable unauthenticated", StringComparison.Ordinal));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentBag<string> Warnings { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Warnings);

        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentBag<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning)
                    warnings.Add(formatter(state, exception));
            }
        }
    }
}
