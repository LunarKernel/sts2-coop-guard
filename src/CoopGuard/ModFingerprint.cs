using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Modding;

namespace CoopGuard;

internal sealed record FingerprintSnapshot(
    string Digest,
    IReadOnlyList<string> Errors,
    IReadOnlyList<PackageCapture> Packages,
    IReadOnlyList<MountedPckCapture> MountedPcks,
    IReadOnlyList<LoadedModStamp> LoadedMods,
    int ModCount,
    int FileCount,
    long TotalBytes);

internal sealed record LoadedModStamp(
    string Id,
    string Version,
    string Root,
    bool AffectsGameplay,
    int AssemblyCount);

internal static class ModFingerprint
{
    private const int MaxManifestBytes = 1024 * 1024;
    private const int MaxMetadataCharacters = 1024;
    private const int MaxTotalFiles = 4096;
    private const long MaxTotalBytes = 1024L * 1024 * 1024;
    private const int MaxCanonicalCharacters = 8 * 1024 * 1024;
    private const int MaxTotalEntries = 16_384;
    private const string CompatibilityPrefix = "CoopGuard-package-v3-";

    private static readonly object CaptureLock = new();
    private static readonly string UnsafeCompatibilityEntry =
        CompatibilityPrefix + "error-" + Guid.NewGuid().ToString("N");

    private static FingerprintSnapshot? _baseline;
    private static FingerprintSnapshot? _lastFailure;
    private static bool _trackingRuntimeChanges;
    private static bool _restartRequired;
    private static bool _compatibilityEntryIssued;

    public static void Precompute()
    {
        lock (CaptureLock)
        {
            StartTrackingRuntimeChanges();
            if (_restartRequired)
            {
                return;
            }

            FingerprintSnapshot snapshot = CaptureSafely();
            if (snapshot.Errors.Count == 0)
            {
                _baseline = snapshot;
                _lastFailure = null;
                return;
            }

            _lastFailure = snapshot;
            Main.Log.Error(
                "Initial package fingerprint failed. Multiplayer will fail closed until a later validation succeeds.");
        }
    }

    public static FingerprintSnapshot ValidateCurrent()
    {
        lock (CaptureLock)
        {
            StartTrackingRuntimeChanges();

            if (_restartRequired)
            {
                return Failure(
                    "Mod packages changed after startup. Restart the game before multiplayer.");
            }

            FingerprintSnapshot current = CaptureSafely();
            if (current.Errors.Count > 0)
            {
                _lastFailure = current;
                return current;
            }

            if (_baseline == null)
            {
                _baseline = current;
                _lastFailure = null;
                return current;
            }

            if (!string.Equals(
                    current.Digest,
                    _baseline.Digest,
                    StringComparison.Ordinal))
            {
                _restartRequired = true;
                _lastFailure = Failure(
                    "A Mod package changed after startup. Restart the game before multiplayer.");
                Main.Log.Error(
                    "A captured Mod package changed on disk. Restart is required before multiplayer.");
                return _lastFailure;
            }

            _baseline = current;
            _lastFailure = null;
            return current;
        }
    }

    public static void SettleStartupMounts()
    {
        lock (CaptureLock)
        {
            if (_compatibilityEntryIssued || _restartRequired)
            {
                return;
            }

            if (_baseline != null)
            {
                try
                {
                    int remainingEntries = MaxTotalEntries;
                    if (MountedPcksAreCurrent(
                            _baseline.MountedPcks,
                            ref remainingEntries))
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Main.Log.Info(
                        $"Startup mounted-PCK state needs recapture: {ex.GetType().Name}.");
                }
            }

            FingerprintSnapshot settled = CaptureSafely();
            if (settled.Errors.Count > 0)
            {
                _lastFailure = settled;
                return;
            }

            bool changed = _baseline == null
                || !string.Equals(
                    settled.Digest,
                    _baseline.Digest,
                    StringComparison.Ordinal);
            _baseline = settled;
            _lastFailure = null;
            if (changed)
            {
                Main.Log.Info(
                    "Refreshed the package fingerprint after startup PCK mounts settled.");
            }
        }
    }

