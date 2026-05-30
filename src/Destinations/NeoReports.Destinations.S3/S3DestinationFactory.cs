using Amazon.S3;
using NeoReports.Abstractions;

namespace NeoReports.Destinations.S3;

/// <summary>
/// Creates <see cref="S3Destination"/> instances. Resolves an <see cref="IAmazonS3"/> from the
/// service provider when available; otherwise builds a default client from ambient AWS config.
/// </summary>
public sealed class S3DestinationFactory : IDestinationFactory
{
    private readonly string _bucket;
    private readonly string _keyTemplate;

    /// <summary>Creates the factory.</summary>
    /// <param name="bucket">Target bucket name.</param>
    /// <param name="keyTemplate">Key template applied to every created destination.</param>
    public S3DestinationFactory(string bucket, string keyTemplate)
    {
        _bucket = bucket;
        _keyTemplate = keyTemplate;
    }

    /// <inheritdoc />
    public string Type => "s3";

    /// <inheritdoc />
    public IReportDestination Create(IReadOnlyDictionary<string, object?> options, IServiceProvider services)
    {
        var client = services?.GetService(typeof(IAmazonS3)) as IAmazonS3;
        return client is not null
            ? new S3Destination(client, _bucket, _keyTemplate)
            : S3Destination.WithDefaultClient(_bucket, _keyTemplate);
    }
}
