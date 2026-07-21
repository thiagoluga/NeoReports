using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NeoReports.Sources.Http.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Http.UnitTests;

/// <summary>Tests <see cref="OAuth2ClientCredentialsProvider"/> (ADR D68) directly against a stubbed <see cref="HttpMessageHandler"/>.</summary>
public sealed class OAuth2ClientCredentialsProviderTests
{
    private static HttpResponseMessage TokenResponse(string accessToken, int? expiresIn = 3600) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                expiresIn is null
                    ? $$"""{"access_token":"{{accessToken}}","token_type":"Bearer"}"""
                    : $$"""{"access_token":"{{accessToken}}","token_type":"Bearer","expires_in":{{expiresIn}}}""",
                Encoding.UTF8, "application/json")
        };

    [Fact]
    public async Task Fetches_a_token_via_client_credentials_grant()
    {
        // Content is read synchronously inside the stub's respond callback — by the time this test
        // method resumes, RefreshAsync's `using var request` has already disposed the request (and
        // its FormUrlEncodedContent), so handler.Requests[0].Content can no longer be read.
        var bodies = new List<string>();
        HttpClient client = StubHttpMessageHandler.CreateClient(request =>
        {
            bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return TokenResponse("token-1");
        }, out StubHttpMessageHandler handler);

        var provider = new OAuth2ClientCredentialsProvider(client, "https://auth.test/token", "client-id", "client-secret");

        string token = await provider.GetAccessTokenAsync(CancellationToken.None);

        token.ShouldBe("token-1");
        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].Method.ShouldBe(HttpMethod.Post);
        handler.Requests[0].RequestUri!.ToString().ShouldBe("https://auth.test/token");

        Uri.UnescapeDataString(bodies[0]).ShouldContain("grant_type=client_credentials");
    }

    [Fact]
    public async Task Authenticates_via_http_basic_with_client_id_and_secret()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => TokenResponse("token-1"), out StubHttpMessageHandler handler);

        var provider = new OAuth2ClientCredentialsProvider(client, "https://auth.test/token", "my-client", "my-secret");

        await provider.GetAccessTokenAsync(CancellationToken.None);

        AuthenticationHeaderValue authHeader = handler.Requests[0].Headers.Authorization!;
        authHeader.Scheme.ShouldBe("Basic");
        string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authHeader.Parameter!));
        decoded.ShouldBe("my-client:my-secret");
    }

    [Fact]
    public async Task Includes_scope_when_configured()
    {
        var bodies = new List<string>();
        HttpClient client = StubHttpMessageHandler.CreateClient(request =>
        {
            bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return TokenResponse("token-1");
        }, out _);

        var provider = new OAuth2ClientCredentialsProvider(client, "https://auth.test/token", "client-id", "client-secret", "reports:read");

        await provider.GetAccessTokenAsync(CancellationToken.None);

        Uri.UnescapeDataString(bodies[0]).ShouldContain("scope=reports:read");
    }

    [Fact]
    public async Task Caches_the_token_and_does_not_refetch_before_expiry()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => TokenResponse("token-1", expiresIn: 3600), out StubHttpMessageHandler handler);

        var provider = new OAuth2ClientCredentialsProvider(client, "https://auth.test/token", "client-id", "client-secret");

        string first = await provider.GetAccessTokenAsync(CancellationToken.None);
        string second = await provider.GetAccessTokenAsync(CancellationToken.None);

        first.ShouldBe("token-1");
        second.ShouldBe("token-1");
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Refetches_once_the_cached_token_is_within_the_expiry_buffer()
    {
        // ADR D68: refreshes 30s before real expiry, so a token expiring "now" (or already within
        // that buffer) is never handed back as if still valid.
        var call = 0;
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
        {
            call++;
            return TokenResponse($"token-{call}", expiresIn: 1);
        }, out StubHttpMessageHandler handler);

        var provider = new OAuth2ClientCredentialsProvider(client, "https://auth.test/token", "client-id", "client-secret");

        await provider.GetAccessTokenAsync(CancellationToken.None);
        await provider.GetAccessTokenAsync(CancellationToken.None);

        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Missing_expires_in_falls_back_to_a_conservative_default_rather_than_never_expiring()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => TokenResponse("token-1", expiresIn: null), out StubHttpMessageHandler handler);

        var provider = new OAuth2ClientCredentialsProvider(client, "https://auth.test/token", "client-id", "client-secret");

        string token = await provider.GetAccessTokenAsync(CancellationToken.None);
        string cached = await provider.GetAccessTokenAsync(CancellationToken.None);

        token.ShouldBe("token-1");
        cached.ShouldBe("token-1");
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Missing_access_token_in_the_response_throws()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"token_type":"Bearer"}""", Encoding.UTF8, "application/json") }, out _);

        var provider = new OAuth2ClientCredentialsProvider(client, "https://auth.test/token", "client-id", "client-secret");

        await Should.ThrowAsync<HttpSourceException>(() => provider.GetAccessTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Non_success_response_throws_with_status_and_retry_after()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("invalid_client") };
            return response;
        }, out _);

        var provider = new OAuth2ClientCredentialsProvider(client, "https://auth.test/token", "client-id", "wrong-secret");

        HttpSourceException ex = await Should.ThrowAsync<HttpSourceException>(() => provider.GetAccessTokenAsync(CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void Construction_requires_token_endpoint_client_id_and_secret()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => TokenResponse("token-1"), out _);

        Should.Throw<ArgumentException>(() => new OAuth2ClientCredentialsProvider(client, "", "client-id", "client-secret"));
        Should.Throw<ArgumentException>(() => new OAuth2ClientCredentialsProvider(client, "https://auth.test/token", "", "client-secret"));
        Should.Throw<ArgumentException>(() => new OAuth2ClientCredentialsProvider(client, "https://auth.test/token", "client-id", ""));
    }
}
