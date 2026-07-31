using System.Reflection;
using System.Security.Cryptography;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.DailyRun;

namespace BetterCoop;

internal static class CompatibilityGate
{
  public static bool CanProceed(out string reason) =>
      IsValid(ModFingerprint.ValidateCurrent(), out reason)
      && ToolkitDiagnosticsRuntime.RunControlSettingMatches(
          out reason)
      && ToolkitDiagnosticsRuntime.CanReadyAfterRollback(
          out reason);

  public static bool CanProceedQuick(out string reason) =>
      IsValid(ModFingerprint.ValidateQuick(), out reason)
      && ToolkitDiagnosticsRuntime.RunControlSettingMatches(
          out reason)
      && ToolkitDiagnosticsRuntime.CanReadyAfterRollback(
          out reason);

  public static bool CanProceed(
      int playerCount,
      out string reason)
  {
    if (!CanProceed(out reason)
        || playerCount <= 4)
    {
      return reason.Length == 0;
    }

    // Limit Break receives the host settings sidecar only after RunManager
    // exists, so a lobby can verify the pre-run contract but not that sidecar.
    if (ToolkitDiagnosticsRuntime.CanReadyWithLimitBreak(
            playerCount,
            out reason))
    {
      return true;
    }

    reason =
        $"BetterCoop cannot prove safe {playerCount}-player Limit Break operation:\n"
        + reason;
    return false;
  }

  public static bool CanReady(
      int playerCount,
      out string reason)
  {
    if (!CanProceed(out reason)
        || playerCount <= 4)
    {
      return reason.Length == 0;
    }

    if (ToolkitDiagnosticsRuntime.CanReadyWithLimitBreak(
            playerCount,
            out reason))
    {
      return true;
    }

    reason =
        $"BetterCoop cannot prove safe {playerCount}-player Limit Break readiness:\n"
        + reason;
    return false;
  }

  private static bool IsValid(
      FingerprintSnapshot snapshot,
      out string reason)
  {
    if (snapshot.Errors.Count == 0)
    {
      reason = string.Empty;
      return true;
    }

    reason = "BetterCoop could not verify the local Mod packages:\n"
        + string.Join('\n', snapshot.Errors.Take(6));
    return false;
  }

  public static void ReportBlocked(string action, string reason)
  {
    Main.Log.Warn($"Blocked {action}: " + reason.Replace('\n', ' '));
    FatalIncidentReporter.ShowLocalVerification(reason);
  }

  public static bool IsMultiplayer(NetGameType type) =>
      type is NetGameType.Host or NetGameType.Client;

  public static bool CanAcceptClientBegin(
      INetGameService netService,
      string action,
      int playerCount = 0)
  {
    if (netService.Type != NetGameType.Client
        || (CanProceedQuick(out string reason)
            && (playerCount <= 4
                || ToolkitDiagnosticsRuntime.CanReadyWithLimitBreak(
                    playerCount,
                    out reason))))
    {
      return true;
    }

    Main.Log.Warn($"Rejected {action}: " + reason.Replace('\n', ' '));
    try
    {
      netService.Disconnect(NetError.ModMismatch);
    }
    catch (Exception ex)
    {
      // Suppressing the begin handler still fails closed if disconnect fails.
      Main.Log.Error($"Could not disconnect after rejecting {action}: {ex}");
    }

    return false;
  }

  public static int PlayerCount(StartRunLobby lobby) =>
      lobby.Players.Select(player => player.id)
          .Append(lobby.NetService.NetId)
          .Distinct()
          .Take(ToolkitLimits.MaxPlayers + 1)
          .Count();

  public static int PlayerCount(LoadRunLobby lobby) =>
      lobby.ConnectedPlayerIds
          .Append(lobby.NetService.NetId)
          .Distinct()
          .Take(ToolkitLimits.MaxPlayers + 1)
          .Count();
}

internal static class LimitBreakAdapter
{
  private const string LimitBreakId =
      "STS2-MultiplayerLimitBreak";
  private const string RitsuId = "STS2-RitsuLib";
  private const string SupportedGameVersion = "0.109.1";
  private const string SupportedModVersion = "0.1.3";
  private const string SupportedDllHash =
      "b2785afd3dc31fd6b32cb073af495ab343fcc31fa9e479049959ff43eb09356f";
  private static readonly Version MinimumRitsuVersion =
      new(0, 4, 13);

