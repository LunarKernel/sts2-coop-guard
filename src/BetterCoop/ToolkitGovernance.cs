using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace BetterCoop;

public static class BetterCoopApi
{
  [MethodImpl(MethodImplOptions.NoInlining)]
  public static bool PublishDeterministicSettings(
      string providerId,
      int schemaVersion,
      byte[] digest)
  {
    try
    {
      if (digest == null)
      {
        return false;
      }

      bool accepted = DeterminismRegistry.Publish(
          Assembly.GetCallingAssembly(),
          providerId,
          schemaVersion,
          digest,
          out string status);
      if (!accepted)
      {
        Main.Log.Warn(
            "Rejected deterministic-settings digest: " + status);
      }

      return accepted;
    }
    catch
    {
      return false;
    }
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  public static bool PublishSettingsDigest(
      string providerId,
      int schemaVersion,
      byte[] digest)
  {
    try
    {
      return digest != null
          && DeterminismRegistry.Publish(
              Assembly.GetCallingAssembly(),
              providerId,
              schemaVersion,
              digest,
              out _);
    }
    catch
    {
      return false;
    }
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  public static bool PublishStateDigest(
      string modId,
      int schemaVersion,
      ulong sourceRevision,
      byte[] digest)
  {
    try
    {
      return digest != null
          && OptionalDiagnosticsRegistry.PublishState(
              Assembly.GetCallingAssembly(),
              modId,
              schemaVersion,
              sourceRevision,
              digest);
    }
    catch
    {
      return false;
    }
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  public static bool RecordPublicDiagnosticEvent(
      string modId,
      ushort eventCode)
  {
    try
    {
      return OptionalDiagnosticsRegistry.RecordEvent(
          Assembly.GetCallingAssembly(),
          modId,
          eventCode);
    }
    catch
    {
      return false;
    }
  }
}

internal sealed record DeterministicSettingStamp(
    string ProviderId,
    int SchemaVersion,
    string Digest);

internal static class SettingsDeclarationCatalog
{
  public const string FileName = "bettercoop.settings.json";

  public static IReadOnlyDictionary<string, SettingsProviderDeclaration>
      Load(IEnumerable<Mod> source)
  {
    Mod[] mods = source.Take(LocalModDoctor.MaxMods + 1).ToArray();
    if (mods.Length > LocalModDoctor.MaxMods)
    {
      throw new InvalidDataException(
          "Settings declaration scan exceeded 256 Mods.");
    }

    Dictionary<string, SettingsProviderDeclaration> declarations =
        new(StringComparer.Ordinal);
    int totalBytes = 0;
    foreach (Mod mod in mods)
    {
      string? ownerId = mod.manifest?.id;
      if (string.IsNullOrWhiteSpace(ownerId))
      {
        continue;
      }

      SettingsDeclarationRead read = Read(mod);
      if (!string.IsNullOrEmpty(read.Error))
      {
        throw new InvalidDataException(
            $"Settings declaration for '{Safe(ownerId)}' is invalid: "
            + read.Error);
      }

      foreach (SettingsProviderDeclaration declaration in read.Providers)
      {
        totalBytes = checked(totalBytes + declaration.MetadataBytes);
        if (totalBytes
            > SettingsDeclarationCodec.MaxTotalMetadataBytes)
        {
          throw new InvalidDataException(
              "Settings declarations exceed 64 KiB.");
        }

        if (!declarations.TryAdd(
                declaration.ProviderId,
                declaration))
        {
          throw new InvalidDataException(
              "A settings provider ID is declared by more than one Mod.");
        }
      }
    }

    return declarations;
  }

  public static SettingsDeclarationRead Read(Mod mod)
  {
    string? ownerId = mod.manifest?.id;
    if (string.IsNullOrWhiteSpace(ownerId))
    {
      return new(false, [], "Owner Mod ID is unavailable.");
    }

    try
    {
      string root = Path.GetFullPath(mod.path);
      DirectoryInfo directory = new(root);
      if (!directory.Exists
          || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
      {
        return new(false, [], "Mod package root is missing or unsafe.");
      }

      string path = Path.GetFullPath(Path.Combine(root, FileName));
      if (!string.Equals(
              Path.GetDirectoryName(path),
              root.TrimEnd(
                  Path.DirectorySeparatorChar,
                  Path.AltDirectorySeparatorChar),
              StringComparison.OrdinalIgnoreCase))
      {
        return new(false, [], "Declaration path escaped the Mod root.");
      }

      FileInfo file = new(path);
      if (!file.Exists)
      {
        return new(false, [], string.Empty);
      }

      if ((file.Attributes & FileAttributes.ReparsePoint) != 0
          || file.LinkTarget != null
          || file.Length > SettingsDeclarationCodec.MaxFileBytes)
      {
        return new(true, [], "Declaration file is oversized or unsafe.");
      }

      byte[] bytes = new byte[checked((int)file.Length)];
      using (FileStream stream = new(
                 path,
                 FileMode.Open,
                 FileAccess.Read,
                 FileShare.Read,
                 bufferSize: 4096,
                 FileOptions.SequentialScan))
      {
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
        {
          return new(
              true,
              [],
              "Declaration changed while it was being read.");
        }
      }

      file.Refresh();
      if (!file.Exists
          || file.Length != bytes.Length
          || (file.Attributes & FileAttributes.ReparsePoint) != 0)
      {
        return new(
            true,
            [],
            "Declaration changed while it was being read.");
      }

      return SettingsDeclarationCodec.Parse(bytes, ownerId);
    }
    catch (Exception ex) when (
        ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or DecoderFallbackException
            or JsonException
            or OverflowException)
    {
      return new(
          true,
          [],
          "Declaration could not be read safely ("
          + ex.GetType().Name
          + ").");
    }
  }

  private static string Safe(string value) =>
      FlightRecorder.BoundedText(value, 128);
}

internal static class DeterminismRegistry
{
  public const int MaxProviders = 256;
  private static readonly object Sync = new();
  private static readonly Dictionary<string, DeterministicSettingStamp>
      Entries = new(StringComparer.Ordinal);
  private static readonly Dictionary<string, Assembly> Publishers =
      new(StringComparer.Ordinal);
  private static IReadOnlyDictionary<string, SettingsProviderDeclaration>?
      _frozenDeclarations;
  private static bool _frozen;

  public static bool Publish(
      Assembly caller,
      string providerId,
      int schemaVersion,
      ReadOnlySpan<byte> digest,
      out string status)
  {
    Mod? owner = ResolveOwner(caller);

    string? actualId = owner?.manifest?.id;
    if (actualId == null
        || !SettingsDeclarationCodec.OwnsProvider(
            actualId,
            providerId))
    {
      status = "Publisher identity does not match its loaded Mod.";
      return false;
    }

    SettingsDeclarationRead declaration = SettingsDeclarationCatalog.Read(
        owner!);
    SettingsProviderDeclaration? declared = declaration.Providers
        .FirstOrDefault(item => string.Equals(
            item.ProviderId,
            providerId,
            StringComparison.Ordinal));
    if (!string.IsNullOrEmpty(declaration.Error)
        || declared == null
        || declared.SchemaVersion != schemaVersion)
    {
      status = !string.IsNullOrEmpty(declaration.Error)
          ? declaration.Error
          : "Provider is missing from the package declaration or its schema differs.";
      return false;
    }

    bool accepted = PublishTrusted(
        providerId,
        schemaVersion,
        digest,
        out status);
    if (!accepted)
    {
      return false;
    }

    lock (Sync)
    {
      if (Publishers.TryGetValue(providerId, out Assembly? publisher)
          && publisher != caller)
      {
        status = "A different assembly already owns this provider.";
        return false;
      }

      Publishers[providerId] = caller;
      return true;
    }
  }

  internal static bool PublishTrusted(
      string providerId,
      int schemaVersion,
      ReadOnlySpan<byte> digest,
      out string status)
  {
    string safeId = FlightRecorder.BoundedText(providerId, 128);
    if (safeId != providerId
        || string.IsNullOrWhiteSpace(providerId)
        || providerId.Contains("..", StringComparison.Ordinal)
        || providerId.Contains('/')
        || providerId.Contains('\\')
        || providerId.Any(char.IsControl)
        || Path.IsPathRooted(providerId)
        || schemaVersion is < 1 or > 1024
        || digest.Length != 32)
    {
      status = "Invalid deterministic-settings declaration.";
      return false;
    }

    DeterministicSettingStamp stamp = new(
        providerId,
        schemaVersion,
        Convert.ToHexString(digest).ToLowerInvariant());
    lock (Sync)
    {
      if (Entries.TryGetValue(providerId, out var existing))
      {
        if (existing == stamp)
        {
          status = "Declaration already published.";
          return true;
        }

        status = _frozen
            ? "Declaration changed after fingerprint freeze; restart required."
            : "Conflicting declaration for the same provider.";
        if (_frozen)
        {
          ModFingerprint.MarkRuntimeChange();
        }

        return false;
      }

      if (_frozen || Entries.Count >= MaxProviders)
      {
        status = _frozen
            ? "Fingerprint is frozen; restart required."
            : "Deterministic-settings provider limit reached.";
        if (_frozen)
        {
          ModFingerprint.MarkRuntimeChange();
        }

        return false;
      }

      Entries.Add(providerId, stamp);
      status = "Declaration accepted.";
      return true;
    }
  }

  public static IReadOnlyList<DeterministicSettingStamp> Freeze()
      => Freeze(ReadDeclarations());

  internal static IReadOnlyDictionary<string, SettingsProviderDeclaration>
      ReadDeclarations()
  {
    lock (Sync)
    {
      if (_frozen)
      {
        return _frozenDeclarations!;
      }
    }

    return SettingsDeclarationCatalog.Load(ModManager.Mods);
  }

  internal static IReadOnlyList<DeterministicSettingStamp> Freeze(
      IReadOnlyDictionary<string, SettingsProviderDeclaration> declarations)
  {
    lock (Sync)
    {
      if (_frozen)
      {
        return Entries.Values
            .OrderBy(
                entry => entry.ProviderId,
                StringComparer.Ordinal)
            .ToArray();
      }

      if (declarations.Count != Entries.Count
          || Publishers.Count != Entries.Count)
      {
        throw new InvalidDataException(
            "A declared deterministic-settings digest is missing or an undeclared digest was published.");
      }

      foreach ((string providerId,
                   SettingsProviderDeclaration declared)
               in declarations)
      {
        if (!Entries.TryGetValue(
                providerId,
                out DeterministicSettingStamp? stamp)
            || stamp.SchemaVersion != declared.SchemaVersion)
        {
          throw new InvalidDataException(
              "A deterministic-settings declaration is missing, stale or uses a different schema.");
        }
      }

      foreach ((string providerId, Assembly publisher) in Publishers)
      {
        Mod? owner = ResolveOwner(publisher);

        if (owner?.manifest?.id is not string ownerId
            || !SettingsDeclarationCodec.OwnsProvider(
                ownerId,
                providerId)
            || !declarations.TryGetValue(
                providerId,
                out SettingsProviderDeclaration? declared)
            || !string.Equals(
                declared.OwnerModId,
                ownerId,
                StringComparison.Ordinal))
        {
          throw new InvalidDataException(
              "A deterministic-settings publisher could not be authenticated.");
        }
      }

      _frozenDeclarations =
          new Dictionary<string, SettingsProviderDeclaration>(
              declarations,
              StringComparer.Ordinal);
      _frozen = true;
      return Entries.Values
          .OrderBy(entry => entry.ProviderId, StringComparer.Ordinal)
          .ToArray();
    }
  }

  internal static void ResetForTests()
  {
    lock (Sync)
    {
      Entries.Clear();
      Publishers.Clear();
      _frozenDeclarations = null;
      _frozen = false;
    }
  }

  internal static IReadOnlyDictionary<string, DeterministicSettingStamp>
      Snapshot()
  {
    lock (Sync)
    {
      return new Dictionary<string, DeterministicSettingStamp>(
          Entries,
          StringComparer.Ordinal);
    }
  }

  internal static Mod? ResolveOwner(Assembly assembly)
  {
    try
    {
      if (MegaCrit.Sts2.Core.Modding.AssemblyInfo.ModMap?
              .TryGetValue(assembly, out Mod? mapped) == true)
      {
        return mapped;
      }

      string location = Path.GetFullPath(assembly.Location);
      StringComparison comparison = OperatingSystem.IsWindows()
          ? StringComparison.OrdinalIgnoreCase
          : StringComparison.Ordinal;
      Mod[] matches = ModManager.Mods
          .Take(LocalModDoctor.MaxMods + 1)
          .Where(mod =>
              !string.IsNullOrWhiteSpace(mod.manifest?.id)
              && string.Equals(
                  Path.GetFullPath(Path.Combine(
                      mod.path,
                      mod.manifest!.id + ".dll")),
                  location,
                  comparison))
          .Take(2)
          .ToArray();
      return matches.Length == 1 ? matches[0] : null;
    }
    catch
    {
      return null;
    }
  }

}

internal sealed record OptionalStateDigest(
    string ModId,
    ushort SchemaVersion,
    ulong SourceRevision,
    byte[] Digest);

internal static class OptionalDiagnosticsRegistry
{
  public const int MaxPublishers = 256;
  private static readonly object Sync = new();
  private static readonly Dictionary<string, OptionalStateDigest> State =
      new(StringComparer.Ordinal);
  private static readonly Dictionary<string, Queue<long>> Rates =
      new(StringComparer.Ordinal);
  private static bool _enabled;

  public static void SetEnabled(bool enabled)
  {
    lock (Sync)
    {
      _enabled = enabled;
      if (!enabled)
      {
        State.Clear();
        Rates.Clear();
      }
    }
  }

  public static bool PublishState(
      Assembly caller,
      string modId,
      int schemaVersion,
      ulong sourceRevision,
      ReadOnlySpan<byte> digest)
  {
    if (!ValidateCaller(caller, modId)
        || schemaVersion is < 1 or > 1024
        || sourceRevision == 0
        || digest.Length != 32)
    {
      return false;
    }

    lock (Sync)
    {
      if (!_enabled
          || !TakeRateLocked(modId)
          || (!State.ContainsKey(modId)
              && State.Count >= MaxPublishers))
      {
        return false;
      }

      if (State.TryGetValue(modId, out OptionalStateDigest? current)
          && sourceRevision <= current.SourceRevision)
      {
        return sourceRevision == current.SourceRevision
            && schemaVersion == current.SchemaVersion
            && digest.SequenceEqual(current.Digest);
      }

      State[modId] = new(
          modId,
          (ushort)schemaVersion,
          sourceRevision,
          digest.ToArray());
      return true;
    }
  }

  public static bool RecordEvent(
      Assembly caller,
      string modId,
      ushort eventCode)
  {
    if (!ValidateCaller(caller, modId)
        || eventCode is 0 or > 4095)
    {
      return false;
    }

    lock (Sync)
    {
      if (!_enabled || !TakeRateLocked(modId))
      {
        return false;
      }
    }

    ToolkitRuntime.RecordExternalDiagnostic(modId, eventCode);
    return true;
  }

  public static F1ModContribution[] SnapshotAtCheckpoint()
  {
    lock (Sync)
    {
      if (!_enabled)
      {
        return [];
      }

      return State.Values
          .OrderBy(item => item.ModId, StringComparer.Ordinal)
          .Select(item => new F1ModContribution(
              0xF,
              item.ModId,
              item.SchemaVersion,
              item.SourceRevision,
              item.Digest.ToArray()))
          .ToArray();
    }
  }

  private static bool ValidateCaller(Assembly caller, string modId)
  {
    if (string.IsNullOrWhiteSpace(modId)
        || modId.Length > 64
        || modId.Any(char.IsControl))
    {
      return false;
    }

    Mod? owner = DeterminismRegistry.ResolveOwner(caller);
    return string.Equals(
        owner?.manifest?.id,
        modId,
        StringComparison.Ordinal);
  }

  private static bool TakeRateLocked(string modId)
  {
    long now = Stopwatch.GetTimestamp();
    if (!Rates.TryGetValue(modId, out Queue<long>? queue))
    {
      queue = new Queue<long>(2);
      Rates[modId] = queue;
    }

    while (queue.Count > 0
        && now - queue.Peek() >= Stopwatch.Frequency)
    {
      queue.Dequeue();
    }

    if (queue.Count >= 2)
    {
      return false;
    }

    queue.Enqueue(now);
    return true;
  }
}

internal sealed record EnvironmentLockMod(
    string Id,
    string Version,
    string Digest,
    IReadOnlyDictionary<string, string> Categories);

internal sealed record EnvironmentLock(
    int Schema,
    string GameBuild,
    string GuardVersion,
    int GuardProtocol,
    string Status,
    string EnvironmentDigest,
    IReadOnlyList<EnvironmentLockMod> Mods,
    string PckDigest,
    IReadOnlyList<DeterministicSettingStamp> Settings);

internal static class EnvironmentLockCodec
{
  private static readonly JsonSerializerOptions ReadOptions = new()
  {
    MaxDepth = 16,
    PropertyNameCaseInsensitive = false
  };
  public const int Schema = 1;
  public const int MaxBytes = 512 * 1024;
  public const int MaxMods = 256;

  public static string Create(
      FingerprintSnapshot snapshot,
      string gameBuild,
      string guardVersion)
  {
    if (snapshot.Packages.Count > MaxMods)
    {
      throw new InvalidDataException("Environment has too many Mods.");
    }

    IReadOnlyList<DeterministicSettingStamp> settings =
        DeterminismRegistry.Freeze();
    EnvironmentLock data = new(
        Schema,
        FlightRecorder.BoundedText(gameBuild, 64),
        FlightRecorder.BoundedText(guardVersion, 32),
        FingerprintCodec.ProtocolVersion,
        snapshot.Errors.Count == 0 ? "healthy" : "failed",
        snapshot.Digest,
        snapshot.Packages.Select(package => new EnvironmentLockMod(
                FlightRecorder.BoundedText(package.ModId, 128),
                FlightRecorder.BoundedText(package.ModVersion, 64),
                package.Digest,
                CategoryDigests(package, settings)))
            .ToArray(),
        MountedDigest(snapshot.MountedPcks),
        settings);
    string json = JsonSerializer.Serialize(data);
    if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
    {
      throw new InvalidDataException("Environment lockfile is oversized.");
    }

    return json;
  }

  public static SaveEnvironmentStamp CreateStamp(
      FingerprintSnapshot snapshot,
      string gameBuild)
  {
    if (snapshot.Errors.Count > 0
        || snapshot.Packages.Count > MaxMods)
    {
      throw new InvalidDataException(
          "Only a healthy bounded environment can seal a save.");
    }

    IReadOnlyList<DeterministicSettingStamp> settings =
        DeterminismRegistry.Freeze();
    (PackageCapture Package, IReadOnlyDictionary<string, string> Categories)[]
        packages = snapshot.Packages
            .Select(package => (
                package,
                CategoryDigests(package, settings)))
            .ToArray();
    return new(
        FlightRecorder.BoundedText(gameBuild, 64),
        FingerprintCodec.ProtocolVersion,
        snapshot.Digest,
        Aggregate(packages.Select(item =>
            FingerprintCodec.Line(
                item.Package.ModId,
                item.Package.Digest))),
        MountedDigest(snapshot.MountedPcks),
        Category("manifest"),
        Category("assembly"),
        Category("content"),
        Category("other"),
        Aggregate(settings.Select(setting =>
            FingerprintCodec.Line(
                setting.ProviderId,
                setting.SchemaVersion,
                setting.Digest))));

    string Category(string category) =>
        Aggregate(packages.Select(item =>
            FingerprintCodec.Line(
                item.Package.ModId,
                item.Categories[category])));
  }

  public static bool TryParse(
      string json,
      out EnvironmentLock? environment,
      out string error)
  {
    environment = null;
    if (json == null || Encoding.UTF8.GetByteCount(json) > MaxBytes)
    {
      error = "Lockfile exceeds 512 KiB.";
      return false;
    }

    try
    {
      environment = JsonSerializer.Deserialize<EnvironmentLock>(
          json,
          ReadOptions);
      if (environment == null
          || environment.GameBuild == null
          || environment.GuardVersion == null
          || environment.Status == null
          || environment.EnvironmentDigest == null
          || environment.Mods == null
          || environment.PckDigest == null
          || environment.Settings == null
          || environment.Schema != Schema
          || environment.GameBuild.Length > 64
          || environment.GuardVersion.Length > 32
          || environment.Status is not ("healthy" or "failed")
          || environment.Mods.Count > MaxMods
          || environment.Settings.Count > DeterminismRegistry.MaxProviders
          || (environment.Status == "healthy"
              ? !IsDigest(environment.EnvironmentDigest)
              : !IsDigestOrEmpty(environment.EnvironmentDigest))
          || !IsDigestOrEmpty(environment.PckDigest)
          || environment.Mods.Any(mod =>
              mod == null
              || string.IsNullOrEmpty(mod.Id)
              || mod.Id.Length > 128
              || mod.Id.Any(char.IsControl)
              || mod.Version == null
              || mod.Version.Length > 64
              || mod.Version.Any(char.IsControl)
              || mod.Digest == null
              || !IsDigest(mod.Digest)
              || mod.Categories == null
              || mod.Categories.Count > 8
              || mod.Categories.Any(category =>
                  !AllowedCategories.Contains(category.Key)
                  || category.Value == null
                  || !IsDigest(category.Value)))
          || environment.Settings.Any(setting =>
              setting == null
              || string.IsNullOrEmpty(setting.ProviderId)
              || setting.ProviderId.Length > 128
              || setting.ProviderId.Any(char.IsControl)
              || setting.SchemaVersion is < 1 or > 1024
              || setting.Digest == null
              || !IsDigest(setting.Digest))
          || environment.Mods
              .Select(mod => mod.Id)
              .Distinct(StringComparer.Ordinal)
              .Count() != environment.Mods.Count
          || environment.Settings
              .Select(setting => setting.ProviderId)
              .Distinct(StringComparer.Ordinal)
              .Count() != environment.Settings.Count)
      {
        environment = null;
        error = "Lockfile schema or field bounds are invalid.";
        return false;
      }

      error = string.Empty;
      return true;
    }
    catch (Exception ex) when (
        ex is JsonException
            or NotSupportedException
            or ArgumentException)
    {
      error = "Lockfile JSON is invalid.";
      return false;
    }
  }

  public static IReadOnlyList<string> Compare(
      EnvironmentLock left,
      EnvironmentLock right)
  {
    List<string> differences = [];
    if (left.GameBuild != right.GameBuild)
    {
      differences.Add("game-build");
    }

    if (left.GuardProtocol != right.GuardProtocol)
    {
      differences.Add("guard-protocol");
    }

    if (left.EnvironmentDigest != right.EnvironmentDigest)
    {
      differences.Add("environment");
    }

    Dictionary<string, EnvironmentLockMod> rightMods =
        right.Mods.ToDictionary(mod => mod.Id, StringComparer.Ordinal);
    foreach (EnvironmentLockMod mod in left.Mods)
    {
      if (!rightMods.TryGetValue(mod.Id, out var other))
      {
        differences.Add("only-left:" + mod.Id);
      }
      else if (mod.Digest != other.Digest)
      {
        differences.Add("mod-content:" + mod.Id);
      }
    }

    HashSet<string> leftIds = left.Mods
        .Select(mod => mod.Id)
        .ToHashSet(StringComparer.Ordinal);
    differences.AddRange(right.Mods
        .Where(mod => !leftIds.Contains(mod.Id))
        .Select(mod => "only-right:" + mod.Id));
    return differences.Take(200).ToArray();
  }

  private static readonly HashSet<string> AllowedCategories =
  [
      "manifest",
        "assembly",
        "content",
        "other",
        "settings"
  ];

  private static IReadOnlyDictionary<string, string> CategoryDigests(
      PackageCapture package,
      IReadOnlyList<DeterministicSettingStamp> settings)
  {
    Dictionary<string, List<string>> categories =
        new(StringComparer.Ordinal)
        {
          ["manifest"] = [],
          ["assembly"] = [],
          ["content"] = [],
          ["other"] = []
        };
    foreach (string line in package.CanonicalText.Split('\n'))
    {
      string lower = line.ToLowerInvariant();
      string category = lower.Contains(".dll", StringComparison.Ordinal)
          ? "assembly"
          : lower.Contains(".pck", StringComparison.Ordinal)
              ? "content"
              : lower.Contains("manifest", StringComparison.Ordinal)
                  || lower.Contains(".json", StringComparison.Ordinal)
                  ? "manifest"
                  : "other";
      categories[category].Add(line);
    }

    Dictionary<string, string> digests = categories.ToDictionary(
        pair => pair.Key,
        pair => FingerprintCodec.Hash(string.Join('\n', pair.Value)),
        StringComparer.Ordinal);
    DeterministicSettingStamp? declared = settings.FirstOrDefault(
        setting => string.Equals(
            setting.ProviderId,
            package.ModId,
            StringComparison.Ordinal));
    if (declared != null)
    {
      digests["settings"] = declared.Digest;
    }

    return digests;
  }

  private static string MountedDigest(
      IReadOnlyList<MountedPckCapture> mounted) =>
      mounted.Count == 0
          ? string.Empty
          : FingerprintCodec.Hash(string.Join(
              '\n',
              mounted.Select(item =>
                  FingerprintCodec.Line(item.Length, item.Digest))));

  private static string Aggregate(IEnumerable<string> lines)
  {
    string text = string.Join('\n', lines);
    return text.Length == 0
        ? string.Empty
        : FingerprintCodec.Hash(text);
  }

  private static bool IsDigestOrEmpty(string value) =>
      string.IsNullOrEmpty(value) || IsDigest(value);

  private static bool IsDigest(string value) =>
      value.Length == 64
      && value.All(character =>
          character is >= '0' and <= '9'
              or >= 'a' and <= 'f');
}

internal static class EnvironmentLockStore
{
  private static string? _root;
  private static long _sequence;

  public static void Configure(string dataRoot)
  {
    _root = Path.GetFullPath(Path.Combine(dataRoot, "environments"));
  }

  public static bool TrySave(string json, out string status)
  {
    if (_root == null
        || Encoding.UTF8.GetByteCount(json) > EnvironmentLockCodec.MaxBytes)
    {
      status = "Environment lockfile is unavailable or oversized.";
      return false;
    }

    try
    {
      Directory.CreateDirectory(_root);
      if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
      {
        throw new InvalidDataException(
            "Environment directory cannot be a reparse point.");
      }

      string stamp = DateTimeOffset.UtcNow.ToString(
          "yyyyMMdd-HHmmss-fffffff",
          System.Globalization.CultureInfo.InvariantCulture);
      string name =
          $"environment-{stamp}-{Interlocked.Increment(ref _sequence)}.json";
      string final = Path.Combine(_root, name);
      string temporary = final + ".tmp";
      File.WriteAllText(
          temporary,
          json,
          new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
      File.Move(temporary, final);
      foreach (FileInfo old in Directory.EnumerateFiles(
                   _root,
                   "environment-*.json",
                   SearchOption.TopDirectoryOnly)
                   .Select(path => new FileInfo(path))
                   .OrderByDescending(file => file.Name)
                   .Skip(10))
      {
        old.Delete();
      }

      status = name;
      return true;
    }
    catch (Exception ex)
    {
      status = "Environment save failed: " + ex.GetType().Name;
      return false;
    }
  }
}

internal static class LocalModDoctor
{
  public const int MaxMods = 256;
  public const int MaxReportCharacters = 64 * 1024;

  public static string BuildReport()
  {
    try
    {
      Mod[] mods = ModManager.Mods.Take(MaxMods + 1).ToArray();
      List<string> issues = [];
      foreach (IGrouping<string?, Mod> duplicate in mods
                   .Where(mod => !string.IsNullOrWhiteSpace(
                       mod.manifest?.id))
                   .GroupBy(
                       mod => mod.manifest!.id,
                       StringComparer.Ordinal)
                   .Where(group => group.Count() > 1))
      {
        string sources = string.Join(
            ", ",
            duplicate.Select(mod => mod.modSource.ToString())
                .Distinct(StringComparer.Ordinal));
        issues.Add(
            $"[Confirmed] duplicate/override: {Safe(duplicate.Key, 100)}"
            + $" ({Safe(sources, 200)}). Keep exactly one intended source and restart.");
      }

      foreach (Mod mod in mods.Take(MaxMods))
      {
        string id = Safe(mod.manifest?.id ?? "<missing manifest>", 100);
        if (mod.manifest == null)
        {
          issues.Add(
              $"[Confirmed] {id}: manifest unavailable; reinstall or remove the broken package manually.");
        }

        if (mod.state is ModLoadState.Failed
            or ModLoadState.DisabledDuplicate
            or ModLoadState.AddedAtRuntime)
        {
          issues.Add(
              $"[Confirmed] {id}: native ModManager state={mod.state},"
              + $" source={Safe(mod.modSource.ToString(), 64)}. Resolve the reported load/duplicate issue and restart.");
        }

        if (mod.errors is not { Count: > 0 })
        {
          continue;
        }

        foreach (object? error in mod.errors.Take(8))
        {
          issues.Add(
              $"[Confirmed] {id}: "
              + Safe(error?.ToString() ?? "unknown ModManager error", 1024));
        }

        if (mod.errors.Count > 8)
        {
          issues.Add(
              $"[Confirmed] {id}: +{mod.errors.Count - 8} more native load errors.");
        }
      }

      DoctorModRecord[] dependencyRecords = CurrentDependencyRecords(
          mods.Take(MaxMods))
          .ToArray();
      issues.AddRange(ModDependencyDoctor.Analyze(dependencyRecords)
          .Select(finding =>
              $"[{finding.Confidence}] {Safe(finding.Evidence, 1024)} "
              + Safe(finding.Action, 512)));

      IReadOnlyDictionary<string, DeterministicSettingStamp> published =
          DeterminismRegistry.Snapshot();
      List<string> settingsStatus = [];
      foreach (Mod mod in mods.Take(MaxMods)
                   .Where(mod =>
                       !string.IsNullOrWhiteSpace(mod.manifest?.id)))
      {
        string id = Safe(mod.manifest!.id, 100);
        SettingsDeclarationRead declaration =
            SettingsDeclarationCatalog.Read(mod);
        if (!string.IsNullOrEmpty(declaration.Error))
        {
          string issue =
              $"[Confirmed] {id}: settings declaration invalid: "
              + Safe(declaration.Error, 512);
          settingsStatus.Add(issue);
          issues.Add(issue);
          continue;
        }

        if (!declaration.Present)
        {
          settingsStatus.Add(
              $"[Unclaimed] {id}: no settings declaration; deterministic-setting compatibility is unknown.");
          continue;
        }

        foreach (SettingsProviderDeclaration provider
                 in declaration.Providers)
        {
          string label = Safe(provider.ProviderId, 128);
          if (!published.TryGetValue(
                  provider.ProviderId,
                  out DeterministicSettingStamp? stamp))
          {
            string issue =
                $"[Blocking] {label}: declared schema "
                + provider.SchemaVersion
                + " but no digest was published.";
            settingsStatus.Add(issue);
            issues.Add(issue);
          }
          else if (stamp.SchemaVersion != provider.SchemaVersion)
          {
            string issue =
                $"[Blocking] {label}: declared schema "
                + provider.SchemaVersion
                + " differs from published schema "
                + stamp.SchemaVersion
                + ".";
            settingsStatus.Add(issue);
            issues.Add(issue);
          }
          else
          {
            settingsStatus.Add(
                $"[Valid] {label}: schema {stamp.SchemaVersion}, 32-byte digest published; values remain private.");
          }
        }
      }

      if (mods.Length > MaxMods)
      {
        issues.Add(
            "[Inconclusive] Mod count exceeds 256; the Doctor stopped at its safety bound.");
      }

      FingerprintSnapshot snapshot = ModFingerprint.ValidateQuick();
      issues.AddRange(snapshot.Errors.Take(20).Select(error =>
          "[Confirmed] package guard: " + Safe(error, 1024)));

      StringBuilder report = new();
      report.AppendLine("BetterCoop Local Mod Doctor");
      report.AppendLine(FormattableString.Invariant(
          $"Inspected: {Math.Min(mods.Length, MaxMods)} native Mod records"));
      report.AppendLine(
          "Scope: native ModManager state/errors, duplicate IDs, dependency graph, deterministic-settings declarations, source overlap and BetterCoop package freshness.");
      report.AppendLine(
          "Read-only: no Mod was enabled, disabled, moved, removed, downloaded or patched.");
      report.AppendLine(
          "G9 wire: disabled on STS2 0.109.1 because native PacketReader.ReadString allocates from a peer length before applying a fixed bound; protocol 5 remains strict, while local lockfile categories remain available.");
      report.AppendLine("Settings declarations:");
      foreach (string status in settingsStatus.Take(MaxMods * 2))
      {
        AppendBounded(report, status);
      }

      if (issues.Count == 0)
      {
        report.AppendLine(
            "[Inconclusive] No blocking issue was reported. Undeclared dependencies or settings cannot be proven safe.");
      }
      else
      {
        foreach (string issue in issues.Take(200))
        {
          AppendBounded(report, issue);
        }

        if (issues.Count > 200)
        {
          AppendBounded(
              report,
              $"+{issues.Count - 200} more issues omitted; overall status remains not passed.");
        }
      }

      return report.ToString();
    }
    catch (Exception ex)
    {
      return "BetterCoop Local Mod Doctor\n"
          + "[Inconclusive] Inspection unavailable: "
          + Safe(ex.GetType().Name, 100)
          + ". No files or Mod state were changed.";
    }
  }

  internal static IReadOnlyList<DoctorModRecord> CurrentDependencyRecords(
      IEnumerable<Mod>? source = null) =>
      (source ?? ModManager.Mods)
              .Take(MaxMods)
              .Where(mod => !string.IsNullOrWhiteSpace(mod.manifest?.id))
              .Select(mod => new DoctorModRecord(
                  mod.manifest!.id!,
                  mod.manifest.version ?? string.Empty,
                  mod.state == ModLoadState.Loaded,
                  (mod.manifest.dependencies ?? [])
                  .Take(ModDependencyDoctor.MaxDependenciesPerMod + 1)
                  .Select(dependency => new DoctorDependency(
                      dependency.id ?? string.Empty,
                      dependency.minVersion ?? string.Empty))
                  .ToArray()))
              .ToArray();

  private static void AppendBounded(StringBuilder report, string line)
  {
    int remaining = MaxReportCharacters - report.Length - 1;
    if (remaining <= 0)
    {
      return;
    }

    report.AppendLine(line.Length <= remaining ? line : line[..remaining]);
  }

  internal static string Safe(string? value, int maxCharacters) =>
      FlightRecorder.BoundedText(
          IncidentExplainer.Redact(value ?? string.Empty),
          maxCharacters);
}

internal static class HarmonyConflictReport
{
  public const int MaxPatchRecords = 5000;

  public static string Build()
  {
    try
    {
      StringBuilder report = new("BetterCoop Harmony conflict map\n");
      int count = 0;
      foreach (MethodBase target in Harmony.GetAllPatchedMethods()
                   .OrderBy(MethodName, StringComparer.Ordinal))
      {
        Patches? patches = Harmony.GetPatchInfo(target);
        if (patches == null)
        {
          continue;
        }

        (string Kind, Patch Patch)[] entries =
            patches.Prefixes.Select(patch => ("prefix", patch))
                .Concat(patches.Postfixes.Select(
                    patch => ("postfix", patch)))
                .Concat(patches.Transpilers.Select(
                    patch => ("transpiler", patch)))
                .Concat(patches.Finalizers.Select(
                    patch => ("finalizer", patch)))
                .Take(MaxPatchRecords - count)
                .ToArray();
        if (entries.Length == 0)
        {
          continue;
        }

        string[] owners = entries
            .Select(entry => entry.Patch.owner)
            .Where(owner => !string.IsNullOrEmpty(owner))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        bool orderRisk = owners.Length > 1
            && entries.Any(entry =>
                entry.Patch.before.Length > 0
                || entry.Patch.after.Length > 0);
        report.AppendLine(
            $"{MethodName(target)} | "
            + (orderRisk
                ? "order-risk [Highly suspected]"
                : owners.Length > 1
                    ? "shared-target [Inconclusive]"
                    : "single-owner"));
        foreach ((string kind, Patch patch) in entries)
        {
          report.Append("  ")
              .Append(kind)
              .Append(" owner=")
              .Append(LocalModDoctor.Safe(patch.owner, 200))
              .Append(" priority=")
              .Append(patch.priority)
              .Append(" method=")
              .Append(LocalModDoctor.Safe(
                  MethodName(patch.PatchMethod),
                  200));
          if (patch.before.Length > 0)
          {
            report.Append(" before=")
                .Append(string.Join(
                    ",",
                    patch.before.Take(16).Select(owner =>
                        LocalModDoctor.Safe(owner, 200))));
          }

          if (patch.after.Length > 0)
          {
            report.Append(" after=")
                .Append(string.Join(
                    ",",
                    patch.after.Take(16).Select(owner =>
                        LocalModDoctor.Safe(owner, 200))));
          }

          report.AppendLine();
        }

        count += entries.Length;
        if (count >= MaxPatchRecords
            || report.Length >= LocalModDoctor.MaxReportCharacters)
        {
          report.AppendLine(
              "Output reached its safety bound; remaining patches were not inspected.");
          break;
        }
      }

      report.AppendLine(FormattableString.Invariant(
          $"Patch records inspected: {count}"));
      report.AppendLine(
          "Read-only: this report did not unpatch, reorder or execute any patch.");
      return report.ToString();
    }
    catch (Exception ex)
    {
      return "BetterCoop Harmony conflict map\n"
          + "[Inconclusive] Metadata inspection unavailable: "
          + LocalModDoctor.Safe(ex.GetType().Name, 100);
    }
  }

  private static string MethodName(MethodBase? method) =>
      method == null
          ? "<unknown>"
          : LocalModDoctor.Safe(
              $"{method.DeclaringType?.FullName ?? "<unknown>"}.{method.Name}",
              200);
}

internal sealed record KnownIssueRule(
    string Id,
    int Schema,
    string Mode,
    string GameBuild,
    string? ModId,
    string? ModVersion,
    string Signature,
    string Confidence,
    string Summary,
    string Action);

internal sealed record KnownIssueQuery(
    string GameBuild,
    string? ModId,
    string? ModVersion,
    string Signature);

internal static class KnownIssueCatalog
{
  public const int Schema = 1;
  public const int MaxRules = 5000;
  private static readonly KnownIssueRule[] BuiltIn =
  [
      new(
            "STS2-01012-STATE-DIVERGENCE",
            Schema,
            "exact",
            "v0.109.1",
            null,
            null,
            "StateDivergence",
            "Confirmed",
            "The peers produced different native state checksums; this confirms divergence, not one responsible Mod.",
            "Do not keep reconnecting to the diverged run. Save matching peer logs, restart, verify the environment, then reproduce in a fresh run."),
        new(
            "STS2-04001-TIMEOUT",
            Schema,
            "exact",
            "v0.109.1",
            null,
            null,
            "Timeout",
            "Inconclusive",
            "The native connection timed out. A timeout alone does not identify network, host, game or Mod responsibility.",
            "Retry once after checking Steam/network state; if repeatable, save same-window logs from host and affected client."),
        new(
            "STS2-RUN-REJOIN-UNSUPPORTED",
            Schema,
            "exact",
            "v0.109.1",
            null,
            null,
            "RunInProgress",
            "Confirmed",
            "This game build has no verified production contract for BetterCoop to rejoin a run already in progress.",
            "Use manual re-entry only before the run starts. Keep the native save and restart instead of forcing recovery.")
  ];

  public static IReadOnlyList<KnownIssueRule> Match(KnownIssueQuery query) =>
      ValidateAndMatch(BuiltIn, query, out _);

  internal static IReadOnlyList<KnownIssueRule> ValidateAndMatch(
      IReadOnlyList<KnownIssueRule> rules,
      KnownIssueQuery query,
      out string status)
  {
    if (rules.Count > MaxRules
        || rules.Any(rule =>
            rule.Schema != Schema
            || rule.Mode != "exact"
            || string.IsNullOrWhiteSpace(rule.Id)
            || rule.Id.Length > 100
            || rule.GameBuild.Length > 64
            || rule.ModId?.Length > 100
            || rule.ModVersion?.Length > 64
            || string.IsNullOrEmpty(rule.Signature)
            || rule.Signature.Length > 256
            || rule.Summary.Length > 2048
            || rule.Action.Length > 2048)
        || rules.GroupBy(rule => rule.Id, StringComparer.Ordinal)
            .Any(group => group.Count() > 1)
        || rules.GroupBy(
                rule => (
                    rule.GameBuild,
                    rule.ModId,
                    rule.ModVersion,
                    rule.Signature))
            .Any(group => group.Select(rule =>
                    (rule.Confidence, rule.Summary, rule.Action))
                .Distinct()
                .Count() > 1))
    {
      status =
          "Known-issue catalog unavailable: invalid, duplicate or conflicting rules.";
      return [];
    }

    status = "Known-issue catalog healthy.";
    return rules.Where(rule =>
            string.Equals(
                rule.GameBuild,
                query.GameBuild,
                StringComparison.Ordinal)
            && string.Equals(
                rule.Signature,
                query.Signature,
                StringComparison.Ordinal)
            && (rule.ModId == null
                || string.Equals(
                    rule.ModId,
                    query.ModId,
                    StringComparison.Ordinal))
            && (rule.ModVersion == null
                || string.Equals(
                    rule.ModVersion,
                    query.ModVersion,
                    StringComparison.Ordinal)))
        .ToArray();
  }

  public static string BuildReport(string gameBuild)
  {
    KnownIssueQuery query = new(gameBuild, null, null, string.Empty);
    _ = ValidateAndMatch(BuiltIn, query, out string status);
    StringBuilder report = new("BetterCoop built-in known issues\n");
    report.AppendLine(status);
    foreach (KnownIssueRule rule in BuiltIn.Where(rule =>
                 string.Equals(
                     rule.GameBuild,
                     gameBuild,
                     StringComparison.Ordinal)))
    {
      report.AppendLine(FormattableString.Invariant(
          $"{rule.Id} | signature={rule.Signature} | [{rule.Confidence}]"));
      report.AppendLine("  " + LocalModDoctor.Safe(rule.Summary, 2048));
      report.AppendLine("  " + LocalModDoctor.Safe(rule.Action, 2048));
    }

    report.AppendLine(
        "Local static data only: no download, remote rule, command or automatic block.");
    return report.ToString();
  }
}
