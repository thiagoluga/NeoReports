using Amazon.S3;
using Amazon.S3.Model;
using NeoReports.Abstractions;
using NeoReports.Destinations.Local;

namespace NeoReports.Destinations.S3;

/// <summary>
/// Uploads the finished report file to an Amazon S3 bucket at a key resolved from a template.
/// The upload is all-or-nothing: <c>PutObject</c> creates the object only on a fully successful
/// transfer, so a failure never leaves a partial object (S3 PUT is atomic per object).
/// </summary>
public sealed class S3Destination : IReportDestination
{
    private readonly IAmazonS3 _client;
    private readonly bool _ownsClient;
    private readonly string _bucket;
    private readonly string _keyTemplate;

    /// <summary>Creates the destination with an explicit S3 client (the caller owns its lifetime).</summary>
    /// <param name="client">The S3 client.</param>
    /// <param name="bucket">Target bucket name.</param>
    /// <param name="keyTemplate">Key template (e.g. <c>reports/{name}/{date:yyyy-MM-dd}.{ext}</c>).</param>
    public S3Destination(IAmazonS3 client, string bucket, string keyTemplate)
        : this(client, ownsClient: false, bucket, keyTemplate)
    {
    }

    private S3Destination(IAmazonS3 client, bool ownsClient, string bucket, string keyTemplate)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = ownsClient;
        if (string.IsNullOrWhiteSpace(bucket))
            throw new ArgumentException("Bucket must be provided.", nameof(bucket));
        if (string.IsNullOrWhiteSpace(keyTemplate))
            throw new ArgumentException("Key template must be provided.", nameof(keyTemplate));
        _bucket = bucket;
        _keyTemplate = keyTemplate;
    }

    /// <summary>
    /// Creates a destination that builds its own <see cref="AmazonS3Client"/> from ambient AWS
    /// credentials/region (resolved by the SDK). The client is disposed with the destination.
    /// </summary>
    /// <param name="bucket">Target bucket name.</param>
    /// <param name="keyTemplate">Key template.</param>
    public static S3Destination WithDefaultClient(string bucket, string keyTemplate) =>
        new(new AmazonS3Client(), ownsClient: true, bucket, keyTemplate);

    /// <inheritdoc />
    public string Type => "s3";

    /// <inheritdoc />
    public async Task<UploadResult> UploadAsync(ReportFile file, DestinationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(context);

        var extension = Path.GetExtension(file.FileName).TrimStart('.');
        var key = PathTemplate.Expand(
            _keyTemplate,
            context.Execution.ReportName,
            extension,
            context.Execution.StartedAt,
            context.Execution.Parameters);

        try
        {
            await using var content = file.OpenRead();
            var request = new PutObjectRequest
            {
                BucketName = _bucket,
                Key = key,
                InputStream = content,
                ContentType = file.MimeType,
                AutoCloseStream = false,
                // Let the SDK compute the length from the seekable stream; disable chunked
                // signing so a transient failure aborts the whole PUT (no partial object).
                UseChunkEncoding = false,
            };

            var response = await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.HttpStatusCode is < 200 or >= 300)
                return UploadResult.Fail($"S3 PutObject returned HTTP {(int)response.HttpStatusCode} for s3://{_bucket}/{key}.");

            var url = $"s3://{_bucket}/{key}";
            return UploadResult.Ok(url, key);
        }
        catch (Exception ex)
        {
            return UploadResult.Fail($"S3 upload to s3://{_bucket}/{key} failed: {ex.Message}");
        }
    }

    /// <summary>Disposes the S3 client if this destination created it.</summary>
    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }
}
