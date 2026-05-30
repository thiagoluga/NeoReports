using NeoReports.Core.Building;

namespace NeoReports.Destinations.S3;

/// <summary>Fluent entry point for the Amazon S3 destination.</summary>
public static class Destination
{
    /// <summary>Configures an Amazon S3 destination.</summary>
    /// <param name="bucket">Target bucket name.</param>
    /// <param name="keyTemplate">Key template (e.g. <c>reports/{name}/{date:yyyy-MM-dd}.{ext}</c>).</param>
    /// <returns>A <see cref="DestinationSpec"/> to pass to <c>ReportBuilder.UploadTo(...)</c>.</returns>
    public static DestinationSpec S3(string bucket, string keyTemplate) =>
        new(new S3DestinationFactory(bucket, keyTemplate));
}
