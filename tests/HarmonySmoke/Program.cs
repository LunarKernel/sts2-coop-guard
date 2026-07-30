using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;

if (args.Length != 2)
{
    throw new ArgumentException(
        "Usage: HarmonySmoke <game-data-dir> <CoopGuard.dll>");
}

string gameDirectory = Path.GetFullPath(args[0]);
string guardPath = Path.GetFullPath(args[1]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string candidate = Path.Combine(gameDirectory, name.Name + ".dll");
    return File.Exists(candidate)
        ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate)
        : null;
};

Assembly guard = AssemblyLoadContext.Default.LoadFromAssemblyPath(guardPath);
Type main = guard.GetType("CoopGuard.Main", throwOnError: true)!;
string[] owners =
[
    "CoopGuard.guard.sentinel",
    "CoopGuard.guard",
    "CoopGuard.diagnostics",
    "CoopGuard.toolkit.runtime",
    "CoopGuard.toolkit.persistence",
    "CoopGuard.toolkit.lobby",
    "CoopGuard.toolkit.join",
    "CoopGuard.toolkit.protocol",
    "CoopGuard.toolkit.observers",
    "CoopGuard.toolkit.forensics",
    "CoopGuard.toolkit.rng",
    "CoopGuard.toolkit.contributions"
];

Dictionary<string, string[]> expected = new(StringComparer.Ordinal)
{
    ["CoopGuard.guard.sentinel"] =
    [
        "MegaCrit.Sts2.Core.Modding.ModManager.GetGameplayRelevantModNameList"
    ],
    ["CoopGuard.guard"] =
    [
        "MegaCrit.Sts2.Core.Helpers.OneTimeInitialization.ExecuteEssential",
        "MegaCrit.Sts2.Core.Modding.ModManager.AssociateAssemblyWithMod",
        "MegaCrit.Sts2.Core.Multiplayer.Game.JoinFlow.Begin",
        "MegaCrit.Sts2.Core.Multiplayer.Game.JoinFlow.HandleInitialGameInfoMessage",
        "MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.LoadRunLobby.HandleLobbyBeginRunMessage",
        "MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.LoadRunLobby.IsAboutToBeginGame",
        "MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.LoadRunLobby.SetReady",
        "MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.StartRunLobby.HandleLobbyBeginRunMessage",
        "MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.StartRunLobby.IsAboutToBeginGame",
        "MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.StartRunLobby.SetReady",
        "MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby.InitialGameInfoMessage.Basic",
        "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NMultiplayerLoadGameScreen.ShouldAllowRunToBegin",
        "MegaCrit.Sts2.Core.Nodes.Screens.CustomRun.NCustomRunLoadScreen.ShouldAllowRunToBegin",
        "MegaCrit.Sts2.Core.Nodes.Screens.DailyRun.NDailyRunLoadScreen.ShouldAllowRunToBegin"
    ],
    ["CoopGuard.diagnostics"] =
    [
        "MegaCrit.Sts2.Core.Nodes.CommonUi.NErrorPopup.Create",
        "MegaCrit.Sts2.Core.Nodes.CommonUi.NErrorPopup.Create",
        "MegaCrit.Sts2.Core.Nodes.CommonUi.NErrorPopup.OnReportBugButtonPressed",
        "MegaCrit.Sts2.Core.Nodes.CommonUi.NErrorPopup._Ready",
        "MegaCrit.Sts2.Core.Nodes.NGame._Input",
        "MegaCrit.Sts2.Core.Nodes.NGame.ReturnToMainMenuWithInternalError"
    ],
    ["CoopGuard.toolkit.runtime"] =
    [
        "MegaCrit.Sts2.Core.Nodes.CommonUi.NControllerManager._Process",
        "MegaCrit.Sts2.Core.Nodes.NGame._Input",
        "MegaCrit.Sts2.Core.Nodes.NGame._Ready",
        "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu._Ready"
    ],
    ["CoopGuard.toolkit.persistence"] =
    [
        "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMultiplayerSubmenu.StartLoad",
        "MegaCrit.Sts2.Core.Saves.Managers.RunSaveManager.SaveRun"
    ],
    ["CoopGuard.toolkit.lobby"] =
    [
        "MegaCrit.Sts2.Core.Combat.CombatManager.Reset",
        "MegaCrit.Sts2.Core.Combat.CombatManager.SetReadyToEndTurn",
        "MegaCrit.Sts2.Core.Combat.CombatManager.SetUpCombat",
        "MegaCrit.Sts2.Core.Combat.CombatManager.UndoReadyToEndTurn",
        "MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.LoadRunLobby..ctor",
        "MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.LoadRunLobby.CleanUp",
        "MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.LoadRunLobby.SetReady",
        "MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.RunLobby..ctor",
        "MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.StartRunLobby..ctor",
        "MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.StartRunLobby.CleanUp",
        "MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.StartRunLobby.SetReady"
    ],
    ["CoopGuard.toolkit.join"] =
    [
        "MegaCrit.Sts2.Core.Multiplayer.Game.JoinFlow.AttemptJoin",
        "MegaCrit.Sts2.Core.Multiplayer.Game.JoinFlow.AttemptLoadJoin",
        "MegaCrit.Sts2.Core.Multiplayer.Game.JoinFlow.AttemptRejoin",
        "MegaCrit.Sts2.Core.Multiplayer.Game.JoinFlow.Begin",
        "MegaCrit.Sts2.Core.Multiplayer.Game.JoinFlow.HandleInitialGameInfoMessage"
    ],
    ["CoopGuard.toolkit.protocol"] =
    [
        "MegaCrit.Sts2.Core.Multiplayer.NetClientGameService.OnPacketReceived",
        "MegaCrit.Sts2.Core.Multiplayer.NetHostGameService.OnPacketReceived"
    ],
    ["CoopGuard.toolkit.observers"] =
    [
        "MegaCrit.Sts2.Core.GameActions.ActionExecutor..ctor",
        "MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceSynchronizer.WaitForRemoteChoice",
        "MegaCrit.Sts2.Core.Multiplayer.Game.MapSelectionSynchronizer.OnLocationChanged",
        "MegaCrit.Sts2.Core.Multiplayer.Game.MapSelectionSynchronizer.PlayerVotedForMapCoord"
    ],
    ["CoopGuard.toolkit.forensics"] =
    [
        "MegaCrit.Sts2.Core.Multiplayer.Game.ChecksumTracker..ctor"
    ],
    ["CoopGuard.toolkit.rng"] =
    [
        "MegaCrit.Sts2.Core.Random.Rng.NextBool",
        "MegaCrit.Sts2.Core.Random.Rng.NextDouble",
        "MegaCrit.Sts2.Core.Random.Rng.NextDouble",
        "MegaCrit.Sts2.Core.Random.Rng.NextFloat",
        "MegaCrit.Sts2.Core.Random.Rng.NextInt",
        "MegaCrit.Sts2.Core.Random.Rng.NextInt",
        "MegaCrit.Sts2.Core.Random.Rng.NextUnsignedInt",
        "MegaCrit.Sts2.Core.Random.Rng.NextUnsignedLong",
        "MegaCrit.Sts2.Core.Random.Rng.NextUnsignedLong"
    ],
    ["CoopGuard.toolkit.contributions"] =
    [
        "MegaCrit.Sts2.Core.Commands.CreatureCmd.Heal",
        "MegaCrit.Sts2.Core.Combat.History.CombatHistory.BlockGained",
        "MegaCrit.Sts2.Core.Combat.History.CombatHistory.DamageReceived"
    ]
};