  public static bool TryCapture(
      INetGameService? service,
      byte originOrdinal,
      uint membershipEpoch,
      out ToolkitLimitBreakCapability capability,
      out string reason) =>
      TryCapture(
          service,
          originOrdinal,
          membershipEpoch,
          requireSettingsSynchronized: true,
          out capability,
          out reason);

  public static bool TryCaptureForReady(
      INetGameService? service,
      byte originOrdinal,
      uint membershipEpoch,
      out ToolkitLimitBreakCapability capability,
      out string reason) =>
      TryCapture(
          service,
          originOrdinal,
          membershipEpoch,
          requireSettingsSynchronized: false,
          out capability,
          out reason);

  private static bool TryCapture(
      INetGameService? service,
      byte originOrdinal,
      uint membershipEpoch,
      bool requireSettingsSynchronized,
      out ToolkitLimitBreakCapability capability,
      out string reason)
  {
    List<string> issues = [];
    ToolkitLimitBreakFlags flags =
        ToolkitLimitBreakFlags.None;
    byte capacity = 0;
    byte slotBits = 0;
    byte listBits = 0;
    bool enabled = false;
    double multiplier = double.NaN;
    uint settingsEpoch = 0;
    string gameVersion = MegaCrit.Sts2.Core.Nodes.NGame
        .GetGameVersion().TrimStart('v');
    string modVersion = string.Empty;
    string ritsuVersion = string.Empty;
    string dllHash = string.Empty;

    try
    {
      Mod? limitBreak = ModManager.Mods.SingleOrDefault(mod =>
          mod.state == ModLoadState.Loaded
          && string.Equals(
              mod.manifest?.id,
              LimitBreakId,
              StringComparison.Ordinal));
      if (limitBreak == null)
      {
        issues.Add("STS2-MultiplayerLimitBreak is not loaded");
      }
      else
      {
        modVersion = limitBreak.manifest?.version ?? string.Empty;
        Assembly? assembly = limitBreak.assemblies.SingleOrDefault(
            item => string.Equals(
                item.GetName().Name,
                LimitBreakId,
                StringComparison.Ordinal));
        if (assembly == null
            || string.IsNullOrEmpty(assembly.Location)
            || !File.Exists(assembly.Location))
        {
          issues.Add("Limit Break DLL is not active");
        }
        else
        {
          using FileStream stream = File.OpenRead(assembly.Location);
          dllHash = Convert.ToHexString(
              SHA256.HashData(stream)).ToLowerInvariant();
          ProbeAssembly(
              assembly,
              service,
              ref flags,
              ref capacity,
              ref slotBits,
              ref listBits,
              ref enabled,
              ref multiplier,
              ref settingsEpoch,
              issues);
        }
      }

      Mod? ritsu = ModManager.Mods.SingleOrDefault(mod =>
          mod.state == ModLoadState.Loaded
          && string.Equals(
              mod.manifest?.id,
              RitsuId,
              StringComparison.Ordinal));
      ritsuVersion = ritsu?.manifest?.version ?? string.Empty;
      if (!Version.TryParse(ritsuVersion, out Version? parsedRitsu)
          || parsedRitsu < MinimumRitsuVersion)
      {
        issues.Add(
            $"RitsuLib {ritsuVersion} is below 0.4.13 or unavailable");
      }

      bool knownContract =
          string.Equals(
              gameVersion,
              SupportedGameVersion,
              StringComparison.Ordinal)
          && string.Equals(
              modVersion,
              SupportedModVersion,
              StringComparison.Ordinal)
          && string.Equals(
              dllHash,
              SupportedDllHash,
              StringComparison.Ordinal)
          && Version.TryParse(
              ritsuVersion,
              out Version? contractRitsu)
          && contractRitsu >= MinimumRitsuVersion;
      if (knownContract)
      {
        flags |= ToolkitLimitBreakFlags.KnownContract;
      }
      else
      {
        issues.Add(
            "unsupported game/Limit Break/RitsuLib version or DLL hash");
      }
    }
    catch (Exception ex)
    {
      issues.Add("capability probe failed: " + ex.GetType().Name);
    }

    byte[] contractDigest =
        ToolkitLimitBreakContract.DigestIdentity(
            gameVersion,
            modVersion,
            dllHash,
            ritsuVersion);
    byte[] settingsDigest =
        ToolkitLimitBreakContract.DigestSettings(
            enabled,
            multiplier);
    if (settingsDigest.Length
        != ToolkitLimitBreakContract.DigestBytes)
    {
      settingsDigest =
          new byte[ToolkitLimitBreakContract.DigestBytes];
    }

    capability = new(
        originOrdinal,
        membershipEpoch,
        flags,
        capacity,
        slotBits,
        listBits,
        settingsEpoch,
        contractDigest,
        settingsDigest);
    bool compatible = requireSettingsSynchronized
        ? ToolkitLimitBreakContract.IsCompatible(
            capability,
            out string contractReason)
        : ToolkitLimitBreakContract.IsReadyCompatible(
            capability,
            out contractReason);
    if (!compatible)
    {
      issues.Add(contractReason);
    }

    reason = string.Join(
        "; ",
        issues.Distinct(StringComparer.Ordinal).Take(6));
    return reason.Length == 0;
  }

