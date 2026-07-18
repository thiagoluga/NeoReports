using Amazon.S3;
using NeoReports.Abstractions;

namespace NeoReports.Sources.Files.Common;

/// <summary>
/// Shared parsing of the <c>path</c>/<c>bucket</c>+<c>key</c>/<c>hasHeader</c> config properties every
/// file-based source's dynamic (<c>IConfigSourceProvider</c>) path reads, and the stream-opening
/// factory built from them. Each source's own format-specific properties (CSV's <c>delimiter</c>,
/// XLSX's <c>sheetName</c>) stay local to that source — only the storage-location resolution is common.
/// </summary>
public static class FileSourceProperties
{
    /// <summary>
    /// Resolves a stream-opening factory from <paramref name="properties"/>: a local <c>path</c>, or an
    /// S3 <c>bucket</c>+<c>key</c> (resolving a DI-registered <see cref="IAmazonS3"/> first, the same
    /// precedence <see cref="S3Stream.OpenAsync"/> and <c>NeoReports.Destinations.S3.S3DestinationFactory</c>
    /// use, before falling back to ambient AWS credentials).
    /// </summary>
    /// <param name="sourceTypeName">The source's display name for the "requires a property" error message (e.g. <c>"CSV"</c>, <c>"XLSX"</c>).</param>
    /// <param name="properties">The source config's <c>properties</c> bag.</param>
    /// <param name="services">Resolves a DI-registered <see cref="IAmazonS3"/> for the S3 case, if one is registered.</param>
    public static Func<CancellationToken, Task<Stream>> ResolveStreamFactory(
        string sourceTypeName, IReadOnlyDictionary<string, object?>? properties, IServiceProvider services)
    {
        if (properties is not null
            && properties.TryGetValue("bucket", out var bucketValue)
            && bucketValue is string { Length: > 0 } bucket)
        {
            string key = RequireString(sourceTypeName, properties, "key");
            var client = services?.GetService(typeof(IAmazonS3)) as IAmazonS3;
            return ct => S3Stream.OpenAsync(client, bucket, key, ct);
        }

        string path = RequireString(sourceTypeName, properties, "path");
        return _ => Task.FromResult<Stream>(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true));
    }

    /// <summary>Reads a required, non-empty string property, or throws a caller-safe <see cref="ConfigurationException"/>.</summary>
    /// <param name="sourceTypeName">The source's display name for the error message.</param>
    /// <param name="properties">The source config's <c>properties</c> bag.</param>
    /// <param name="key">The property name.</param>
    public static string RequireString(string sourceTypeName, IReadOnlyDictionary<string, object?>? properties, string key)
    {
        if (properties is not null && properties.TryGetValue(key, out var value) && value is string text && !string.IsNullOrWhiteSpace(text))
            return text;

        throw new ConfigurationException($"The {sourceTypeName} source requires a non-empty '{key}' property (set 'path' for a local file, or 'bucket'+'key' for S3).");
    }

    /// <summary>Reads a boolean property that may arrive as a native <c>bool</c> or a parseable string.</summary>
    /// <param name="value">The raw property value.</param>
    /// <param name="result">The parsed boolean, when this returns <c>true</c>.</param>
    public static bool TryGetBool(object? value, out bool result)
    {
        switch (value)
        {
            case bool b:
                result = b;
                return true;
            case string s when bool.TryParse(s, out var parsed):
                result = parsed;
                return true;
            default:
                result = false;
                return false;
        }
    }
}