    public static FingerprintSnapshot ValidateQuick()
    {
        // ponytail: network callbacks use bounded metadata checks; add watchers
        // plus background rehashing if the threat model expands past trusted peers.
        lock (CaptureLock)
        {
            StartTrackingRuntimeChanges();

            if (_restartRequired)
            {
                return Failure(
                    "Mod packages changed after startup. Restart the game before multiplayer.");
            }

            if (_lastFailure != null)
            {
                return _lastFailure;
            }

            if (_baseline == null)
            {
                _lastFailure = Failure(
                    "A full package fingerprint is not available. Retry before multiplayer.");
                return _lastFailure;
            }

            try
            {
                int remainingEntries = MaxTotalEntries;
                bool current = LoadedModsAreCurrent(_baseline.LoadedMods);
                if (current
                    && !MountedPcksAreCurrent(
                        _baseline.MountedPcks,
                        ref remainingEntries))
                {
                    current = false;
                }

                foreach (PackageCapture package in _baseline.Packages)
                {
                    if (!current
                        || !PackageHasher.IsCurrent(package, ref remainingEntries))
                    {
                        current = false;
                        break;
                    }
                }

                if (current)
                {
                    return _baseline;
                }
            }
            catch (Exception ex)
            {
                Main.Log.Error($"Package freshness check failed: {ex}");
                _lastFailure = Failure(
                    $"Package freshness check failed ({ex.GetType().Name}).");
                return _lastFailure;
            }

            _restartRequired = true;
            _lastFailure = Failure(
                "A Mod package changed after startup. Restart the game before multiplayer.");
            Main.Log.Error(
                "A captured Mod package changed on disk. Restart is required before multiplayer.");
            return _lastFailure;
        }
    }

    public static string GetCompatibilityEntry()
    {
        lock (CaptureLock)
        {
            _compatibilityEntryIssued = true;
            return !_restartRequired && _baseline != null && _lastFailure == null
                ? CompatibilityPrefix + _baseline.Digest
                : UnsafeCompatibilityEntry;
        }
    }

    public static void MarkRuntimeChange()
    {
        lock (CaptureLock)
        {
            if (!_trackingRuntimeChanges)
            {
                return;
            }

            _restartRequired = true;
            _lastFailure = Failure(
                "A Mod or Mod assembly changed after startup. Restart the game before multiplayer.");
            Main.Log.Error(
                "A Mod or Mod assembly was detected after startup. Restart is required before multiplayer.");
        }
    }

    public static int LoadedAssemblyCount(string modId)
    {
        try
        {
            return ModManager.Mods
                .Where(mod => mod.state == ModLoadState.Loaded
                    && string.Equals(
                        mod.manifest?.id,
                        modId,
                        StringComparison.Ordinal))
                .Sum(mod => mod.assemblies.Count);
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Could not inspect associated Mod assemblies: {ex}");
            MarkRuntimeChange();
            return -1;
        }
    }

    private static FingerprintSnapshot CaptureSafely()
    {
        try
        {
            return Capture();
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Unexpected package fingerprint failure: {ex}");
            return Failure($"Package fingerprint failed ({ex.GetType().Name}).");
        }
    }

    private static FingerprintSnapshot Capture()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        List<string> lines = [];
        List<string> errors = [];
        List<PackageCapture> packages = [];
        List<MountedPckCapture> mountedPcks = [];
        List<LoadedModStamp> loadedModStamps = [];
        int order = 0;
        int fileCount = 0;
        long totalBytes = 0;
        int canonicalCharacters = 0;
        int entryCount = 0;

        if (ModManager.State != ModManagerState.Initialized)
        {
            errors.Add("The game Mod manager is not fully initialized.");
        }

        foreach (Mod failed in ModManager.Mods.Where(mod =>
                     mod.state == ModLoadState.Failed
                     || (mod.state == ModLoadState.AddedAtRuntime
                         && TouchesLoadedMod(mod))))
        {
            errors.Add(
                $"{SafeLabel(failed.manifest?.id)}: game reported Mod state {failed.state}.");
        }

