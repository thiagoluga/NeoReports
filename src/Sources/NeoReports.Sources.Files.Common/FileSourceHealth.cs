using System.Diagnostics;
using Amazon.S3;
using Amazon.S3.Model;
using NeoReports.Core.SourceRegistry;

namespace NeoReports.Sources.Files.Common;

/// <summary>
/// Shared on-demand health check body for every file-based source (ADR D42/D58/D59): for a local
/// file, confirms the path can be opened for read; for S3, confirms the object exists via
/// <c>HeadObject</c> (no data downloaded), resolving a DI-registered <see cref="IAmazonS3"/> first —
/// the same precedence <c>S3Stream.OpenAsync</c> and <c>NeoReports.Destinations.S3.S3DestinationFactory</c>
/// use — before falling back to ambient AWS credentials. Unlike the ADO family's uniform "open a
/// connection, run SELECT 1", a file source's only honest health signal is "can this path/object be
/// read right now" — there is no catalog/query protocol to probe further (the same capability gap as
/// each file source's missing <c>ISchemaExplorer</c>/<c>IFilterTranslator</c>, D36).
/// </summary>
public static class FileSourceHealth
{
    /// <summary>Checks whether the source's configured <c>path</c> or <c>bucket</c>/<c>key</c> is reachable.</summary>
    /// <param name="definition">The registered source's definition (its <c>properties</c> are read from here).</param>
    /// <param name="services">Resolves a DI-registered <see cref="IAmazonS3"/> for the S3 case, if one is registered.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public static async Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken)
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

                var ownedClient = services?.GetService(typeof(IAmazonS3)) as IAmazonS3;
                var ownsClient = ownedClient is null;
                IAmazonS3 client = ownedClient ?? new AmazonS3Client();
                try
                {
                    await client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key }, cancellationToken)
                        .ConfigureAwait(false);
                    stopwatch.Stop();
                    return new SourceHealthResult(Healthy: true, Error: null, stopwatch.Elapsed);
                }
                finally
                {
                    if (ownsClient)
                        client.Dispose();
                }
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
