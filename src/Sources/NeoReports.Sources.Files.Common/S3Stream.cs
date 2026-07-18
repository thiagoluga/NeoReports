using Amazon.S3;
using Amazon.S3.Model;

namespace NeoReports.Sources.Files.Common;

/// <summary>
/// Opens an S3 object as a readable stream — shared by every file-based source package (CSV, XLSX,
/// ...) across both their typed and dynamic paths. When no client is supplied, builds one from
/// ambient AWS credentials/region (mirroring <c>NeoReports.Destinations.S3.S3Destination.WithDefaultClient</c>)
/// and ties its lifetime to the returned stream, since the client must stay alive for the whole
/// streamed read; an explicitly passed client's lifetime stays the caller's own responsibility.
/// </summary>
public static class S3Stream
{
    /// <summary>Opens the given S3 object for reading, resolving a default client if none is supplied.</summary>
    /// <param name="client">An explicit S3 client (caller owns its lifetime), or <c>null</c> to build one from ambient AWS credentials/region.</param>
    /// <param name="bucket">The S3 bucket.</param>
    /// <param name="key">The object key.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static async Task<Stream> OpenAsync(IAmazonS3? client, string bucket, string key, CancellationToken cancellationToken)
    {
        var ownsClient = client is null;
        IAmazonS3 resolvedClient = client ?? new AmazonS3Client();

        try
        {
            GetObjectResponse response = await resolvedClient
                .GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, cancellationToken)
                .ConfigureAwait(false);
            return ownsClient ? new ClientOwningStream(response.ResponseStream, resolvedClient) : response.ResponseStream;
        }
        catch when (ownsClient)
        {
            resolvedClient.Dispose();
            throw;
        }
    }

    // Disposes the S3 client alongside the response stream it produced, when this call built its own
    // client rather than being handed one by the caller.
    private sealed class ClientOwningStream : Stream
    {
        private readonly Stream _inner;
        private readonly IAmazonS3 _client;

        public ClientOwningStream(Stream inner, IAmazonS3 client)
        {
            _inner = inner;
            _client = client;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _client.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            // Deliberately does NOT call base.DisposeAsync(): Stream's default implementation calls
            // the synchronous Dispose(), which re-invokes the Dispose(bool) override above and would
            // double-dispose both _inner and _client. GC.SuppressFinalize is the one thing that call
            // would otherwise have done for us.
            await _inner.DisposeAsync().ConfigureAwait(false);
            _client.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