        foreach (Mod mod in ModManager.Mods.Where(mod => mod.state == ModLoadState.Loaded))
        {
            string id = mod.manifest?.id ?? "<missing-id>";
            string safeId = SafeLabel(id);
            string version = mod.manifest?.version ?? "<missing-version>";
            bool affectsGameplay = mod.manifest?.affectsGameplay ?? true;

            try
            {
                if (id.Length > MaxMetadataCharacters
                    || version.Length > MaxMetadataCharacters)
                {
                    throw new FingerprintLimitException(
                        "Mod manifest metadata exceeds the fingerprint safety limit.");
                }

                AddCanonicalLine(
                    lines,
                    ref canonicalCharacters,
                    FingerprintCodec.Line(
                        "mod",
                        order,
                        id,
                        version,
                        affectsGameplay));

                if (mod.errors is { Count: > 0 })
                {
                    errors.Add(
                        $"{safeId}: game reported {mod.errors.Count} Mod load error(s).");
                }

                ValidateManifest(errors, mod, id, safeId);
                ValidateDeclaredPck(errors, mod, id, safeId);
                List<(Assembly Assembly, string RelativePath)> loadedAssemblies =
                    ValidateLoadedAssemblies(errors, mod, safeId);
                for (int assemblyIndex = 0;
                     assemblyIndex < loadedAssemblies.Count;
                     assemblyIndex++)
                {
                    (Assembly assembly, string relativePath) =
                        loadedAssemblies[assemblyIndex];
                    AddCanonicalLine(
                        lines,
                        ref canonicalCharacters,
                        FingerprintCodec.Line(
                            "assembly",
                            order,
                            assemblyIndex,
                            id,
                            relativePath,
                            assembly.FullName,
                            assembly.ManifestModule.ModuleVersionId.ToString("N")));
                }

                int separatorLength = lines.Count == 0 ? 0 : 1;
                PackageCapture package = PackageHasher.Capture(
                    mod.path,
                    order,
                    id,
                    version,
                    MaxTotalFiles - fileCount,
                    MaxTotalBytes - totalBytes,
                    MaxCanonicalCharacters - canonicalCharacters - separatorLength,
                    MaxTotalEntries - entryCount);

                if (package.CanonicalText.Length > 0)
                {
                    AddCanonicalLine(
                        lines,
                        ref canonicalCharacters,
                        package.CanonicalText);
                }

                StringComparer pathComparer = OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal;
                foreach ((_, string relativePath) in loadedAssemblies)
                {
                    if (!package.Files.Any(file =>
                            pathComparer.Equals(
                                file.RelativePath,
                                relativePath)))
                    {
                        errors.Add(
                            $"{safeId}: a loaded assembly is excluded from package hashing.");
                    }
                }

                packages.Add(package);
                loadedModStamps.Add(new LoadedModStamp(
                    id,
                    version,
                    Path.GetFullPath(mod.path),
                    affectsGameplay,
                    mod.assemblies.Count));
                fileCount += package.Files.Count;
                totalBytes += package.TotalBytes;
                entryCount += package.ScannedEntryCount;
            }
            catch (FingerprintLimitException ex)
            {
                Main.Log.Error(
                    $"Fingerprint safety limit reached at Mod '{safeId}': {ex}");
                errors.Add($"{safeId}: fingerprint safety limit was exceeded.");
                order++;
                break;
            }
            catch (Exception ex)
            {
                Main.Log.Error($"Could not fingerprint Mod package '{safeId}': {ex}");
                errors.Add($"{safeId}: package hashing failed ({ex.GetType().Name}).");
            }

            order++;
        }

        bool mountedPcksCaptured = false;
        IReadOnlyList<string> mountedPaths = [];
        try
        {
            mountedPaths = SkinManagerMounts.GetCurrentPaths();
            if (mountedPaths.Count > MaxTotalFiles - fileCount
                || mountedPaths.Count > MaxTotalEntries - entryCount)
            {
                throw new FingerprintLimitException(
                    "Mounted PCK files exceed the aggregate file or entry safety limit.");
            }

            for (int index = 0; index < mountedPaths.Count; index++)
            {
                MountedPckCapture mounted = PackageHasher.CaptureMountedPck(
                    mountedPaths[index],
                    MaxTotalBytes - totalBytes);
                AddCanonicalLine(
                    lines,
                    ref canonicalCharacters,
                    FingerprintCodec.Line(
                        "mounted-pck",
                        index,
                        mounted.Length,
                        mounted.Digest));
                mountedPcks.Add(mounted);
                fileCount++;
                entryCount++;
                totalBytes += mounted.Length;
            }

            mountedPcksCaptured = true;
        }
        catch (FingerprintLimitException ex)
        {
            Main.Log.Error(
                $"Mounted-PCK fingerprint safety limit was reached: {ex}");
            errors.Add(
                "Mounted PCK verification exceeded a fingerprint safety limit.");
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Could not fingerprint mounted PCK files: {ex}");
            errors.Add(
                $"Mounted PCK verification failed ({ex.GetType().Name}).");
        }

