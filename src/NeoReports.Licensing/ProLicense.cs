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
    /// <b>Production key, generated 2026-08-08 (ADR D83).</b> It replaces the placeholder this
    /// package shipped with, whose private half had been generated inside a chat session and was
    /// therefore burned before it ever signed anything. Rotating this constant again invalidates
    /// <b>every</b> license already issued under it — validation is offline, so there is no way to
    /// re-issue selectively. Treat a rotation as a breaking release, not a maintenance detail.
    /// </para>
    /// </summary>
    public const string PublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEVFPHemBTUrnP9IOObkGNSIy/y5vblPPlirW9o0jk0zG51PzuzLvxf6c+OnQWRxvsWGkF1yU3b/kyZVgAULAT3w==";

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
