using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace NeoReports.Licensing.UnitTests;

/// <summary>ADR D70: <see cref="ServiceCollectionExtensions"/> — the DI registration used at Pro-package startup.</summary>
[Collection(LicenseEnvironmentCollection.Name)]
public sealed class ServiceCollectionExtensionsTests : IDisposable
{
    // A successful registration also opens the process-wide ProLicenseGate (Q2), so each test starts
    // and leaves it shut — otherwise one test's valid license would satisfy the next one's
    // deliberately-invalid setup.
    public ServiceCollectionExtensionsTests() => ProLicenseGate.Reset();

    public void Dispose()
    {
        ProLicenseGate.Reset();
        GC.SuppressFinalize(this);
    }

    private static ECDsa NewKeyPair() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    [Fact]
    public void Valid_key_registers_the_license_token_as_a_singleton()
    {
        using ECDsa keyPair = NewKeyPair();
        string key = LicenseSigner.Sign(
            new LicenseToken("Acme Corp", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30)),
            keyPair);
        var services = new ServiceCollection();

        ServiceCollectionExtensions.AddNeoReportsProLicenseCore(services, key, keyPair);

        using ServiceProvider provider = services.BuildServiceProvider();
        LicenseToken token = provider.GetRequiredService<LicenseToken>();
        token.Licensee.ShouldBe("Acme Corp");
    }

    [Fact]
    public void Calling_it_twice_keeps_the_first_registered_token()
    {
        using ECDsa keyPair = NewKeyPair();
        string firstKey = LicenseSigner.Sign(
            new LicenseToken("First Corp", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30)), keyPair);
        string secondKey = LicenseSigner.Sign(
            new LicenseToken("Second Corp", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30)), keyPair);
        var services = new ServiceCollection();

        ServiceCollectionExtensions.AddNeoReportsProLicenseCore(services, firstKey, keyPair);
        ServiceCollectionExtensions.AddNeoReportsProLicenseCore(services, secondKey, keyPair);

        using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<LicenseToken>().Licensee.ShouldBe("First Corp");
    }

    [Fact]
    public void AddNeoReportsProLicense_skips_revalidating_once_a_token_is_already_registered()
    {
        // The no-key form short-circuits: with a LicenseToken already registered there is nothing to
        // validate, which is what makes it safe for several Pro packages to each call it at startup.
        var services = new ServiceCollection();
        services.AddSingleton(new LicenseToken("Pre-registered Corp", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30)));

        Should.NotThrow(() => services.AddNeoReportsProLicense());

        using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<LicenseToken>().Licensee.ShouldBe("Pre-registered Corp");
    }

    [Fact]
    public void An_explicit_key_is_always_validated_even_when_a_license_is_already_established()
    {
        // Regression guard: the short-circuit must never silently discard a key the host passed on
        // purpose — otherwise an environment variable would quietly outrank an explicitly pinned key.
        var services = new ServiceCollection();
        services.AddSingleton(new LicenseToken("Pre-registered Corp", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30)));

        Should.Throw<NeoReportsLicenseException>(() => services.AddNeoReportsProLicense("this is not a valid key"))
            .Reason.ShouldBe(LicenseFailureReason.Malformed);
    }

    [Fact]
    public void Missing_key_and_no_environment_variable_throws()
    {
        using ECDsa keyPair = NewKeyPair();
        Environment.SetEnvironmentVariable(ServiceCollectionExtensions.LicenseKeyEnvironmentVariable, null);
        var services = new ServiceCollection();

        Should.Throw<NeoReportsLicenseException>(() => ServiceCollectionExtensions.AddNeoReportsProLicenseCore(services, null, keyPair))
            .Message.ShouldContain("No NeoReports Pro license key was configured");
    }

    [Fact]
    public void Null_key_falls_back_to_the_environment_variable()
    {
        using ECDsa keyPair = NewKeyPair();
        try
        {
            Environment.SetEnvironmentVariable(ServiceCollectionExtensions.LicenseKeyEnvironmentVariable, "not-a-real-key");
            var services = new ServiceCollection();

            Should.Throw<NeoReportsLicenseException>(() => ServiceCollectionExtensions.AddNeoReportsProLicenseCore(services, null, keyPair))
                .Message.ShouldContain("malformed");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ServiceCollectionExtensions.LicenseKeyEnvironmentVariable, null);
        }
    }

    [Fact]
    public void An_explicit_key_takes_precedence_over_the_environment_variable()
    {
        using ECDsa keyPair = NewKeyPair();
        try
        {
            // Env var alone would fail with "No ... configured" (blank); an explicit malformed key
            // failing with "malformed" instead proves the explicit value, not the env var, was used.
            Environment.SetEnvironmentVariable(ServiceCollectionExtensions.LicenseKeyEnvironmentVariable, "");
            var services = new ServiceCollection();

            Should.Throw<NeoReportsLicenseException>(() => ServiceCollectionExtensions.AddNeoReportsProLicenseCore(services, "not-a-real-key", keyPair))
                .Message.ShouldContain("malformed");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ServiceCollectionExtensions.LicenseKeyEnvironmentVariable, null);
        }
    }
}
