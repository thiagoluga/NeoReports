using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Http;

/// <summary>
/// Bridges <see cref="HttpSourceOptions"/>'s plain OAuth2 configuration fields to a live
/// <see cref="OAuth2ClientCredentialsProvider"/> (P4b, ADR D68). Kept in this package rather than
/// <c>Http.Common</c> since it operates on <see cref="HttpSourceOptions"/> directly — only the
/// generic HTTP source has adopted OAuth2 so far; the provider itself is the reusable, source-agnostic
/// primitive other sources could adopt later (an honest, undone gap, not silently worked around).
/// </summary>
internal static class HttpOAuth2
{
    /// <summary>
    /// Builds the provider once per source instance when OAuth2 is configured — callers cache the
    /// result (a constructor field) so the token is fetched/refreshed once and reused across pages,
    /// not re-fetched on every request.
    /// </summary>
    public static OAuth2ClientCredentialsProvider? CreateProvider(HttpClient client, HttpSourceOptions options) =>
        options.OAuth2TokenEndpoint is not null
            ? new OAuth2ClientCredentialsProvider(client, options.OAuth2TokenEndpoint, options.OAuth2ClientId!, options.OAuth2ClientSecret!, options.OAuth2Scope)
            : null;

    /// <summary>
    /// Resolves the <see cref="HttpAuth"/> to apply to one request: the static snapshot
    /// (<see cref="HttpSourceOptions.ToAuth"/>) when no OAuth2 provider is configured, or that same
    /// snapshot with <see cref="HttpAuth.BearerTokenValue"/> overridden by a current (possibly
    /// freshly-refreshed) access token otherwise.
    /// </summary>
    public static async Task<HttpAuth> ResolveAuthAsync(HttpSourceOptions options, OAuth2ClientCredentialsProvider? provider, CancellationToken cancellationToken)
    {
        HttpAuth auth = options.ToAuth();
        if (provider is null)
            return auth;

        string accessToken = await provider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        return auth with { BearerTokenValue = accessToken };
    }
}
