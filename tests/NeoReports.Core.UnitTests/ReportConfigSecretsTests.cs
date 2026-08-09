using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Core.Configuration;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

/// <summary>ADR D86: redacting a stored configuration for an editor, and restoring it on the way back.</summary>
public class ReportConfigSecretsTests
{
    private const string Document = """
        {
          "name": "sales",
          "source": {
            "type": "sql",
            "properties": {
              "sql": "SELECT Id FROM Sales",
              "key": "Id",
              "pageSize": 500,
              "connectionString": "Server=db;User=sa;Password=hunter2",
              "apiKey": "${SALES_API_KEY}"
            }
          },
          "columns": [{ "name": "Id", "type": "Integer" }],
          "outputs": [{ "format": "csv", "properties": { "delimiter": ";" } }],
          "destinations": [{ "type": "s3", "properties": { "bucket": "reports", "accessKey": "AKIAEXAMPLE" } }]
        }
        """;

    private static JsonElement Properties(string document, string section)
    {
        using JsonDocument doc = JsonDocument.Parse(document);
        JsonElement root = doc.RootElement.Clone();
        return section switch
        {
            "source" => root.GetProperty("source").GetProperty("properties").Clone(),
            "output" => root.GetProperty("outputs").EnumerateArray().First().GetProperty("properties").Clone(),
            _ => root.GetProperty("destinations").EnumerateArray().First().GetProperty("properties").Clone(),
        };
    }

    [Fact]
    public void A_literal_credential_is_replaced_by_the_placeholder()
    {
        JsonElement properties = Properties(ReportConfigSecrets.Redact(Document), "source");

        properties.GetProperty("connectionString").GetString().ShouldBe(ReportConfigSecrets.RedactedValue);
    }

    [Fact]
    public void An_environment_placeholder_is_left_alone()
    {
        JsonElement properties = Properties(ReportConfigSecrets.Redact(Document), "source");

        // The secret lives in the environment, not in the document — hiding this would only cost
        // the user the ability to see and change which variable the report reads.
        properties.GetProperty("apiKey").GetString().ShouldBe("${SALES_API_KEY}");
    }

    [Fact]
    public void Ordinary_configuration_survives_redaction()
    {
        JsonElement properties = Properties(ReportConfigSecrets.Redact(Document), "source");

        properties.GetProperty("sql").GetString().ShouldBe("SELECT Id FROM Sales");
        properties.GetProperty("key").GetString().ShouldBe("Id");
        properties.GetProperty("pageSize").GetInt32().ShouldBe(500);
    }

    [Fact]
    public void Destination_and_output_bags_are_redacted_too()
    {
        string redacted = ReportConfigSecrets.Redact(Document);

        Properties(redacted, "destination").GetProperty("accessKey").GetString().ShouldBe(ReportConfigSecrets.RedactedValue);
        Properties(redacted, "destination").GetProperty("bucket").GetString().ShouldBe("reports");
        Properties(redacted, "output").GetProperty("delimiter").GetString().ShouldBe(";");
    }

