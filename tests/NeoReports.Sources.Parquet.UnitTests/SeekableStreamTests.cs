using System.Text;
using NeoReports.Sources.Files.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Parquet.UnitTests;

/// <summary>
/// Direct tests for the shared <see cref="SeekableStream"/> helper (ADR D60) — the piece of new
/// infrastructure Parquet's seekability requirement forced. Exercised end to end through the S3 read
/// path in <see cref="ParquetS3SourceReadingTests"/> too, but tested in isolation here for its own
/// contract (pass-through, copy, and original-disposal behavior).
/// </summary>
public sealed class SeekableStreamTests
{
    [Fact]
    public async Task Returns_an_already_seekable_stream_unchanged()
    {
        using var seekable = new MemoryStream(Encoding.UTF8.GetBytes("hello"));

        Stream result = await SeekableStream.EnsureSeekableAsync(seekable, CancellationToken.None);

        result.ShouldBeSameAs(seekable);
    }

    [Fact]
    public async Task Copies_a_non_seekable_stream_into_a_seekable_one_with_identical_bytes()
    {
        byte[] payload = Enumerable.Range(0, 1000).Select(i => (byte)(i % 256)).ToArray();
        var forwardOnly = new ForwardOnlyStream(payload);

        Stream result = await SeekableStream.EnsureSeekableAsync(forwardOnly, CancellationToken.None);
        await using (result.ConfigureAwait(false))
        {
            result.CanSeek.ShouldBeTrue();
            result.Position.ShouldBe(0);

            using var buffer = new MemoryStream();
            await result.CopyToAsync(buffer);
            buffer.ToArray().ShouldBe(payload);
        }
    }

    [Fact]
    public async Task Disposes_the_original_non_seekable_stream_after_copying()
    {
        var forwardOnly = new ForwardOnlyStream(new byte[] { 1, 2, 3 });

        Stream result = await SeekableStream.EnsureSeekableAsync(forwardOnly, CancellationToken.None);
        await result.DisposeAsync();

        // The original is consumed and disposed by the helper; reading it again throws.
        Should.Throw<ObjectDisposedException>(() => forwardOnly.ReadByte());
    }

    [Fact]
    public async Task The_temp_copy_is_deleted_when_disposed()
    {
        var forwardOnly = new ForwardOnlyStream(new byte[] { 1, 2, 3 });

        var result = (FileStream)await SeekableStream.EnsureSeekableAsync(forwardOnly, CancellationToken.None);
        string tempPath = result.Name;
        File.Exists(tempPath).ShouldBeTrue();

        await result.DisposeAsync();

        File.Exists(tempPath).ShouldBeFalse();
    }
}