        if (mountedPcksCaptured)
        {
            try
            {
                if (!MountedPathsEqual(
                        mountedPaths,
                        SkinManagerMounts.GetCurrentPaths()))
                {
                    throw new IOException(
                        "The mounted-PCK set changed while it was being hashed.");
                }

                int remainingEntries = MaxTotalEntries - mountedPcks.Count;
                foreach (PackageCapture package in packages)
                {
                    if (!PackageHasher.IsCurrent(
                            package,
                            ref remainingEntries))
                    {
                        throw new IOException(
                            "A Mod package changed before the fingerprint completed.");
                    }
                }

                if (mountedPcks.Any(
                        mounted => !PackageHasher.IsCurrent(mounted)))
                {
                    throw new IOException(
                        "A mounted PCK changed before the fingerprint completed.");
                }
            }
            catch (Exception ex)
            {
                Main.Log.Error(
                    $"Final fingerprint consistency check failed: {ex}");
                errors.Add(
                    $"Final fingerprint consistency check failed ({ex.GetType().Name}).");
            }
        }

        string canonicalText = string.Join('\n', lines);
        FingerprintSnapshot snapshot = new(
            FingerprintCodec.Hash(canonicalText),
            errors,
            packages,
            mountedPcks,
            loadedModStamps,
            order,
            fileCount,
            totalBytes);

        stopwatch.Stop();
        Main.Log.Info(
            $"Captured {order} loaded Mods and {fileCount} files "
            + $"({totalBytes} bytes) in {stopwatch.ElapsedMilliseconds} ms; "
            + $"digest {snapshot.Digest}.");

        foreach (string error in errors)
        {
            Main.Log.Error("Fingerprint error: " + error);
        }

