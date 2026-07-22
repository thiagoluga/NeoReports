using System.Security.Cryptography;
using Shouldly;
using Xunit;

namespace NeoReports.Licensing.UnitTests;

/// <summary>ADR D70: <see cref="ProLicense"/> — the embedded-public-key convenience wrapper.</summary>
public class ProLicenseTests
{
    [Fact]
    public void The_embedded_public_key_imports_as_a_valid_P256_ECDsa_key()
    {
        using ECDsa publicKey = ECDsa.Create();

        Should.NotThrow(() => publicKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ProLicense.PublicKeyBase64), out _));
        publicKey.KeySize.ShouldBe(256);
    }

    [Fact]
    public void A_key_signed_by_a_different_key_pair_is_rejected_by_the_embedded_public_key()
    {
        // Proves ProLicense.Validate actually enforces the embedded key rather than being a
        // pass-through — a token signed by some other (non-NeoReports) key pair must fail here.
        using ECDsa someOtherKeyPair = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string key = LicenseSigner.Sign(
            new LicenseToken("Acme Corp", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30)),
            someOtherKeyPair);

        Should.Throw<NeoReportsLicenseException>(() => ProLicense.Validate(key))
            .Message.ShouldContain("signature is invalid");
    }

    [Fact]
    public void Missing_key_throws()
    {
        Should.Throw<NeoReportsLicenseException>(() => ProLicense.Validate(null));
    }
}
