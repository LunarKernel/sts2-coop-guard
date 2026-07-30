using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BetterCoop;

internal sealed record ToolkitPreferences(
    int Schema,
    bool SoundEnabled,
    float SoundVolume,
    bool ReadyCues,
    bool NetworkCues,
    bool RejoinCues,
    bool ResultCues,
    float UiScale,
    bool ReducedMotion,
    bool HighContrast)
{
    public static ToolkitPreferences Default { get; } = new(
        1,
        true,
        0.65f,
        true,
        true,
        true,
        true,
        1f,
        false,
        false);
}

internal static class ToolkitPreferenceStore
{
    public const int MaxBytes = 4096;
    private static readonly object Sync = new();
    private static string? _path;

    public static ToolkitPreferences ConfigureAndLoad(string dataRoot)
    {
        string path = Path.GetFullPath(
            Path.Combine(dataRoot, "preferences.json"));
        lock (Sync)
        {
            _path = path;
        }

        try
        {
            FileInfo file = new(path);
            if (!file.Exists)
            {
                return ToolkitPreferences.Default;
            }

            if (file.Length > MaxBytes
                || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Preferences file is unsafe.");
            }

            ToolkitPreferences? preferences =
                JsonSerializer.Deserialize<ToolkitPreferences>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions { MaxDepth = 4 });
            return IsValid(preferences)
                ? preferences!
                : ToolkitPreferences.Default;
        }
        catch
        {
            return ToolkitPreferences.Default;
        }
    }

    public static bool TrySave(
        ToolkitPreferences preferences,
        out string status)
    {
        string? path;
        lock (Sync)
        {
            path = _path;
        }

        if (path == null || !IsValid(preferences))
        {
            status = "Preferences unavailable or invalid.";
            return false;
        }

        try
        {
            string root = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Preferences directory cannot be a reparse point.");
            }

            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(preferences);
            if (bytes.Length > MaxBytes)
            {
                throw new InvalidDataException("Preferences exceeded 4 KiB.");
            }

            string temporary = path + ".tmp";
            using (FileStream stream = new(
                       temporary,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            status = "Preferences saved.";
            return true;
        }
        catch (Exception ex)
        {
            status = "Preferences save failed: " + ex.GetType().Name;
            return false;
        }
    }

    private static bool IsValid(ToolkitPreferences? preferences) =>
        preferences is
        {
            Schema: 1,
            SoundVolume: >= 0f and <= 1f,
            UiScale: >= 1f and <= 2f
        }
        && float.IsFinite(preferences.SoundVolume)
        && float.IsFinite(preferences.UiScale);
}

internal sealed record SaveEnvironmentStamp(
    string GameBuild,
    int GuardProtocol,
    string EnvironmentDigest,
    string ModDigest,
    string PckDigest,
    string ManifestDigest,
    string AssemblyDigest,
    string ContentDigest,
    string OtherDigest,
    string SettingsDigest);

internal sealed record SaveEnvironmentSidecar(
    int Schema,
    long SaveLength,
    string SaveDigest,
    SaveEnvironmentStamp Environment,
    DateTimeOffset CreatedUtc);

internal enum SaveSealState
{
    Missing,
    MatchingLocalRecord,
    SaveBindingMismatch,
    EnvironmentMismatch,
    Invalid
}

internal sealed record SaveSealReview(
    SaveSealState State,
    IReadOnlyList<string> Differences,
    string Evidence);

internal static class SaveEnvironmentSidecars
{
    public const int Schema = 1;
    public const int MaxSidecarBytes = 64 * 1024;
    public const long MaxSaveBytes = 64L * 1024 * 1024;
    private static readonly object Sync = new();
    private static string? _root;

    public static void Configure(string dataRoot)
    {
        string root = Path.GetFullPath(
            Path.Combine(dataRoot, "save-environments"));
        lock (Sync)
        {
            _root = root;
        }

        if (!Directory.Exists(root)
            || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            return;
        }

        foreach (string temporary in Directory.EnumerateFiles(
                     root,
                     "*.sidecar.tmp",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
                // A stale optional sidecar never affects a native save.
            }
        }
    }

