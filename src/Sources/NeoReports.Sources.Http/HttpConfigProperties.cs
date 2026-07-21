using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Http;

/// <summary>
/// Reads <see cref="HttpSourceOptions"/> and the base URL from a dynamic-path source's
/// <c>properties</c> bag (ADR D61). Generic property-bag reads delegate to
/// <see cref="PropertyBag"/>; this type keeps only the HTTP-specific reads (the required
/// <c>url</c>, and assembling <see cref="HttpSourceOptions"/> from the rest).
/// </summary>
internal static class HttpConfigProperties
{
    /// <summary>Reads the required <c>url</c> property.</summary>
    public static string RequireUrl(IReadOnlyDictionary<string, object?>? properties) =>
        PropertyBag.RequireString(properties, "url", "HTTP");

    /// <summary>Reads every <see cref="HttpSourceOptions"/> setting from the properties bag; unset properties keep the option's default.</summary>
    public static HttpSourceOptions ReadOptions(IReadOnlyDictionary<string, object?>? properties)
    {
        var options = new HttpSourceOptions();
        if (properties is null)
            return options;

        if (PropertyBag.TryGetString(properties, "strategy", out string? strategyText))
        {
            if (!Enum.TryParse(strategyText, ignoreCase: true, out HttpPaginationStrategy strategy))
            {
                throw new ConfigurationException(
                    $"The HTTP source's 'strategy' property '{strategyText}' is not a recognized pagination strategy " +
                    $"(expected one of: {string.Join(", ", Enum.GetNames<HttpPaginationStrategy>())}).");
            }

            options.Paginate(strategy);
        }

        if (PropertyBag.TryGetString(properties, "recordsPath", out string? recordsPath))
            options.RecordsAt(recordsPath);

        if (PropertyBag.TryGetObject(properties, "fieldMap", out JsonElement fieldMapElement))
            options.FieldsFrom(PropertyBag.ToStringMap(fieldMapElement));

        string? cursorResponsePath = PropertyBag.TryGetString(properties, "cursorResponsePath", out string? crp) ? crp : null;
        string? cursorRequestParam = PropertyBag.TryGetString(properties, "cursorRequestParam", out string? crq) ? crq : null;
        if (cursorResponsePath is not null || cursorRequestParam is not null)
            options.CursorField(cursorResponsePath ?? "nextCursor", cursorRequestParam ?? "cursor");

        string? pageParam = PropertyBag.TryGetString(properties, "pageParam", out string? pp) ? pp : null;
        string? pageSizeParam = PropertyBag.TryGetString(properties, "pageSizeParam", out string? psp) ? psp : null;
        int? startPage = PropertyBag.TryGetInt(properties, "startPage", out int sp) ? sp : null;
        if (pageParam is not null || pageSizeParam is not null || startPage is not null)
            options.PageParams(pageParam ?? "page", pageSizeParam ?? "pageSize", startPage ?? 1);

        string? offsetParam = PropertyBag.TryGetString(properties, "offsetParam", out string? op) ? op : null;
        string? limitParam = PropertyBag.TryGetString(properties, "limitParam", out string? lp) ? lp : null;
        if (offsetParam is not null || limitParam is not null)
            options.OffsetParams(offsetParam ?? "offset", limitParam ?? "pageSize");

        if (PropertyBag.TryGetObject(properties, "headers", out JsonElement headersElement))
        {
            foreach (KeyValuePair<string, string> header in PropertyBag.ToStringMap(headersElement))
                options.Header(header.Key, header.Value);
        }

        if (PropertyBag.TryGetString(properties, "apiKeyHeader", out string? apiKeyHeader) && PropertyBag.TryGetString(properties, "apiKeyValue", out string? apiKeyValue))
            options.ApiKey(apiKeyHeader, apiKeyValue);

        if (PropertyBag.TryGetString(properties, "bearerToken", out string? bearerToken))
            options.Bearer(bearerToken);

        bool hasOAuth2TokenEndpoint = PropertyBag.TryGetString(properties, "oauth2TokenEndpoint", out string? oauth2TokenEndpoint);
        bool hasOAuth2ClientId = PropertyBag.TryGetString(properties, "oauth2ClientId", out string? oauth2ClientId);
        bool hasOAuth2ClientSecret = PropertyBag.TryGetString(properties, "oauth2ClientSecret", out string? oauth2ClientSecret);
        if (hasOAuth2TokenEndpoint || hasOAuth2ClientId || hasOAuth2ClientSecret)
        {
            if (!(hasOAuth2TokenEndpoint && hasOAuth2ClientId && hasOAuth2ClientSecret))
            {
                throw new ConfigurationException(
                    "The HTTP source's OAuth2 client-credentials properties ('oauth2TokenEndpoint', 'oauth2ClientId', 'oauth2ClientSecret') must all be configured together.");
            }

            string? oauth2Scope = PropertyBag.TryGetString(properties, "oauth2Scope", out string? scope) ? scope : null;
            options.OAuth2ClientCredentials(oauth2TokenEndpoint!, oauth2ClientId!, oauth2ClientSecret!, oauth2Scope);
        }

        if (PropertyBag.TryGetString(properties, "healthCheckPath", out string? healthCheckPath))
            options.HealthCheckAt(healthCheckPath);

        return options;
    }
}
