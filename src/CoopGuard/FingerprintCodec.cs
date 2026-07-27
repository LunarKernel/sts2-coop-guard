using System.Security.Cryptography;
using System.Text;

namespace CoopGuard;

public static class FingerprintCodec
{
    public static string Line(params object?[] fields) =>
        string.Join('|', fields.Select(field => Escape(field?.ToString() ?? string.Empty)));

    public static string Hash(string canonicalText) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalText))).ToLowerInvariant();

    public static IReadOnlyList<string> Diff(string local, string remote, int limit = 12)
    {
        HashSet<string> localLines = Lines(local);
        HashSet<string> remoteLines = Lines(remote);

        return localLines.Except(remoteLines, StringComparer.Ordinal)
            .Select(line => "Local only: " + line)
            .Concat(remoteLines.Except(localLines, StringComparer.Ordinal)
                .Select(line => "Peer only: " + line))
            .Take(limit)
            .ToArray();
    }

    private static HashSet<string> Lines(string value) =>
        value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

    private static string Escape(string value) =>
        value.Replace("%", "%25", StringComparison.Ordinal)
            .Replace("|", "%7C", StringComparison.Ordinal)
            .Replace("\r", "%0D", StringComparison.Ordinal)
            .Replace("\n", "%0A", StringComparison.Ordinal);
}