    public static bool TrySeal(
        string savePath,
        SaveEnvironmentStamp environment,
        out string status)
    {
        if (!ValidEnvironment(environment))
        {
            status = "Environment stamp is invalid.";
            return false;
        }

        if (!TryHashSave(
                savePath,
                out long saveLength,
                out string saveDigest,
                out status))
        {
            return false;
        }

        string? root;
        lock (Sync)
        {
            root = _root;
        }

        if (root == null)
        {
            status = "Sidecar storage is not configured.";
            return false;
        }

        try
        {
            Directory.CreateDirectory(root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Sidecar directory cannot be a reparse point.");
            }

            string final = Path.Combine(root, SidecarName(savePath));
            string temporary = final + ".tmp";
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                new SaveEnvironmentSidecar(
                    Schema,
                    saveLength,
                    saveDigest,
                    environment,
                    DateTimeOffset.UtcNow));
            if (bytes.Length > MaxSidecarBytes)
            {
                throw new InvalidDataException("Sidecar exceeds 64 KiB.");
            }

            using (FileStream stream = new(
                       temporary,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, final, overwrite: true);
            status = "Multiplayer save sealed to a local environment record.";
            return true;
        }
        catch (Exception ex)
        {
            status = "Native save succeeded, but environment sealing failed: "
                + ex.GetType().Name;
            return false;
        }
    }

    public static SaveSealReview Review(
        string savePath,
        SaveEnvironmentStamp current)
    {
        string? root;
        lock (Sync)
        {
            root = _root;
        }

        if (root == null)
        {
            return Invalid("Sidecar storage is not configured.");
        }

        string path;
        try
        {
            path = Path.Combine(root, SidecarName(savePath));
            FileInfo file = new(path);
            if (!file.Exists)
            {
                return new(
                    SaveSealState.Missing,
                    [],
                    "No local seal exists; compatibility cannot be proven.");
            }

            if (file.Length > MaxSidecarBytes
                || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return Invalid("The local sidecar is oversized or unsafe.");
            }

            SaveEnvironmentSidecar? sidecar =
                JsonSerializer.Deserialize<SaveEnvironmentSidecar>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions { MaxDepth = 8 });
            if (!ValidSidecar(sidecar))
            {
                return Invalid(
                    "The local sidecar schema or fields are invalid.");
            }

            if (!TryHashSave(
                    savePath,
                    out long saveLength,
                    out string saveDigest,
                    out string error))
            {
                return Invalid(error);
            }

            if (saveLength != sidecar!.SaveLength
                || saveDigest != sidecar.SaveDigest)
            {
                return new(
                    SaveSealState.SaveBindingMismatch,
                    ["save-binding"],
                    "The sidecar does not bind to the current save bytes.");
            }

            IReadOnlyList<string> differences = Compare(
                sidecar.Environment,
                current);
            return differences.Count == 0
                ? new(
                    SaveSealState.MatchingLocalRecord,
                    [],
                    "The save bytes and current environment match this machine's prior record; this is not proof of compatibility.")
                : new(
                    SaveSealState.EnvironmentMismatch,
                    differences,
                    "The current environment differs from this machine's sealed record.");
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException
                or ArgumentException)
        {
            return Invalid(
                "The local sidecar could not be verified: "
                + ex.GetType().Name);
        }
    }

    private static IReadOnlyList<string> Compare(
        SaveEnvironmentStamp left,
        SaveEnvironmentStamp right)
    {
        List<string> differences = [];
        Add("game-build", left.GameBuild, right.GameBuild);
        if (left.GuardProtocol != right.GuardProtocol)
        {
            differences.Add("guard-protocol");
        }

        Add("environment", left.EnvironmentDigest, right.EnvironmentDigest);
        Add("mod-packages", left.ModDigest, right.ModDigest);
        Add("pck-content", left.PckDigest, right.PckDigest);
        Add("manifest-dependencies", left.ManifestDigest, right.ManifestDigest);
        Add("assemblies", left.AssemblyDigest, right.AssemblyDigest);
        Add("content", left.ContentDigest, right.ContentDigest);
        Add("other-package-files", left.OtherDigest, right.OtherDigest);
        Add("declared-settings", left.SettingsDigest, right.SettingsDigest);
        return differences;

        void Add(string name, string first, string second)
        {
            if (first != second)
            {
                differences.Add(name);
            }
        }
    }

    private static bool TryHashSave(
        string savePath,
        out long length,
        out string digest,
        out string status)
    {
        length = 0;
        digest = string.Empty;
        try
        {
            string fullPath = Path.GetFullPath(savePath);
            FileInfo before = new(fullPath);
            if (!before.Exists
                || before.Length > MaxSaveBytes
                || (before.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                status = "Native save is missing, oversized or unsafe.";
                return false;
            }

            using FileStream stream = new(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            digest = Convert.ToHexString(SHA256.HashData(stream))
                .ToLowerInvariant();
            FileInfo after = new(fullPath);
            after.Refresh();
            if (!after.Exists
                || after.Length != before.Length
                || after.LastWriteTimeUtc.Ticks
                    != before.LastWriteTimeUtc.Ticks)
            {
                status = "Native save changed while its sidecar was created.";
                digest = string.Empty;
                return false;
            }

            length = before.Length;
            status = string.Empty;
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            status = "Native save could not be read for sealing: "
                + ex.GetType().Name;
            return false;
        }
    }

    private static string SidecarName(string savePath)
    {
        string stableKey = Path.GetFullPath(savePath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Normalize(NormalizationForm.FormC);
        string digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(stableKey)))
            .ToLowerInvariant();
        return digest + ".sidecar";
    }

    private static bool ValidSidecar(SaveEnvironmentSidecar? sidecar) =>
        sidecar is
        {
            Schema: Schema,
            SaveLength: >= 0 and <= MaxSaveBytes
        }
        && IsDigest(sidecar.SaveDigest)
        && ValidEnvironment(sidecar.Environment)
        && sidecar.CreatedUtc != default;

    private static bool ValidEnvironment(SaveEnvironmentStamp? stamp) =>
        stamp != null
        && !string.IsNullOrEmpty(stamp.GameBuild)
        && stamp.GameBuild.Length <= 64
        && !stamp.GameBuild.Any(char.IsControl)
        && stamp.GuardProtocol is >= 1 and <= 1024
        && IsDigest(stamp.EnvironmentDigest)
        && IsDigestOrEmpty(stamp.ModDigest)
        && IsDigestOrEmpty(stamp.PckDigest)
        && IsDigestOrEmpty(stamp.ManifestDigest)
        && IsDigestOrEmpty(stamp.AssemblyDigest)
        && IsDigestOrEmpty(stamp.ContentDigest)
        && IsDigestOrEmpty(stamp.OtherDigest)
        && IsDigestOrEmpty(stamp.SettingsDigest);

    private static bool IsDigestOrEmpty(string value) =>
        string.IsNullOrEmpty(value) || IsDigest(value);

    private static bool IsDigest(string value) =>
        value is { Length: 64 }
        && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f');

    private static SaveSealReview Invalid(string evidence) =>
        new(SaveSealState.Invalid, [], evidence);
}

