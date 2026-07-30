using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BetterCoop;

public static class FingerprintCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public const int ProtocolVersion = 5;
    public const string CompatibilityFamilyPrefix = "BetterCoop-package-v";
    public const string ComponentFamilyPrefix = "BetterCoop-component-v";
    public static readonly string CompatibilityPrefix =
        CompatibilityFamilyPrefix + ProtocolVersion.ToString(CultureInfo.InvariantCulture) + "-";
    public static readonly string ComponentPrefix =
        ComponentFamilyPrefix + ProtocolVersion.ToString(CultureInfo.InvariantCulture) + "-";

    public static string Line(params object?[] fields) =>
        string.Join('|', fields.Select(field =>
            Escape(Convert.ToString(field, CultureInfo.InvariantCulture) ?? string.Empty)));

    public static string Hash(string canonicalText) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalText))).ToLowerInvariant();

    public static string ComponentEntry(string modId, string digest) =>
        ComponentPrefix
        + Convert.ToBase64String(Encoding.UTF8.GetBytes(modId))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_')
        + "-"
        + digest;

    public static bool TryParseComponentEntry(
        string value,
        out string modId)
    {
        modId = string.Empty;
        if (!value.StartsWith(ComponentPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> payload = value.AsSpan(ComponentPrefix.Length);
        int separator = payload.LastIndexOf('-');
        if (separator <= 0
            || payload.Length - separator - 1 != 64
            || !payload[(separator + 1)..].ToString().All(Uri.IsHexDigit))
        {
            return false;
        }

        string encoded = payload[..separator]
            .ToString()
            .Replace('-', '+')
            .Replace('_', '/');
        if (encoded.Length > 2048)
        {
            return false;
        }

        encoded = encoded.PadRight(
            encoded.Length + ((4 - encoded.Length % 4) % 4),
            '=');
        try
        {
            modId = StrictUtf8.GetString(
                Convert.FromBase64String(encoded));
            return !string.IsNullOrWhiteSpace(modId);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string Escape(string value) =>
        value.Replace("%", "%25", StringComparison.Ordinal)
            .Replace("|", "%7C", StringComparison.Ordinal)
            .Replace("\r", "%0D", StringComparison.Ordinal)
            .Replace("\n", "%0A", StringComparison.Ordinal);
}
