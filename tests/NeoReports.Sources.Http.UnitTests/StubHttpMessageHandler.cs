namespace NeoReports.Sources.Http.UnitTests;

/// <summary>Test double for <see cref="HttpMessageHandler"/>: answers every request from a delegate, and records requests seen.</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    public List<HttpRequestMessage> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_respond(request));
    }

    public static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> respond, out StubHttpMessageHandler handler)
    {
        handler = new StubHttpMessageHandler(respond);
        return new HttpClient(handler);
    }
}

/// <summary>
/// A read-only stream that returns at most <see cref="MaxBytesPerRead"/> bytes per
/// <see cref="ReadAsync(Memory{byte},CancellationToken)"/> call, to genuinely exercise a
/// multi-chunk-refill code path (e.g. <see cref="JsonRecords.StreamArrayAsync"/>) instead of
/// silently handing back the whole payload in one read, which a <see cref="MemoryStream"/> would.
/// </summary>
internal sealed class ThrottledReadStream : Stream
{
    private readonly Stream _inner;
    private readonly int _maxBytesPerRead;

    public ThrottledReadStream(Stream inner, int maxBytesPerRead)
    {
        _inner = inner;
        _maxBytesPerRead = maxBytesPerRead;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int take = Math.Min(buffer.Length, _maxBytesPerRead);
        return await _inner.ReadAsync(buffer[..take], cancellationToken).ConfigureAwait(false);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        _inner.Read(buffer, offset, Math.Min(count, _maxBytesPerRead));

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }
}
