using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace BetterCoop;

[ModInitializer(nameof(Initialize))]
public static class Main
{
  public const string ModId = "BetterCoop";
  private const string GuardSentinelOwner = ModId + ".guard.sentinel";
  private const string GuardOwner = ModId + ".guard";
  private const string DiagnosticsOwner = ModId + ".diagnostics";
  private const string ToolkitRuntimeOwner = ModId + ".toolkit.runtime";
  private const string ToolkitPersistenceOwner =
      ModId + ".toolkit.persistence";
  private const string ToolkitLobbyOwner = ModId + ".toolkit.lobby";
  private const string ToolkitJoinOwner = ModId + ".toolkit.join";
  private const string ToolkitObserverOwner =
      ModId + ".toolkit.observers";
  private const string ToolkitForensicsOwner =
      ModId + ".toolkit.forensics";
  private const string ToolkitRngOwner =
      ModId + ".toolkit.rng";
  private const string ToolkitContributionOwner =
      ModId + ".toolkit.contributions";
  private const string ToolkitProtocolOwner =
      ModId + ".toolkit.protocol";

  internal static Logger Log { get; } = new(ModId, LogType.Network);

  public static void Initialize()
  {
    Harmony sentinel = new(GuardSentinelOwner);
    // Install the native-list sentinel first. If any later patch breaks after
    // a game update, peers still receive process-unique unsafe entries.
    GameplayModListPatch.Apply(sentinel);

    try
    {
      Install(
          GuardOwner,
          [
              typeof(FingerprintPrecomputePatch),
                    typeof(LateAssemblyPatch),
                    typeof(JoinFlowBeginPatch),
                    typeof(JoinFlowInitialGameInfoPatch),
                    typeof(InitialGameInfoPatch),
                    typeof(StartRunLobbyReadyPatch),
                    typeof(StartRunLobbyBeginPatch),
                    typeof(StartRunLobbyClientBeginPatch),
                    typeof(LoadRunLobbyReadyPatch),
                    typeof(LoadRunLobbyBeginPatch),
                    typeof(LoadedRunFinalConfirmationPatch),
                    typeof(LoadRunLobbyClientBeginPatch)
          ]);
    }
    catch (Exception ex)
    {
      ModFingerprint.MarkInitializationFailure(ex);
      Log.Error(
          "BetterCoop Guard installation failed. Multiplayer will fail closed: "
              + ex);
    }

    InstallOptional(
        DiagnosticsOwner,
        [
            typeof(NetworkErrorExplanationPatch),
                typeof(InternalErrorCapturePatch),
                typeof(InternalErrorExplanationPatch),
                typeof(DiagnosticCopyButtonLabelPatch),
                typeof(DiagnosticCopyButtonPatch),
                typeof(ManualSnapshotHotkeyPatch)
        ],
        FatalIncidentReporter.Initialize);
    InstallOptional(
        ToolkitRuntimeOwner,
        [
            typeof(ToolkitAttachPatch),
                typeof(ToolkitFramePatch),
                typeof(ToolkitPanelHotkeyPatch),
                typeof(ToolkitMainMenuPatch)
        ]);
    InstallOptional(
        ToolkitProtocolOwner,
        [typeof(ToolkitTransportSenderPatch)],
        ToolkitDiagnosticsRuntime.EnableTransportValidation);
    InstallOptional(
        ToolkitPersistenceOwner,
        [
            typeof(ToolkitMultiplayerSavePatch),
                typeof(ToolkitMultiplayerLoadWarningPatch)
        ]);
    InstallOptional(
        ToolkitLobbyOwner,
        [
            typeof(ToolkitStartLobbyPatch),
                typeof(ToolkitLoadLobbyPatch),
                typeof(ToolkitRunLobbyPatch),
                typeof(ToolkitStartLobbyCleanupPatch),
                typeof(ToolkitLoadLobbyCleanupPatch),
                typeof(ToolkitStartReadyPatch),
                typeof(ToolkitLoadReadyPatch),
                typeof(ToolkitTurnReadyPatch),
                typeof(ToolkitTurnUndoPatch),
                typeof(ToolkitCombatStartPatch),
                typeof(ToolkitCombatResetPatch)
        ]);
    InstallOptional(
        ToolkitJoinOwner,
        [
            typeof(ToolkitJoinBeginPatch),
                typeof(ToolkitInitialInfoPatch),
                typeof(ToolkitAttemptJoinPatch),
                typeof(ToolkitAttemptLoadJoinPatch),
                typeof(ToolkitAttemptRunningPatch)
        ]);
    InstallOptional(
        ToolkitObserverOwner,
        [
            typeof(ToolkitRemoteChoicePatch),
                typeof(ToolkitMapVotePatch),
                typeof(ToolkitMapResetPatch),
                typeof(ToolkitActionObserverPatch)
        ]);
    InstallOptional(
        ToolkitForensicsOwner,
        [typeof(ToolkitCheckpointObserverPatch)],
        ToolkitForensicsRuntime.EnableCheckpointObserver);
    InstallOptional(
        ToolkitRngOwner,
        [
            typeof(ToolkitRunRngSetPatch),
                typeof(ToolkitRngBoolPatch),
                typeof(ToolkitRngIntMaxPatch),
                typeof(ToolkitRngIntRangePatch),
                typeof(ToolkitRngUIntRangePatch),
                typeof(ToolkitRngULongPatch),
                typeof(ToolkitRngULongRangePatch),
                typeof(ToolkitRngFloatRangePatch),
                typeof(ToolkitRngDoublePatch),
                typeof(ToolkitRngDoubleRangePatch)
        ],
        ToolkitRngCounter.Enable);
    InstallOptional(
        ToolkitContributionOwner,
        [
            typeof(ToolkitContributionDamagePatch),
                typeof(ToolkitContributionBlockPatch),
                typeof(ToolkitContributionHealPatch)
        ],
        ToolkitForensicsRuntime.EnableContributionObservers);

    Log.Info(
        "Initialized. Guard, bounded local observations and bilingual diagnostics are active.");
  }

  private static void Install(string owner, IReadOnlyList<Type> patchTypes)
  {
    Harmony harmony = new(owner);
    try
    {
      foreach (Type patchType in patchTypes)
      {
        harmony.CreateClassProcessor(patchType).Patch();
      }
    }
    catch
    {
      harmony.UnpatchAll(owner);
      throw;
    }
  }

  private static void InstallOptional(
      string owner,
      IReadOnlyList<Type> patchTypes,
      Action? initialize = null)
  {
    try
    {
      Install(owner, patchTypes);
      initialize?.Invoke();
    }
    catch (Exception ex)
    {
      new Harmony(owner).UnpatchAll(owner);
      Log.Error(
          $"Optional module '{owner}' was disabled without affecting Guard: {ex}");
    }
  }
}