  private static void ProbeAssembly(
      Assembly assembly,
      INetGameService? service,
      ref ToolkitLimitBreakFlags flags,
      ref byte capacity,
      ref byte slotBits,
      ref byte listBits,
      ref bool enabled,
      ref double multiplier,
      ref uint settingsEpoch,
      List<string> issues)
  {
    Type? entry = assembly.GetType(
        "STS2MultiplayerLimitBreak.ModEntry",
        throwOnError: false);
    if (GetStatic<bool>(entry, "IsActive"))
    {
      flags |= ToolkitLimitBreakFlags.Active;
    }
    else
    {
      issues.Add("Limit Break reports inactive");
    }

    Type? constants = assembly.GetType(
        "STS2MultiplayerLimitBreak.Const",
        throwOnError: false);
    capacity = ToByte(GetLiteral<int>(
        constants,
        "PlayerLimit"));
    slotBits = ToByte(GetLiteral<int>(
        constants,
        "SlotIdBits"));
    listBits = ToByte(GetLiteral<int>(
        constants,
        "LobbyListLengthBits"));

    (int network, int layout, int scaling) =
        CountPatchTargets(assembly);
    if (network == 14)
    {
      flags |= ToolkitLimitBreakFlags.NetworkPatches
          | ToolkitLimitBreakFlags.SteamCapacity;
    }
    else
    {
      issues.Add($"network patch group={network}/14");
    }

    if (layout == 4)
    {
      flags |= ToolkitLimitBreakFlags.LayoutPatches;
    }
    else
    {
      issues.Add($"layout patch group={layout}/4");
    }

    if (scaling == 2)
    {
      flags |= ToolkitLimitBreakFlags.ScalingPatches;
    }
    else
    {
      issues.Add($"scaling patch group={scaling}/2");
    }

    Type? settings = assembly.GetType(
        "STS2MultiplayerLimitBreak.Settings.RuntimeMultiplayerSettings",
        throwOnError: false);
    enabled = GetStatic<bool>(settings, "LimitBreakEnabled");
    multiplier = GetStatic<double>(
        settings,
        "ExtraPlayerScalingMultiplier");
    if (enabled)
    {
      flags |= ToolkitLimitBreakFlags.Enabled;
    }
    else
    {
      issues.Add("Limit Break is disabled");
    }

    bool synchronized = service?.Type == NetGameType.Host
        || service?.Type == NetGameType.Client
        && settings?.GetField(
            "_remoteHostSettings",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null) != null;
    if (synchronized)
    {
      flags |= ToolkitLimitBreakFlags.SettingsSynchronized;
      settingsEpoch = 1;
    }
    else
    {
      settingsEpoch = 0;
    }
  }

  private static (int Network, int Layout, int Scaling)
      CountPatchTargets(Assembly assembly)
  {
    HashSet<MethodBase> network = [];
    HashSet<MethodBase> layout = [];
    HashSet<MethodBase> scaling = [];
    foreach (MethodBase target in Harmony.GetAllPatchedMethods())
    {
      Patches? patches = Harmony.GetPatchInfo(target);
      if (patches == null)
      {
        continue;
      }

      Patch[] entries = patches.Prefixes
          .Concat(patches.Postfixes)
          .Concat(patches.Transpilers)
          .Concat(patches.Finalizers)
          .Where(patch =>
              patch.PatchMethod.DeclaringType?.Assembly == assembly)
          .ToArray();
      foreach (Patch patch in entries)
      {
        string typeName =
            patch.PatchMethod.DeclaringType?.FullName ?? string.Empty;
        if (typeName.Contains(
                "DifficultyScalingPatches",
                StringComparison.Ordinal))
        {
          scaling.Add(target);
        }
        else if (typeName.StartsWith(
                     "STS2MultiplayerLimitBreak.Layout.",
                     StringComparison.Ordinal))
        {
          layout.Add(target);
        }
        else if (typeName.StartsWith(
                     "STS2MultiplayerLimitBreak.Network.",
                     StringComparison.Ordinal))
        {
          network.Add(target);
        }
      }
    }

    return (network.Count, layout.Count, scaling.Count);
  }

