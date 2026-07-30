using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;

if (args.Length != 2)
{
    throw new ArgumentException(
        "Usage: HarmonySmoke <game-data-dir> <BetterCoop.dll>");
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
Type main = guard.GetType("BetterCoop.Main", throwOnError: true)!;
string[] owners =
[
    "BetterCoop.guard.sentinel",
    "BetterCoop.guard",
    "BetterCoop.diagnostics",
    "BetterCoop.toolkit.runtime",
    "BetterCoop.toolkit.persistence",
    "BetterCoop.toolkit.lobby",
    "BetterCoop.toolkit.join",
    "BetterCoop.toolkit.protocol",
    "BetterCoop.toolkit.observers",
    "BetterCoop.toolkit.forensics",
    "BetterCoop.toolkit.rng",
    "BetterCoop.toolkit.contributions"
];

Dictionary<string, string[]> expected = new(StringComparer.Ordinal)
{
    ["BetterCoop.guard.sentinel"] =
    [
        "MegaCrit.Sts2.Core.Modding.ModManager.GetGameplayRelevantModNameList"
    ],
    ["BetterCoop.guard"] =
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
    ["BetterCoop.diagnostics"] =
    [
        "MegaCrit.Sts2.Core.Nodes.CommonUi.NErrorPopup.Create",
        "MegaCrit.Sts2.Core.Nodes.CommonUi.NErrorPopup.Create",
        "MegaCrit.Sts2.Core.Nodes.CommonUi.NErrorPopup.OnReportBugButtonPressed",
        "MegaCrit.Sts2.Core.Nodes.CommonUi.NErrorPopup._Ready",
        "MegaCrit.Sts2.Core.Nodes.NGame._Input",
        "MegaCrit.Sts2.Core.Nodes.NGame.ReturnToMainMenuWithInternalError"
    ],
    ["BetterCoop.toolkit.runtime"] =
    [
        "MegaCrit.Sts2.Core.Nodes.CommonUi.NControllerManager._Process",
        "MegaCrit.Sts2.Core.Nodes.NGame._Input",
        "MegaCrit.Sts2.Core.Nodes.NGame._Ready",
        "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu._Ready"
    ],
    ["BetterCoop.toolkit.persistence"] =
    [
        "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMultiplayerSubmenu.StartLoad",
        "MegaCrit.Sts2.Core.Saves.Managers.RunSaveManager.SaveRun"
    ],
    ["BetterCoop.toolkit.lobby"] =
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
    ["BetterCoop.toolkit.join"] =
    [
        "MegaCrit.Sts2.Core.Multiplayer.Game.JoinFlow.AttemptJoin",
        "MegaCrit.Sts2.Core.Multiplayer.Game.JoinFlow.AttemptLoadJoin",
        "MegaCrit.Sts2.Core.Multiplayer.Game.JoinFlow.AttemptRejoin",
        "MegaCrit.Sts2.Core.Multiplayer.Game.JoinFlow.Begin",
        "MegaCrit.Sts2.Core.Multiplayer.Game.JoinFlow.HandleInitialGameInfoMessage"
    ],
    ["BetterCoop.toolkit.protocol"] =
    [
        "MegaCrit.Sts2.Core.Multiplayer.NetClientGameService.OnPacketReceived",
        "MegaCrit.Sts2.Core.Multiplayer.NetHostGameService.OnPacketReceived"
    ],
    ["BetterCoop.toolkit.observers"] =
    [
        "MegaCrit.Sts2.Core.GameActions.ActionExecutor..ctor",
        "MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceSynchronizer.WaitForRemoteChoice",
        "MegaCrit.Sts2.Core.Multiplayer.Game.MapSelectionSynchronizer.OnLocationChanged",
        "MegaCrit.Sts2.Core.Multiplayer.Game.MapSelectionSynchronizer.PlayerVotedForMapCoord"
    ],
    ["BetterCoop.toolkit.forensics"] =
    [
        "MegaCrit.Sts2.Core.Multiplayer.Game.ChecksumTracker..ctor"
    ],
    ["BetterCoop.toolkit.rng"] =
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
    ["BetterCoop.toolkit.contributions"] =
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
        "BetterCoop.GameplayModListPatch",
        throwOnError: true)!;
    sentinel.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static)!
        .Invoke(null, [new Harmony("BetterCoop.guard.sentinel")]);

    InstallOwner(
        install,
        "BetterCoop.guard",
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
        "BetterCoop.diagnostics",
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
        "BetterCoop.toolkit.runtime",
        [
            "ToolkitAttachPatch",
            "ToolkitFramePatch",
            "ToolkitPanelHotkeyPatch",
            "ToolkitMainMenuPatch"
        ]);
    InstallOwner(
        install,
        "BetterCoop.toolkit.persistence",
        [
            "ToolkitMultiplayerSavePatch",
            "ToolkitMultiplayerLoadWarningPatch"
        ]);
    InstallOwner(
        install,
        "BetterCoop.toolkit.lobby",
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
        "BetterCoop.toolkit.join",
        [
            "ToolkitJoinBeginPatch",
            "ToolkitInitialInfoPatch",
            "ToolkitAttemptJoinPatch",
            "ToolkitAttemptLoadJoinPatch",
            "ToolkitAttemptRunningPatch"
        ]);
    InstallOwner(
        install,
        "BetterCoop.toolkit.protocol",
        ["ToolkitTransportSenderPatch"]);
    InstallOwner(
        install,
        "BetterCoop.toolkit.observers",
        [
            "ToolkitRemoteChoicePatch",
            "ToolkitMapVotePatch",
            "ToolkitMapResetPatch",
            "ToolkitActionObserverPatch"
        ]);
    InstallOwner(
        install,
        "BetterCoop.toolkit.forensics",
        ["ToolkitCheckpointObserverPatch"]);
    InstallOwner(
        install,
        "BetterCoop.toolkit.rng",
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
        "BetterCoop.toolkit.contributions",
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
                "BetterCoop.smoke.optional",
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

    if (TargetsFor("BetterCoop.smoke.optional").Length != 0
        || owners.Any(owner => TargetsFor(owner).Length != expected[owner].Length))
    {
        throw new InvalidOperationException(
            "An optional patch failure changed Guard or left partial patches.\n"
                + "smoke="
                + string.Join(
                    ',',
                    TargetsFor("BetterCoop.smoke.optional"))
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
    foreach (string owner in owners.Append("BetterCoop.smoke.optional"))
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
            "BetterCoop." + name,
            throwOnError: true)!)
        .ToArray();
    install.Invoke(null, [owner, patchTypes]);
}

[HarmonyPatch(typeof(string), "BetterCoopMissingOptionalTarget")]
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
