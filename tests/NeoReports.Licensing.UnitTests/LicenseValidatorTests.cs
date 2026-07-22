using System.Globalization;
using System.Security.Cryptography;
using Shouldly;
using Xunit;

namespace NeoReports.Licensing.UnitTests;

/// <summary>ADR D70: <see cref="LicenseSigner"/>/<see cref="LicenseValidator"/> round trip and failure modes.</summary>
public class LicenseValidatorTests
{
    private static ECDsa NewKeyPair() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static LicenseToken ValidToken() =>
        new("Acme Corp", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), DateTimeOffset.Parse("2026-01-31T00:00:00Z"));

    [Fact]
    public void Sign_then_validate_round_trips_the_token()
    {
        using ECDsa keyPair = NewKeyPair();
        string key = LicenseSigner.Sign(ValidToken(), keyPair);

        LicenseToken result = LicenseValidator.Validate(key, keyPair, DateTimeOffset.Parse("2026-01-15T00:00:00Z"));

        result.Licensee.ShouldBe("Acme Corp");
        result.IssuedAtUtc.ShouldBe(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        result.ExpiresAtUtc.ShouldBe(DateTimeOffset.Parse("2026-01-31T00:00:00Z"));
    }

    [Fact]
    public void A_key_with_surrounding_whitespace_still_validates()
    {
        // A key sourced from a mounted file (e.g. a Kubernetes Secret) commonly carries a trailing newline.
        using ECDsa keyPair = NewKeyPair();
        string key = LicenseSigner.Sign(ValidToken(), keyPair);

        LicenseToken result = LicenseValidator.Validate($"  {key}\n", keyPair, DateTimeOffset.Parse("2026-01-15T00:00:00Z"));

        result.Licensee.ShouldBe("Acme Corp");
    }

    [Fact]
    public void Null_license_key_throws_with_the_Missing_reason()
    {
        using ECDsa keyPair = NewKeyPair();

        NeoReportsLicenseException ex = Should.Throw<NeoReportsLicenseException>(() => LicenseValidator.Validate(null, keyPair));

        ex.Reason.ShouldBe(LicenseFailureReason.Missing);
        ex.Message.ShouldContain("No NeoReports Pro license key was configured");
    }

    [Fact]
    public void Blank_license_key_throws()
    {
        using ECDsa keyPair = NewKeyPair();

        Should.Throw<NeoReportsLicenseException>(() => LicenseValidator.Validate("   ", keyPair));
    }

    [Fact]
    public void Key_without_a_dot_separator_throws_malformed()
    {
        using ECDsa keyPair = NewKeyPair();

        NeoReportsLicenseException ex = Should.Throw<NeoReportsLicenseException>(() => LicenseValidator.Validate("not-a-real-key", keyPair));

        ex.Reason.ShouldBe(LicenseFailureReason.Malformed);
        ex.Message.ShouldContain("malformed");
    }

    [Fact]
    public void Key_with_invalid_base64_throws_malformed()
    {
        using ECDsa keyPair = NewKeyPair();

        Should.Throw<NeoReportsLicenseException>(() => LicenseValidator.Validate("!!!.!!!", keyPair))
            .Message.ShouldContain("malformed");
    }

    [Fact]
    public void Key_signed_by_a_different_key_pair_fails_signature_check()
    {
        using ECDsa signingKey = NewKeyPair();
        using ECDsa verifyingKey = NewKeyPair();
        string key = LicenseSigner.Sign(ValidToken(), signingKey);

        NeoReportsLicenseException ex = Should.Throw<NeoReportsLicenseException>(() => LicenseValidator.Validate(key, verifyingKey));

        ex.Reason.ShouldBe(LicenseFailureReason.SignatureInvalid);
        ex.Message.ShouldContain("signature is invalid");
    }

    [Fact]
    public void Tampered_payload_fails_signature_check()
    {
        using ECDsa keyPair = NewKeyPair();
        string key = LicenseSigner.Sign(ValidToken(), keyPair);
        string[] parts = key.Split('.');
        byte[] tamperedPayloadBytes = "{\"licensee\":\"Evil Corp\",\"issuedAtUtc\":\"2026-01-01T00:00:00Z\",\"expiresAtUtc\":\"2099-01-01T00:00:00Z\"}"u8.ToArray();
        string tamperedKey = $"{Base64Url.Encode(tamperedPayloadBytes)}.{parts[1]}";

        Should.Throw<NeoReportsLicenseException>(() => LicenseValidator.Validate(tamperedKey, keyPair))
            .Message.ShouldContain("signature is invalid");
    }

    [Fact]
    public void Expired_license_throws_with_the_licensee_and_window_in_the_message()
    {
        using ECDsa keyPair = NewKeyPair();
        string key = LicenseSigner.Sign(ValidToken(), keyPair);

        NeoReportsLicenseException ex = Should.Throw<NeoReportsLicenseException>(
            () => LicenseValidator.Validate(key, keyPair, DateTimeOffset.Parse("2026-02-01T00:00:00Z")));

        ex.Reason.ShouldBe(LicenseFailureReason.OutOfValidityWindow);
        ex.Message.ShouldContain("Acme Corp");
        ex.Message.ShouldContain("2026-01-31");
    }

    [Fact]
    public void Expiry_message_formats_dates_with_the_invariant_culture()
    {
        using ECDsa keyPair = NewKeyPair();
        string key = LicenseSigner.Sign(ValidToken(), keyPair);
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            // Thai culture uses the Buddhist calendar (year 2569, not 2026) — a non-invariant
            // formatter would leak that into a message meant to be read by any operator/support team.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");

            NeoReportsLicenseException ex = Should.Throw<NeoReportsLicenseException>(
                () => LicenseValidator.Validate(key, keyPair, DateTimeOffset.Parse("2026-02-01T00:00:00Z")));

            ex.Message.ShouldContain("2026-01-31");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Not_yet_issued_license_throws()
    {
        using ECDsa keyPair = NewKeyPair();
        string key = LicenseSigner.Sign(ValidToken(), keyPair);

        Should.Throw<NeoReportsLicenseException>(
            () => LicenseValidator.Validate(key, keyPair, DateTimeOffset.Parse("2025-12-31T00:00:00Z")));
    }

    [Fact]
    public void Valid_signature_over_non_json_payload_throws_malformed_payload()
    {
        using ECDsa keyPair = NewKeyPair();
        byte[] payloadBytes = "not valid json"u8.ToArray();
        byte[] signatureBytes = keyPair.SignData(payloadBytes, HashAlgorithmName.SHA256);

        Should.Throw<NeoReportsLicenseException>(() => LicenseValidator.Validate($"{Base64Url.Encode(payloadBytes)}.{Base64Url.Encode(signatureBytes)}", keyPair))
            .Message.ShouldContain("payload is malformed");
    }

    [Fact]
    public void Valid_signature_over_a_payload_with_no_licensee_throws_incomplete_payload()
    {
        using ECDsa keyPair = NewKeyPair();
        byte[] payloadBytes = "{\"issuedAtUtc\":\"2026-01-01T00:00:00Z\",\"expiresAtUtc\":\"2099-01-01T00:00:00Z\"}"u8.ToArray();
        byte[] signatureBytes = keyPair.SignData(payloadBytes, HashAlgorithmName.SHA256);

        NeoReportsLicenseException ex = Should.Throw<NeoReportsLicenseException>(
            () => LicenseValidator.Validate($"{Base64Url.Encode(payloadBytes)}.{Base64Url.Encode(signatureBytes)}", keyPair));

        ex.Reason.ShouldBe(LicenseFailureReason.Malformed);
        ex.Message.ShouldContain("incomplete");
    }

    [Fact]
    public void IsValidAt_is_exclusive_of_the_expiry_instant()
    {
        LicenseToken token = ValidToken();

        token.IsValidAt(token.ExpiresAtUtc).ShouldBeFalse();
        token.IsValidAt(token.ExpiresAtUtc.AddSeconds(-1)).ShouldBeTrue();
        token.IsValidAt(token.IssuedAtUtc).ShouldBeTrue();
    }
}
