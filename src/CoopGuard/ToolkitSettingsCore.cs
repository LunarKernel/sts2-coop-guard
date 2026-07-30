using System.Text;
using System.Text.Json;

namespace CoopGuard;

internal sealed record SettingsProviderDeclaration(
    string OwnerModId,
    string ProviderId,
    int SchemaVersion,
    int MetadataBytes);

internal sealed record SettingsDeclarationRead(
    bool Present,
    IReadOnlyList<SettingsProviderDeclaration> Providers,
    string Error);

internal static class SettingsDeclarationCodec
{
    public const int Schema = 1;
    public const int MaxProvidersPerMod = 64;
    public const int MaxProviderMetadataBytes = 256;
    public const int MaxTotalMetadataBytes = 64 * 1024;
    public const int MaxFileBytes = 32 * 1024;
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static SettingsDeclarationRead Parse(
        byte[] bytes,
        string ownerModId)
    {
        try
        {
            if (bytes.Length > MaxFileBytes)
            {
                return new(true, [], "Declaration file exceeds 32 KiB.");
            }

            _ = StrictUtf8.GetString(bytes);
            using JsonDocument document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4
                });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !HasExactProperties(root, "Schema", "Providers")
                || !root.TryGetProperty("Schema", out JsonElement schema)
                || !schema.TryGetInt32(out int schemaValue)
                || schemaValue != Schema
                || !root.TryGetProperty(
                    "Providers",
                    out JsonElement providers)
                || providers.ValueKind != JsonValueKind.Array
                || providers.GetArrayLength() > MaxProvidersPerMod)
            {
                return new(
                    true,
                    [],
                    "Schema, fields or provider count are invalid.");
            }

            List<SettingsProviderDeclaration> result = [];
            HashSet<string> ids = new(StringComparer.Ordinal);
            foreach (JsonElement provider in providers.EnumerateArray())
            {
                if (provider.ValueKind != JsonValueKind.Object
                    || !HasExactProperties(provider, "Id", "Schema")
                    || !provider.TryGetProperty(
                        "Id",
                        out JsonElement idElement)
                    || idElement.ValueKind != JsonValueKind.String
                    || !provider.TryGetProperty(
                        "Schema",
                        out JsonElement providerSchema)
                    || !providerSchema.TryGetInt32(
                        out int providerSchemaValue))
                {
                    return new(true, [], "A provider record is malformed.");
                }

                string? providerId = idElement.GetString();
                int metadataBytes = providerId == null
                    ? int.MaxValue
                    : Encoding.UTF8.GetByteCount(providerId) + 16;
                if (providerId == null
                    || providerSchemaValue is < 1 or > 1024
                    || metadataBytes > MaxProviderMetadataBytes
                    || !OwnsProvider(ownerModId, providerId)
                    || !ids.Add(providerId))
                {
                    return new(
                        true,
                        [],
                        "A provider ID/schema is unsafe, duplicated or not owned by the Mod.");
                }

                result.Add(new(
                    ownerModId,
                    providerId,
                    providerSchemaValue,
                    metadataBytes));
            }

            return new(true, result, string.Empty);
        }
        catch (Exception ex) when (
            ex is JsonException
                or DecoderFallbackException
                or ArgumentException
                or InvalidOperationException
                or OverflowException)
        {
            return new(
                true,
                [],
                "Declaration syntax is invalid ("
                + ex.GetType().Name
                + ").");
        }
    }

    public static bool OwnsProvider(string ownerModId, string providerId) =>
        !string.IsNullOrWhiteSpace(ownerModId)
        && providerId.Length is > 0 and <= 128
        && !providerId.Any(char.IsControl)
        && !providerId.Contains("..", StringComparison.Ordinal)
        && !providerId.Contains('/')
        && !providerId.Contains('\\')
        && !Path.IsPathRooted(providerId)
        && (string.Equals(providerId, ownerModId, StringComparison.Ordinal)
            || providerId.StartsWith(
                ownerModId + ":",
                StringComparison.Ordinal));

    private static bool HasExactProperties(
        JsonElement element,
        params string[] names)
    {
        string[] actual = element.EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        return actual.Length == names.Length
            && actual.Distinct(StringComparer.Ordinal).Count() == actual.Length
            && names.All(name => actual.Contains(name, StringComparer.Ordinal));
    }
}
