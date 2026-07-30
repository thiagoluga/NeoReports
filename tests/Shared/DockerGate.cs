namespace NeoReports.IntegrationTests.Support;

/// <summary>
/// Decides whether a Testcontainers fixture may quietly skip when Docker can't start.
/// <para>
/// Locally a contributor without Docker should still be able to run <c>dotnet test</c>, so a failed
/// container start degrades the suite to skipped. In CI, however, "all skipped" would hide a broken
/// container image or a Docker outage behind a green build, so the workflow sets
/// <c>NEOREPORTS_REQUIRE_DOCKER=1</c> and the fixture rethrows the start failure instead — turning a
/// silent skip into a hard failure.
/// </para>
/// <para>
/// Linked (not copied) into each container integration test project via
/// <c>&lt;Compile Include="..\Shared\DockerGate.cs" /&gt;</c>, following the same shared-file pattern
/// as <c>ProLicenseTestSeed</c>. Used as an exception filter — <c>catch (Exception) when
/// (DockerGate.SkipWhenUnavailable)</c> — so that when Docker is required the catch does not match
/// and the original exception propagates out of the fixture's <c>InitializeAsync</c>.
/// </para>
/// </summary>
internal static class DockerGate
{
    /// <summary>
    /// The environment variable CI sets to demand a working Docker; any other value (including unset)
    /// leaves the local skip-on-missing-Docker behaviour in place.
    /// </summary>
    internal const string RequireDockerVariable = "NEOREPORTS_REQUIRE_DOCKER";

    /// <summary>
    /// <see langword="true"/> when a container start failure should be swallowed (tests skip);
    /// <see langword="false"/> when <c>NEOREPORTS_REQUIRE_DOCKER=1</c> demands a hard failure instead.
    /// </summary>
    internal static bool SkipWhenUnavailable =>
        ShouldSkip(Environment.GetEnvironmentVariable(RequireDockerVariable));

    /// <summary>
    /// The pure decision behind <see cref="SkipWhenUnavailable"/>: skip unless the require-Docker
    /// variable is exactly <c>"1"</c>. Split out so it can be unit-tested without mutating the
    /// process environment (which would race the container fixtures' own reads).
    /// </summary>
    internal static bool ShouldSkip(string? requireDockerValue) => requireDockerValue != "1";
}
