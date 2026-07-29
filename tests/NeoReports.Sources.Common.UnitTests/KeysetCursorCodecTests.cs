using System.Globalization;
using NeoReports.Abstractions;
using NeoReports.Sources.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Common.UnitTests;

public sealed class KeysetCursorCodecTests
{
    [Fact]
    public void FormatKey_Int_RoundTripsInvariant()
    {
        KeysetCursorCodec.FormatKey(42, "Id").ShouldBe("42");
        KeysetCursorCodec.FormatKey(9_000_000_000L, "Id").ShouldBe("9000000000");
    }

    [Fact]
    public void FormatKey_Guid_UsesStandardForm()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        KeysetCursorCodec.FormatKey(id, "Id").ShouldBe("11111111-2222-3333-4444-555555555555");
    }

    [Fact]
    public void FormatKey_Decimal_UsesInvariantCulture_NotHostCulture()
    {
        // A host in a comma-decimal culture must still emit a dot so the DB casts it back correctly.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            KeysetCursorCodec.FormatKey(1234.5m, "Amount").ShouldBe("1234.5");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void FormatKey_DateTime_PreservesSubSecond_AndIsIso8601()
    {
        // The bug: the general date format dropped the fractional second, so same-second rows on
        // the next page re-satisfied `key > @cursor` and were read twice.
        var dt = new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Unspecified).AddTicks(1_234_567);
        var cursor = KeysetCursorCodec.FormatKey(dt, "OrderDate");

        cursor.ShouldBe("2026-07-29T12:00:00.1234567");
        // Round-trips back to the identical instant.
        DateTime.ParseExact(cursor, "O", CultureInfo.InvariantCulture, DateTimeStyles.None).ShouldBe(dt);
    }

    [Fact]
    public void FormatKey_DateTimeUnspecified_HasNoTrailingZ()
    {
        // A datetime2 column reads back as Unspecified; the cursor must not carry a 'Z' (which is
        // datetimeoffset syntax SQL Server's datetime2 cast rejects).
        var dt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);
        KeysetCursorCodec.FormatKey(dt, "OrderDate").ShouldNotContain("Z");
    }

    [Fact]
    public void FormatKey_DateTimeUtc_EmitsTrailingZ_AndRoundTrips()
    {
        // Only reachable from a tz-aware provider (e.g. PostgreSQL timestamptz via Npgsql), never
        // from SQL Server datetime2 (which reads as Unspecified). The 'Z' its own column casts back.
        var dt = new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc).AddTicks(1_234_567);
        var cursor = KeysetCursorCodec.FormatKey(dt, "OrderDate");

        cursor.ShouldEndWith("Z");
        DateTime.ParseExact(cursor, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ShouldBe(dt);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-5)]
    public void FormatKey_DateTimeOffset_KeepsOffset_BothSigns(int offsetHours)
    {
        var dto = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.FromHours(offsetHours)).AddTicks(1_234_567);
        var cursor = KeysetCursorCodec.FormatKey(dto, "OrderDate");

        DateTimeOffset.ParseExact(cursor, "O", CultureInfo.InvariantCulture, DateTimeStyles.None).ShouldBe(dto);
    }

    [Fact]
    public void FormatKey_DateOnly_RoundTrips()
    {
        var d = new DateOnly(2026, 7, 29);
        var cursor = KeysetCursorCodec.FormatKey(d, "Day");

        cursor.ShouldBe("2026-07-29");
        DateOnly.ParseExact(cursor, "O", CultureInfo.InvariantCulture).ShouldBe(d);
    }

    [Fact]
    public void FormatKey_TimeOnly_RoundTrips()
    {
        var t = new TimeOnly(13, 5, 9).Add(TimeSpan.FromTicks(1_234_567));
        var cursor = KeysetCursorCodec.FormatKey(t, "TimeOfDay");

        TimeOnly.ParseExact(cursor, "O", CultureInfo.InvariantCulture).ShouldBe(t);
    }

    [Fact]
    public void FormatKey_ByteArray_ThrowsActionableError_NotSystemByteArray()
    {
        // Convert.ToString(byte[]) returns the literal "System.Byte[]" — a constant cursor that
        // never advances. We fail fast instead.
        var ex = Should.Throw<ConfigurationException>(
            () => KeysetCursorCodec.FormatKey(new byte[] { 0, 0, 0, 7 }, "RowVer"));

        ex.Message.ShouldContain("RowVer");
        ex.Message.ShouldContain("byte[]");
        ex.Message.ShouldNotContain("System.Byte[]");
    }

    [Fact]
    public void FormatKey_Null_Throws()
    {
        Should.Throw<ArgumentNullException>(() => KeysetCursorCodec.FormatKey(null!, "Id"));
    }
}
