using System.Security.Cryptography;
using System.Text;

namespace JobMatch.Api.WhatsApp;

/// <summary>
/// Validates the <c>X-Hub-Signature-256</c> header Meta sends with every webhook POST:
/// HMAC-SHA256 over the <b>raw</b> request body keyed with the app secret, compared in
/// constant time. (DL-003)
/// </summary>
public static class WhatsAppSignatureValidator
{
    /// <param name="appSecret">Meta app secret.</param>
    /// <param name="rawBody">The exact bytes received (not re-serialised JSON).</param>
    /// <param name="signatureHeader">Value of X-Hub-Signature-256, e.g. "sha256=abcd...".</param>
    public static bool IsValid(string appSecret, ReadOnlySpan<byte> rawBody, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(appSecret) || string.IsNullOrEmpty(signatureHeader))
            return false;

        const string prefix = "sha256=";
        if (!signatureHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var providedHex = signatureHeader.AsSpan(prefix.Length);

        Span<byte> computed = stackalloc byte[32];
        if (!HMACSHA256.TryHashData(Encoding.UTF8.GetBytes(appSecret), rawBody, computed, out _))
            return false;

        // Decode the provided hex into bytes; malformed hex fails closed.
        if (providedHex.Length != 64) return false;
        Span<byte> provided = stackalloc byte[32];
        if (!TryDecodeHex(providedHex, provided)) return false;

        return CryptographicOperations.FixedTimeEquals(computed, provided);
    }

    private static bool TryDecodeHex(ReadOnlySpan<char> hex, Span<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            int hi = FromHex(hex[i * 2]);
            int lo = FromHex(hex[i * 2 + 1]);
            if (hi < 0 || lo < 0) return false;
            bytes[i] = (byte)((hi << 4) | lo);
        }
        return true;
    }

    private static int FromHex(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };
}
