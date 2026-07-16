using NeoReports.Core.QueryBuilder;
using NeoReports.QueryBuilder.Pro;
using Shouldly;
using Xunit;

namespace NeoReports.QueryBuilder.Pro.UnitTests;

/// <summary>
/// ADR D49 (K5a): the <see cref="QuerySqlGenerator"/> is the Pro side of the MIT
/// <see cref="IQuerySqlGenerator"/> seam. It deserializes the UI's raw model JSON (camelCase, string
/// enums), resolves the dialect from the source type, and delegates to <see cref="KeysetSqlGenerator"/>.
/// The keyset SQL shaping itself is covered by <see cref="KeysetSqlGeneratorTests"/>; here we cover the
/// JSON binding, the dialect selection, and the caller-safe failure translation.
/// </summary>
public class QuerySqlGeneratorTests
{
    private static readonly IQuerySqlGenerator Generator = new QuerySqlGenerator();

    // A minimal but valid single-table model, serialized exactly as the UI would (camelCase + string enum).
    private const string ValidModelJson = """
        {
          "from": { "schema": "public", "name": "customers", "alias": "t0" },
          "joins": [],
          "select": [
            { "source": { "tableAlias": "t0", "column": "id" }, "outputName": "Id", "dataType": "integer", "aggregate": "None" },
            { "source": { "tableAlias": "t0", "column": "name" }, "outputName": "Name", "dataType": "varchar" }
          ],
          "where": [
            { "column": { "tableAlias": "t0", "column": "city" }, "operator": "Equals", "value": "Porto", "dataType": "varchar" }
          ],
          "groupBy": [],
          "key": { "tableAlias": "t0", "column": "id" },
          "keyDataType": "integer"
        }
        """;

    [Fact]
    public void Generate_binds_the_json_model_and_produces_keyset_sql()
    {
        GeneratedReportSql result = Generator.Generate(ValidModelJson, "postgres");

        result.Sql.ShouldContain("FROM \"public\".\"customers\" t0");
        result.Sql.ShouldContain("(@cursor IS NULL OR t0.\"id\" > @cursor::bigint)");
        result.Sql.TrimEnd().ShouldEndWith("ORDER BY t0.\"id\"");
        // The WHERE value is bound, never inlined.
        result.Parameters.Values.ShouldContain("Porto");
        result.Sql.ShouldNotContain("Porto");
        // The schema is derived from the selected columns, in order.
        result.Schema.Columns.Select(c => c.Name).ShouldBe(new[] { "Id", "Name" });
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mysql")]
    [InlineData("sql")] // SQL Server's source-type id
    [InlineData("oracle")]
    public void Generate_supports_every_sql_dialect(string sourceType)
    {
        GeneratedReportSql result = Generator.Generate(ValidModelJson, sourceType);

        result.Sql.ShouldNotBeNullOrWhiteSpace();
        result.Schema.Columns.Count.ShouldBe(2);
    }

    [Fact]
    public void Generate_throws_for_an_unsupported_source_type()
    {
        Should.Throw<QuerySqlGenerationException>(() => Generator.Generate(ValidModelJson, "mongodb"))
            .Message.ShouldContain("mongodb");
    }

    [Fact]
    public void Generate_throws_a_caller_safe_error_for_malformed_json()
    {
        var ex = Should.Throw<QuerySqlGenerationException>(() => Generator.Generate("{ not json", "postgres"));

        // The raw (possibly sensitive) model text is never echoed back.
        ex.Message.ShouldNotContain("not json");
    }

    [Fact]
    public void Generate_surfaces_model_validation_errors_as_generation_errors()
    {
        // A model that parses but is invalid (no select columns) — KeysetSqlGenerator rejects it, and
        // that rejection reaches the caller as a QuerySqlGenerationException, not a leaked internal type.
        const string noColumns = """
            {
              "from": { "schema": "public", "name": "customers", "alias": "t0" },
              "joins": [], "select": [], "where": [], "groupBy": [],
              "key": { "tableAlias": "t0", "column": "id" }, "keyDataType": "integer"
            }
            """;

        Should.Throw<QuerySqlGenerationException>(() => Generator.Generate(noColumns, "postgres"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Generate_rejects_an_empty_model(string modelJson) =>
        Should.Throw<ArgumentException>(() => Generator.Generate(modelJson, "postgres"));

    [Theory]
    [InlineData("{}")] // every required member absent → bound to null by the JSON binder
    [InlineData("""{ "select": [], "joins": [], "where": [], "groupBy": [], "keyDataType": "integer" }""")] // missing from + key
    [InlineData("""{ "from": { "schema": "public", "name": "customers", "alias": "t0" }, "select": [], "joins": [], "where": [], "groupBy": [] }""")] // missing key + keyDataType
    public void Generate_rejects_a_parseable_but_incomplete_model(string modelJson)
    {
        // A null member left by the binder must surface as a caller-safe QuerySqlGenerationException
        // (→ 400), never a NullReferenceException leaking out as a 500.
        Should.Throw<QuerySqlGenerationException>(() => Generator.Generate(modelJson, "postgres"));
    }

    [Fact]
    public void Generate_rejects_an_empty_source_type() =>
        Should.Throw<ArgumentException>(() => Generator.Generate(ValidModelJson, "  "));
}
