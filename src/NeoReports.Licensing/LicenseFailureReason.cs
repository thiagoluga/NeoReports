namespace NeoReports.Licensing;

/// <summary>
/// Why <see cref="LicenseValidator.Validate"/> rejected a license key (ADR D70) — lets a caller
/// distinguish failure modes (e.g. show a "start a trial" call to action only when
/// <see cref="Missing"/>, a "renew" call to action only when <see cref="OutOfValidityWindow"/>)
/// without matching on <see cref="Exception.Message"/> text.
/// </summary>
public enum LicenseFailureReason
{
    /// <summary>No license key was configured at all.</summary>
    Missing,

    /// <summary>The key's structure, base64 encoding, or JSON payload could not be parsed.</summary>
    Malformed,

    /// <summary>The key's signature did not verify against the expected public key.</summary>
    SignatureInvalid,

    /// <summary>The key parsed and verified, but is outside its validity window (not yet issued, or expired).</summary>
    OutOfValidityWindow,
}
