using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MegaCrit.Sts2.Core.Modding;

namespace CoopGuard;

public sealed record FingerprintSnapshot(string Digest, string Details, IReadOnlyList<string> Errors);

internal static class ModFingerprint
{
    private static readonly object CaptureLock = new();
    private static FingerprintSnapshot? _cached;

    public static FingerprintSnapshot GetOrCapture()
    {
        lock (CaptureLock)
        {
            return _cached ??= Capture();
        }
    }

    private static FingerprintSnapshot Capture()
    {
        List<string> lines = [];
        List<string> errors = [];
        int order = 0;

        foreach (Mod failed in ModManager.Mods.Where(mod =>
                     mod.state is ModLoadState.Failed or ModLoadState.AddedAtRuntime))
        {
            errors.Add($"{failed.manifest?.id ?? "<missing-id>"}: game reported Mod state {failed.state}.");
        }

        foreach (Mod mod in ModManager.Mods.Where(mod => mod.state == ModLoadState.Loaded))
        {
            string id = mod.manifest?.id ?? "<missing-id>";
            string version = mod.manifest?.version ?? "<missing-version>";
            bool affectsGameplay = mod.manifest?.affectsGameplay ?? true;

            lines.Add(FingerprintCodec.Line("mod", order, id, version, affectsGameplay));

            if (mod.errors is { Count: > 0 })
            {
                errors.Add($"{id}: game reported {mod.errors.Count} Mod load error(s).");
            }

            try
            {
                AddManifest(lines, errors, order, mod, id);
                AddDeclaredPck(lines, errors, order, mod, id);
                AddLoadedAssemblies(lines, errors, order, mod, id);
            }
            catch (Exception ex)
            {
                errors.Add($"{id}: {ex.GetType().Name}: {ex.Message}");
            }

            order++;
        }

        string details = string.Join('\n', lines);
        FingerprintSnapshot snapshot = new(FingerprintCodec.Hash(details), details, errors);
        Main.Log.Info($"Captured {order} loaded Mods, {lines.Count} fingerprint lines, digest {snapshot.Digest}.");

        foreach (string error in errors)
        {
            Main.Log.Error("Fingerprint error: " + error);
        }

        return snapshot;
    }

    private static void AddManifest(
        List<string> lines,
        List<string> errors,
        int order,
        Mod mod,
        string id)
    {
        string[] matches = Directory.EnumerateFiles(mod.path, "*.json", SearchOption.TopDirectoryOnly)
            .Where(path => ManifestId(path) == id)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();

        if (matches.Length == 0)
        {
            errors.Add($"{id}: matching manifest was not found in the loaded Mod directory.");
            return;
        }

        foreach (string path in matches)
        {
            AddFile(lines, order, id, "manifest/" + Path.GetFileName(path), path);
        }
    }

    private static void AddDeclaredPck(
        List<string> lines,
        List<string> errors,
        int order,
        Mod mod,
        string id)
    {
        if (mod.manifest?.hasPck != true)
        {
            return;
        }

        string path = Path.Combine(mod.path, id + ".pck");
        if (!File.Exists(path))
        {
            errors.Add($"{id}: declared PCK is missing.");
            return;
        }

        AddFile(lines, order, id, "pck/" + Path.GetFileName(path), path);
    }

    private static void AddLoadedAssemblies(
        List<string> lines,
        List<string> errors,
        int order,
        Mod mod,
        string id)
    {
        if (mod.manifest?.hasDll == true && mod.assemblies.Count == 0)
        {
            errors.Add($"{id}: declared DLL has no loaded assembly.");
            return;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (Assembly assembly in mod.assemblies.OrderBy(assembly => assembly.FullName, StringComparer.Ordinal))
        {
            string path = assembly.Location;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                // Fail closed: generated assemblies cannot be proven byte-identical.
                errors.Add($"{id}: loaded assembly {assembly.FullName} has no hashable file.");
                continue;
            }

            string fullPath = Path.GetFullPath(path);
            if (seen.Add(fullPath))
            {
                AddFile(lines, order, id, "dll/" + Path.GetFileName(fullPath), fullPath);
            }
        }
    }

    private static void AddFile(
        List<string> lines,
        int order,
        string id,
        string label,
        string path)
    {
        using FileStream stream = File.OpenRead(path);
        string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

        // Absolute paths are intentionally excluded from the wire data.
        lines.Add(FingerprintCodec.Line("file", order, id, label, stream.Length, hash));
    }

    private static string? ManifestId(string path)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.TryGetProperty("id", out JsonElement id)
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
