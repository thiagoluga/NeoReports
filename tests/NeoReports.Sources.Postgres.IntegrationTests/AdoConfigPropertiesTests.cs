using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Sources.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Postgres.IntegrationTests;

/// <summary>
/// Pure-logic tests for the shared relational-provider helpers in <c>NeoReports.Sources.Common</c>
/// — no database or Docker required.
/// </summary>
public class AdoConfigPropertiesTests
{
    [Fact]
    public void RequireString_returns_the_value_when_present_and_non_empty()
    {
        var properties = new Dictionary<string, object?> { ["sql"] = "SELECT 1" };
        AdoConfigProperties.RequireString(properties, "sql", "Test").ShouldBe("SELECT 1");
    }

    [Fact]
    public void RequireString_throws_when_missing()
    {
        Should.Throw<ConfigurationException>(() => AdoConfigProperties.RequireString(null, "sql", "Test"));
        Should.Throw<ConfigurationException>(() =>
            AdoConfigProperties.RequireString(new Dictionary<string, object?>(), "sql", "Test"));
    }

    [Fact]
    public void RequireString_throws_when_blank()
    {
        var properties = new Dictionary<string, object?> { ["sql"] = "   " };
        Should.Throw<ConfigurationException>(() => AdoConfigProperties.RequireString(properties, "sql", "Test"));
    }

    [Fact]
    public void OptionalInt_returns_null_when_missing()
    {
        AdoConfigProperties.OptionalInt(null, "pageSize", "Test").ShouldBeNull();
        AdoConfigProperties.OptionalInt(new Dictionary<string, object?>(), "pageSize", "Test").ShouldBeNull();
    }

    [Theory]
    [InlineData(500)]
    public void OptionalInt_reads_a_clr_int(int value)
    {
        var properties = new Dictionary<string, object?> { ["pageSize"] = value };
        AdoConfigProperties.OptionalInt(properties, "pageSize", "Test").ShouldBe(value);
    }

    [Fact]
    public void OptionalInt_reads_a_clr_long()
    {
        var properties = new Dictionary<string, object?> { ["pageSize"] = 500L };
        AdoConfigProperties.OptionalInt(properties, "pageSize", "Test").ShouldBe(500);
    }

    [Fact]
    public void OptionalInt_reads_a_clr_double()
    {
        var properties = new Dictionary<string, object?> { ["pageSize"] = 500.0 };
        AdoConfigProperties.OptionalInt(properties, "pageSize", "Test").ShouldBe(500);
    }

    [Fact]
    public void OptionalInt_parses_a_numeric_string()
    {
        var properties = new Dictionary<string, object?> { ["pageSize"] = "500" };
        AdoConfigProperties.OptionalInt(properties, "pageSize", "Test").ShouldBe(500);
    }

    [Fact]
    public void OptionalInt_reads_a_json_number_element()
    {
        using var doc = JsonDocument.Parse("500");
        var properties = new Dictionary<string, object?> { ["pageSize"] = doc.RootElement };
        AdoConfigProperties.OptionalInt(properties, "pageSize", "Test").ShouldBe(500);
    }

    [Fact]
    public void OptionalInt_throws_for_an_unparseable_type()
    {
        var properties = new Dictionary<string, object?> { ["pageSize"] = true };
        Should.Throw<ConfigurationException>(() => AdoConfigProperties.OptionalInt(properties, "pageSize", "Test"));
    }

    [Fact]
    public void MaterializeReportRecord_reads_declared_columns_and_nulls_out_missing_ones()
    {
        var schema = new ReportSchema(new[] { new ReportColumn("Id", ColumnType.Integer), new ReportColumn("Missing", ColumnType.String) });
        var record = AdoConfigProperties.MaterializeReportRecord(
            new FakeReader(new Dictionary<string, object?> { ["Id"] = 1L }), new Dictionary<string, int> { ["Id"] = 0 }, schema);

        record.Values[0].ShouldBe(1L);
        record.Values[1].ShouldBeNull();
    }

    private sealed class FakeReader : System.Data.Common.DbDataReader
    {
        private readonly IReadOnlyDictionary<string, object?> _row;
        public FakeReader(IReadOnlyDictionary<string, object?> row) => _row = row;

        public override object GetValue(int ordinal) => _row.Values.ElementAt(ordinal) ?? DBNull.Value;
        public override bool IsDBNull(int ordinal) => _row.Values.ElementAt(ordinal) is null;

        public override int FieldCount => _row.Count;
        public override bool Read() => true;
        public override bool HasRows => true;

        // Members below aren't exercised by MaterializeReportRecord — minimal stubs.
        public override object this[int ordinal] => throw new NotSupportedException();
        public override object this[string name] => throw new NotSupportedException();
        public override int Depth => 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => -1;
        public override bool NextResult() => false;
        public override void Close() { }
        public override System.Data.DataTable? GetSchemaTable() => null;
        public override System.Collections.IEnumerator GetEnumerator() => throw new NotSupportedException();
        public override bool GetBoolean(int ordinal) => throw new NotSupportedException();
        public override byte GetByte(int ordinal) => throw new NotSupportedException();
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override char GetChar(int ordinal) => throw new NotSupportedException();
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override string GetDataTypeName(int ordinal) => throw new NotSupportedException();
        public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();
        public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();
        public override double GetDouble(int ordinal) => throw new NotSupportedException();
        public override Type GetFieldType(int ordinal) => throw new NotSupportedException();
        public override float GetFloat(int ordinal) => throw new NotSupportedException();
        public override Guid GetGuid(int ordinal) => throw new NotSupportedException();
        public override short GetInt16(int ordinal) => throw new NotSupportedException();
        public override int GetInt32(int ordinal) => throw new NotSupportedException();
        public override long GetInt64(int ordinal) => throw new NotSupportedException();
        public override string GetName(int ordinal) => _row.Keys.ElementAt(ordinal);
        public override int GetOrdinal(string name) => _row.Keys.ToList().IndexOf(name);
        public override string GetString(int ordinal) => throw new NotSupportedException();
        public override int GetValues(object[] values) => throw new NotSupportedException();
    }
}
