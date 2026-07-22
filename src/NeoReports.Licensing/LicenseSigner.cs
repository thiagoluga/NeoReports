using System.Security.Cryptography;
using System.Text.Json;

namespace NeoReports.Licensing;

/// <summary>
/// Signs a <see cref="LicenseToken"/> into the compact key format NeoReports Pro packages accept
/// (ADR D70) — used only by whatever issues real licenses (the maintainer's own tooling/website,
/// out of scope for this repo). Customers never call this; they only ever call
/// <see cref="LicenseValidator"/>/<see cref="ProLicense"/>. Shipping the signing code publicly
/// alongside the verifier does not weaken enforcement — security comes from keeping the private key
/// secret, not from hiding this algorithm (Kerckhoffs's principle), matching D70's "auditable
/// verification code is a trust signal" reasoning.
/// </summary>
public static class LicenseSigner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Signs <paramref name="token"/> with <paramref name="privateKey"/> (an ECDsa P-256 key pair's
    /// private half) and returns the compact license key string.
    /// </summary>
    public static string Sign(LicenseToken token, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(privateKey);

        byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(token, Json);
        byte[] signatureBytes = privateKey.SignData(payloadBytes, HashAlgorithmName.SHA256);

        return $"{Base64Url.Encode(payloadBytes)}.{Base64Url.Encode(signatureBytes)}";
    }
}
