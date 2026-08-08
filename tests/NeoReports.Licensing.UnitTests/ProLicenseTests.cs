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

    /// <summary>
    /// The burned placeholder key (ADR D83). Its private half was generated inside a chat session, so
    /// anyone reading that transcript can mint licenses that validate forever — offline validation has
    /// no revocation list. It is written out here, in the open, precisely because it is worthless: the
    /// only thing it can still do is come back by accident.
    /// </summary>
    private const string BurnedPlaceholderKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE8CTV2vXjiGkicpdqu6zK5kzHUCAotsIs7ysuwvAP/ZhxSmAcK/ZuZ4w//6XT0I71il/8qeuobgic5csTNd5How==";

    [Fact]
    public void The_embedded_public_key_is_not_the_burned_placeholder()
    {
        // A revert, a bad merge resolution, or a copy-paste from an old branch would silently ship
        // packages that trust a compromised key — and NuGet versions are immutable, so it could not be
        // taken back. The Pro packages are publishable (IsPackable, D83), which is exactly what makes
        // this worth a test rather than a comment: the release path no longer has a human in it.
        ProLicense.PublicKeyBase64.ShouldNotBe(
            BurnedPlaceholderKey,
            "the placeholder signing key is compromised and must never be shipped — see ADR D83");
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