public enum CrashReviewConfidence
{
    Insufficient,
    High
}

public sealed record CrashReview(
    CrashReviewConfidence Confidence,
    string Signature,
    string LastStage);

public static class ToolkitReportHistory
{
    public const int MaxReports = 20;
    public const int MaxReportBytes = 1024 * 1024;
    public const long MaxTotalBytes = 25L * 1024 * 1024;
    private static readonly object Sync = new();
    private static string? _root;
    private static string? _latest;
    private static long _sequence;

    public static bool HasLatest
    {
        get
        {
            lock (Sync)
            {
                return !string.IsNullOrEmpty(_latest);
            }
        }
    }

    public static void Configure(string root)
    {
        string fullRoot = Path.GetFullPath(root);
        lock (Sync)
        {
            _root = fullRoot;
        }

        if (!Directory.Exists(fullRoot)
            || (File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
        {
            return;
        }

        foreach (string temporary in Directory.EnumerateFiles(
                     fullRoot,
                     "report-*.tmp",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
                // A stale temp file never blocks startup or later manual saves.
            }
        }
    }

    public static void Remember(string report)
    {
        string redacted = IncidentExplainer.RedactReport(
            report,
            MaxReportBytes);
        lock (Sync)
        {
            _latest = TruncateUtf8(redacted, MaxReportBytes);
        }
    }

    public static bool TrySaveLatest(out string status)
    {
        string? root;
        string? report;
        long sequence;
        lock (Sync)
        {
            root = _root;
            report = _latest;
            sequence = ++_sequence;
        }

        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(report))
        {
            status = "No redacted report is available.";
            return false;
        }

        try
        {
            Directory.CreateDirectory(root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "The BetterCoop report directory cannot be a reparse point.");
            }

            string stamp = DateTimeOffset.UtcNow.ToString(
                "yyyyMMdd-HHmmss-fffffff",
                CultureInfo.InvariantCulture);
            string finalPath = Path.Combine(
                root,
                $"report-{stamp}-{sequence}.txt");
            string temporaryPath = Path.Combine(
                root,
                $"report-{stamp}-{sequence}.tmp");
            byte[] bytes = new UTF8Encoding(false).GetBytes(report);
            using (FileStream stream = new(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 16 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, finalPath);
            EnforceRetention(root);
            status = Path.GetFileName(finalPath);
            return true;
        }
        catch (Exception ex)
        {
            status = "Manual report save failed: " + ex.GetType().Name;
            return false;
        }
    }

    public static int ReportCount()
    {
        string? root;
        lock (Sync)
        {
            root = _root;
        }

        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return 0;
        }

        return Directory.EnumerateFiles(
                root,
                "report-*.txt",
                SearchOption.TopDirectoryOnly)
            .Count();
    }

