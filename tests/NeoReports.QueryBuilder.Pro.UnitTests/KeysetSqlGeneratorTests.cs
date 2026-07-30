using NeoReports.Abstractions;
using NeoReports.QueryBuilder.Pro;
using Shouldly;
using Xunit;

namespace NeoReports.QueryBuilder.Pro.UnitTests;

public class KeysetSqlGeneratorTests
{
    private static QueryTable Table(string name, string alias, string schema = "public") => new(schema, name, alias);
    private static QueryColumnRef Ref(string alias, string column) => new(alias, column);

    private static QueryModel SingleTable(params QuerySelectColumn[] select) => new(
        From: Table("customers", "t0"),
        Joins: Array.Empty<QueryJoin>(),
        Select: select,
        Where: Array.Empty<QueryFilter>(),
        GroupBy: Array.Empty<QueryColumnRef>(),
        Key: Ref("t0", "id"),
        KeyDataType: "integer");

    [Fact]
    public void Simple_query_selects_from_and_appends_a_keyset_wrapper()
    {
        QueryModel model = SingleTable(
            new QuerySelectColumn(Ref("t0", "id"), "Id", "integer"),
            new QuerySelectColumn(Ref("t0", "name"), "Name", "varchar"));

        GeneratedQuery result = KeysetSqlGenerator.Generate(model, SqlDialect.Postgres);

        result.Sql.ShouldContain("SELECT t0.\"id\" AS \"Id\", t0.\"name\" AS \"Name\"");
        result.Sql.ShouldContain("FROM \"public\".\"customers\" t0");
        // Postgres binds the cursor as text, so an integer key's cursor is cast — the same pattern a
        // hand-written keyset report uses (e.g. `@cursor::uuid`).
        result.Sql.ShouldContain("(@cursor IS NULL OR t0.\"id\" > @cursor::bigint)");
        result.Sql.TrimEnd().ShouldEndWith("ORDER BY t0.\"id\"");
        result.Parameters.ShouldBeEmpty();
    }

    [Fact]
    public void Inner_and_left_joins_render_with_the_on_condition()
    {
        var model = new QueryModel(
            From: Table("orders", "t0"),
            Joins: new[]
            {
                new QueryJoin(JoinKind.Inner, Table("customers", "t1"), Ref("t0", "customer_id"), Ref("t1", "id")),
                new QueryJoin(JoinKind.Left, Table("regions", "t2"), Ref("t1", "region_id"), Ref("t2", "id")),
            },
            Select: new[] { new QuerySelectColumn(Ref("t0", "id"), "Id", "integer") },
            Where: Array.Empty<QueryFilter>(),
            GroupBy: Array.Empty<QueryColumnRef>(),
            Key: Ref("t0", "id"),
            KeyDataType: "integer");

        GeneratedQuery result = KeysetSqlGenerator.Generate(model, SqlDialect.Postgres);

        result.Sql.ShouldContain("INNER JOIN \"public\".\"customers\" t1 ON t0.\"customer_id\" = t1.\"id\"");
        result.Sql.ShouldContain("LEFT JOIN \"public\".\"regions\" t2 ON t1.\"region_id\" = t2.\"id\"");
    }

    [Fact]
    public void Where_values_are_parameterized_never_inlined()
    {
        var model = SingleTable(new QuerySelectColumn(Ref("t0", "id"), "Id", "integer")) with
        {
            Where = new[]
            {
                new QueryFilter(Ref("t0", "name"), QueryFilterOperator.Equals, "Ac'me", "varchar"),
                new QueryFilter(Ref("t0", "city"), QueryFilterOperator.Contains, "port", "varchar"),
            },
        };

        GeneratedQuery result = KeysetSqlGenerator.Generate(model, SqlDialect.Postgres);

        result.Sql.ShouldContain("t0.\"name\" = @qbfilter0");
        result.Sql.ShouldContain("t0.\"city\" LIKE @qbfilter1");
        result.Sql.ShouldNotContain("Ac'me"); // the value never reaches the SQL text
        result.Parameters["qbfilter0"].ShouldBe("Ac'me");
        result.Parameters["qbfilter1"].ShouldBe("%port%");
    }

