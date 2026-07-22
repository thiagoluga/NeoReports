using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace NeoReports.Licensing;

/// <summary>
/// Verifies a NeoReports Pro license key's signature and validity window (ADR D70). Fully offline —
/// no network call, no revocation check (an accepted, documented gap; see D70).
/// </summary>
public static class LicenseValidator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Parses and verifies <paramref name="licenseKey"/> against <paramref name="publicKey"/>,
    /// returning the signed <see cref="LicenseToken"/>. Throws <see cref="NeoReportsLicenseException"/>
    /// when the key is missing, malformed, has an invalid signature, or is outside its validity
    /// window as of <paramref name="utcNow"/> (defaults to <see cref="DateTimeOffset.UtcNow"/> when
    /// omitted). Leading/trailing whitespace in <paramref name="licenseKey"/> is trimmed first — a
    /// key sourced from a mounted file (e.g. a Kubernetes Secret) commonly carries a trailing newline.
    /// </summary>
    public static LicenseToken Validate(string? licenseKey, ECDsa publicKey, DateTimeOffset? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(publicKey);

        if (string.IsNullOrWhiteSpace(licenseKey))
            throw new NeoReportsLicenseException(LicenseFailureReason.Missing, "No NeoReports Pro license key was configured.");

        string[] parts = licenseKey.Trim().Split('.');
        if (parts.Length != 2)
            throw new NeoReportsLicenseException(LicenseFailureReason.Malformed, "The NeoReports Pro license key is malformed.");

        byte[] payloadBytes;
        byte[] signatureBytes;
        try
        {
            payloadBytes = Base64Url.Decode(parts[0]);
            signatureBytes = Base64Url.Decode(parts[1]);
        }
        catch (FormatException ex)
        {
            throw new NeoReportsLicenseException(LicenseFailureReason.Malformed, "The NeoReports Pro license key is malformed.", ex);
        }

        if (!publicKey.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256))
            throw new NeoReportsLicenseException(LicenseFailureReason.SignatureInvalid, "The NeoReports Pro license key's signature is invalid.");

        LicenseToken? token;
        try
        {
            token = JsonSerializer.Deserialize<LicenseToken>(payloadBytes, Json);
        }
        catch (JsonException ex)
        {
            throw new NeoReportsLicenseException(LicenseFailureReason.Malformed, "The NeoReports Pro license key's payload is malformed.", ex);
        }

        if (token is null || string.IsNullOrWhiteSpace(token.Licensee))
            throw new NeoReportsLicenseException(LicenseFailureReason.Malformed, "The NeoReports Pro license key's payload is incomplete.");

        DateTimeOffset now = utcNow ?? DateTimeOffset.UtcNow;
        if (!token.IsValidAt(now))
        {
            string Format(DateTimeOffset value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            throw new NeoReportsLicenseException(
                LicenseFailureReason.OutOfValidityWindow,
                $"The NeoReports Pro license (licensed to \"{token.Licensee}\") is not valid as of {Format(now)} " +
                $"(window: {Format(token.IssuedAtUtc)} to {Format(token.ExpiresAtUtc)}). Contact your NeoReports Pro provider to renew.");
        }

        return token;
    }
}
