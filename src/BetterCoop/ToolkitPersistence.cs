using System.Globalization;
using System.IO.Compression;
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
    bool HighContrast,
    bool RollbackEnabled)
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
      false,
      false);
}

internal static class ToolkitPreferenceStore
{
  public const int MaxBytes = 4096;
  private static readonly object Sync = new();
  private static readonly JsonSerializerOptions ReadOptions = new()
  {
    MaxDepth = 4
  };
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
              ReadOptions);
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
  private static readonly JsonSerializerOptions ReadOptions = new()
  {
    MaxDepth = 8
  };
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
              ReadOptions);
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

internal sealed record RollbackCheckpointBinding(
    string RunId,
    string BranchId,
    string ParentBranchId,
    ulong ForkVisitIndex,
    uint RollbackEpoch,
    int Act,
    int Floor,
    int Row,
    int Column,
    string NodeLabel,
    string GameBuild,
    int GuardProtocol,
    string EnvironmentDigest,
    string RosterDigest,
    string SeedTag,
    string RngDigest);

internal sealed record RollbackCheckpointMetadata(
    int Schema,
    string CheckpointId,
    string RunId,
    string BranchId,
    string ParentBranchId,
    ulong ForkVisitIndex,
    uint RollbackEpoch,
    ulong VisitIndex,
    int Act,
    int Floor,
    int Row,
    int Column,
    string NodeLabel,
    string GameBuild,
    int GuardProtocol,
    string EnvironmentDigest,
    string RosterDigest,
    string SeedTag,
    string RngDigest,
    long SaveLength,
    string SaveDigest,
    long CompressedLength,
    string CompressedDigest,
    DateTimeOffset CreatedUtc);

internal sealed record RollbackTransactionMarker(
    int Schema,
    string TransactionId,
    string CheckpointId,
    string State,
    string NativeSavePath,
    string EmergencyBackupPath,
    long EmergencyLength,
    string EmergencyDigest,
    DateTimeOffset UpdatedUtc);

internal static class RollbackCheckpointIdentity
{
  public static byte[] Create(
      string runId,
      int act,
      int floor,
      int row,
      int column)
  {
    string value = string.Join(
        '\n',
        runId,
        act.ToString(CultureInfo.InvariantCulture),
        floor.ToString(CultureInfo.InvariantCulture),
        row.ToString(CultureInfo.InvariantCulture),
        column.ToString(CultureInfo.InvariantCulture));
    return SHA256.HashData(Encoding.UTF8.GetBytes(value));
  }

  public static byte[] Create(
      RollbackCheckpointMetadata checkpoint) =>
      Create(
          checkpoint.RunId,
          checkpoint.Act,
          checkpoint.Floor,
          checkpoint.Row,
          checkpoint.Column);
}

