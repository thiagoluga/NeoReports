using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NeoReports.Licensing;

/// <summary>DI helpers for validating a NeoReports Pro license once at startup (ADR D70).</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Environment variable read when a license key isn't supplied directly to <see cref="AddNeoReportsProLicense"/>.</summary>
    public const string LicenseKeyEnvironmentVariable = "NEOREPORTS_LICENSE_KEY";

    /// <summary>
    /// Validates a NeoReports Pro license immediately (not deferred to first use) and registers the
    /// resulting <see cref="LicenseToken"/> as a singleton, so any Pro package can require one
    /// already be present instead of re-validating the raw key itself. Throws
    /// <see cref="NeoReportsLicenseException"/> synchronously when the license is missing,
    /// malformed, has an invalid signature, or is expired — a misconfigured license fails loudly at
    /// startup rather than surfacing mid-run (D36 posture). One NeoReports Pro license covers every
    /// Pro package (ADR D70) — safe to call multiple times (e.g. once per referenced Pro package):
    /// a call after a <see cref="LicenseToken"/> is already registered skips re-validating entirely.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="licenseKey">
    /// The license key; when <c>null</c>, read from the <see cref="LicenseKeyEnvironmentVariable"/>
    /// environment variable instead — the same <c>${VAR}</c>-style convention this codebase already
    /// uses for every other secret (connection strings, API keys, OAuth2 client secrets).
    /// </param>
    public static IServiceCollection AddNeoReportsProLicense(this IServiceCollection services, string? licenseKey = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(LicenseToken)))
            return services;

        using ECDsa publicKey = ProLicense.ImportEmbeddedPublicKey();
        return AddNeoReportsProLicenseCore(services, licenseKey, publicKey);
    }

    /// <summary>
    /// Core implementation, taking an explicit verification key — exists so tests can exercise the
    /// full registration behavior without the real NeoReports Pro private key, which deliberately
    /// never lives in this repo.
    /// </summary>
    internal static IServiceCollection AddNeoReportsProLicenseCore(IServiceCollection services, string? licenseKey, ECDsa publicKey)
    {
        string? key = licenseKey ?? Environment.GetEnvironmentVariable(LicenseKeyEnvironmentVariable);
        LicenseToken token = LicenseValidator.Validate(key, publicKey);
        services.TryAddSingleton(token);
        return services;
    }
}