    [Theory]
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("clientSecret")]
    [InlineData("bearerToken")]
    [InlineData("privateKey")]
    [InlineData("sasToken")]
    [InlineData("oauth2ClientSecret")]
    public void Credential_bearing_key_names_are_matched_case_insensitively_and_by_substring(string key)
    {
        string document = """{"source":{"properties":{"KEY":"literal"}}}""".Replace("KEY", key, StringComparison.Ordinal);

        Properties(ReportConfigSecrets.Redact(document), "source").GetProperty(key).GetString()
            .ShouldBe(ReportConfigSecrets.RedactedValue);
    }

    [Fact]
    public void A_url_carrying_userinfo_is_redacted_whatever_the_key_is_called()
    {
        // Value-based rather than name-based: "url" is about as innocent as a key name gets, and
        // https://user:pass@host is a credential regardless.
        string document = """{"source":{"properties":{"url":"https://admin:hunter2@api.example.com/items"}}}""";

        Properties(ReportConfigSecrets.Redact(document), "source").GetProperty("url").GetString()
            .ShouldBe(ReportConfigSecrets.RedactedValue);
    }

    [Fact]
    public void A_plain_url_is_not_redacted()
    {
        string document = """{"source":{"properties":{"url":"https://api.example.com/items"}}}""";

        Properties(ReportConfigSecrets.Redact(document), "source").GetProperty("url").GetString()
            .ShouldBe("https://api.example.com/items");
    }

    [Fact]
    public void A_member_spelled_with_different_casing_is_still_walked()
    {
        // The JSON parser matches member names case-insensitively, so a hand-written document may
        // spell them any way it likes. A structural walk that did not would leak the secret.
        string document = """{"Source":{"Properties":{"connectionString":"Password=hunter2"}}}""";

        using JsonDocument doc = JsonDocument.Parse(ReportConfigSecrets.Redact(document));
        doc.RootElement.GetProperty("Source").GetProperty("Properties").GetProperty("connectionString")
            .GetString().ShouldBe(ReportConfigSecrets.RedactedValue);
    }

    [Fact]
    public void Redact_then_Restore_returns_the_original_values()
    {
        string redacted = ReportConfigSecrets.Redact(Document);

        string restored = ReportConfigSecrets.Restore(redacted, Document);

        Properties(restored, "source").GetProperty("connectionString").GetString()
            .ShouldBe("Server=db;User=sa;Password=hunter2");
        Properties(restored, "destination").GetProperty("accessKey").GetString().ShouldBe("AKIAEXAMPLE");
    }

    [Fact]
    public void Restore_leaves_a_value_the_editor_actually_replaced()
    {
        string edited = ReportConfigSecrets.Redact(Document)
            .Replace($"\"connectionString\":\"{ReportConfigSecrets.RedactedValue}\"", "\"connectionString\":\"${NEW_DB}\"", StringComparison.Ordinal);

        string restored = ReportConfigSecrets.Restore(edited, Document);

        Properties(restored, "source").GetProperty("connectionString").GetString().ShouldBe("${NEW_DB}");
    }

    [Fact]
    public void Restore_pairs_outputs_and_destinations_by_identity_not_by_position()
    {
        const string stored = """
            {"destinations":[{"type":"local","properties":{"path":"./out"}},
                             {"type":"s3","properties":{"accessKey":"AKIAEXAMPLE"}}]}
            """;
        // The editor reordered them; matching by index would restore the S3 key into the local
        // destination, which is both wrong and invisible.
        const string reordered = """
            {"destinations":[{"type":"s3","properties":{"accessKey":"${neoreports:redacted}"}},
                             {"type":"local","properties":{"path":"./out"}}]}
            """;

        using JsonDocument doc = JsonDocument.Parse(ReportConfigSecrets.Restore(reordered, stored));
        doc.RootElement.GetProperty("destinations").EnumerateArray().First()
            .GetProperty("properties").GetProperty("accessKey").GetString().ShouldBe("AKIAEXAMPLE");
    }

    [Fact]
    public void Restore_rejects_a_placeholder_with_nothing_to_restore_from()
    {
        const string stored = """{"source":{"properties":{"sql":"SELECT 1"}}}""";
        const string incoming = """{"source":{"properties":{"connectionString":"${neoreports:redacted}"}}}""";

        // Rejected rather than dropped: persisting the literal placeholder as a connection string
        // would fail later, somewhere much less obvious.
        Should.Throw<ConfigurationException>(() => ReportConfigSecrets.Restore(incoming, stored))
            .Message.ShouldContain("connectionString");
    }

    [Fact]
    public void ContainsRedactedValue_finds_a_placeholder_anywhere_it_can_appear()
    {
        ReportConfigSecrets.ContainsRedactedValue(Document).ShouldBeFalse();
        ReportConfigSecrets.ContainsRedactedValue(ReportConfigSecrets.Redact(Document)).ShouldBeTrue();
        ReportConfigSecrets.ContainsRedactedValue(
            """{"outputs":[{"format":"csv","properties":{"x":"${neoreports:redacted}"}}]}""").ShouldBeTrue();
    }

    [Fact]
    public void A_placeholder_that_reaches_compilation_is_rejected_rather_than_used_literally()
    {
        // The colon keeps the sentinel outside ReportConfigEnvironment's ${NAME} grammar, which
        // means the substitution pass would otherwise wave it through as an ordinary string — and a
        // report would go live with the literal "${neoreports:redacted}" as its connection string.
        // The endpoint guards are the first line; this is the one that does not depend on any
        // particular caller remembering.
        Should.Throw<ConfigurationException>(() => ReportConfigEnvironment.Substitute(new ReportConfig(
            "sales",
            new SourceConfig("sql", new Dictionary<string, object?> { ["connectionString"] = ReportConfigSecrets.RedactedValue }),
            [new ColumnConfig("Id", ColumnType.Integer)],
            [new OutputConfig("csv")])));
    }

    [Fact]
    public void An_unreadable_document_is_reported_as_a_configuration_error()
    {
        Should.Throw<ConfigurationException>(() => ReportConfigSecrets.Redact("not json"));
        Should.Throw<ConfigurationException>(() => ReportConfigSecrets.Redact("   "));
        Should.Throw<ConfigurationException>(() => ReportConfigSecrets.Redact("[1,2,3]"));
    }
}
