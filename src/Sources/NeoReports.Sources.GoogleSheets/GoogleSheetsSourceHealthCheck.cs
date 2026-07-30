using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.GoogleSheets;

/// <summary>
/// On-demand health check for a registered Google Sheets source (ADR D42/D66, <c>type: "googlesheets"</c>).
/// Probes the spreadsheet's minimal metadata endpoint with the configured API key via a plain
/// <c>GET</c> — deliberately <b>not</b> <see cref="HttpHealthProbe.ProbeAsync"/>'s <c>HEAD</c>-then-
/// <c>GET</c> fallback (code review finding): that fallback only retries on <c>405</c>/<c>501</c>,
/// but Google's REST-transcoded API frontend is not a general-purpose HTTP server and is not
/// confirmed to reject an unsupported <c>HEAD</c> with one of those two codes specifically (could
/// plausibly be a <c>400</c>, which the fallback wouldn't catch, false-negatively reporting a
/// perfectly-reachable spreadsheet as unhealthy) — researched, not verified against a live call, so
/// going straight to the one HTTP method this API is documented to support avoids the gamble
/// entirely rather than risk it. Reachability/auth/spreadsheet-exists only — does not validate the
/// configured <c>firstColumn</c>/<c>lastColumn</c>/<c>headerRow</c>, the same honesty boundary every
/// prior health check in Epic P keeps.
/// </summary>
public sealed class GoogleSheetsSourceHealthCheck : ISourceHealthCheck
{
    /// <inheritdoc />
    public string Type => "googlesheets";

    /// <inheritdoc />
    public Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return HttpHealthProbe.CheckAsync(async () =>
        {
            string spreadsheetId = GoogleSheetsConfigProperties.RequireSpreadsheetId(definition.Properties);
            GoogleSheetsSourceOptions options = GoogleSheetsConfigProperties.ReadOptions(definition.Properties, requireColumns: false);
            HttpClient client = HttpClients.ResolveFrom(services);
            Uri targetUrl = GoogleSheetsUrls.Metadata(spreadsheetId, options.ApiKeyValue!);

            return await HttpHealthProbe.SendAsync(client, HttpMethod.Get, targetUrl.ToString(), new HttpAuth(), cancellationToken: cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }
}
