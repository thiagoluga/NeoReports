using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Salesforce;

/// <summary>
/// <see cref="ISourceRowCounter"/> for a Salesforce SOQL query (ADR D67) — the second non-SQL source
/// in Epic P (after OData's <c>$count</c>) with a genuinely accurate row-count mechanism rather than
/// a documented proxy or gap: SOQL's <c>COUNT()</c> aggregate, issued against the exact same query
/// with only the <c>SELECT</c> clause rewritten (<see cref="SalesforceCountQuery"/>), reports the
/// true count of the filtered result set. Best-effort by <see cref="ISourceRowCounter"/>'s documented
/// contract (D36): any non-2xx, unrecognized query shape, or missing <c>totalSize</c> returns
/// <c>null</c> rather than fabricating a count or failing the run.
/// </summary>
public sealed class SalesforceRowCounter : ISourceRowCounter
{
    private readonly HttpClient _client;
    private readonly string _instanceUrl;
    private readonly string _soql;
    private readonly SalesforceSourceOptions _options;

    /// <summary>Creates the counter.</summary>
    /// <param name="client">The HTTP client used for the count request.</param>
    /// <param name="instanceUrl">The Salesforce org's instance URL.</param>
    /// <param name="soql">The same SOQL query the batch source reads from.</param>
    /// <param name="options">Auth/API-version options — typically the same instance the batch source reads from.</param>
    public SalesforceRowCounter(HttpClient client, string instanceUrl, string soql, SalesforceSourceOptions options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _instanceUrl = string.IsNullOrWhiteSpace(instanceUrl) ? throw new ArgumentException("Instance URL must be provided.", nameof(instanceUrl)) : instanceUrl;
        _soql = string.IsNullOrWhiteSpace(soql) ? throw new ArgumentException("SOQL query must be provided.", nameof(soql)) : soql;
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task<long?> CountAsync(ReportExecutionContext execution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);

        string? countSoql = SalesforceCountQuery.TryBuildCountQuery(_soql);
        if (countSoql is null)
            return null;

        try
        {
            Uri countUri = SalesforceUrls.Query(_instanceUrl, _options.ApiVersionValue, countSoql);

            using var request = new HttpRequestMessage(HttpMethod.Get, countUri);
            HttpRequests.ApplyAuth(request, _options.ToAuth());

            using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
                return JsonRecords.TryGetField(document.RootElement, "totalSize", out JsonElement totalSize) && totalSize.ValueKind == JsonValueKind.Number
                    ? totalSize.GetInt64()
                    : null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
