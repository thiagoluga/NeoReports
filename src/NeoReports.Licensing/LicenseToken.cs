namespace NeoReports.Licensing;

/// <summary>
/// The signature-verified contents of a NeoReports Pro license key (ADR D70). Only ever produced by
/// <see cref="LicenseValidator.Validate"/> after a successful signature check, or by
/// <see cref="LicenseSigner"/> before signing — never construct one from untrusted input directly.
/// </summary>
/// <param name="Licensee">Free-text licensee name (person, email, or organization) — informational only, not a technical enforcement key (ADR D70's "no machine binding" gap).</param>
/// <param name="IssuedAtUtc">When the license was issued.</param>
/// <param name="ExpiresAtUtc">When the license stops being valid.</param>
public sealed record LicenseToken(string Licensee, DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc)
{
    /// <summary>Whether this license is within its validity window at <paramref name="utcNow"/>.</summary>
    public bool IsValidAt(DateTimeOffset utcNow) => utcNow >= IssuedAtUtc && utcNow < ExpiresAtUtc;
}