  private static T GetStatic<T>(Type? type, string property)
  {
    object? value = type?.GetProperty(
            property,
            BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Static)
        ?.GetValue(null);
    return value is T result ? result : default!;
  }

  private static T GetLiteral<T>(Type? type, string field)
  {
    object? value = type?.GetField(
            field,
            BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Static)
        ?.GetRawConstantValue();
    return value is T result ? result : default!;
  }

  private static byte ToByte(int value) =>
      value is >= byte.MinValue and <= byte.MaxValue
          ? (byte)value
          : (byte)0;
}

[HarmonyPatch(typeof(OneTimeInitialization), nameof(OneTimeInitialization.ExecuteEssential))]
internal static class FingerprintPrecomputePatch
{
  [HarmonyPriority(Priority.Last)]
  private static async void Postfix()
  {
    try
    {
      ModFingerprint.Precompute();

      if (Engine.GetMainLoop() is not SceneTree tree)
      {
        Main.Log.Warn(
            "Could not schedule the startup mounted-PCK settlement check.");
        return;
      }

      await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
      await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
      ModFingerprint.SettleStartupMounts();
    }
    catch (Exception ex)
    {
      // A missing/failed baseline still yields the process-stable failure token.
      Main.Log.Error(
          $"Startup mounted-PCK settlement check failed: {ex}");
    }
  }
}

internal static class GameplayModListPatch
{
  public static void Apply(Harmony harmony)
  {
    MethodInfo original = AccessTools.DeclaredMethod(
            typeof(ModManager),
            nameof(ModManager.GetGameplayRelevantModNameList))
        ?? throw new MissingMethodException(
            typeof(ModManager).FullName,
            nameof(ModManager.GetGameplayRelevantModNameList));
    MethodInfo postfix = AccessTools.DeclaredMethod(
            typeof(GameplayModListPatch),
            nameof(Postfix))
        ?? throw new MissingMethodException(
            typeof(GameplayModListPatch).FullName,
            nameof(Postfix));
    harmony.Patch(original, postfix: new HarmonyMethod(postfix));
  }

  [HarmonyPriority(Priority.Last)]
  private static void Postfix(ref List<string>? __result)
  {
    __result ??= [];
    __result.AddRange(ModFingerprint.GetCompatibilityEntries());
  }
}

[HarmonyPatch(typeof(ModManager), nameof(ModManager.AssociateAssemblyWithMod))]
internal static class LateAssemblyPatch
{
  private static void Prefix(string __0, out int __state) =>
      __state = ModFingerprint.LoadedAssemblyCount(__0);

  private static void Postfix(string __0, int __state)
  {
    if (ModFingerprint.LoadedAssemblyCount(__0) != __state)
    {
      ModFingerprint.MarkRuntimeChange();
    }
  }
}

[HarmonyPatch(typeof(JoinFlow), nameof(JoinFlow.Begin))]
internal static class JoinFlowBeginPatch
{
  [HarmonyPriority(Priority.First)]
  private static void Prefix() =>
      ModFingerprint.ValidateCurrent();
}

[HarmonyPatch(
    typeof(JoinFlow),
    "HandleInitialGameInfoMessage",
    [typeof(InitialGameInfoMessage), typeof(ulong)])]
internal static class JoinFlowInitialGameInfoPatch
{
  [HarmonyPriority(Priority.First)]
  private static bool Prefix(JoinFlow __instance) =>
      CompatibilityGate.CanAcceptClientBegin(
          __instance.NetService,
          "initial game info");
}

