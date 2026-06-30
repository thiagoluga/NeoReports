using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Configuration;
using NeoReports.Core.Pipeline;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

/// <summary>
/// Epic A / A2: a JSON document is parsed into a <see cref="ReportConfig"/> and compiled into the
/// same runnable report the fluent path produces. The source/format/destination are resolved from
/// DI by their stable ids; the compiled report runs on the existing pipeline.
/// </summary>
public class DynamicConfigTests
{
    private const string Json = """
    {
      "name": "sales",
      "pageSize": 500,
      "source": {
        "type": "inmemory",
        "properties": { "connectionString": "Server=.", "limit": 10 }
      },
      "columns": [
        { "name": "Id", "type": "Integer", "displayName": "Sale ID", "nullable": false },
        { "name": "Customer", "type": "String" }
      ],
      "outputs": [ { "format": "csv", "properties": { "delimiter": ";" } } ],
      "destinations": [ { "type": "capture" } ]
    }
    """;

    private static readonly IReportConfigParser Parser = new JsonReportConfigParser();

    private static ReportExecutionContext Exec() =>
        new(Guid.NewGuid().ToString("N"), "sales", null, NullLogger.Instance, CancellationToken.None);

    private static ServiceProvider BuildServices(
        out FakeConfigSourceProvider source,
        out CapturingDestinationFactory destination,
        params (long Id, string Customer)[] rows)
    {
        source = new FakeConfigSourceProvider(
            rows.Select(r => new object?[] { r.Id, r.Customer }).ToArray());
        destination = new CapturingDestinationFactory();

        var services = new ServiceCollection();
        services.AddSingleton<IConfigSourceProvider>(source);
        services.AddSingleton<IWriterFactory>(new FakeWriterFactory("csv", "csv"));
        services.AddSingleton<IDestinationFactory>(destination);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Parses_all_sections_and_converts_property_values_to_clr_primitives()
    {
        var config = Parser.Parse(Json);

        config.Name.ShouldBe("sales");
        config.PageSize.ShouldBe(500);

        config.Source.Type.ShouldBe("inmemory");
        config.Source.Properties!["connectionString"].ShouldBe("Server=.");
        config.Source.Properties["limit"].ShouldBe(10L); // JSON number → long, not JsonElement

        config.Columns.Count.ShouldBe(2);
        config.Columns[0].Type.ShouldBe(ColumnType.Integer); // enum read from string
        config.Columns[0].DisplayName.ShouldBe("Sale ID");
        config.Columns[0].Nullable.ShouldBeFalse();
        config.Columns[1].Nullable.ShouldBeTrue(); // default when omitted

        config.Outputs.ShouldHaveSingleItem().Format.ShouldBe("csv");
        config.Outputs[0].Properties!["delimiter"].ShouldBe(";");
        config.Destinations.ShouldNotBeNull().ShouldHaveSingleItem().Type.ShouldBe("capture");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    public void Rejects_empty_or_malformed_documents(string document) =>
        Should.Throw<ConfigurationException>(() => Parser.Parse(document));

    [Fact]
    public async Task Compiles_and_runs_the_config_through_the_pipeline()
    {
        var config = Parser.Parse(Json);
        var services = BuildServices(out var source, out var destination, (1, "Acme"), (2, "Globex"));

        var report = ReportConfigCompiler.Compile(config, services);
        var result = await ReportRunner.ExecuteAsync(report, Exec(), services, CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Completed);
        result.Stats.RecordsRead.ShouldBe(2);
        result.Stats.RecordsWritten.ShouldBe(2);

        // The provider received the parsed source section (the connection string flows through).
        source.LastConfig!.Properties!["connectionString"].ShouldBe("Server=.");

        var content = Encoding.UTF8.GetString(destination.LastDestination!.Files["sales.csv"]);
        content.ShouldContain("Acme");
        content.ShouldContain("Globex");
    }

    [Fact]
    public void Compile_rejects_a_filter_until_the_jsonlogic_epic()
    {
        const string withFilter = """
        {
          "name": "r",
          "source": { "type": "inmemory" },
          "columns": [ { "name": "Id", "type": "Integer" } ],
          "outputs": [ { "format": "csv" } ],
          "filter": "{\"==\":[{\"var\":\"Id\"},1]}"
        }
        """;
        var config = Parser.Parse(withFilter);
        var services = BuildServices(out _, out _, (1, "x"));

        var ex = Should.Throw<ConfigurationException>(() => ReportConfigCompiler.Compile(config, services));
        ex.Message.ShouldContain("filter");
    }

    [Fact]
    public void Compile_fails_when_a_referenced_factory_is_not_registered()
    {
        const string xlsx = """
        {
          "name": "r",
          "source": { "type": "inmemory" },
          "columns": [ { "name": "Id", "type": "Integer" } ],
          "outputs": [ { "format": "xlsx" } ]
        }
        """;
        var config = Parser.Parse(xlsx);
        var services = BuildServices(out _, out _, (1, "x")); // only "csv" writer is registered

        var ex = Should.Throw<ConfigurationException>(() => ReportConfigCompiler.Compile(config, services));
        ex.Message.ShouldContain("xlsx");
    }
}
