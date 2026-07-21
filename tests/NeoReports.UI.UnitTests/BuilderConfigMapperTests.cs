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
    public void AbortWhen_is_omitted_when_failure_strategy_is_abort()
    {
        var state = FullState();
        state.FailureStrategy = "abort";
        state.AbortOnConsecutiveFailures = true;
        state.AbortConsecutiveFailures = 3;

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        doc.RootElement.GetProperty("resilience").TryGetProperty("abortWhen", out _).ShouldBeFalse();
    }

    [Fact]
    public void AbortWhen_is_omitted_when_no_threshold_switch_is_on()
    {
        var state = FullState();
        state.FailureStrategy = "skip-and-log";

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        doc.RootElement.GetProperty("resilience").TryGetProperty("abortWhen", out _).ShouldBeFalse();
    }

    [Fact]
    public void AbortWhen_serializes_only_the_enabled_thresholds()
    {
        var state = FullState();
        state.FailureStrategy = "skip-and-log";
        state.AbortOnConsecutiveFailures = true;
        state.AbortConsecutiveFailures = 3;
        state.AbortOnFailureRate = true;
        state.AbortFailureRatePercent = 25;

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        JsonElement abortWhen = doc.RootElement.GetProperty("resilience").GetProperty("abortWhen");
        abortWhen.GetProperty("consecutiveFailures").GetInt32().ShouldBe(3);
        abortWhen.GetProperty("failureRate").GetDouble().ShouldBe(0.25);
        abortWhen.TryGetProperty("totalFailures", out _).ShouldBeFalse();
    }

    [Fact]
    public void Blank_ScheduleCron_omits_the_schedule_field()
    {
        var state = FullState();
        state.ScheduleCron = "";

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        doc.RootElement.TryGetProperty("schedule", out _).ShouldBeFalse();
    }

    [Fact]
    public void ScheduleCron_serializes_a_schedule_object()
    {
        var state = FullState();
        state.ScheduleCron = "0 6 * * 1";

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        doc.RootElement.GetProperty("schedule").GetProperty("cron").GetString().ShouldBe("0 6 * * 1");
    }

    [Fact]
    public void ScheduleCron_is_trimmed()
    {
        var state = FullState();
        state.ScheduleCron = "  0 6 * * 1  ";

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        doc.RootElement.GetProperty("schedule").GetProperty("cron").GetString().ShouldBe("0 6 * * 1");
    }

    [Fact]
    public void SourceRef_serializes_a_ref_field_and_omits_type_and_connection_string()
    {
        var state = FullState();
        state.SourceRef = "sales-db";

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        JsonElement source = doc.RootElement.GetProperty("source");
        source.GetProperty("ref").GetString().ShouldBe("sales-db");
        source.TryGetProperty("type", out _).ShouldBeFalse();
        source.GetProperty("properties").TryGetProperty("connectionString", out _).ShouldBeFalse();

        // Query/key stay report-local overlay properties even for a ref-based source.
        source.GetProperty("properties").GetProperty("sql").GetString().ShouldBe(state.SqlQuery);
        source.GetProperty("properties").GetProperty("key").GetString().ShouldBe(state.KeyColumn);
    }

    [Fact]
    public void Blank_SourceRef_omits_the_ref_field_and_uses_the_inline_type()
    {
        var state = FullState();
        state.SourceRef = "";

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        JsonElement source = doc.RootElement.GetProperty("source");
        source.TryGetProperty("ref", out _).ShouldBeFalse();
        source.GetProperty("type").GetString().ShouldBe("sql");
    }

    [Fact]
    public void SourceRef_is_trimmed()
    {
        var state = FullState();
        state.SourceRef = "  sales-db  ";

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        doc.RootElement.GetProperty("source").GetProperty("ref").GetString().ShouldBe("sales-db");
    }

    [Fact]
    public void Report_name_passes_through_unchanged()
    {
        var state = FullState();
        state.ReportName = "some-weird-Name_123";

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        doc.RootElement.GetProperty("name").GetString().ShouldBe("some-weird-Name_123");
    }

    [Fact]
    public void TrackProgress_defaults_to_true_and_is_always_serialized()
    {
        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(FullState()));

        doc.RootElement.GetProperty("trackProgress").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void TrackProgress_false_is_serialized_explicitly_not_omitted()
    {
        var state = FullState();
        state.TrackProgress = false;

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        doc.RootElement.GetProperty("trackProgress").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void Non_ado_source_type_serializes_the_generic_property_rows_not_sql_and_key()
    {
        var state = FullState();
        state.SourceType = "http";
        state.SourceProperties = new List<PropertyRow>
        {
            new() { Key = "url", Value = "https://api.example.com/items" },
            new() { Key = "strategy", Value = "cursor" },
        };

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        JsonElement properties = doc.RootElement.GetProperty("source").GetProperty("properties");
        properties.GetProperty("url").GetString().ShouldBe("https://api.example.com/items");
        properties.GetProperty("strategy").GetString().ShouldBe("cursor");
        properties.TryGetProperty("sql", out _).ShouldBeFalse();
        properties.TryGetProperty("key", out _).ShouldBeFalse();
    }

    [Fact]
    public void Non_ado_source_type_omits_rows_with_a_blank_key()
    {
        var state = FullState();
        state.SourceType = "http";
        state.ConnectionStringVariable = "";
        state.SourceProperties = new List<PropertyRow>
        {
            new() { Key = "url", Value = "https://api.example.com/items" },
            new() { Key = "  ", Value = "ignored" },
        };

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        JsonElement properties = doc.RootElement.GetProperty("source").GetProperty("properties");
        properties.EnumerateObject().Select(p => p.Name).ShouldBe(new[] { "url" });
    }

    [Fact]
    public void Non_ado_source_type_still_sends_connectionString_when_a_variable_is_set()
    {
        var state = FullState();
        state.SourceType = "http";
        state.SourceProperties = new List<PropertyRow> { new() { Key = "url", Value = "https://api.example.com" } };

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        doc.RootElement.GetProperty("source").GetProperty("properties")
            .GetProperty("connectionString").GetString().ShouldBe("${SALES_DB}");
    }

    [Fact]
    public void Ado_shape_types_still_use_the_sql_and_key_fields()
    {
        var state = FullState();
        state.SourceType = "postgres";

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        JsonElement properties = doc.RootElement.GetProperty("source").GetProperty("properties");
        properties.GetProperty("sql").GetString().ShouldBe(state.SqlQuery);
        properties.GetProperty("key").GetString().ShouldBe(state.KeyColumn);
    }
}
