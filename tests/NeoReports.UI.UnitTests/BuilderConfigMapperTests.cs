using System.Text.Json;
using NeoReports.UI.Services;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests;

/// <summary>Epic D / D6: <see cref="BuilderConfigMapper.ToConfigJson"/> — BuilderState to config JSON.</summary>
public class BuilderConfigMapperTests
{
    private const string PropertiesMember = "properties";

    private static readonly string[] IdCustomerAmountColumns = { "Id", "Customer", "Amount" };
    private static readonly string[] CsvXlsxFormats = { "csv", "xlsx" };
    private static readonly string[] ZetaAlphaMiddleColumns = { "Zeta", "Alpha", "Middle" };
    private static readonly string[] UrlPropertyOnly = { "url" };

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
        state.SourceProperties =
        [
            new() { Key = "url", Value = "https://api.example.com/items" },
            new() { Key = "strategy", Value = "cursor" },
        ];

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
        state.SourceProperties =
        [
            new() { Key = "url", Value = "https://api.example.com/items" },
            new() { Key = "  ", Value = "ignored" },
        ];

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        JsonElement properties = doc.RootElement.GetProperty("source").GetProperty("properties");
        properties.EnumerateObject().Select(p => p.Name).ShouldBe(UrlPropertyOnly);
    }

    [Fact]
    public void Non_ado_source_type_still_sends_connectionString_when_a_variable_is_set()
    {
        var state = FullState();
        state.SourceType = "http";
        state.SourceProperties = [new() { Key = "url", Value = "https://api.example.com" }];

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

    // ---- Editing an existing report (ADR D86) --------------------------------------------------

    private const string StoredDocument = """
        {
          "name": "monthly-sales",
          "source": {
            "type": "sql",
            "properties": {
              "sql": "SELECT Id FROM Sales ORDER BY Id",
              "key": "Id",
              "connectionString": "${neoreports:redacted}",
              "commandTimeoutSeconds": 90
            }
          },
          "columns": [{ "name": "Id", "type": "Integer", "displayName": "Sale ID" }],
          "outputs": [{ "format": "xlsx", "properties": { "autoFilter": true } }],
          "destinations": [
            { "type": "local", "properties": { "path": "./out/{name}.{ext}" } },
            { "type": "s3", "properties": { "bucket": "reports", "path": "{name}.{ext}" } }
          ],
          "pageSize": 1000,
          "filter": { "==": [{ "var": "Active" }, true] }
        }
        """;

    private static BuilderState HydratedState()
    {
        var state = new BuilderState();
        BuilderConfigMapper.Hydrate(state, StoredDocument).ShouldBeTrue();
        return state;
    }

    [Fact]
    public void Hydrate_reads_back_everything_the_wizard_edits()
    {
        var state = HydratedState();

        state.ReportName.ShouldBe("monthly-sales");
        state.SourceType.ShouldBe("sql");
        state.SqlQuery.ShouldBe("SELECT Id FROM Sales ORDER BY Id");
        state.KeyColumn.ShouldBe("Id");
        state.ColumnNames.ShouldBe("Id");
        state.Formats.ShouldBe(["xlsx"]);
        state.DestinationType.ShouldBe("local");
        state.DestinationPath.ShouldBe("./out/{name}.{ext}");
        state.PageSize.ShouldBe(1000);
        state.AdditionalDestinationCount.ShouldBe(1);
        state.ConnectionStringRedacted.ShouldBeTrue();
        state.ConnectionStringVariable.ShouldBe("");
    }

    [Fact]
    public void Hydrate_returns_false_for_a_document_it_cannot_read()
    {
        var state = new BuilderState();

        BuilderConfigMapper.Hydrate(state, "not json at all").ShouldBeFalse();

        state.OriginalDocument.ShouldBeNull();
        state.ReportName.ShouldBe("");
    }

    [Fact]
    public void An_untouched_edit_round_trips_everything_the_wizard_cannot_show()
    {
        var state = HydratedState();

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));
        JsonElement root = doc.RootElement;

        // None of these have an editor in the wizard. Regenerating the document from the form —
        // what this used to do — deleted every one of them on the way past.
        root.GetProperty("filter").GetProperty("==").GetArrayLength().ShouldBe(2);
        root.GetProperty("columns").EnumerateArray().Single().GetProperty("type").GetString().ShouldBe("Integer");
        root.GetProperty("columns").EnumerateArray().Single().GetProperty("displayName").GetString().ShouldBe("Sale ID");
        root.GetProperty("outputs").EnumerateArray().Single().GetProperty("properties").GetProperty("autoFilter").GetBoolean().ShouldBeTrue();
        root.GetProperty("source").GetProperty("properties").GetProperty("commandTimeoutSeconds").GetInt32().ShouldBe(90);

        JsonElement[] destinations = root.GetProperty("destinations").EnumerateArray().ToArray();
        destinations.Length.ShouldBe(2);
        destinations[1].GetProperty("type").GetString().ShouldBe("s3");
        destinations[1].GetProperty("properties").GetProperty("bucket").GetString().ShouldBe("reports");
    }

    [Fact]
    public void An_untouched_redacted_connection_string_is_sent_back_as_the_placeholder()
    {
        var state = HydratedState();

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        doc.RootElement.GetProperty("source").GetProperty("properties")
            .GetProperty("connectionString").GetString().ShouldBe("${neoreports:redacted}");
    }

    [Fact]
    public void Naming_a_connection_variable_replaces_the_redacted_one()
    {
        var state = HydratedState();
        state.ConnectionStringVariable = "SALES_DB";

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        doc.RootElement.GetProperty("source").GetProperty("properties")
            .GetProperty("connectionString").GetString().ShouldBe("${SALES_DB}");
    }

    [Fact]
    public void An_untouched_generic_property_keeps_its_original_json_type()
    {
        var state = new BuilderState();
        BuilderConfigMapper.Hydrate(state, """
            {"name":"feed","source":{"type":"http","properties":{"pageSize":90,"hasHeader":true,"url":"https://x"}}}
            """).ShouldBeTrue();

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        // Every row in the generic editor is text. Writing them all back as strings would turn 90
        // into "90" and true into "true" on every single edit — silently, and only some providers
        // parse their way out of it.
        JsonElement properties = doc.RootElement.GetProperty("source").GetProperty("properties");
        properties.GetProperty("pageSize").ValueKind.ShouldBe(JsonValueKind.Number);
        properties.GetProperty("hasHeader").ValueKind.ShouldBe(JsonValueKind.True);
        properties.GetProperty("url").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public void An_edited_generic_property_becomes_the_text_the_user_typed()
    {
        var state = new BuilderState();
        BuilderConfigMapper.Hydrate(state, """
            {"name":"feed","source":{"type":"http","properties":{"pageSize":90}}}
            """).ShouldBeTrue();
        state.SourceProperties.Single().Value = "120";

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        // Typed values are text — the editor has no type picker, and guessing would make a password
        // of "12345" into a number.
        doc.RootElement.GetProperty("source").GetProperty("properties").GetProperty("pageSize")
            .GetString().ShouldBe("120");
    }

    [Fact]
    public void A_json_null_property_survives_an_untouched_round_trip()
    {
        var state = new BuilderState();
        BuilderConfigMapper.Hydrate(state, """
            {"name":"feed","source":{"type":"http","properties":{"proxy":null,"url":"https://x"}}}
            """).ShouldBeTrue();

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        // A JSON null IS a null node, so "present but null" has to be told apart from "absent" —
        // otherwise the row reads as changed and `null` is written back as `""` on every edit.
        JsonElement properties = doc.RootElement.GetProperty("source").GetProperty(PropertiesMember);
        properties.GetProperty("proxy").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void Two_outputs_of_the_same_format_both_survive_an_edit()
    {
        var state = new BuilderState();
        BuilderConfigMapper.Hydrate(state, """
            {"name":"feed","source":{"type":"http"},
             "outputs":[{"format":"csv","properties":{"delimiter":";"}},
                        {"format":"csv","properties":{"delimiter":"|"}},
                        {"format":"xlsx"}]}
            """).ShouldBeTrue();

        // The Format step is a set of checkboxes and collapses these to {csv, xlsx}; emitting one
        // output per distinct format would silently delete the second csv on any edit.
        state.AdditionalOutputCount.ShouldBe(1);

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        JsonElement[] outputs = doc.RootElement.GetProperty("outputs").EnumerateArray().ToArray();
        outputs.Length.ShouldBe(3);
        outputs.Where(o => o.GetProperty("format").GetString() == "csv")
            .Select(o => o.GetProperty(PropertiesMember).GetProperty("delimiter").GetString())
            .ShouldBe([";", "|"]);
    }

    [Fact]
    public void Clearing_a_format_removes_every_output_of_it()
    {
        var state = new BuilderState();
        BuilderConfigMapper.Hydrate(state, """
            {"name":"feed","source":{"type":"http"},
             "outputs":[{"format":"csv"},{"format":"csv"},{"format":"xlsx"}]}
            """).ShouldBeTrue();
        state.Formats.Remove("csv");

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        doc.RootElement.GetProperty("outputs").EnumerateArray()
            .Select(o => o.GetProperty("format").GetString()).ShouldBe(["xlsx"]);
    }

    [Fact]
    public void Switching_the_source_drops_the_stored_properties_and_the_kept_connection()
    {
        var state = HydratedState();
        state.SourceType = "http";
        state.SourceProperties = [new() { Key = "url", Value = "https://api.example.com" }];

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        JsonElement properties = doc.RootElement.GetProperty("source").GetProperty("properties");
        properties.EnumerateObject().Select(p => p.Name).ShouldBe(["url"]);
        // Restoring the old connection into a source nobody pointed it at is the one outcome that
        // would be both invisible and wrong.
        properties.TryGetProperty("connectionString", out _).ShouldBeFalse();
        properties.TryGetProperty("commandTimeoutSeconds", out _).ShouldBeFalse();
    }

    [Fact]
    public void Changing_the_destination_type_does_not_inherit_the_old_types_properties()
    {
        var state = HydratedState();
        state.DestinationType = "s3";
        state.DestinationPath = "{name}.{ext}";

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        JsonElement[] destinations = doc.RootElement.GetProperty("destinations").EnumerateArray().ToArray();
        destinations[0].GetProperty("type").GetString().ShouldBe("s3");
        destinations[0].GetProperty("properties").GetProperty("path").GetString().ShouldBe("{name}.{ext}");
        // The wizard edits the first destination; the second is a different one and stays put.
        destinations[1].GetProperty("type").GetString().ShouldBe("s3");
    }

    [Fact]
    public void Adding_a_column_types_it_as_String_without_disturbing_the_existing_ones()
    {
        var state = HydratedState();
        state.ColumnNames = "Id, Customer";

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        JsonElement[] columns = doc.RootElement.GetProperty("columns").EnumerateArray().ToArray();
        columns[0].GetProperty("type").GetString().ShouldBe("Integer");
        columns[1].GetProperty("name").GetString().ShouldBe("Customer");
        columns[1].GetProperty("type").GetString().ShouldBe("String");
    }

    [Fact]
    public void Removing_the_only_destination_keeps_the_ones_the_wizard_never_showed()
    {
        var state = HydratedState();
        state.DestinationType = "";

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        JsonElement destination = doc.RootElement.GetProperty("destinations").EnumerateArray().Single();
        destination.GetProperty("type").GetString().ShouldBe("s3");
    }

    [Fact]
    public void A_document_with_differently_cased_members_is_patched_not_duplicated()
    {
        var state = new BuilderState();
        BuilderConfigMapper.Hydrate(state, """{"Name":"legacy","Source":{"Type":"sql","Properties":{"sql":"SELECT 1","key":"Id"}}}""")
            .ShouldBeTrue();
        state.PageSize = 42;

        using JsonDocument doc = JsonDocument.Parse(BuilderConfigMapper.ToConfigJson(state));

        // The engine reads member names case-insensitively, so a hand-written document may spell
        // them any way it likes; emitting both "Name" and "name" would be a document the engine
        // parses with one of them silently winning.
        doc.RootElement.EnumerateObject().Count(p => string.Equals(p.Name, "name", StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
        doc.RootElement.GetProperty("name").GetString().ShouldBe("legacy");
        doc.RootElement.GetProperty("source").GetProperty("properties").GetProperty("sql").GetString().ShouldBe("SELECT 1");
    }
}
