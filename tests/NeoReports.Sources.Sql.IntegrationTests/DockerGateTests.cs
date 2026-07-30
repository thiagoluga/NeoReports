using NeoReports.IntegrationTests.Support;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Sql.IntegrationTests;

/// <summary>
/// Verifies the CI-vs-local skip decision of <see cref="DockerGate"/> without touching Docker or the
/// process environment. The gate is what turns a container start failure into a hard failure in CI
/// (via the fixtures' <c>catch (Exception) when (DockerGate.SkipWhenUnavailable)</c> filter) while
/// keeping the local skip-on-missing-Docker behaviour.
/// </summary>
public sealed class DockerGateTests
{
    [Theory]
    [InlineData(null, true)]   // unset (local dev) → skip on missing Docker
    [InlineData("", true)]     // empty → skip
    [InlineData("0", true)]    // any non-"1" value → skip
    [InlineData("true", true)] // not the exact opt-in value → skip
    [InlineData("1", false)]   // CI opt-in → do not skip, let the start failure propagate
    public void ShouldSkip_is_false_only_for_the_exact_require_docker_opt_in(string? value, bool expectedSkip) =>
        DockerGate.ShouldSkip(value).ShouldBe(expectedSkip);

    [Fact]
    public void SkipWhenUnavailable_reflects_ShouldSkip_of_the_ambient_variable() =>
        // Environment-independent invariant: the live property must equal the pure decision applied
        // to whatever the require-Docker variable currently is (unset locally, "1" in CI).
        DockerGate.SkipWhenUnavailable.ShouldBe(
            DockerGate.ShouldSkip(Environment.GetEnvironmentVariable(DockerGate.RequireDockerVariable)));
}
