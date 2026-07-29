namespace NeoReports.Licensing;

/// <summary>
/// Process-wide license gate every NeoReports Pro entry point calls before doing any Pro work
/// (ADR D70, Q2). Exists because a Pro package has two independent entry paths — DI registration
/// (which can use <see cref="ServiceCollectionExtensions.AddNeoReportsProLicense"/>) and the static
/// fluent API used by typed code-first reports, which never touches a DI container at all. Gating
/// only the first would leave the library's primary documented usage pattern unlicensed.
/// </summary>
public static class ProLicenseGate
{
    // The validated license for this process, or null while none has been established. Immutable,
    // swapped whole via Volatile.Read/Write rather than lock-guarded: a benign race just re-validates
    // and writes an equivalent token, and readers can never observe a half-written one (the same
    // pattern OAuth2ClientCredentialsProvider's token cache uses, ADR D68).
    private static LicenseToken? _token;

    /// <summary>
    /// Throws <see cref="NeoReportsLicenseException"/> unless a valid NeoReports Pro license has been
    /// established for this process — either explicitly (<see cref="Register"/> or
    /// <see cref="ServiceCollectionExtensions.AddNeoReportsProLicense"/>) or via the
    /// <see cref="ServiceCollectionExtensions.LicenseKeyEnvironmentVariable"/> environment variable,
    /// which this method falls back to on the first call. Cheap enough to call from any Pro entry
    /// point: once validated it is a single volatile read.
    /// </summary>
    public static void EnsureValidated()
    {
        // Re-checks the validity window, not just presence: an expensive signature verification
        // really does happen once (D70's "checked once at startup"), but letting a long-running host
        // outlive its own trial indefinitely just because it never restarted would make a 30-day
        // license unbounded in practice. Comparing two DateTimeOffsets costs nothing.
        if (Volatile.Read(ref _token) is { } cached)
        {
            if (cached.IsValidAt(DateTimeOffset.UtcNow))
                return;

            throw new NeoReportsLicenseException(
                LicenseFailureReason.OutOfValidityWindow,
                $"The NeoReports Pro license (licensed to \"{cached.Licensee}\") is no longer within its validity window. " +
                "Contact your NeoReports Pro provider to renew, then restart the application with the new key.");
        }

        // Deliberately does NOT cache the failure: a host that fixes NEOREPORTS_LICENSE_KEY (or calls
        // Register) should recover without restarting the process.
        string? key = Environment.GetEnvironmentVariable(ServiceCollectionExtensions.LicenseKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new NeoReportsLicenseException(
                LicenseFailureReason.Missing,
                "This feature requires a NeoReports Pro license. Supply one by setting the " +
                $"{ServiceCollectionExtensions.LicenseKeyEnvironmentVariable} environment variable, or by calling " +
                $"{nameof(ProLicenseGate)}.{nameof(Register)}(key) (code-first) or AddNeoReportsProLicense(key) (dependency injection) at startup.");
        }

        Accept(ProLicense.Validate(key));
    }

    /// <summary>
    /// Validates <paramref name="licenseKey"/> and, on success, opens the gate for this process — the
    /// code-first equivalent of <see cref="ServiceCollectionExtensions.AddNeoReportsProLicense"/> for
    /// applications that never build a DI container. Call once at startup: validating up front makes a
    /// bad license fail immediately and loudly rather than at the first Pro call site (ADR D70).
    /// Throws <see cref="NeoReportsLicenseException"/> when the key is missing, malformed, has an
    /// invalid signature, or is outside its validity window.
    /// </summary>
    /// <param name="licenseKey">The NeoReports Pro license key.</param>
    public static void Register(string licenseKey) => Accept(ProLicense.Validate(licenseKey));

    /// <summary>The license this process is running under, or <c>null</c> if the gate is still closed.</summary>
    public static LicenseToken? Current => Volatile.Read(ref _token);

    /// <summary>
    /// Opens the gate with an already-validated token. Internal: every public route into the gate
    /// validates a real signature first. Also used by the Pro packages' own test suites, which cannot
    /// sign a token the embedded production key would accept (the matching private key deliberately
    /// does not exist in this repo).
    /// </summary>
    internal static void Accept(LicenseToken token) => Volatile.Write(ref _token, token);

    /// <summary>Closes the gate again. Test-only — lets the enforcement tests assert that a gated entry point actually throws.</summary>
    internal static void Reset() => Volatile.Write(ref _token, null);
}
