using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core;
using NeoReports.Core.Configuration;
using NeoReports.Core.Pipeline;
using NeoReports.Destinations.Local;
using NeoReports.Formats.Csv;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Http.UnitTests;

/// <summary>
/// A report defined entirely in JSON config reads from an HTTP/REST endpoint (ADR D61) and writes
/// CSV, end to end — the HTTP analog of <c>NeoReports.Sources.Csv.UnitTests.DynamicConfigCsvTests</c>.
/// </summary>
public class DynamicConfigHttpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nr-dyn-http", Guid.NewGuid().ToString("N"));

    public DynamicConfigHttpTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task Config_drives_http_source_to_csv_output_end_to_end()
    {
        var outDir = Path.Combine(_dir, "out");

        HttpClient client = StubHttpMessageHandler.CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """[{"id":1,"customer":"C1","amount":10.5},{"id":2,"customer":"C2","amount":20.0}]""",
                Encoding.UTF8, "application/json"),
        }, out _);

        const string json = """
        {
          "name": "sales-http-dyn",
          "pageSize": 1000,
          "source": {
            "type": "http",
            "properties": { "url": "http://api.test/sales" }
          },
          "columns": [
            { "name": "id", "type": "Integer", "nullable": false },
            { "name": "customer", "type": "String" },
            { "name": "amount", "type": "Decimal" }
          ],
          "outputs": [ { "format": "csv" } ],
          "destinations": [ { "type": "local" } ]
        }
        """;

        ReportConfig config = new JsonReportConfigParser().Parse(json);

        var services = new ServiceCollection();
        services.AddHttpConfigSource();
        services.AddSingleton(client);
        services.AddSingleton<IWriterFactory>(new CsvWriterFactory(new CsvOptions()));
        services.AddSingleton<IDestinationFactory>(new LocalDestinationFactory(Path.Combine(outDir, "{name}.{ext}")));
        await using ServiceProvider provider = services.BuildServiceProvider();

        CompiledReport report = ReportConfigCompiler.Compile(config, provider);
        var exec = new ReportExecutionContext(Guid.NewGuid().ToString("N"), report.Name, null, NullLogger.Instance, CancellationToken.None);
        ReportRunResult result = await ReportRunner.ExecuteAsync(report, exec, provider, CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Completed);
        result.Stats.RecordsRead.ShouldBe(2);
        result.Stats.RecordsWritten.ShouldBe(2);

        var outputPath = Path.Combine(outDir, "sales-http-dyn.csv");
        File.Exists(outputPath).ShouldBeTrue();
        string[] lines = await File.ReadAllLinesAsync(outputPath);
        lines[0].ShouldBe("id,customer,amount");
        lines[1].ShouldBe("1,C1,10.5");
        lines[2].ShouldBe("2,C2,20");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
