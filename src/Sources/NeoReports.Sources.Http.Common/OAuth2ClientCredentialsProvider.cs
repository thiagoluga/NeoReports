using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace NeoReports.Sources.Http.Common;

/// <summary>
/// Fetches and caches an OAuth2 access token via the client-credentials grant (RFC 6749 §4.4) —
/// P4b's answer to the "static token only" gap every HTTP-family source shipped with until now
/// (ADR D61–D67 all deferred OAuth2 to this pass). Chosen over the other grant types (authorization
/// code, resource-owner password, JWT bearer) because it is the only one with no interactive/human
/// step, matching NeoReports' headless, scheduled-job execution model (D6). Authenticates the token
/// request via HTTP Basic (<c>client_secret_basic</c>, RFC 6749 §2.3.1) — the RFC's own recommended
/// default and the most broadly supported method — rather than <c>client_secret_post</c> (client
/// credentials in the form body); a provider that only accepts the POST-body method is a documented,
/// narrow gap (D36), not silently guessed at.
/// </summary>
public sealed class OAuth2ClientCredentialsProvider
{
    // Refreshes this many seconds before the token's real expiry, so a request that starts an
    // instant before expiry doesn't race a token that goes stale mid-flight.
    private const int ExpiryBufferSeconds = 30;

    // Assumed lifetime when a token response omits expires_in (RFC 6749 doesn't require it) — a
    // conservative, common default rather than treating an unspecified expiry as "never," which
    // risks holding a stale/revoked token indefinitely.
    private const int DefaultExpirySeconds = 3600;

    private readonly HttpClient _client;
    private readonly string _tokenEndpoint;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string? _scope;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // Bundled into one immutable snapshot (rather than two separate fields) so a concurrent
    // GetAccessTokenAsync reading outside the lock can never observe a torn combination of an old
    // token with a new expiry (or vice versa) — Volatile.Read/Write give a full read/write barrier
    // around the single reference swap.
    private sealed record CachedToken(string Value, DateTimeOffset ExpiresAt);

    private CachedToken? _cached;

    /// <summary>Creates the provider.</summary>
    /// <param name="client">The HTTP client used for the token request — typically the same client the source's own API calls use.</param>
    /// <param name="tokenEndpoint">The OAuth2 token endpoint URL.</param>
    /// <param name="clientId">The OAuth2 client id.</param>
    /// <param name="clientSecret">The OAuth2 client secret.</param>
    /// <param name="scope">Optional space-delimited scope string.</param>
    public OAuth2ClientCredentialsProvider(HttpClient client, string tokenEndpoint, string clientId, string clientSecret, string? scope = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _tokenEndpoint = string.IsNullOrWhiteSpace(tokenEndpoint) ? throw new ArgumentException("Token endpoint must be provided.", nameof(tokenEndpoint)) : tokenEndpoint;
        _clientId = string.IsNullOrWhiteSpace(clientId) ? throw new ArgumentException("Client id must be provided.", nameof(clientId)) : clientId;
        _clientSecret = string.IsNullOrWhiteSpace(clientSecret) ? throw new ArgumentException("Client secret must be provided.", nameof(clientSecret)) : clientSecret;
        _scope = scope;
    }

    /// <summary>
    /// Returns a currently-valid access token, fetching or refreshing it first if the cached one is
    /// missing or within <see cref="ExpiryBufferSeconds"/> of expiry. Concurrent callers serialize on
    /// a single refresh (<see cref="SemaphoreSlim"/>) rather than each firing their own token request.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        CachedToken? cached = Volatile.Read(ref _cached);
        if (cached is not null && DateTimeOffset.UtcNow < cached.ExpiresAt)
            return cached.Value;

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = Volatile.Read(ref _cached);
            if (cached is not null && DateTimeOffset.UtcNow < cached.ExpiresAt)
                return cached.Value;

            return await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<string> RefreshAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(_clientId)}:{Uri.EscapeDataString(_clientSecret)}")));

        var form = new List<KeyValuePair<string, string>> { new("grant_type", "client_credentials") };
        if (_scope is { Length: > 0 })
            form.Add(new KeyValuePair<string, string>("scope", _scope));

        request.Content = new FormUrlEncodedContent(form);

        using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            HttpSourceException failure = await HttpRequests.BuildExceptionAsync(response, cancellationToken).ConfigureAwait(false);
            throw new HttpSourceException(failure.StatusCode, failure.RetryAfter, $"The OAuth2 token endpoint request failed. {failure.Message}");
        }

        Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;

            if (!JsonRecords.TryGetField(root, "access_token", out JsonElement tokenElement) || tokenElement.ValueKind != JsonValueKind.String)
                throw new HttpSourceException(null, null, "The OAuth2 token endpoint's response did not include an 'access_token' string.");

            int expiresIn = JsonRecords.TryGetField(root, "expires_in", out JsonElement expiresElement) && expiresElement.ValueKind == JsonValueKind.Number
                ? expiresElement.GetInt32()
                : DefaultExpirySeconds;

            string token = tokenElement.GetString()!;
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(expiresIn - ExpiryBufferSeconds, 0));
            Volatile.Write(ref _cached, new CachedToken(token, expiresAt));
            return token;
        }
    }
}
