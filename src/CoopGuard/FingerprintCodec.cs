using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CoopGuard;

public static class FingerprintCodec
{
    public const int ProtocolVersion = 3;
    public const string CompatibilityFamilyPrefix = "CoopGuard-package-v";
    public static readonly string CompatibilityPrefix =
        CompatibilityFamilyPrefix + ProtocolVersion.ToString(CultureInfo.InvariantCulture) + "-";

    public static string Line(params object?[] fields) =>
        string.Join('|', fields.Select(field =>
            Escape(Convert.ToString(field, CultureInfo.InvariantCulture) ?? string.Empty)));

    public static string Hash(string canonicalText) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalText))).ToLowerInvariant();

    private static string Escape(string value) =>
        value.Replace("%", "%25", StringComparison.Ordinal)
            .Replace("|", "%7C", StringComparison.Ordinal)
            .Replace("\r", "%0D", StringComparison.Ordinal)
            .Replace("\n", "%0A", StringComparison.Ordinal);
}
