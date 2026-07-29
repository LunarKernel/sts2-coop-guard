using System.Reflection;
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

namespace CoopGuard;

internal static class CompatibilityGate
{
    public static bool CanProceed(out string reason)
        => IsValid(ModFingerprint.ValidateCurrent(), out reason);

    public static bool CanProceedQuick(out string reason)
        => IsValid(ModFingerprint.ValidateQuick(), out reason);

    private static bool IsValid(
        FingerprintSnapshot snapshot,
        out string reason)
    {
        if (snapshot.Errors.Count == 0)
        {
            reason = string.Empty;
            return true;
        }

        reason = "CoopGuard could not verify the local Mod packages:\n"
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
        string action)
    {
        if (netService.Type != NetGameType.Client
            || CanProceedQuick(out string reason))
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

        if (CompatibilityGate.CanProceed(out string reason))
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
            && !CompatibilityGate.CanProceed(out string reason))
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
            "begin-run message");
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

        if (CompatibilityGate.CanProceed(out string reason))
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
            && !CompatibilityGate.CanProceed(out string reason))
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
            "loaded-run begin message");
}