    [Fact]
    public void Group_by_with_aggregates_renders_and_derives_the_schema()
    {
        var model = new QueryModel(
            From: Table("sales", "t0"),
            Joins: Array.Empty<QueryJoin>(),
            Select: new[]
            {
                new QuerySelectColumn(Ref("t0", "country"), "Country", "varchar"),
                new QuerySelectColumn(Ref("t0", "amount"), "Total", "numeric", QueryAggregate.Sum),
                new QuerySelectColumn(Ref("t0", "id"), "N", "integer", QueryAggregate.Count),
            },
            Where: Array.Empty<QueryFilter>(),
            GroupBy: new[] { Ref("t0", "country") },
            Key: Ref("t0", "country"),
            KeyDataType: "varchar");

        GeneratedQuery result = KeysetSqlGenerator.Generate(model, SqlDialect.Postgres);

        result.Sql.ShouldContain("sum(t0.\"amount\") AS \"Total\"");
        result.Sql.ShouldContain("count(t0.\"id\") AS \"N\"");
        result.Sql.ShouldContain("GROUP BY t0.\"country\"");
        result.Schema.Columns.Select(c => (c.Name, c.Type)).ShouldBe(new[]
        {
            ("Country", ColumnType.String), ("Total", ColumnType.Decimal), ("N", ColumnType.Integer),
        });
    }

    [Fact]
    public void Each_dialect_quotes_identifiers_its_own_way()
    {
        QueryModel model = SingleTable(new QuerySelectColumn(Ref("t0", "id"), "Id", "integer"));

        KeysetSqlGenerator.Generate(model, SqlDialect.Postgres).Sql.ShouldContain("t0.\"id\"");
        KeysetSqlGenerator.Generate(model, SqlDialect.MySql).Sql.ShouldContain("t0.`id`");
        KeysetSqlGenerator.Generate(model, SqlDialect.SqlServer).Sql.ShouldContain("t0.[id]");
        KeysetSqlGenerator.Generate(model, SqlDialect.Oracle).Sql.ShouldContain(":cursor"); // Oracle bind prefix
        KeysetSqlGenerator.Generate(model, SqlDialect.Sqlite).Sql.ShouldContain("t0.\"id\"");
        KeysetSqlGenerator.Generate(model, SqlDialect.Redshift).Sql.ShouldContain("t0.\"id\"");
        KeysetSqlGenerator.Generate(model, SqlDialect.Snowflake).Sql.ShouldContain(":cursor"); // Snowflake bind prefix
    }

    [Fact]
    public void Postgres_casts_the_cursor_to_the_key_type()
    {
        QueryModel model = SingleTable(new QuerySelectColumn(Ref("t0", "id"), "Id", "uuid")) with { KeyDataType = "uuid" };

        GeneratedQuery result = KeysetSqlGenerator.Generate(model, SqlDialect.Postgres);

        result.Sql.ShouldContain("t0.\"id\" > @cursor::uuid");
    }

    [Fact]
    public void Oracle_casts_a_numeric_cursor_with_TO_NUMBER()
    {
        QueryModel model = SingleTable(new QuerySelectColumn(Ref("t0", "id"), "Id", "NUMBER")) with { KeyDataType = "NUMBER" };

        GeneratedQuery result = KeysetSqlGenerator.Generate(model, SqlDialect.Oracle);

        result.Sql.ShouldContain("t0.\"id\" > TO_NUMBER(:cursor,");
    }

    [Fact]
    public void Oracle_casts_a_temporal_cursor_with_TO_TIMESTAMP()
    {
        // Regression: a DATE/TIMESTAMP key's cursor is the codec's ISO-8601 text. Without an explicit
        // format model Oracle implicit-converts it with the session NLS_DATE_FORMAT (not ISO-8601) and
        // throws ORA-01858 on the second page. The generated cast must parse it with the exact model
        // the codec documents.
        QueryModel model = SingleTable(new QuerySelectColumn(Ref("t0", "id"), "Id", "TIMESTAMP")) with { KeyDataType = "TIMESTAMP" };

        GeneratedQuery result = KeysetSqlGenerator.Generate(model, SqlDialect.Oracle);

        result.Sql.ShouldContain("t0.\"id\" > TO_TIMESTAMP(:cursor, 'YYYY-MM-DD\"T\"HH24:MI:SS.FF7')");
    }

    [Fact]
    public void A_hostile_column_name_is_neutralized_by_quoting()
    {
        QueryModel model = SingleTable(new QuerySelectColumn(Ref("t0", "x\"; DROP TABLE t; --"), "X", "varchar"));

        GeneratedQuery result = KeysetSqlGenerator.Generate(model, SqlDialect.Postgres);

        // The closing quote is doubled, so the payload stays a single quoted identifier.
        result.Sql.ShouldContain("t0.\"x\"\"; DROP TABLE t; --\"");
    }