    public static IReadOnlyList<string> ReportNames()
    {
        string? root;
        lock (Sync)
        {
            root = _root;
        }

        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateFiles(
                root,
                "report-*.txt",
                SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(file =>
                (file.Attributes & FileAttributes.ReparsePoint) == 0
                && file.Length <= MaxReportBytes)
            .OrderByDescending(file => file.Name, StringComparer.Ordinal)
            .Select(file => file.Name)
            .Take(MaxReports)
            .ToArray();
    }

    public static bool TryRead(
        string fileName,
        out string report,
        out string status)
    {
        report = string.Empty;
        if (!TryResolveReport(fileName, out string path, out status))
        {
            return false;
        }

        try
        {
            FileInfo file = new(path);
            if (!file.Exists
                || file.Length > MaxReportBytes
                || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                status = "Report is missing, oversized or unsafe.";
                return false;
            }

            report = IncidentExplainer.RedactReport(
                File.ReadAllText(path),
                MaxReportBytes);
            status = file.Name;
            return true;
        }
        catch (Exception ex)
        {
            status = "Report read failed: " + ex.GetType().Name;
            return false;
        }
    }

    public static bool TryDelete(string fileName, out string status)
    {
        if (!TryResolveReport(fileName, out string path, out status))
        {
            return false;
        }

        try
        {
            File.Delete(path);
            status = fileName;
            return true;
        }
        catch (Exception ex)
        {
            status = "Report delete failed: " + ex.GetType().Name;
            return false;
        }
    }

    public static int Clear()
    {
        int deleted = 0;
        foreach (string fileName in ReportNames())
        {
            if (TryDelete(fileName, out _))
            {
                deleted++;
            }
        }

        return deleted;
    }

    private static void EnforceRetention(string root)
    {
        DateTime cutoff = DateTime.UtcNow.AddDays(-30);
        List<FileInfo> files = Directory.EnumerateFiles(
                root,
                "report-*.txt",
                SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(file =>
                (file.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderBy(file => file.CreationTimeUtc)
            .ThenBy(file => file.Name, StringComparer.Ordinal)
            .ToList();

        foreach (FileInfo expired in files
                     .Where(file => file.LastWriteTimeUtc < cutoff)
                     .ToArray())
        {
            expired.Delete();
            files.Remove(expired);
        }

        long total = files.Sum(file => file.Length);
        while (files.Count > MaxReports || total > MaxTotalBytes)
        {
            FileInfo oldest = files[0];
            total -= oldest.Length;
            oldest.Delete();
            files.RemoveAt(0);
        }
    }

    private static string TruncateUtf8(string value, int maxBytes)
    {
        StringBuilder result = new(Math.Min(value.Length, maxBytes));
        int used = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            int bytes = rune.Utf8SequenceLength;
            if (bytes > maxBytes - used)
            {
                break;
            }

            result.Append(rune);
            used += bytes;
        }

        return result.ToString();
    }

    private static bool TryResolveReport(
        string fileName,
        out string path,
        out string status)
    {
        path = string.Empty;
        string? root;
        lock (Sync)
        {
            root = _root;
        }

        if (string.IsNullOrEmpty(root)
            || string.IsNullOrEmpty(fileName)
            || fileName != Path.GetFileName(fileName)
            || !fileName.StartsWith("report-", StringComparison.Ordinal)
            || !fileName.EndsWith(".txt", StringComparison.Ordinal))
        {
            status = "Invalid BetterCoop report name.";
            return false;
        }

        path = Path.Combine(root, fileName);
        status = fileName;
        return true;
    }
}

public static class ToolkitCrashMarker
{
    public const int MaxMarkerBytes = 1024;
    public const int MaxLogTailBytes = 1024 * 1024;
    private static readonly object Sync = new();
    private static string? _markerPath;

    public static CrashReview? Start(string root, string logPath)
    {
        string fullRoot = Path.GetFullPath(root);
        Directory.CreateDirectory(fullRoot);
        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "The BetterCoop data directory cannot be a reparse point.");
        }

        string marker = Path.Combine(fullRoot, "session.marker");
        string? previous = ReadBounded(marker, MaxMarkerBytes);
        string? logTail = ReadTail(logPath, MaxLogTailBytes);
        lock (Sync)
        {
            _markerPath = marker;
        }

        WriteMarker("startup");
        if (previous == null)
        {
            return null;
        }

        string lastStage = previous
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line =>
                line.StartsWith("stage=", StringComparison.Ordinal))
            ?["stage=".Length..]
            ?? "unknown";
        string signature = NativeCrashSignature(logTail);
        return new CrashReview(
            signature == "none"
                ? CrashReviewConfidence.Insufficient
                : CrashReviewConfidence.High,
            signature,
            FlightRecorder.BoundedText(lastStage, 128));
    }