[HarmonyPatch(typeof(InitialGameInfoMessage), nameof(InitialGameInfoMessage.Basic))]
internal static class InitialGameInfoPatch
{
  [HarmonyPriority(Priority.First)]
  private static void Prefix() =>
      ModFingerprint.ValidateQuick();
}

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.SetReady))]
internal static class StartRunLobbyReadyPatch
{
  [HarmonyPriority(Priority.First)]
  private static bool Prefix(StartRunLobby __instance, bool ready)
  {
    if (!ready
        || !CompatibilityGate.IsMultiplayer(__instance.NetService.Type))
    {
      return true;
    }

    if (CompatibilityGate.CanReady(
            CompatibilityGate.PlayerCount(__instance),
            out string reason))
    {
      FatalIncidentReporter.ShowVerified();
      return true;
    }

    CompatibilityGate.ReportBlocked("ready", reason);
    return false;
  }
}

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.IsAboutToBeginGame))]
internal static class StartRunLobbyBeginPatch
{
  [HarmonyPriority(Priority.Last)]
  private static void Postfix(StartRunLobby __instance, ref bool __result)
  {
    if (__result
        && CompatibilityGate.IsMultiplayer(__instance.NetService.Type)
        && !CompatibilityGate.CanProceed(
            CompatibilityGate.PlayerCount(__instance),
            out string reason))
    {
      Main.Log.Warn("Blocked begin-run: " + reason.Replace('\n', ' '));
      __result = false;
    }
  }
}

[HarmonyPatch(
    typeof(StartRunLobby),
    "HandleLobbyBeginRunMessage",
    [typeof(LobbyBeginRunMessage), typeof(ulong)])]
internal static class StartRunLobbyClientBeginPatch
{
  [HarmonyPriority(Priority.First)]
  private static bool Prefix(StartRunLobby __instance) =>
      CompatibilityGate.CanAcceptClientBegin(
          __instance.NetService,
          "begin-run message",
          CompatibilityGate.PlayerCount(__instance));
}

[HarmonyPatch(typeof(LoadRunLobby), nameof(LoadRunLobby.SetReady))]
internal static class LoadRunLobbyReadyPatch
{
  [HarmonyPriority(Priority.First)]
  private static bool Prefix(LoadRunLobby __instance, bool ready)
  {
    if (!ready
        || !CompatibilityGate.IsMultiplayer(__instance.NetService.Type))
    {
      return true;
    }

    if (CompatibilityGate.CanReady(
            CompatibilityGate.PlayerCount(__instance),
            out string reason))
    {
      FatalIncidentReporter.ShowVerified();
      return true;
    }

    CompatibilityGate.ReportBlocked("loaded-run ready", reason);
    return false;
  }
}

[HarmonyPatch(typeof(LoadRunLobby), nameof(LoadRunLobby.IsAboutToBeginGame))]
internal static class LoadRunLobbyBeginPatch
{
  [HarmonyPriority(Priority.Last)]
  private static void Postfix(LoadRunLobby __instance, ref bool __result)
  {
    if (__result
        && CompatibilityGate.IsMultiplayer(__instance.NetService.Type)
        && !CompatibilityGate.CanProceed(
            CompatibilityGate.PlayerCount(__instance),
            out string reason))
    {
      Main.Log.Warn("Blocked loaded-run begin: " + reason.Replace('\n', ' '));
      __result = false;
    }
  }
}

[HarmonyPatch]
internal static class LoadedRunFinalConfirmationPatch
{
  private static IEnumerable<MethodBase> TargetMethods()
  {
    Type[] listenerTypes =
    [
        typeof(NMultiplayerLoadGameScreen),
            typeof(NDailyRunLoadScreen),
            typeof(NCustomRunLoadScreen)
    ];

    foreach (Type listenerType in listenerTypes)
    {
      MethodInfo? method = AccessTools.DeclaredMethod(
          listenerType,
          nameof(ILoadRunLobbyListener.ShouldAllowRunToBegin),
          Type.EmptyTypes);
      yield return method
          ?? throw new MissingMethodException(
              listenerType.FullName,
              nameof(ILoadRunLobbyListener.ShouldAllowRunToBegin));
    }
  }

  [HarmonyPriority(Priority.Last)]
  private static void Postfix(ref Task<bool> __result) =>
      __result = RevalidateAfterConfirmation(__result);

  private static async Task<bool> RevalidateAfterConfirmation(
      Task<bool> original)
  {
    if (!await original)
    {
      return false;
    }

    if (CompatibilityGate.CanProceed(out string reason))
    {
      return true;
    }

    CompatibilityGate.ReportBlocked(
        "loaded-run final confirmation",
        reason);
    return false;
  }
}

[HarmonyPatch(
    typeof(LoadRunLobby),
    "HandleLobbyBeginRunMessage",
    [typeof(LobbyBeginLoadedRunMessage), typeof(ulong)])]
internal static class LoadRunLobbyClientBeginPatch
{
  [HarmonyPriority(Priority.First)]
  private static bool Prefix(LoadRunLobby __instance) =>
      CompatibilityGate.CanAcceptClientBegin(
          __instance.NetService,
          "loaded-run begin message",
          CompatibilityGate.PlayerCount(__instance));
}
