using System.Diagnostics;
using Amazon.S3;
using Amazon.S3.Model;
using NeoReports.Core.SourceRegistry;

namespace NeoReports.Sources.Csv;

/// <summary>
/// On-demand health check for a registered CSV source (ADR D42/D58, <c>type: "csv"</c>): for a
/// local file, confirms the path can be opened for read; for S3, confirms the object exists via
/// <c>HeadObject</c> (no data downloaded). Unlike the ADO family's uniform "open a connection, run
/// SELECT 1", a file source's only honest health signal is "can this path/object be read right now" —
/// there is no catalog/query protocol to probe further (the same capability gap as its missing
/// <c>ISchemaExplorer</c>/<c>IFilterTranslator</c>, D36).
/// </summary>
public sealed class CsvSourceHealthCheck : ISourceHealthCheck
{
    /// <inheritdoc />
    public string Type => "csv";

    /// <inheritdoc />
    public async Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            IReadOnlyDictionary<string, object?>? properties = definition.Properties;

            if (properties is not null && properties.TryGetValue("bucket", out var bucketValue) && bucketValue is string { Length: > 0 } bucket)
            {
                if (!properties.TryGetValue("key", out var keyValue) || keyValue is not string { Length: > 0 } key)
                    return new SourceHealthResult(Healthy: false, Error: "Source has no 'key' property.", Latency: TimeSpan.Zero);

                using var client = new AmazonS3Client();
                await client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key }, cancellationToken)
                    .ConfigureAwait(false);
                stopwatch.Stop();
                return new SourceHealthResult(Healthy: true, Error: null, stopwatch.Elapsed);
            }

            if (properties is null || !properties.TryGetValue("path", out var pathValue) || pathValue is not string { Length: > 0 } path)
                return new SourceHealthResult(Healthy: false, Error: "Source has no 'path' or 'bucket'/'key' property.", Latency: TimeSpan.Zero);

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            stopwatch.Stop();
            return new SourceHealthResult(Healthy: true, Error: null, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new SourceHealthResult(Healthy: false, ex.Message, stopwatch.Elapsed);
        }
    }
}
