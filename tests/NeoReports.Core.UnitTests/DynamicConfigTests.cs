using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Configuration;
using NeoReports.Core.Pipeline;
using NeoReports.Core.Sections;
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
        "properties": {
          "connectionString": "Server=.",
          "limit": 10,
          "ratio": 1.5,
          "active": true,
          "disabled": false,
          "since": "2026-01-15T00:00:00Z",
          "missing": null,
          "nested": { "a": 1 }
        }
      },
      "columns": [
        { "name": "Id", "type": "Integer", "displayName": "Sale ID", "nullable": false },
        { "name": "Customer", "type": "String" }
      ],
      "outputs": [ { "format": "csv", "properties": { "delimiter": ";" } } ],
      "destinations": [ { "type": "capture" } ]
    }
    """;

    private static readonly JsonReportConfigParser Parser = new();

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
        var props = config.Source.Properties!;
        props["connectionString"].ShouldBe("Server=.");
        props["limit"].ShouldBe(10L);   // integer JSON number → long
        props["ratio"].ShouldBe(1.5);   // fractional JSON number → double
        props["active"].ShouldBe(true);
        props["disabled"].ShouldBe(false);
        props["since"].ShouldBe(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)); // ISO string → DateTime
        props["missing"].ShouldBeNull();
        props["nested"].ShouldBeOfType<JsonElement>(); // nested object preserved as raw element

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
    public async Task Compiles_and_applies_a_jsonlogic_filter()
    {
        // The filter is written as a JsonLogic object (captured as raw JSON by the parser) and keeps
        // only rows where Id > 1.
        const string filtered = """
        {
          "name": "sales",
          "source": { "type": "inmemory" },
          "columns": [
            { "name": "Id", "type": "Integer" },
            { "name": "Customer", "type": "String" }
          ],
          "outputs": [ { "format": "csv" } ],
          "destinations": [ { "type": "capture" } ],
          "filter": { ">": [ { "var": "Id" }, 1 ] }
        }
        """;
        var config = Parser.Parse(filtered);
        config.Filter.ShouldNotBeNull(); // object syntax captured as raw JSON text
        var services = BuildServices(out _, out var destination, (1, "Acme"), (2, "Globex"), (3, "Initech"));

        var report = ReportConfigCompiler.Compile(config, services);
        var result = await ReportRunner.ExecuteAsync(report, Exec(), services, CancellationToken.None);

        result.Stats.RecordsRead.ShouldBe(3);
        result.Stats.RecordsWritten.ShouldBe(2); // Ids 2 and 3

        var content = Encoding.UTF8.GetString(destination.LastDestination!.Files["sales.csv"]);
        content.ShouldNotContain("Acme");
        content.ShouldContain("Globex");
        content.ShouldContain("Initech");
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

    private static readonly string[] BigSmall = { "Big", "Small" };
    private static readonly long[] BigIds = { 2, 3 };
    private static readonly long[] SmallIds = { 1 };

    [Fact]
    public async Task Compiles_a_sectioned_workbook_output_from_config()
    {
        const string json = """
        {
          "name": "sales",
          "source": { "type": "inmemory" },
          "columns": [
            { "name": "Id", "type": "Integer" },
            { "name": "Customer", "type": "String" }
          ],
          "outputs": [
            {
              "format": "sectioned-fake",
              "sections": [
                { "name": "Big", "filter": { ">": [ { "var": "Id" }, 1 ] } },
                { "name": "Small", "filter": { "<=": [ { "var": "Id" }, 1 ] }, "columns": [ "Id" ] }
              ]
            }
          ]
        }
        """;
        var config = Parser.Parse(json);
        config.Outputs[0].Sections.ShouldNotBeNull().Count.ShouldBe(2); // parsed from JSON

        var writerFactory = new FakeSectionedWriterFactory();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(
            new[] { new object?[] { 1L, "Acme" }, new object?[] { 2L, "Globex" }, new object?[] { 3L, "Initech" } }));
        services.AddSingleton<ISectionedWriterFactory>(writerFactory);
        await using var provider = services.BuildServiceProvider();

        var report = ReportConfigCompiler.Compile(config, provider);
        var result = await ReportRunner.ExecuteAsync(report, Exec(), provider, CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Completed);

        var writer = writerFactory.Last!;
        writer.InitSections.Select(x => x.Name).ShouldBe(BigSmall);
        writer.Sections[0].Select(r => (long)r[0]!).ShouldBe(BigIds); // Big: Id > 1, all report columns
        writer.Sections[0].ShouldAllBe(r => r.Length == 2);
        writer.Sections[1].Select(r => (long)r[0]!).ShouldBe(SmallIds); // Small: Id <= 1, one column
        writer.Sections[1].ShouldAllBe(r => r.Length == 1);
    }
}