    [Fact]
    public void A_hostile_table_alias_is_rejected()
    {
        var model = SingleTable(new QuerySelectColumn(Ref("t0", "id"), "Id", "integer")) with
        {
            From = Table("customers", "t0; DROP TABLE t; --"),
            Key = Ref("t0; DROP TABLE t; --", "id"),
        };

        Should.Throw<QueryModelException>(() => KeysetSqlGenerator.Generate(model, SqlDialect.Postgres));
    }

    [Fact]
    public void An_alias_with_a_trailing_newline_is_rejected()
    {
        var model = SingleTable(new QuerySelectColumn(Ref("t0", "id"), "Id", "integer")) with
        {
            From = Table("customers", "t0\n"),
            Key = Ref("t0\n", "id"),
        };

        Should.Throw<QueryModelException>(() => KeysetSqlGenerator.Generate(model, SqlDialect.Postgres));
    }

    [Fact]
    public void A_hostile_output_name_is_neutralized_by_quoting()
    {
        QueryModel model = SingleTable(new QuerySelectColumn(Ref("t0", "id"), "x\" FROM secrets; --", "integer"));

        GeneratedQuery result = KeysetSqlGenerator.Generate(model, SqlDialect.Postgres);

        result.Sql.ShouldContain("AS \"x\"\" FROM secrets; --\"");
    }

    [Fact]
    public void A_hostile_join_table_alias_is_rejected()
    {
        var model = new QueryModel(
            From: Table("orders", "t0"),
            Joins: new[] { new QueryJoin(JoinKind.Inner, Table("customers", "t1; DROP TABLE t; --"), Ref("t0", "cid"), Ref("t1; DROP TABLE t; --", "id")) },
            Select: new[] { new QuerySelectColumn(Ref("t0", "id"), "Id", "integer") },
            Where: Array.Empty<QueryFilter>(),
            GroupBy: Array.Empty<QueryColumnRef>(),
            Key: Ref("t0", "id"),
            KeyDataType: "integer");

        Should.Throw<QueryModelException>(() => KeysetSqlGenerator.Generate(model, SqlDialect.Postgres));
    }

    [Fact]
    public void A_hostile_group_by_column_is_neutralized_by_quoting()
    {
        var model = new QueryModel(
            From: Table("sales", "t0"),
            Joins: Array.Empty<QueryJoin>(),
            Select: new[]
            {
                new QuerySelectColumn(Ref("t0", "g\"; --"), "G", "varchar"),
                new QuerySelectColumn(Ref("t0", "amount"), "Total", "numeric", QueryAggregate.Sum),
            },
            Where: Array.Empty<QueryFilter>(),
            GroupBy: new[] { Ref("t0", "g\"; --") },
            Key: Ref("t0", "g\"; --"),
            KeyDataType: "varchar");

        GeneratedQuery result = KeysetSqlGenerator.Generate(model, SqlDialect.Postgres);

        result.Sql.ShouldContain("GROUP BY t0.\"g\"\"; --\"");
    }

    [Fact]
    public void Aggregate_without_group_by_is_rejected()
    {
        QueryModel model = SingleTable(new QuerySelectColumn(Ref("t0", "amount"), "Total", "numeric", QueryAggregate.Sum));

        Should.Throw<QueryModelException>(() => KeysetSqlGenerator.Generate(model, SqlDialect.Postgres));
    }

    [Fact]
    public void A_non_aggregated_selected_column_outside_group_by_is_rejected()
    {
        var model = new QueryModel(
            From: Table("sales", "t0"),
            Joins: Array.Empty<QueryJoin>(),
            Select: new[]
            {
                new QuerySelectColumn(Ref("t0", "country"), "Country", "varchar"),
                new QuerySelectColumn(Ref("t0", "city"), "City", "varchar"), // not grouped, not aggregated
                new QuerySelectColumn(Ref("t0", "amount"), "Total", "numeric", QueryAggregate.Sum),
            },
            Where: Array.Empty<QueryFilter>(),
            GroupBy: new[] { Ref("t0", "country") },
            Key: Ref("t0", "country"),
            KeyDataType: "varchar");

        Should.Throw<QueryModelException>(() => KeysetSqlGenerator.Generate(model, SqlDialect.Postgres));
    }