try
{
    MethodInfo install = main.GetMethod(
            "Install",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(main.FullName, "Install");
    Type sentinel = guard.GetType(
        "CoopGuard.GameplayModListPatch",
        throwOnError: true)!;
    sentinel.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static)!
        .Invoke(null, [new Harmony("CoopGuard.guard.sentinel")]);

    InstallOwner(
        install,
        "CoopGuard.guard",
        [
            "FingerprintPrecomputePatch",
            "LateAssemblyPatch",
            "JoinFlowBeginPatch",
            "JoinFlowInitialGameInfoPatch",
            "InitialGameInfoPatch",
            "StartRunLobbyReadyPatch",
            "StartRunLobbyBeginPatch",
            "StartRunLobbyClientBeginPatch",
            "LoadRunLobbyReadyPatch",
            "LoadRunLobbyBeginPatch",
            "LoadedRunFinalConfirmationPatch",
            "LoadRunLobbyClientBeginPatch"
        ]);
    InstallOwner(
        install,
        "CoopGuard.diagnostics",
        [
            "NetworkErrorExplanationPatch",
            "InternalErrorCapturePatch",
            "InternalErrorExplanationPatch",
            "DiagnosticCopyButtonLabelPatch",
            "DiagnosticCopyButtonPatch",
            "ManualSnapshotHotkeyPatch"
        ]);
    InstallOwner(
        install,
        "CoopGuard.toolkit.runtime",
        [
            "ToolkitAttachPatch",
            "ToolkitFramePatch",
            "ToolkitPanelHotkeyPatch",
            "ToolkitMainMenuPatch"
        ]);
    InstallOwner(
        install,
        "CoopGuard.toolkit.persistence",
        [
            "ToolkitMultiplayerSavePatch",
            "ToolkitMultiplayerLoadWarningPatch"
        ]);
    InstallOwner(
        install,
        "CoopGuard.toolkit.lobby",
        [
            "ToolkitStartLobbyPatch",
            "ToolkitLoadLobbyPatch",
            "ToolkitRunLobbyPatch",
            "ToolkitStartLobbyCleanupPatch",
            "ToolkitLoadLobbyCleanupPatch",
            "ToolkitStartReadyPatch",
            "ToolkitLoadReadyPatch",
            "ToolkitTurnReadyPatch",
            "ToolkitTurnUndoPatch",
            "ToolkitCombatStartPatch",
            "ToolkitCombatResetPatch"
        ]);
    InstallOwner(
        install,
        "CoopGuard.toolkit.join",
        [
            "ToolkitJoinBeginPatch",
            "ToolkitInitialInfoPatch",
            "ToolkitAttemptJoinPatch",
            "ToolkitAttemptLoadJoinPatch",
            "ToolkitAttemptRunningPatch"
        ]);
    InstallOwner(
        install,
        "CoopGuard.toolkit.protocol",
        ["ToolkitTransportSenderPatch"]);
    InstallOwner(
        install,
        "CoopGuard.toolkit.observers",
        [
            "ToolkitRemoteChoicePatch",
            "ToolkitMapVotePatch",
            "ToolkitMapResetPatch",
            "ToolkitActionObserverPatch"
        ]);
    InstallOwner(
        install,
        "CoopGuard.toolkit.forensics",
        ["ToolkitCheckpointObserverPatch"]);
    InstallOwner(
        install,
        "CoopGuard.toolkit.rng",
        [
            "ToolkitRngBoolPatch",
            "ToolkitRngIntMaxPatch",
            "ToolkitRngIntRangePatch",
            "ToolkitRngUIntRangePatch",
            "ToolkitRngULongPatch",
            "ToolkitRngULongRangePatch",
            "ToolkitRngFloatRangePatch",
            "ToolkitRngDoublePatch",
            "ToolkitRngDoubleRangePatch"
        ]);
    InstallOwner(
        install,
        "CoopGuard.toolkit.contributions",
        [
            "ToolkitContributionDamagePatch",
            "ToolkitContributionBlockPatch",
            "ToolkitContributionHealPatch"
        ]);

    foreach (string owner in owners)
    {
        string[] actual = TargetsFor(owner);
        string[] wanted = expected[owner]
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!actual.SequenceEqual(wanted))
        {
            throw new InvalidOperationException(
                $"Patch targets for '{owner}' differ.\nActual:\n"
                    + string.Join('\n', actual));
        }
    }

    try
    {
        install.Invoke(
            null,
            [
                "CoopGuard.smoke.optional",
                new Type[]
                {
                    typeof(WorkingOptionalPatch),
                    typeof(MissingOptionalPatch)
                }
            ]);
        throw new InvalidOperationException(
            "A missing optional target did not fail its install transaction.");
    }
    catch (TargetInvocationException ex) when (ex.InnerException != null)
    {
    }

    if (TargetsFor("CoopGuard.smoke.optional").Length != 0
        || owners.Any(owner => TargetsFor(owner).Length != expected[owner].Length))
    {
        throw new InvalidOperationException(
            "An optional patch failure changed Guard or left partial patches.\n"
                + "smoke="
                + string.Join(
                    ',',
                    TargetsFor("CoopGuard.smoke.optional"))
                + "\nchanged="
                + string.Join(
                    ',',
                    owners.Where(owner =>
                        TargetsFor(owner).Length
                            != expected[owner].Length)));
    }

    Console.WriteLine(
        $"Harmony owner/isolation smoke passed: {expected.Values.Sum(value => value.Length)} owner-target bindings, optional rollback contained.");
}
finally
{
    foreach (string owner in owners.Append("CoopGuard.smoke.optional"))
    {
        new Harmony(owner).UnpatchAll(owner);
    }
}

string[] TargetsFor(string owner) =>
    Harmony.GetAllPatchedMethods()
        .Where(method =>
            Harmony.GetPatchInfo(method)?.Owners.Contains(
                owner,
                StringComparer.Ordinal) == true)
        .Select(method => $"{method.DeclaringType?.FullName}.{method.Name}")
        .Order(StringComparer.Ordinal)
        .ToArray();

void InstallOwner(
    MethodInfo install,
    string owner,
    IReadOnlyList<string> patchNames)
{
    Type[] patchTypes = patchNames
        .Select(name => guard.GetType(
            "CoopGuard." + name,
            throwOnError: true)!)
        .ToArray();
    install.Invoke(null, [owner, patchTypes]);
}

[HarmonyPatch(typeof(string), "CoopGuardMissingOptionalTarget")]
internal static class MissingOptionalPatch
{
    private static void Prefix()
    {
    }
}

internal static class SmokeTarget
{
    public static void Probe()
    {
    }
}

[HarmonyPatch(typeof(SmokeTarget), nameof(SmokeTarget.Probe))]
internal static class WorkingOptionalPatch
{
    private static void Prefix()
    {
    }
}
