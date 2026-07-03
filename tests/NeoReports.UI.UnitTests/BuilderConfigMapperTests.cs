using System.Text.Json;
using NeoReports.UI.Services;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests;

/// <summary>Epic D / D6: <see cref="BuilderConfigMapper.ToConfigJson"/> — BuilderState to config JSON.</summary>
public class BuilderConfigMapperTests
{
    private static readonly string[] IdCustomerAmountColumns = { "Id", "Customer", "Amount" };
    private static readonly string[] CsvXlsxFormats = { "csv", "xlsx" };
    private static readonly string[] ZetaAlphaMiddleColumns = { "Zeta", "Alpha", "Middle" };

    private static BuilderState FullState() => new()
    {
        ReportName = "monthly-sales",
        SourceType = "sql",
        ConnectionStringVariable = "SALES_DB",
        SqlQuery = "SELECT Id, Customer FROM Sales",
        KeyColumn = "Id",
        PageSize = 500,
        ColumnNames = "Id, Customer, Amount",
        Formats = ["csv", "xlsx"],
        DestinationType = "local",
        DestinationPath = "./out/{name}.{ext}",
    };

    [Fact]
    public void Happy_path_produces_the_expected_document_shape()
    {
        string json = BuilderConfigMapper.ToConfigJson(FullState());
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.GetProperty("name").GetString().ShouldBe("monthly-sales");
        root.GetProperty("pageSize").GetInt32().ShouldBe(500);

        JsonElement source = root.GetProperty("source");
        source.GetProperty("type").GetString().ShouldBe("sql");
        source.GetProperty("properties").GetProperty("sql").GetString().ShouldBe("SELECT Id, Customer FROM Sales");
        source.GetProperty("properties").GetProperty("key").GetString().ShouldBe("Id");
        source.GetProperty("properties").GetProperty("connectionString").GetString().ShouldBe("${SALES_DB}");

        JsonElement[] columns = root.GetProperty("columns").EnumerateArray().ToArray();
        columns.Length.ShouldBe(3);
        columns.Select(c => c.GetProperty("name").GetString()).ShouldBe(IdCustomerAmountColumns);
        columns.ShouldAllBe(c => c.GetProperty("type").GetString() == "String");

        JsonElement[] outputs = root.GetProperty("outputs").EnumerateArray().ToArray();
        outputs.Select(o => o.GetProperty("format").GetString()).ShouldBe(CsvXlsxFormats);

        JsonElement[] destinations = root.GetProperty("destinations").EnumerateArray().ToArray();
        destinations.ShouldHaveSingleItem();
        destinations[0].GetProperty("type").GetString().ShouldBe("local");
        destinations[0].GetProperty("properties").GetProperty("path").GetString().ShouldBe("./out/{name}.{ext}");

        // Untouched BuilderState mirrors the engine's own defaults (RetryOptions/FailureStrategyBuilder).
        JsonElement resilience = root.GetProperty("resilience");
        resilience.GetProperty("maxAttempts").GetInt32().ShouldBe(1);
        resilience.GetProperty("backoff").GetString().ShouldBe("Constant");
        resilience.GetProperty("baseDelaySeconds").GetDouble().ShouldBe(1);
        resilience.GetProperty("jitter").GetBoolean().ShouldBeFalse();
        resilience.GetProperty("onFailure").GetString().ShouldBe("abort");
    }

    [Fact]
    public void Custom_resilience_values_are_serialized()
    {
        var state = FullState();
        state.RetryMaxAttempts = 5;
        state.RetryBackoff = "Exponential";
        state.RetryBaseDelaySeconds = 2.5;
        state.RetryJitter = true;
        state.FailureStrategy = "skip-and-log";

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        JsonElement resilience = doc.RootElement.GetProperty("resilience");
        resilience.GetProperty("maxAttempts").GetInt32().ShouldBe(5);
        resilience.GetProperty("backoff").GetString().ShouldBe("Exponential");
        resilience.GetProperty("baseDelaySeconds").GetDouble().ShouldBe(2.5);
        resilience.GetProperty("jitter").GetBoolean().ShouldBeTrue();
        resilience.GetProperty("onFailure").GetString().ShouldBe("skip-and-log");
    }

    [Fact]
    public void Column_order_follows_the_comma_separated_input_order()
    {
        var state = FullState();
        state.ColumnNames = "Zeta, Alpha, Middle";

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        doc.RootElement.GetProperty("columns").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString())
            .ShouldBe(ZetaAlphaMiddleColumns);
    }

    [Fact]
    public void Empty_destination_type_omits_the_destinations_array()
    {
        var state = FullState();
        state.DestinationType = "";

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        doc.RootElement.TryGetProperty("destinations", out _).ShouldBeFalse();
    }

    [Fact]
    public void Empty_destination_path_omits_destination_properties()
    {
        var state = FullState();
        state.DestinationPath = "";

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        JsonElement destination = doc.RootElement.GetProperty("destinations").EnumerateArray().Single();
        destination.TryGetProperty("properties", out _).ShouldBeFalse();
    }

    [Fact]
    public void Empty_connection_string_variable_omits_the_connectionString_property()
    {
        var state = FullState();
        state.ConnectionStringVariable = "";

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        doc.RootElement.GetProperty("source").GetProperty("properties")
            .TryGetProperty("connectionString", out _).ShouldBeFalse();
    }

    [Fact]
    public void Report_name_passes_through_unchanged()
    {
        var state = FullState();
        state.ReportName = "some-weird-Name_123";

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        doc.RootElement.GetProperty("name").GetString().ShouldBe("some-weird-Name_123");
    }
}
