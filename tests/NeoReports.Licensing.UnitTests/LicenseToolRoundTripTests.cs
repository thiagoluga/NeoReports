using System.Security.Cryptography;
using Shouldly;
using Xunit;

namespace NeoReports.Licensing.UnitTests;

/// <summary>
/// Proves the exact key handling <c>tools/NeoReports.LicenseTool</c> performs actually produces a
/// license the shipped validator accepts (ADR D70, Q3). The tool is a thin CLI over
/// <see cref="LicenseSigner"/>, so what is worth testing is the round trip through the PEM export /
/// import it uses — the step where an issued license could silently stop verifying.
/// </summary>
public class LicenseToolRoundTripTests
{
    /// <summary>Mirrors the tool's keygen: a P-256 pair, private exported as PKCS#8 PEM.</summary>
    private static (string PrivatePem, string PublicBase64) GenerateKeyPair()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void A_key_signed_from_the_exported_pem_validates_against_the_exported_public_key()
    {
        (string privatePem, string publicBase64) = GenerateKeyPair();

        using ECDsa signingKey = ECDsa.Create();
        signingKey.ImportFromPem(privatePem);
        string licenseKey = LicenseSigner.Sign(
            new LicenseToken("Acme Corp", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30)),
            signingKey);

        using ECDsa verifyingKey = ECDsa.Create();
        verifyingKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicBase64), out _);
        LicenseToken validated = LicenseValidator.Validate(licenseKey, verifyingKey);

        validated.Licensee.ShouldBe("Acme Corp");
    }

    [Fact]
    public void A_key_signed_by_one_pair_does_not_validate_against_another_pairs_public_key()
    {
        // i.e. rotating the signing key really does invalidate previously-issued licenses, which is
        // the property the tool's own rotation warning promises.
        (string privatePem, _) = GenerateKeyPair();
        (_, string otherPublicBase64) = GenerateKeyPair();

        using ECDsa signingKey = ECDsa.Create();
        signingKey.ImportFromPem(privatePem);
        string licenseKey = LicenseSigner.Sign(
            new LicenseToken("Acme Corp", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30)),
            signingKey);

        using ECDsa verifyingKey = ECDsa.Create();
        verifyingKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(otherPublicBase64), out _);

        Should.Throw<NeoReportsLicenseException>(() => LicenseValidator.Validate(licenseKey, verifyingKey))
            .Reason.ShouldBe(LicenseFailureReason.SignatureInvalid);
    }

    [Fact]
    public void A_thirty_day_license_is_valid_today_and_expired_after_the_window()
    {
        (string privatePem, string publicBase64) = GenerateKeyPair();
        DateTimeOffset issuedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        using ECDsa signingKey = ECDsa.Create();
        signingKey.ImportFromPem(privatePem);
        string licenseKey = LicenseSigner.Sign(
            new LicenseToken("Acme Corp", issuedAt, issuedAt.AddDays(30)), signingKey);

        using ECDsa verifyingKey = ECDsa.Create();
        verifyingKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicBase64), out _);

        Should.NotThrow(() => LicenseValidator.Validate(licenseKey, verifyingKey, issuedAt.AddDays(29)));
        Should.Throw<NeoReportsLicenseException>(() => LicenseValidator.Validate(licenseKey, verifyingKey, issuedAt.AddDays(31)))
            .Reason.ShouldBe(LicenseFailureReason.OutOfValidityWindow);
    }
}