    [Fact]
    public void The_key_must_be_grouped_when_aggregating()
    {
        var model = new QueryModel(
            From: Table("sales", "t0"),
            Joins: Array.Empty<QueryJoin>(),
            Select: new[]
            {
                new QuerySelectColumn(Ref("t0", "country"), "Country", "varchar"),
                new QuerySelectColumn(Ref("t0", "amount"), "Total", "numeric", QueryAggregate.Sum),
            },
            Where: Array.Empty<QueryFilter>(),
            GroupBy: new[] { Ref("t0", "country") },
            Key: Ref("t0", "id"), // not a grouped column
            KeyDataType: "integer");

        Should.Throw<QueryModelException>(() => KeysetSqlGenerator.Generate(model, SqlDialect.Postgres));
    }

    [Fact]
    public void An_empty_select_is_rejected()
    {
        var model = SingleTable();
        Should.Throw<QueryModelException>(() => KeysetSqlGenerator.Generate(model, SqlDialect.Postgres));
    }

    [Fact]
    public void KeyColumnName_reuses_the_output_name_when_the_key_is_already_selected()
    {
        QueryModel model = SingleTable(
            new QuerySelectColumn(Ref("t0", "id"), "Id", "integer"),
            new QuerySelectColumn(Ref("t0", "name"), "Name", "varchar"));

        GeneratedQuery result = KeysetSqlGenerator.Generate(model, SqlDialect.Postgres);

        result.KeyColumnName.ShouldBe("Id");
        // No extra SELECT item was appended — the key's own output column already covers it.
        result.Sql.ShouldContain("SELECT t0.\"id\" AS \"Id\", t0.\"name\" AS \"Name\"\n");
    }

    [Fact]
    public void KeyColumnName_appends_a_synthetic_column_when_the_key_is_not_selected()
    {
        // The key ("id") is never one of the Select columns (ADR D49/K6c) — the user only picked "name".
        QueryModel model = SingleTable(new QuerySelectColumn(Ref("t0", "name"), "Name", "varchar"));

        GeneratedQuery result = KeysetSqlGenerator.Generate(model, SqlDialect.Postgres);

        result.KeyColumnName.ShouldBe("__neoreports_key");
        result.Sql.ShouldContain("t0.\"id\" AS \"__neoreports_key\"");
        // The synthetic key column is not part of the report's own output schema.
        result.Schema.Columns.Select(c => c.Name).ShouldBe(new[] { "Name" });
    }

    [Fact]
    public void KeyColumnName_avoids_colliding_with_a_real_output_name()
    {
        // The user's own output name happens to be the generator's default synthetic alias.
        QueryModel model = SingleTable(new QuerySelectColumn(Ref("t0", "name"), "__neoreports_key", "varchar"));

        GeneratedQuery result = KeysetSqlGenerator.Generate(model, SqlDialect.Postgres);

        result.KeyColumnName.ShouldBe("__neoreports_key1");
        result.Sql.ShouldContain("t0.\"id\" AS \"__neoreports_key1\"");
    }

    [Fact]
    public void KeyColumnName_appends_the_key_even_when_only_grouped_not_selected()
    {
        // The key ("country") is a GROUP BY column but not itself one of the Select columns.
        var model = new QueryModel(
            From: Table("sales", "t0"),
            Joins: Array.Empty<QueryJoin>(),
            Select: new[] { new QuerySelectColumn(Ref("t0", "amount"), "Total", "numeric", QueryAggregate.Sum) },
            Where: Array.Empty<QueryFilter>(),
            GroupBy: new[] { Ref("t0", "country") },
            Key: Ref("t0", "country"),
            KeyDataType: "varchar");

        GeneratedQuery result = KeysetSqlGenerator.Generate(model, SqlDialect.Postgres);

        result.KeyColumnName.ShouldBe("__neoreports_key");
        result.Sql.ShouldContain("t0.\"country\" AS \"__neoreports_key\"");
        result.Sql.ShouldContain("GROUP BY t0.\"country\"");
        result.Schema.Columns.Select(c => c.Name).ShouldBe(new[] { "Total" });
    }

    [Fact]
    public void ForType_resolves_the_dialect_preset()
    {
        SqlDialect.ForType("postgres").ShouldBe(SqlDialect.Postgres);
        SqlDialect.ForType("oracle").ShouldBe(SqlDialect.Oracle);
        SqlDialect.ForType("sqlite").ShouldBe(SqlDialect.Sqlite);
        SqlDialect.ForType("redshift").ShouldBe(SqlDialect.Redshift);
        SqlDialect.ForType("snowflake").ShouldBe(SqlDialect.Snowflake);
        SqlDialect.ForType("mongodb").ShouldBeNull();
    }
}
