using NeoReports.Core.Building;

namespace NeoReports.Destinations.Local;

/// <summary>Fluent entry points for destinations. Local lives here; S3 adds its own overload.</summary>
public static class Destination
{
    /// <summary>Configures a local filesystem destination.</summary>
    /// <param name="pathTemplate">Path template (e.g. <c>./out/{name}-{date:yyyy-MM-dd}.{ext}</c>).</param>
    /// <returns>A <see cref="DestinationSpec"/> to pass to <c>ReportBuilder.UploadTo(...)</c>.</returns>
    public static DestinationSpec Local(string pathTemplate) =>
        new(new LocalDestinationFactory(pathTemplate));
}