internal static class RollbackCheckpointJournal
{
  public const int Schema = 1;
  public const int MaxCheckpoints = 128;
  public const long MaxSaveBytes = 32L * 1024 * 1024;
  public const long MaxTotalBytes = 256L * 1024 * 1024;
  public const int MaxMetadataBytes = 64 * 1024;
  private static readonly object Sync = new();
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    MaxDepth = 8
  };
  private static readonly Dictionary<string, (
      long Length,
      long LastWriteTicks,
      string Digest)> PayloadDigestCache =
      new(StringComparer.OrdinalIgnoreCase);
  private static string? _root;
  private static string _recoveryStatus = "No recovery is pending.";

  public static string RecoveryStatus
  {
    get
    {
      lock (Sync)
      {
        return _recoveryStatus;
      }
    }
  }

  public static bool TryGetRecoveryTransaction(
      out Guid transactionId)
  {
    transactionId = Guid.Empty;
    if (!TryGetRoot(out string root, out _))
    {
      return false;
    }

    lock (Sync)
    {
      try
      {
        RollbackTransactionMarker? marker = ReadMarker(root);
        return marker is { State: "Activating" or "Activated" }
            && Guid.TryParseExact(
                marker.TransactionId,
                "N",
                out transactionId);
      }
      catch (Exception ex) when (IsJournalException(ex))
      {
        _recoveryStatus = "Rollback transaction metadata is unreadable.";
        return false;
      }
    }
  }

  public static void Configure(string dataRoot)
  {
    string root = Path.GetFullPath(
        Path.Combine(dataRoot, "rollback-journal"));
    lock (Sync)
    {
      _root = root;
      _recoveryStatus = "No recovery is pending.";
      PayloadDigestCache.Clear();
    }

    if (!Directory.Exists(root)
        || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
    {
      return;
    }

    foreach (string temporary in EnumerateOwnedFiles(
                 root,
                 "*.tmp"))
    {
      try
      {
        File.Delete(temporary);
      }
      catch
      {
        // Stale incomplete files are never considered checkpoints.
      }
    }

    RollbackTransactionMarker? marker = ReadMarker(root);
    if (marker is { State: "Activating" or "Activated" })
    {
      lock (Sync)
      {
        _recoveryStatus =
            "Recovery required for rollback transaction "
            + marker.TransactionId
            + "; use the preserved emergency backup.";
      }
    }
  }

  public static bool TryArchive(
      string nativeSavePath,
      RollbackCheckpointBinding binding,
      out RollbackCheckpointMetadata? checkpoint,
      out string status)
  {
    checkpoint = null;
    status = string.Empty;
    if (!ValidBinding(binding)
        || !TryGetRoot(out string root, out status))
    {
      return false;
    }

    lock (Sync)
    {
      try
      {
        EnsureSafeRoot(root);
        RollbackCheckpointMetadata[] existing =
            ReadAllLocked(root);
        if (existing.Length >= MaxCheckpoints
            || DirectoryBytes(root) >= MaxTotalBytes)
        {
          status =
              "Rollback journal is full; no checkpoint was overwritten.";
          return false;
        }

        if (!TryHashFile(
                nativeSavePath,
                MaxSaveBytes,
                out long saveLength,
                out string saveDigest,
                out status))
        {
          return false;
        }

        RollbackCheckpointMetadata? duplicate = existing
            .Where(item =>
                item.RunId == binding.RunId
                && item.BranchId == binding.BranchId)
            .OrderByDescending(item => item.VisitIndex)
            .FirstOrDefault();
        if (duplicate != null
            && duplicate.Act == binding.Act
            && duplicate.Floor == binding.Floor
            && duplicate.Row == binding.Row
            && duplicate.Column == binding.Column
            && duplicate.SaveDigest == saveDigest)
        {
          checkpoint = duplicate;
          status = "Rollback checkpoint already archived for this node.";
          return true;
        }

        ulong visitIndex = existing
            .Where(item => item.RunId == binding.RunId)
            .Select(item => item.VisitIndex)
            .DefaultIfEmpty()
            .Max() + 1;
        string checkpointId = Guid.NewGuid().ToString("N");
        string runRoot = Path.Combine(root, binding.RunId);
        Directory.CreateDirectory(runRoot);
        if ((File.GetAttributes(runRoot)
             & FileAttributes.ReparsePoint) != 0)
        {
          throw new InvalidDataException(
              "Rollback run directory cannot be a reparse point.");
        }

        string compressedPath =
            Path.Combine(runRoot, checkpointId + ".save.br");
        string compressedTemporary = compressedPath + ".tmp";
        using (FileStream input = new(
                   nativeSavePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   64 * 1024,
                   FileOptions.SequentialScan))
        using (FileStream output = new(
                   compressedTemporary,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   64 * 1024,
                   FileOptions.WriteThrough))
        {
          using (BrotliStream compressor = new(
                     output,
                     CompressionLevel.SmallestSize,
                     leaveOpen: true))
          {
            input.CopyTo(compressor);
          }

          output.Flush(flushToDisk: true);
        }

        FileInfo compressed = new(compressedTemporary);
        if (compressed.Length > MaxSaveBytes)
        {
          throw new InvalidDataException(
              "Compressed checkpoint exceeds 32 MiB.");
        }

        string compressedDigest = HashFile(compressedTemporary);
        checkpoint = new(
            Schema,
            checkpointId,
            binding.RunId,
            binding.BranchId,
            binding.ParentBranchId,
            binding.ForkVisitIndex,
            binding.RollbackEpoch,
            visitIndex,
            binding.Act,
            binding.Floor,
            binding.Row,
            binding.Column,
            binding.NodeLabel,
            binding.GameBuild,
            binding.GuardProtocol,
            binding.EnvironmentDigest,
            binding.RosterDigest,
            binding.SeedTag,
            binding.RngDigest,
            saveLength,
            saveDigest,
            compressed.Length,
            compressedDigest,
            DateTimeOffset.UtcNow);
        string metadataPath =
            Path.Combine(runRoot, checkpointId + ".json");
        File.Move(compressedTemporary, compressedPath);
        WriteJsonAtomic(metadataPath, checkpoint);
        if (DirectoryBytes(root) > MaxTotalBytes)
        {
          File.Delete(compressedPath);
          File.Delete(metadataPath);
          checkpoint = null;
          status =
              "Rollback journal would exceed 256 MiB; checkpoint was not retained.";
          return false;
        }

        status = "Archived rollback checkpoint "
            + checkpointId
            + ".";
        return true;
      }
      catch (Exception ex) when (
          ex is IOException
              or UnauthorizedAccessException
              or InvalidDataException
              or JsonException
              or NotSupportedException
              or ArgumentException)
      {
        checkpoint = null;
        status = "Rollback checkpoint archive failed: "
            + ex.GetType().Name;
        return false;
      }
    }
  }

  public static IReadOnlyList<RollbackCheckpointMetadata>
      List(string? runId = null)
  {
    if (!TryGetRoot(out string root, out _))
    {
      return [];
    }

    lock (Sync)
    {
      try
      {
        return ReadAllLocked(root)
            .Where(item => runId == null || item.RunId == runId)
            .OrderByDescending(item => item.VisitIndex)
            .ToArray();
      }
      catch
      {
        return [];
      }
    }
  }

  public static bool TryFindByNativeSave(
      string nativeSavePath,
      out RollbackCheckpointMetadata? checkpoint)
  {
    checkpoint = null;
    if (!TryHashFile(
            nativeSavePath,
            MaxSaveBytes,
            out long length,
            out string digest,
            out _))
    {
      return false;
    }

    checkpoint = List()
        .Where(item =>
            item.SaveLength == length
            && item.SaveDigest == digest)
        .OrderByDescending(item => item.CreatedUtc)
        .FirstOrDefault();
    return checkpoint != null;
  }

  public static bool TryResolveActiveBranch(
      string nativeSavePath,
      out string runId,
      out string branchId,
      out string parentBranchId,
      out ulong forkVisitIndex)
  {
    runId = string.Empty;
    branchId = string.Empty;
    parentBranchId = string.Empty;
    forkVisitIndex = 0;
    if (!TryFindByNativeSave(
            nativeSavePath,
            out RollbackCheckpointMetadata? matching)
        || matching == null)
    {
      return false;
    }

    runId = matching.RunId;
    branchId = matching.BranchId;
    parentBranchId = matching.ParentBranchId;
    forkVisitIndex = matching.ForkVisitIndex;
    if (!TryGetRoot(out string root, out _))
    {
      return true;
    }

    lock (Sync)
    {
      RollbackTransactionMarker? marker = ReadMarker(root);
      if (marker is not
        {
          State: "Activated" or "Committed"
        }
          || !string.Equals(
              Path.GetFullPath(nativeSavePath),
              marker.NativeSavePath,
              StringComparison.OrdinalIgnoreCase)
          || marker.CheckpointId != matching.CheckpointId)
      {
        return true;
      }

      branchId = marker.TransactionId;
      parentBranchId = matching.BranchId;
      forkVisitIndex = matching.VisitIndex;
      return true;
    }
  }

  public static bool TryActivate(
      string checkpointId,
      Guid transactionId,
      string nativeSavePath,
      out RollbackCheckpointMetadata? checkpoint,
      out string status)
  {
    checkpoint = null;
    if (!Guid.TryParseExact(checkpointId, "N", out _)
        || transactionId == Guid.Empty
        || !TryGetRoot(out string root, out status))
    {
      status = "Rollback activation arguments are invalid.";
      return false;
    }

    lock (Sync)
    {
      try
      {
        EnsureSafeRoot(root);
        RollbackTransactionMarker? current = ReadMarker(root);
        if (current is { State: "Activating" or "Activated" })
        {
          status =
              "A prior rollback requires recovery before another activation.";
          return false;
        }

        checkpoint = ReadAllLocked(root).SingleOrDefault(
            item => item.CheckpointId == checkpointId);
        if (checkpoint == null)
        {
          status = "Selected rollback checkpoint is unavailable.";
          return false;
        }

        string runRoot = Path.Combine(root, checkpoint.RunId);
        string compressedPath =
            Path.Combine(runRoot, checkpointId + ".save.br");
        if (!TryHashFile(
                compressedPath,
                MaxSaveBytes,
                out long compressedLength,
                out string compressedDigest,
                out status)
            || compressedLength != checkpoint.CompressedLength
            || compressedDigest != checkpoint.CompressedDigest)
        {
          status =
              "Selected rollback checkpoint payload failed verification.";
          return false;
        }

        if (!TryHashFile(
                nativeSavePath,
                MaxSaveBytes,
                out long emergencyLength,
                out string emergencyDigest,
                out status))
        {
          return false;
        }

        string transaction = transactionId.ToString("N");
        string native = Path.GetFullPath(nativeSavePath);
        string nativeDirectory =
            Path.GetDirectoryName(native)!;
        string temporary = Path.Combine(
            nativeDirectory,
            Path.GetFileName(native)
                + ".bettercoop."
                + transaction
                + ".tmp");
        string emergency = Path.Combine(
            nativeDirectory,
            Path.GetFileName(native)
                + ".bettercoop."
                + transaction
                + ".emergency");
        RollbackTransactionMarker marker = new(
            Schema,
            transaction,
            checkpointId,
            "Activating",
            native,
            emergency,
            emergencyLength,
            emergencyDigest,
            DateTimeOffset.UtcNow);
        WriteMarker(root, marker);

        using (FileStream input = new(
                   compressedPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   64 * 1024,
                   FileOptions.SequentialScan))
        using (BrotliStream decompressor = new(
                   input,
                   CompressionMode.Decompress))
        using (FileStream output = new(
                   temporary,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   64 * 1024,
                   FileOptions.WriteThrough))
        {
          decompressor.CopyTo(output);
          if (output.Length > MaxSaveBytes)
          {
            throw new InvalidDataException(
                "Decompressed checkpoint exceeds 32 MiB.");
          }

          output.Flush(flushToDisk: true);
        }

        if (!TryHashFile(
                temporary,
                MaxSaveBytes,
                out long restoredLength,
                out string restoredDigest,
                out status)
            || restoredLength != checkpoint.SaveLength
            || restoredDigest != checkpoint.SaveDigest)
        {
          throw new InvalidDataException(
              "Decompressed checkpoint digest differs.");
        }

        File.Replace(
            temporary,
            native,
            emergency,
            ignoreMetadataErrors: true);
        if (!TryHashFile(
                native,
                MaxSaveBytes,
                out long activeLength,
                out string activeDigest,
                out status)
            || activeLength != checkpoint.SaveLength
            || activeDigest != checkpoint.SaveDigest)
        {
          throw new InvalidDataException(
              "Activated native save failed verification.");
        }

        WriteMarker(
            root,
            marker with
            {
              State = "Activated",
              UpdatedUtc = DateTimeOffset.UtcNow
            });
        _recoveryStatus =
            "Rollback activated; emergency backup retained until commit.";
        status = "Rollback checkpoint activated atomically.";
        return true;
      }
      catch (Exception ex) when (
          ex is IOException
              or UnauthorizedAccessException
              or InvalidDataException
              or JsonException
              or NotSupportedException
              or ArgumentException)
      {
        status = "Rollback activation failed: "
            + ex.GetType().Name
            + ". Emergency recovery may be required.";
        _recoveryStatus = status;
        return false;
      }
    }
  }

  public static bool TryCommit(Guid transactionId, out string status)
  {
    status = string.Empty;
    if (transactionId == Guid.Empty
        || !TryGetRoot(out string root, out status))
    {
      return false;
    }

    lock (Sync)
    {
      try
      {
        RollbackTransactionMarker? marker = ReadMarker(root);
        if (marker == null
            || marker.TransactionId
                != transactionId.ToString("N")
            || marker.State != "Activated")
        {
          status = "No matching activated rollback transaction exists.";
          return false;
        }

        WriteMarker(
            root,
            marker with
            {
              State = "Committed",
              UpdatedUtc = DateTimeOffset.UtcNow
            });
        _recoveryStatus =
            "Rollback committed; emergency backup retained.";
        status = _recoveryStatus;
        return true;
      }
      catch (Exception ex) when (IsJournalException(ex))
      {
        status = "Rollback commit failed: "
            + ex.GetType().Name
            + ". Emergency recovery remains available.";
        _recoveryStatus = status;
        return false;
      }
    }
  }

  public static bool TryRecover(
      Guid transactionId,
      out string status)
  {
    status = string.Empty;
    if (transactionId == Guid.Empty
        || !TryGetRoot(out string root, out status))
    {
      return false;
    }

    lock (Sync)
    {
      try
      {
        RollbackTransactionMarker? marker = ReadMarker(root);
        if (marker == null
            || marker.TransactionId
                != transactionId.ToString("N")
            || marker.State is not ("Activating" or "Activated")
            || !ValidMarkerPaths(marker))
        {
          status = "No matching emergency backup is available.";
          return false;
        }

        if (!File.Exists(marker.EmergencyBackupPath))
        {
          if (marker.State == "Activating"
              && TryHashFile(
                  marker.NativeSavePath,
                  MaxSaveBytes,
                  out long nativeLength,
                  out string nativeDigest,
                  out _)
              && nativeLength == marker.EmergencyLength
              && nativeDigest == marker.EmergencyDigest)
          {
            WriteMarker(
                root,
                marker with
                {
                  State = "Recovered",
                  UpdatedUtc = DateTimeOffset.UtcNow
                });
            _recoveryStatus =
                "Activation had not replaced the native save; original retained.";
            status = _recoveryStatus;
            return true;
          }

          status =
              "Emergency backup is missing and active-save identity is uncertain.";
          return false;
        }

        if (!TryHashFile(
                marker.EmergencyBackupPath,
                MaxSaveBytes,
                out long emergencyLength,
                out string emergencyDigest,
                out _)
            || emergencyLength != marker.EmergencyLength
            || emergencyDigest != marker.EmergencyDigest)
        {
          status = "Emergency backup failed digest verification.";
          return false;
        }

        string temporary = marker.NativeSavePath
            + ".bettercoop."
            + marker.TransactionId
            + ".recover.tmp";
        if (File.Exists(temporary))
        {
          File.Delete(temporary);
        }

        File.Copy(
            marker.EmergencyBackupPath,
            temporary,
            overwrite: false);
        File.Replace(
            temporary,
            marker.NativeSavePath,
            destinationBackupFileName: null,
            ignoreMetadataErrors: true);
        if (!TryHashFile(
                marker.NativeSavePath,
                MaxSaveBytes,
                out long restoredLength,
                out string restoredDigest,
                out _)
            || restoredLength != marker.EmergencyLength
            || restoredDigest != marker.EmergencyDigest)
        {
          throw new InvalidDataException(
              "Recovered native save failed verification.");
        }

        WriteMarker(
            root,
            marker with
            {
              State = "Recovered",
              UpdatedUtc = DateTimeOffset.UtcNow
            });
        _recoveryStatus = "Emergency native save restored.";
        status = _recoveryStatus;
        return true;
      }
      catch (Exception ex) when (
          ex is IOException
              or UnauthorizedAccessException
              or InvalidDataException
              or JsonException
              or NotSupportedException
              or ArgumentException)
      {
        status = "Emergency recovery failed: "
            + ex.GetType().Name;
        _recoveryStatus = status;
        return false;
      }
    }
  }

  private static RollbackCheckpointMetadata[] ReadAllLocked(
      string root)
  {
    if (!Directory.Exists(root))
    {
      return [];
    }

    List<RollbackCheckpointMetadata> result = [];
    foreach (string path in EnumerateOwnedFiles(
                 root,
                 "*.json"))
    {
      if (Path.GetFileName(path) == "transaction.json")
      {
        continue;
      }

      try
      {
        FileInfo file = new(path);
        if (file.Length > MaxMetadataBytes
            || (file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
          continue;
        }

        RollbackCheckpointMetadata? item =
            JsonSerializer.Deserialize<RollbackCheckpointMetadata>(
                File.ReadAllBytes(path),
                JsonOptions);
        string payloadPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            Path.GetFileNameWithoutExtension(path) + ".save.br");
        if (ValidMetadata(item)
            && string.Equals(
                Path.GetFileNameWithoutExtension(path),
                item!.CheckpointId,
                StringComparison.Ordinal)
            && TryVerifyCheckpointPayload(
                payloadPath,
                item.CompressedLength,
                item.CompressedDigest))
        {
          result.Add(item);
        }
      }
      catch (Exception ex) when (IsJournalException(ex))
      {
        // A single damaged checkpoint must not hide healthy history.
      }
    }

    return result
        .OrderByDescending(item => item.CreatedUtc)
        .Take(MaxCheckpoints + 1)
        .ToArray();
  }

  private static bool IsJournalException(Exception ex) =>
      ex is IOException
          or UnauthorizedAccessException
          or InvalidDataException
          or JsonException
          or NotSupportedException
          or ArgumentException;

  private static bool ValidBinding(
      RollbackCheckpointBinding? value) =>
      value != null
      && IsHex(value.RunId, 64)
      && Guid.TryParseExact(value.BranchId, "N", out _)
      && value.ParentBranchId != null
      && (value.ParentBranchId.Length == 0
          ? value.ForkVisitIndex == 0
          : Guid.TryParseExact(
              value.ParentBranchId,
              "N",
              out _)
            && value.ParentBranchId != value.BranchId
            && value.ForkVisitIndex > 0)
      && value.RollbackEpoch > 0
      && value.Act is >= 1 and <= 16
      && value.Floor is >= 0 and <= 10_000
      && value.NodeLabel is { Length: >= 1 and <= 128 }
      && !value.NodeLabel.Any(char.IsControl)
      && value.GameBuild is { Length: >= 1 and <= 64 }
      && value.GuardProtocol is >= 1 and <= 1024
      && IsHex(value.EnvironmentDigest, 64)
      && IsHex(value.RosterDigest, 32)
      && IsHex(value.SeedTag, 64)
      && IsHex(value.RngDigest, 64);

  private static bool ValidMetadata(
      RollbackCheckpointMetadata? value) =>
      value is
      {
        Schema: Schema,
        RollbackEpoch: > 0,
        VisitIndex: > 0,
        SaveLength: >= 0 and <= MaxSaveBytes,
        CompressedLength: >= 0 and <= MaxSaveBytes
      }
      && Guid.TryParseExact(value.CheckpointId, "N", out _)
      && ValidBinding(new(
          value.RunId,
          value.BranchId,
          value.ParentBranchId,
          value.ForkVisitIndex,
          value.RollbackEpoch,
          value.Act,
          value.Floor,
          value.Row,
          value.Column,
          value.NodeLabel,
          value.GameBuild,
          value.GuardProtocol,
          value.EnvironmentDigest,
          value.RosterDigest,
          value.SeedTag,
          value.RngDigest))
      && IsHex(value.SaveDigest, 64)
      && IsHex(value.CompressedDigest, 64)
      && value.CreatedUtc != default;

  private static bool TryGetRoot(
      out string root,
      out string status)
  {
    lock (Sync)
    {
      root = _root ?? string.Empty;
    }

    status = root.Length == 0
        ? "Rollback journal is not configured."
        : string.Empty;
    return root.Length != 0;
  }

  private static void EnsureSafeRoot(string root)
  {
    Directory.CreateDirectory(root);
    if ((File.GetAttributes(root)
         & FileAttributes.ReparsePoint) != 0)
    {
      throw new InvalidDataException(
          "Rollback journal directory cannot be a reparse point.");
    }
  }

  private static long DirectoryBytes(string root) =>
      EnumerateOwnedFiles(root, "*")
          .Select(path => new FileInfo(path))
          .Where(file =>
              (file.Attributes
               & FileAttributes.ReparsePoint) == 0)
          .Sum(file => file.Length);

  private static bool TryHashFile(
      string path,
      long maxBytes,
      out long length,
      out string digest,
      out string status)
  {
    length = 0;
    digest = string.Empty;
    try
    {
      FileInfo file = new(Path.GetFullPath(path));
      if (!file.Exists
          || file.Length > maxBytes
          || (file.Attributes & FileAttributes.ReparsePoint) != 0)
      {
        status = "File is missing, oversized or unsafe.";
        return false;
      }

      length = file.Length;
      digest = HashFile(file.FullName);
      status = string.Empty;
      return true;
    }
    catch (Exception ex) when (
        ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
    {
      status = "File verification failed: "
          + ex.GetType().Name;
      return false;
    }
  }

  private static string HashFile(string path)
  {
    using FileStream stream = new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        64 * 1024,
        FileOptions.SequentialScan);
    return Convert.ToHexString(SHA256.HashData(stream))
        .ToLowerInvariant();
  }

  private static bool TryVerifyCheckpointPayload(
      string path,
      long expectedLength,
      string expectedDigest)
  {
    try
    {
      FileInfo file = new(path);
      if (!file.Exists
          || file.Length != expectedLength
          || file.Length > MaxSaveBytes
          || (file.Attributes & FileAttributes.ReparsePoint) != 0)
      {
        return false;
      }

      if (!PayloadDigestCache.TryGetValue(
              file.FullName,
              out (long Length, long LastWriteTicks, string Digest)
                  cached)
          || cached.Length != file.Length
          || cached.LastWriteTicks != file.LastWriteTimeUtc.Ticks)
      {
        cached = (
            file.Length,
            file.LastWriteTimeUtc.Ticks,
            HashFile(file.FullName));
        PayloadDigestCache[file.FullName] = cached;
      }

      return cached.Digest == expectedDigest;
    }
    catch (Exception ex) when (
        ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
    {
      return false;
    }
  }

  private static void WriteJsonAtomic<T>(
      string path,
      T value)
  {
    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
        value,
        JsonOptions);
    if (bytes.Length > MaxMetadataBytes)
    {
      throw new InvalidDataException(
          "Rollback metadata exceeds 64 KiB.");
    }

    string temporary = path + ".tmp";
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

    File.Move(temporary, path, overwrite: true);
  }

  private static void WriteMarker(
      string root,
      RollbackTransactionMarker marker) =>
      WriteJsonAtomic(
          Path.Combine(root, "transaction.json"),
          marker);

  private static RollbackTransactionMarker? ReadMarker(
      string root)
  {
    string path = Path.Combine(root, "transaction.json");
    FileInfo file = new(path);
    if (!file.Exists
        || file.Length > MaxMetadataBytes
        || (file.Attributes & FileAttributes.ReparsePoint) != 0)
    {
      return null;
    }

    RollbackTransactionMarker? marker =
        JsonSerializer.Deserialize<RollbackTransactionMarker>(
            File.ReadAllBytes(path),
            JsonOptions);
    return marker is
    {
      Schema: Schema,
      EmergencyLength: >= 0 and <= MaxSaveBytes,
      State: "Activating" or "Activated" or "Committed" or "Recovered"
    }
        && Guid.TryParseExact(
            marker.TransactionId,
            "N",
            out _)
        && Guid.TryParseExact(
            marker.CheckpointId,
            "N",
            out _)
        && IsHex(marker.EmergencyDigest, 64)
        && ValidMarkerPaths(marker)
        ? marker
        : null;
  }

  private static IEnumerable<string> EnumerateOwnedFiles(
      string root,
      string pattern)
  {
    foreach (string file in Directory.EnumerateFiles(
                 root,
                 pattern,
                 SearchOption.TopDirectoryOnly))
    {
      yield return file;
    }

    foreach (string directory in Directory.EnumerateDirectories(
                 root,
                 "*",
                 SearchOption.TopDirectoryOnly))
    {
      DirectoryInfo info = new(directory);
      if ((info.Attributes & FileAttributes.ReparsePoint) != 0
          || !IsHex(info.Name, 64))
      {
        continue;
      }

      foreach (string file in Directory.EnumerateFiles(
                   info.FullName,
                   pattern,
                   SearchOption.TopDirectoryOnly))
      {
        yield return file;
      }
    }
  }

  private static bool ValidMarkerPaths(
      RollbackTransactionMarker marker)
  {
    try
    {
      string native = Path.GetFullPath(marker.NativeSavePath);
      string emergency =
          Path.GetFullPath(marker.EmergencyBackupPath);
      string? directory = Path.GetDirectoryName(native);
      return directory != null
          && string.Equals(
              Path.GetDirectoryName(emergency),
              directory,
              StringComparison.OrdinalIgnoreCase)
          && string.Equals(
              emergency,
              native
                  + ".bettercoop."
                  + marker.TransactionId
                  + ".emergency",
              StringComparison.OrdinalIgnoreCase);
    }
    catch (Exception ex) when (
        ex is NotSupportedException or ArgumentException)
    {
      return false;
    }
  }

  private static bool IsHex(string value, int length) =>
      value is not null
      && value.Length == length
      && value.All(character =>
          character is >= '0' and <= '9'
              or >= 'a' and <= 'f');
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
