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

    // A signed or keyed URL is the credential — that is the whole point of a SAS or a pre-signed
    // URL — and it lives under "url"/"baseUrl"/"instanceUrl", the required properties of every
    // shipped HTTP-family source and the most innocuous key names there are.
    [Theory]
    [InlineData("https://acct.blob.core.windows.net/c/d.json?sv=2023-01-01&sig=Ab3%2FsecretSig%3D")]
    [InlineData("https://bucket.s3.amazonaws.com/k?X-Amz-Signature=deadbeef&X-Amz-Expires=900")]
    [InlineData("https://sheets.googleapis.com/v4/spreadsheets/1/values/A1?key=AIzaSyLiveKey")]
    [InlineData("https://api.example.com/items?access_token=live-abc")]
    [InlineData("https://login.example.com/callback?code=authorization-code")]
    public void A_url_whose_query_carries_a_credential_is_redacted(string url)
    {
        string document = """{"source":{"properties":{"url":"URL"}}}""".Replace("URL", url, StringComparison.Ordinal);

        Properties(ReportConfigSecrets.Redact(document), "source").GetProperty("url").GetString()
            .ShouldBe(ReportConfigSecrets.RedactedValue);
    }

    [Fact]
    public void A_url_whose_query_is_ordinary_is_left_alone()
    {
        // Over-matching costs only visibility, but a URL is the field an editor most needs to see,
        // so an ordinary query must not trip the rule.
        const string url = "https://api.example.com/items?page=2&pageSize=100&orderBy=id";
        string document = """{"source":{"properties":{"url":"URL"}}}""".Replace("URL", url, StringComparison.Ordinal);

        Properties(ReportConfigSecrets.Redact(document), "source").GetProperty("url").GetString().ShouldBe(url);
    }

    [Theory]
    [InlineData("Cookie")]
    [InlineData("X-Api-Key")]
    [InlineData("sessionId")]
    public void Credential_header_names_the_fragment_list_used_to_miss_are_redacted(string header)
    {
        // "Authorization" always matched (via "auth"); these did not, and a header bag is walked
        // into by name, so each one was a plaintext credential in the response.
        string document = """{"source":{"properties":{"headers":{"HEADER":"live-value"}}}}"""
            .Replace("HEADER", header, StringComparison.Ordinal);

        Properties(ReportConfigSecrets.Redact(document), "source").GetProperty("headers")
            .GetProperty(header).GetString().ShouldBe(ReportConfigSecrets.RedactedValue);
    }

    [Fact]
    public void The_keyset_key_property_is_still_not_treated_as_a_credential()
    {
        // "key" is credential-shaped only as a query parameter. As a property-bag key it is the ADO
        // keyset column — redacting it would hide the report's pagination from its own editor.
        Properties(ReportConfigSecrets.Redact(Document), "source").GetProperty("key").GetString().ShouldBe("Id");
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

    // ---- Nested property-bag values -----------------------------------------------------------
    //
    // A bag value is not always a scalar: PrimitiveObjectConverter keeps nested objects and arrays,
    // an HTTP source declares "headers" as an object (Authorization lives there), and a merge-join
    // source nests whole child sources with their own connection strings. A top-level-only walk
    // returned every one of those in plaintext.

    [Fact]
    public void A_secret_nested_inside_an_object_is_redacted()
    {
        const string document = """
            {"source":{"type":"http","properties":{
              "url":"https://api.example.com",
              "headers":{"Accept":"application/json","Authorization":"Bearer sk-live-abc123"}}}}
            """;

        JsonElement headers = Properties(ReportConfigSecrets.Redact(document), "source").GetProperty("headers");
        headers.GetProperty("Authorization").GetString().ShouldBe(ReportConfigSecrets.RedactedValue);
        headers.GetProperty("Accept").GetString().ShouldBe("application/json");
    }

    [Fact]
    public void A_merge_join_child_sources_connection_string_is_redacted()
    {
        const string document = """
            {"source":{"type":"merge-join","properties":{
              "key":"customerId",
              "left":{"type":"sql","properties":{"connectionString":"Server=a;Password=p1","sql":"SELECT 1"}},
              "right":{"type":"sql","properties":{"connectionString":"Server=b;Password=p2","sql":"SELECT 2"}}}}}
            """;

        JsonElement properties = Properties(ReportConfigSecrets.Redact(document), "source");
        foreach (string side in new[] { "left", "right" })
        {
            JsonElement child = properties.GetProperty(side).GetProperty("properties");
            child.GetProperty("connectionString").GetString().ShouldBe(ReportConfigSecrets.RedactedValue);
            child.GetProperty("sql").GetString().ShouldNotBe(ReportConfigSecrets.RedactedValue);
        }
    }

    [Fact]
    public void A_secret_named_key_hides_its_whole_subtree_rather_than_being_descended_into()
    {
        // Descending into "credentials" would mean guessing at inner key names — exactly the
        // fail-open behaviour the fragment list is designed to avoid.
        const string document = """{"source":{"properties":{"credentials":{"user":"admin","pass":"hunter2"}}}}""";

        Properties(ReportConfigSecrets.Redact(document), "source").GetProperty("credentials").GetString()
            .ShouldBe(ReportConfigSecrets.RedactedValue);
    }

    [Fact]
    public void Array_elements_are_walked_and_inherit_the_enclosing_key()
    {
        const string document = """
            {"source":{"properties":{
              "urls":["https://plain.example.com","https://user:pw@secret.example.com"],
              "children":[{"apiKey":"live-1"},{"apiKey":"live-2"}]}}}
            """;

        JsonElement properties = Properties(ReportConfigSecrets.Redact(document), "source");
        JsonElement[] urls = properties.GetProperty("urls").EnumerateArray().ToArray();
        urls[0].GetString().ShouldBe("https://plain.example.com");
        urls[1].GetString().ShouldBe(ReportConfigSecrets.RedactedValue);
        properties.GetProperty("children").EnumerateArray()
            .Select(child => child.GetProperty("apiKey").GetString())
            .ShouldAllBe(value => value == ReportConfigSecrets.RedactedValue);
    }

    [Fact]
    public void A_nested_secret_round_trips_through_Redact_and_Restore()
    {
        const string document = """
            {"source":{"type":"http","properties":{
              "headers":{"Authorization":"Bearer sk-live-abc123"},
              "children":[{"apiKey":"live-1"}]}}}
            """;

        string restored = ReportConfigSecrets.Restore(ReportConfigSecrets.Redact(document), document);

        JsonElement properties = Properties(restored, "source");
        properties.GetProperty("headers").GetProperty("Authorization").GetString().ShouldBe("Bearer sk-live-abc123");
        properties.GetProperty("children").EnumerateArray().Single().GetProperty("apiKey").GetString().ShouldBe("live-1");
    }

    [Fact]
    public void A_redacted_array_element_is_restored_by_index()
    {
        // Descending only into object elements left a scalar element holding the literal sentinel,
        // which was then persisted — found by round-tripping a real report through the running app,
        // after every flat-bag test had passed.
        const string stored = """{"source":{"properties":{"mirrors":["https://plain.example.com","https://u:p@secret.example.com"]}}}""";

        string restored = ReportConfigSecrets.Restore(ReportConfigSecrets.Redact(stored), stored);

        Properties(restored, "source").GetProperty("mirrors").EnumerateArray()
            .Select(m => m.GetString())
            .ShouldBe(["https://plain.example.com", "https://u:p@secret.example.com"]);
    }

    [Fact]
    public void A_redacted_array_element_with_nothing_stored_is_rejected()
    {
        const string stored = """{"source":{"properties":{"mirrors":[]}}}""";
        const string incoming = """{"source":{"properties":{"mirrors":["${neoreports:redacted}"]}}}""";

        Should.Throw<ConfigurationException>(() => ReportConfigSecrets.Restore(incoming, stored))
            .Message.ShouldContain("mirrors");
    }

    [Fact]
    public void A_nested_placeholder_that_reaches_compilation_is_rejected()
    {
        // The string-only guard could not see this one, which is how it reached disk.
        using JsonDocument headers = JsonDocument.Parse($$"""{"Authorization":"{{ReportConfigSecrets.RedactedValue}}"}""");

        Should.Throw<ConfigurationException>(() => ReportConfigEnvironment.Substitute(new ReportConfig(
            "sales",
            new SourceConfig("http", new Dictionary<string, object?> { ["headers"] = headers.RootElement.Clone() }),
            [new ColumnConfig("Id", ColumnType.Integer)],
            [new OutputConfig("csv")])));
    }

    [Fact]
    public void ContainsRedactedValue_sees_a_nested_placeholder()
    {
        ReportConfigSecrets.ContainsRedactedValue(
            """{"source":{"properties":{"headers":{"Authorization":"${neoreports:redacted}"}}}}""").ShouldBeTrue();
        ReportConfigSecrets.ContainsRedactedValue(
            """{"source":{"properties":{"children":[{"apiKey":"${neoreports:redacted}"}]}}}""").ShouldBeTrue();
    }

    // ---- Sections that share an identity --------------------------------------------------------

    [Fact]
    public void Restore_pairs_repeated_section_ids_by_occurrence_not_by_first_match()
    {
        // Nothing stops a report writing to two S3 buckets. Pairing on identity alone would restore
        // bucket A's access key into bucket B — silently, with both sections looking ordinary.
        const string stored = """
            {"destinations":[{"type":"s3","properties":{"bucket":"alpha","accessKey":"KEY-ALPHA"}},
                             {"type":"s3","properties":{"bucket":"beta","accessKey":"KEY-BETA"}}]}
            """;

        string restored = ReportConfigSecrets.Restore(ReportConfigSecrets.Redact(stored), stored);

        JsonElement[] destinations = JsonDocument.Parse(restored).RootElement
            .GetProperty("destinations").EnumerateArray().Select(d => d.Clone()).ToArray();
        destinations[0].GetProperty("properties").GetProperty("accessKey").GetString().ShouldBe("KEY-ALPHA");
        destinations[1].GetProperty("properties").GetProperty("accessKey").GetString().ShouldBe("KEY-BETA");
    }

    [Fact]
    public void An_unreadable_document_is_reported_as_a_configuration_error()
    {
        Should.Throw<ConfigurationException>(() => ReportConfigSecrets.Redact("not json"));
        Should.Throw<ConfigurationException>(() => ReportConfigSecrets.Redact("   "));
        Should.Throw<ConfigurationException>(() => ReportConfigSecrets.Redact("[1,2,3]"));
    }
}