    public static void UpdateStage(string stage)
    {
        try
        {
            WriteMarker(FlightRecorder.BoundedText(stage, 128));
        }
        catch
        {
            // Marker updates are optional and never affect the game.
        }
    }

    public static void Finish()
    {
        string? marker;
        lock (Sync)
        {
            marker = _markerPath;
            _markerPath = null;
        }

        if (marker == null)
        {
            return;
        }

        try
        {
            File.Delete(marker);
            File.Delete(marker + ".tmp");
        }
        catch
        {
            // A stale marker is reported as insufficient evidence next launch.
        }
    }

    private static void WriteMarker(string stage)
    {
        string? marker;
        lock (Sync)
        {
            marker = _markerPath;
        }

        if (marker == null)
        {
            return;
        }

        string text = "schema=1\nstage="
            + stage
            + "\nstarted="
            + DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            + "\n";
        byte[] bytes = new UTF8Encoding(false).GetBytes(text);
        if (bytes.Length > MaxMarkerBytes)
        {
            throw new InvalidDataException("Crash marker exceeded its bound.");
        }

        string temporary = marker + ".tmp";
        using (FileStream stream = new(
                   temporary,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 1024,
                   FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, marker, overwrite: true);
    }

    private static string? ReadBounded(string path, int maxBytes)
    {
        try
        {
            FileInfo file = new(path);
            if (!file.Exists
                || file.Length > maxBytes
                || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return file.Exists ? "stage=invalid-marker" : null;
            }

            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch
        {
            return File.Exists(path) ? "stage=unreadable-marker" : null;
        }
    }

    private static string? ReadTail(string path, int maxBytes)
    {
        try
        {
            FileInfo file = new(path);
            if (!file.Exists
                || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            int length = (int)Math.Min(file.Length, maxBytes);
            byte[] bytes = new byte[length];
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Position = Math.Max(0, stream.Length - length);
            stream.ReadExactly(bytes);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static string NativeCrashSignature(string? logTail)
    {
        if (string.IsNullOrEmpty(logTail))
        {
            return "none";
        }

        (string Needle, string Label)[] signatures =
        [
            ("0xC0000005", "native-access-violation"),
            ("SIGSEGV", "native-segmentation-fault"),
            ("AccessViolationException", "access-violation-exception"),
            ("Fatal error.", "native-fatal-error"),
            ("Unhandled exception", "unhandled-exception")
        ];
        return signatures.FirstOrDefault(signature =>
                logTail.Contains(
                    signature.Needle,
                    StringComparison.OrdinalIgnoreCase))
            .Label
            ?? "none";
    }
}
