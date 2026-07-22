namespace NeoReports.Licensing;

/// <summary>
/// Unpadded, URL-safe base64 (RFC 4648 §5) — used for the NeoReports Pro license key format
/// (ADR D70) so a key can be pasted into a URL, env var, or config file without escaping.
/// </summary>
internal static class Base64Url
{
    /// <summary>Encodes <paramref name="bytes"/> as unpadded base64url text.</summary>
    public static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes base64url text back to bytes. Throws <see cref="FormatException"/> on invalid input.</summary>
    public static byte[] Decode(string text)
    {
        string padded = text.Replace('-', '+').Replace('_', '/');
        int remainder = padded.Length % 4;
        if (remainder > 0)
            padded += new string('=', 4 - remainder);

        return Convert.FromBase64String(padded);
    }
}