        return snapshot;
    }

    private static void ValidateManifest(
        List<string> errors,
        Mod mod,
        string id,
        string safeId)
    {
        bool found = false;
        foreach (string path in Directory.EnumerateFiles(
                     mod.path,
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            FileInfo file = new(path);
            if (file.Length > MaxManifestBytes)
            {
                continue;
            }

            try
            {
                using FileStream stream = File.OpenRead(path);
                using JsonDocument document = JsonDocument.Parse(stream);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty(
                        "id",
                        out JsonElement manifestId)
                    && manifestId.ValueKind == JsonValueKind.String
                    && string.Equals(manifestId.GetString(), id, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }
            catch (JsonException)
            {
                // Non-manifest JSON files are still hashed as package content.
            }
        }

        if (!found)
        {
            errors.Add($"{safeId}: matching manifest was not found in the package root.");
        }
    }

    private static void ValidateDeclaredPck(
        List<string> errors,
        Mod mod,
        string id,
        string safeId)
    {
        if (mod.manifest?.hasPck == true
            && (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || !string.Equals(Path.GetFileName(id), id, StringComparison.Ordinal)
                || !File.Exists(Path.Combine(mod.path, id + ".pck"))))
        {
            errors.Add($"{safeId}: declared PCK is missing.");
        }
    }

    private static List<(Assembly Assembly, string RelativePath)>
        ValidateLoadedAssemblies(
        List<string> errors,
        Mod mod,
        string safeId)
    {
        List<(Assembly Assembly, string RelativePath)> loadedAssemblies = [];
        if (mod.manifest?.hasDll == true && mod.assemblies.Count == 0)
        {
            errors.Add($"{safeId}: declared DLL has no loaded assembly.");
            return loadedAssemblies;
        }

        foreach (Assembly assembly in mod.assemblies)
        {
            string path = assembly.Location;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                errors.Add($"{safeId}: a loaded assembly has no hashable package file.");
                continue;
            }

            try
            {
                loadedAssemblies.Add((
                    assembly,
                    PackageHasher.PackageRelativePath(mod.path, path)));
            }
            catch (InvalidDataException)
            {
                errors.Add(
                    $"{safeId}: a loaded assembly is outside its package root.");
            }
        }

        return loadedAssemblies;
    }

    private static void StartTrackingRuntimeChanges()
    {
        if (_trackingRuntimeChanges || ModManager.State != ModManagerState.Initialized)
        {
            return;
        }

        ModManager.OnModDetected += OnModDetected;
        _trackingRuntimeChanges = true;
    }

    private static void OnModDetected(Mod detected)
    {
        try
        {
            if (TouchesLoadedMod(detected))
            {
                MarkRuntimeChange();
                return;
            }

            Main.Log.Info(
                $"Ignoring newly detected, unloaded Mod '{SafeLabel(detected.manifest?.id)}' "
                + "until the next game restart.");
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Could not classify a runtime Mod detection: {ex}");
            MarkRuntimeChange();
        }
    }

    private static FingerprintSnapshot Failure(string error) =>
        new(
            string.Empty,
            [error],
            [],
            [],
            [],
            0,
            0,
            0);

    private static void AddCanonicalLine(
        List<string> lines,
        ref int characterCount,
        string line)
    {
        int separatorLength = lines.Count == 0 ? 0 : 1;
        if (line.Length > MaxCanonicalCharacters - characterCount - separatorLength)
        {
            throw new FingerprintLimitException(
                $"Fingerprint exceeds the {MaxCanonicalCharacters} character safety limit.");
        }

        lines.Add(line);
        characterCount += separatorLength + line.Length;
    }

    private static bool TouchesLoadedMod(Mod detected)
    {
        string? detectedId = detected.manifest?.id;
        foreach (Mod loaded in ModManager.Mods.Where(
                     mod => mod.state == ModLoadState.Loaded
                         && !ReferenceEquals(mod, detected)))
        {
            if ((!string.IsNullOrEmpty(detectedId)
                    && string.Equals(
                        loaded.manifest?.id,
                        detectedId,
                        StringComparison.Ordinal))
                || PathsEqual(loaded.path, detected.path))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LoadedModsAreCurrent(
        IReadOnlyList<LoadedModStamp> expected)
    {
        if (ModManager.State != ModManagerState.Initialized)
        {
            return false;
        }

        List<Mod> current = ModManager.Mods
            .Where(mod => mod.state == ModLoadState.Loaded)
            .ToList();
        if (current.Count != expected.Count)
        {
            return false;
        }

        for (int index = 0; index < current.Count; index++)
        {
            Mod mod = current[index];
            LoadedModStamp stamp = expected[index];
            if (!string.Equals(
                    mod.manifest?.id ?? "<missing-id>",
                    stamp.Id,
                    StringComparison.Ordinal)
                || !string.Equals(
                    mod.manifest?.version ?? "<missing-version>",
                    stamp.Version,
                    StringComparison.Ordinal)
                || (mod.manifest?.affectsGameplay ?? true) != stamp.AffectsGameplay
                || mod.assemblies.Count != stamp.AssemblyCount
                || !PathsEqual(mod.path, stamp.Root))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MountedPcksAreCurrent(
        IReadOnlyList<MountedPckCapture> expected,
        ref int remainingEntries)
    {
        if (expected.Count > remainingEntries)
        {
            throw new FingerprintLimitException(
                "The aggregate Mod package entry limit was reached.");
        }

        remainingEntries -= expected.Count;
        IReadOnlyList<string> current = SkinManagerMounts.GetCurrentPaths();
        if (current.Count != expected.Count)
        {
            return false;
        }

        for (int index = 0; index < current.Count; index++)
        {
            if (!PathsEqual(current[index], expected[index].Path)
                || !PackageHasher.IsCurrent(expected[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MountedPathsEqual(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (!PathsEqual(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                comparison);
        }
        catch (Exception ex) when (
            ex is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    private static string SafeLabel(string? value)
    {
        string label = string.IsNullOrWhiteSpace(value) ? "<missing-id>" : value;
        return new string(label
                .Where(character => !char.IsControl(character))
                .Take(96)
                .ToArray())
            .Replace("[", "(", StringComparison.Ordinal)
            .Replace("]", ")", StringComparison.Ordinal);
    }
}
