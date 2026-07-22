using System.Security.Cryptography;

namespace NeoReports.Licensing;

/// <summary>
/// Convenience entry point for NeoReports Pro packages (ADR D70): wraps <see cref="LicenseValidator"/>
/// with the embedded NeoReports Pro public verification key, so a Pro package's own registration
/// code doesn't need to manage key material itself.
/// </summary>
public static class ProLicense
{
    /// <summary>
    /// The NeoReports Pro license-signing public key (ECDsa P-256, SubjectPublicKeyInfo, base64) —
    /// safe to publish; only the matching private key (held only by the maintainer's license-issuing
    /// side, out of scope for this repo) can produce a signature this verifies.
    /// <para>
    /// <b>Placeholder key pair.</b> This constant was generated for this package's initial
    /// implementation and has not yet been used to sign any real customer license. Before issuing
    /// any real NeoReports Pro license, generate a fresh production key pair through a process where
    /// the private half never appears in a chat transcript, source control, or any shared document —
    /// move it straight into a secrets vault — then replace this constant in a new release. Existing
    /// license keys signed under a rotated-out key stop verifying.
    /// </para>
    /// </summary>
    public const string PublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE8CTV2vXjiGkicpdqu6zK5kzHUCAotsIs7ysuwvAP/ZhxSmAcK/ZuZ4w//6XT0I71il/8qeuobgic5csTNd5How==";

    /// <summary>
    /// Validates <paramref name="licenseKey"/> against the embedded NeoReports Pro public key.
    /// Throws <see cref="NeoReportsLicenseException"/> when missing, malformed, has an invalid
    /// signature, or is outside its validity window as of <paramref name="utcNow"/>.
    /// </summary>
    public static LicenseToken Validate(string? licenseKey, DateTimeOffset? utcNow = null)
    {
        using ECDsa publicKey = ImportEmbeddedPublicKey();
        return LicenseValidator.Validate(licenseKey, publicKey, utcNow);
    }

    /// <summary>
    /// Imports <see cref="PublicKeyBase64"/> into a usable <see cref="ECDsa"/> instance. Wraps any
    /// failure (a corrupted constant — a packaging defect, not a licensing problem) in
    /// <see cref="NeoReportsLicenseException"/> too, so every failure this package can produce is
    /// the same documented exception type rather than a raw BCL crypto/format exception.
    /// </summary>
    internal static ECDsa ImportEmbeddedPublicKey()
    {
        try
        {
            var publicKey = ECDsa.Create();
            publicKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeyBase64), out _);
            return publicKey;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new NeoReportsLicenseException(
                LicenseFailureReason.Malformed,
                "NeoReports.Licensing's embedded verification key is corrupted — this is a packaging defect, not a licensing problem. Reinstall the package or report this as a bug.",
                ex);
        }
    }
}
