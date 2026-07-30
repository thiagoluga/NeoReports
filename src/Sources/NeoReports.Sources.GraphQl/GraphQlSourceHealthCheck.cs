using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.GraphQl;

/// <summary>
/// On-demand health check for a registered GraphQL source (ADR D42/D63, <c>type: "graphql"</c>).
/// <c>POST</c>s the one universally-valid GraphQL query (<c>{ __typename }</c>) to the configured
/// endpoint with the configured auth via the shared <see cref="HttpHealthProbe.SendAsync"/> (its
/// optional body parameter is exactly for a single-endpoint, <c>POST</c>-only transport like this
/// one — there is no <c>HEAD</c>/<c>GET</c> fallback to dance through, so <see cref="HttpHealthProbe.ProbeAsync"/>
/// doesn't apply). Healthy on a <c>2xx</c> response with no populated <c>errors</c> array. Reads
/// options with <c>requireQueryAndConnection: false</c> — this only confirms the endpoint speaks
/// GraphQL and authenticates, deliberately not requiring (or validating) the author's own configured
/// query/connection (the family's honesty boundary, D36).
/// </summary>
public sealed class GraphQlSourceHealthCheck : ISourceHealthCheck
{
    private const string ProbeQueryBody = """{"query":"{ __typename }"}""";

    /// <inheritdoc />
    public string Type => "graphql";

    /// <inheritdoc />
    public async Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            string endpointUrl = GraphQlConfigProperties.RequireUrl(definition.Properties);
            GraphQlSourceOptions options = GraphQlConfigProperties.ReadOptions(definition.Properties, requireQueryAndConnection: false);
            HttpClient client = HttpClients.ResolveFrom(services);
            using var content = new StringContent(ProbeQueryBody, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await HttpHealthProbe
                .SendAsync(client, HttpMethod.Post, endpointUrl, options.ToAuth(), content, cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
                return new SourceHealthResult(Healthy: false, $"HTTP {(int)response.StatusCode} ({response.StatusCode}).", stopwatch.Elapsed);

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(body);
            return GraphQlErrors.TryGetMessage(document.RootElement, out string? message)
                ? new SourceHealthResult(Healthy: false, $"The GraphQL response contained errors: {message}", stopwatch.Elapsed)
                : new SourceHealthResult(Healthy: true, Error: null, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new SourceHealthResult(Healthy: false, ex.Message, stopwatch.Elapsed);
        }
    }
}
