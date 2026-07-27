using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Modding;

namespace CoopGuard;

internal static class SkinManagerMounts
{
    private const string ModId = "Sts2SkinManager";
    private const string SupportedVersion = "0.27.1";
    private const string SupportedGameVersion = "v0.109.1";
    private const string SupportedGameCommit = "c8c577f6";
    private const int SupportedMainAssemblyHash = 195020890;
    private const int MaxReleaseInfoBytes = 4096;
    private const string RegistryTypeName =
        "Sts2SkinManager.Runtime.ManagedPckRegistry";

    public static IReadOnlyList<string> GetCurrentPaths()
    {
        List<Mod> matches = ModManager.Mods
            .Where(mod => mod.state == ModLoadState.Loaded
                && string.Equals(
                    mod.manifest?.id,
                    ModId,
                    StringComparison.Ordinal))
            .ToList();
        if (matches.Count == 0)
        {
            return [];
        }

        if (matches.Count != 1)
        {
            throw new InvalidDataException(
                "More than one loaded Sts2SkinManager was found.");
        }

        Mod manager = matches[0];
        if (!string.Equals(
                manager.manifest?.version,
                SupportedVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The loaded Sts2SkinManager version is not supported for mounted-PCK verification.");
        }

        VerifyGameBuild();

        Version runtime = Environment.Version;
        if (runtime.Major != 9 || runtime.Minor != 0 || runtime.Build != 7)
        {
            throw new InvalidDataException(
                "The current .NET runtime is not supported for ordered mounted-PCK verification.");
        }

        Type? registry = manager.assemblies
            .Select(assembly => assembly.GetType(
                RegistryTypeName,
                throwOnError: false,
                ignoreCase: false))
            .SingleOrDefault(type => type != null);
        if (registry == null
            || !registry.IsPublic
            || !registry.IsAbstract
            || !registry.IsSealed)
        {
            throw new MissingMemberException(
                RegistryTypeName,
                "public static registry type");
        }

        PropertyInfo? pathsProperty = registry.GetProperty(
            "AllMountedPaths",
            BindingFlags.Public | BindingFlags.Static);
        MethodInfo? markMethod = registry.GetMethod(
            "MarkMounted",
            BindingFlags.Public | BindingFlags.Static,
            [typeof(string)]);
        MethodInfo? isMountedMethod = registry.GetMethod(
            "IsMounted",
            BindingFlags.Public | BindingFlags.Static,
            [typeof(string)]);
        if (pathsProperty?.PropertyType != typeof(IReadOnlyCollection<string>)
            || pathsProperty.GetMethod is not { IsPublic: true, IsStatic: true }
            || markMethod?.ReturnType != typeof(void)
            || isMountedMethod?.ReturnType != typeof(bool))
        {
            throw new MissingMemberException(
                RegistryTypeName,
                "expected mounted-PCK registry API");
        }

        if (pathsProperty.GetValue(null)
            is not IReadOnlyCollection<string> livePaths)
        {
            throw new InvalidDataException(
                "Sts2SkinManager returned an invalid mounted-PCK collection.");
        }

        string[] snapshot = livePaths.ToArray();
        for (int index = 0; index < snapshot.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(snapshot[index]))
            {
                throw new InvalidDataException(
                    "Sts2SkinManager returned an empty mounted-PCK path.");
            }

            snapshot[index] = Path.GetFullPath(snapshot[index]);
        }

        return snapshot;
    }

    private static void VerifyGameBuild()
    {
        string? dataDirectory = Path.GetDirectoryName(
            typeof(ModManager).Assembly.Location);
        string? gameDirectory = dataDirectory == null
            ? null
            : Directory.GetParent(dataDirectory)?.FullName;
        if (gameDirectory == null)
        {
            throw new InvalidDataException(
                "The STS2 installation root could not be verified.");
        }

        string path = Path.Combine(gameDirectory, "release_info.json");
        FileInfo file = new(path);
        file.Refresh();
        if (!file.Exists
            || file.Length > MaxReleaseInfoBytes
            || (file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "The STS2 release metadata could not be verified.");
        }

        using FileStream stream = File.OpenRead(path);
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("version", out JsonElement version)
            || version.ValueKind != JsonValueKind.String
            || !string.Equals(
                version.GetString(),
                SupportedGameVersion,
                StringComparison.Ordinal)
            || !root.TryGetProperty("commit", out JsonElement commit)
            || commit.ValueKind != JsonValueKind.String
            || !string.Equals(
                commit.GetString(),
                SupportedGameCommit,
                StringComparison.Ordinal)
            || !root.TryGetProperty(
                "main_assembly_hash",
                out JsonElement assemblyHash)
            || !assemblyHash.TryGetInt32(out int parsedAssemblyHash)
            || parsedAssemblyHash != SupportedMainAssemblyHash)
        {
            throw new InvalidDataException(
                "The current STS2 build is not supported for mounted-PCK verification.");
        }
    }
}
