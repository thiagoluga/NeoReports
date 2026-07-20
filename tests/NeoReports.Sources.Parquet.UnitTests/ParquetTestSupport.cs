using Parquet;
using Parquet.Serialization;
using Parquet.Serialization.Attributes;

namespace NeoReports.Sources.Parquet.UnitTests;

// Settable-property POCOs: Parquet.Net's ParquetSerializer requires a public parameterless
// constructor (ADR D60), so — unlike the CSV/XLSX tests' positional records — these are classes with
// { get; set; } properties (or init-only records, exercised separately).
public sealed class Sale
{
    public long Id { get; set; }
    public string Customer { get; set; } = "";
    public decimal Amount { get; set; }
    public DateTime Date { get; set; }
    public bool Active { get; set; }

    public override bool Equals(object? obj) =>
        obj is Sale s && s.Id == Id && s.Customer == Customer && s.Amount == Amount && s.Date == Date && s.Active == Active;

    public override int GetHashCode() => HashCode.Combine(Id, Customer, Amount, Date, Active);
}

public sealed class CustomerNote
{
    public long Id { get; set; }
    public string Customer { get; set; } = "";

    public override bool Equals(object? obj) => obj is CustomerNote c && c.Id == Id && c.Customer == Customer;

    public override int GetHashCode() => HashCode.Combine(Id, Customer);
}

// Fewer properties than the file carries — proves extra file columns are ignored on the typed path.
public sealed class IdOnly
{
    public long Id { get; set; }
}

// init-only record: a parameterless ctor exists, so this works too (ADR D60 capability note).
public sealed record InitSale
{
    public long Id { get; init; }
    public string Customer { get; init; } = "";
}

// A row shape for dynamic-path tests, including a nullable column exercised with a null value.
public sealed class WideRow
{
    public long Id { get; set; }
    public string Customer { get; set; } = "";
    public decimal Amount { get; set; }
    public string? Note { get; set; }
}

// A UTC-adjusted timestamp column, the shape a non-.NET producer (Spark/Arrow/pandas) would write.
// Verified empirically (ADR D60): Parquet.Net 6.0.3 normalizes this to a plain DateTime on read, never
// a DateTimeOffset, even with isAdjustedToUTC explicitly set — this fixture proves that reading path.
public sealed class TimestampRow
{
    public long Id { get; set; }

    [ParquetTimestamp(ParquetTimestampResolution.Microseconds, useLogicalTimestamp: true, isAdjustedToUTC: true)]
    public DateTime When { get; set; }
}

/// <summary>
/// A genuinely forward-only stream — throws on every seek-related member and reports
/// <c>CanSeek == false</c>, closer to a real S3 <c>GetObject</c> response body than a
/// <see cref="MemoryStream"/> merely reporting <c>false</c>. Used to prove
/// <c>SeekableStream.EnsureSeekableAsync</c> copies a non-seekable Parquet body so it can be read
/// (mirrors the throwaway forward-only probe ADR D59 used for XLSX).
/// </summary>
public sealed class ForwardOnlyStream : Stream
{
    private readonly MemoryStream _inner;

    public ForwardOnlyStream(byte[] bytes) => _inner = new MemoryStream(bytes);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }
}

internal static class ParquetTestFile
{
    public static async Task<byte[]> WriteBytesAsync<T>(IEnumerable<T> rows, int? rowGroupSize = null)
    {
        var ms = new MemoryStream();
        var options = rowGroupSize is { } size ? new ParquetOptions { RowGroupSize = size } : new ParquetOptions();
        await ParquetSerializer.SerializeAsync(rows, ms, options, cancellationToken: CancellationToken.None);
        return ms.ToArray();
    }

    public static async Task<string> WriteFileAsync<T>(string path, IEnumerable<T> rows, int? rowGroupSize = null)
    {
        byte[] bytes = await WriteBytesAsync(rows, rowGroupSize);
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }
}
