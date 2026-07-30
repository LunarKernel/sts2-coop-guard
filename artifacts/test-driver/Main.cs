using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using System.Security.Cryptography;
using System.Text;
using System.Reflection;

namespace CoopGuardTestDriver;

[ModInitializer(nameof(Initialize))]
public static class Main
{
    private const string ModId = "CoopGuardTestDriver";
    private static readonly Logger Log = new(ModId, LogType.Network);

    internal static int ExpectedPlayers { get; private set; }
    internal static bool InjectPackageChangeAtInitialInfo { get; private set; }
    internal static bool DisconnectClientAfterFreshRun { get; private set; }
    internal static bool VerifyToolkit { get; private set; }
    internal static bool VerifyProtocol { get; private set; }
    internal static bool VerifyCollaboration { get; private set; }
    internal static bool VerifySettings { get; private set; }
    internal static bool LateSettingsChange { get; private set; }
    internal static string? DiagnosticReason { get; private set; }
    internal static string? ExpectedMismatchMod { get; private set; }
    internal static string? FaultId { get; private set; }
    internal static ushort TestPort { get; private set; }
    private static int _toolkitScheduled;
    private static int _protocolScheduled;

    public static void Initialize()
    {
        bool hasPlayerCount = int.TryParse(
                CommandLineHelper.GetValue("cgtest-players"),
                out int expectedPlayers)
            && expectedPlayers >= 2;
        DiagnosticReason = CommandLineHelper.GetValue("cgtest-diagnosis");
        ExpectedMismatchMod = CommandLineHelper.GetValue(
            "cgtest-expect-mismatch");
        VerifyToolkit = CommandLineHelper.GetValue("cgtest-toolkit") == "1";
        VerifyProtocol = CommandLineHelper.GetValue("cgtest-protocol") == "1";
        VerifyCollaboration =
            CommandLineHelper.GetValue("cgtest-collaboration") == "1";
        VerifySettings = VerifyToolkit
            || CommandLineHelper.GetValue("cgtest-settings") == "1";
        LateSettingsChange =
            CommandLineHelper.GetValue("cgtest-late-settings") == "1";
        FaultId = CommandLineHelper.GetValue("cgtest-fault");
        if (!ushort.TryParse(
                CommandLineHelper.GetValue("cgtest-port"),
                out ushort testPort)
            || testPort < 1024)
        {
            testPort = 33771;
        }

        TestPort = testPort;
        ValidateFaultRequest();
        if (LateSettingsChange && !VerifySettings)
        {
            throw new InvalidOperationException(
                "Late-settings fixture requires --cgtest-settings=1.");
        }

        if (VerifySettings)
        {
            byte[] settingsDigest = SHA256.HashData(
                Encoding.UTF8.GetBytes("mode=stable\nplayers=2"));
            if (!global::CoopGuard.CoopGuardApi.PublishDeterministicSettings(
                    ModId,
                    1,
                    settingsDigest)
                || global::CoopGuard.CoopGuardApi.PublishDeterministicSettings(
                    "../" + ModId,
                    1,
                    settingsDigest))
            {
                throw new InvalidOperationException(
                    "Deterministic-settings push contract rejected a valid publisher or accepted traversal.");
            }
        }

        if (!hasPlayerCount
            && string.IsNullOrEmpty(DiagnosticReason)
            && string.IsNullOrEmpty(ExpectedMismatchMod)
            && !VerifyToolkit
            && !VerifyProtocol
            && !VerifyCollaboration
            && !VerifySettings
            && string.IsNullOrEmpty(FaultId))
        {
            return;
        }

        ExpectedPlayers = hasPlayerCount ? expectedPlayers : 0;
        InjectPackageChangeAtInitialInfo =
            CommandLineHelper.GetValue("cgtest-toctou") == "1";
        DisconnectClientAfterFreshRun =
            CommandLineHelper.GetValue("cgtest-disconnect") == "1";
        new Harmony(ModId).PatchAll();
        Log.Info(
            $"Enabled headless driver; players={ExpectedPlayers}, diagnosis={DiagnosticReason ?? "off"}, mismatch={ExpectedMismatchMod ?? "off"}, toolkit={VerifyToolkit}, protocol={VerifyProtocol}, collaboration={VerifyCollaboration}, settings={VerifySettings}.");
    }

    private static void ValidateFaultRequest()
    {
        if (string.IsNullOrEmpty(FaultId))
        {
            return;
        }

#if !DEBUG
        throw new InvalidOperationException(
            "F8 fault injection is absent from Release test-driver builds.");
#else
        if (!string.Equals(
                FaultId,
                "diagnostics-handler-throw-once",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Unknown F8 fault ID was refused.");
        }

        string? rootValue = CommandLineHelper.GetValue(
            "cgtest-isolated-root");
        if (string.IsNullOrWhiteSpace(rootValue))
        {
            throw new InvalidOperationException(
                "F8 requires --cgtest-isolated-root.");
        }

        string root = Path.GetFullPath(rootValue);
        string? appDataValue = Environment.GetEnvironmentVariable("APPDATA");
        if (string.IsNullOrWhiteSpace(appDataValue))
        {
            throw new InvalidOperationException(
                "F8 requires an isolated APPDATA environment.");
        }

        string appData = Path.GetFullPath(appDataValue);
        string relative = Path.GetRelativePath(root, appData);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
            || !File.Exists(
                Path.Combine(root, ".coopguard-isolated-test")))
        {
            throw new InvalidOperationException(
                "F8 refused a live or unmarked profile target.");
        }

        Log.Info(
            "F8_FAULT_ARMED id=diagnostics-handler-throw-once "
            + "target=ToolkitDiagnosticsRuntime.HandleMessageCore");
#endif
    }

#if DEBUG
    internal static void LogFaultInjected() =>
        Log.Info(
            "F8_FAULT_INJECTED id=diagnostics-handler-throw-once "
            + "target=ToolkitDiagnosticsRuntime.HandleMessageCore count=1");
#endif

    internal static void TryPublishLateSettingsChange()
    {
        if (!LateSettingsChange)
        {
            return;
        }

        LateSettingsChange = false;
        byte[] changedDigest = SHA256.HashData(
            Encoding.UTF8.GetBytes("mode=changed\nplayers=2"));
        if (global::CoopGuard.CoopGuardApi.PublishDeterministicSettings(
                ModId,
                1,
                changedDigest))
        {
            throw new InvalidOperationException(
                "A changed post-freeze settings digest was accepted.");
        }

        Log.Info("SETTINGS_LATE_CHANGE_REJECTED restart-required");
    }

    internal static void TryVerifyToolkit(NGame game)
    {
        if (!VerifyToolkit)
        {
            return;
        }

        VerifyToolkit = false;
        Godot.Node node = game.GetNodeOrNull("CoopGuardToolkit")
            ?? throw new InvalidOperationException(
                "Toolkit node was not attached to NGame.");
        Type runtime = Type.GetType(
                "CoopGuard.ToolkitRuntime, CoopGuard",
                throwOnError: true)
            ?? throw new InvalidOperationException(
                "Could not find CoopGuard Toolkit runtime.");
        string hud = (string?)AccessTools.Method(runtime, "HudText").Invoke(
                null,
                [false])
            ?? string.Empty;
        string overview = (string?)AccessTools.Method(
                runtime,
                "BuildOverview").Invoke(
                null,
                [false])
            ?? string.Empty;
        if (!hud.Contains(
                "CoopGuard Multiplayer Cockpit",
                StringComparison.Ordinal)
            || (!hud.Contains("State:", StringComparison.Ordinal)
                && !hud.Contains(
                    "Not in a connected multiplayer session",
                    StringComparison.Ordinal))
            || !overview.Contains("Timeline", StringComparison.Ordinal)
            || !overview.Contains("Report history:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Toolkit cockpit omitted required local observability fields.");
        }

        string[] forbidden =
        [
            "76561198824432109",
            "[2001:db8::1]",
            "Authorization:",
            "Bearer "
        ];
        if (forbidden.Any(value =>
                hud.Contains(value, StringComparison.Ordinal)
                || overview.Contains(value, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Toolkit cockpit leaked a raw privacy probe.");
        }

        if (node.GetType().FullName != "CoopGuard.ToolkitNode")
        {
            throw new InvalidOperationException(
                $"Unexpected Toolkit node type: {node.GetType().FullName}.");
        }

        string[] requiredControls =
        [
            "_scroll",
            "_historySelect",
            "_viewReportButton",
            "_copyReportButton",
            "_markReportAButton",
            "_compareReportsButton",
            "_deleteReportButton",
            "_clearReportsButton",
            "_saveEnvironmentButton",
            "_copyEnvironmentButton",
            "_compareEnvironmentButton",
            "_modDoctorButton",
            "_bisectPlanButton",
            "_harmonyReportButton",
            "_knownIssuesButton",
            "_quickStatusSelect",
            "_sendQuickStatusButton",
            "_muteQuickStatusButton",
            "_handConsentButton",
            "_forensicsConsentButton",
            "_contributionsButton",
            "_contributionSharingButton",
            "_soundButton",
            "_soundVolumeButton",
            "_readyCueButton",
            "_networkCueButton",
            "_rejoinCueButton",
            "_resultCueButton",
            "_uiScaleButton",
            "_reducedMotionButton",
            "_highContrastButton",
            "_acknowledgeButton"
        ];
        foreach (string fieldName in requiredControls)
        {
            object? control = AccessTools.Field(node.GetType(), fieldName)
                .GetValue(node);
            if (control is not Godot.Control godotControl
                || (control is Godot.BaseButton button
                    && button.FocusMode
                        != Godot.Control.FocusModeEnum.All))
            {
                throw new InvalidOperationException(
                    $"Toolkit accessibility control '{fieldName}' was unavailable or unfocusable.");
            }
        }

        long nextUiTicks = (long?)AccessTools.Field(
                node.GetType(),
                "_nextUiTicks")
            .GetValue(node)
            ?? 0;
        if (nextUiTicks <= 0)
        {
            throw new InvalidOperationException(
                "Toolkit frame driver did not refresh the cockpit.");
        }

        AccessTools.Method(node.GetType(), "SetControlMode").Invoke(
            node,
            [true]);
        object preferences = AccessTools.Property(runtime, "Preferences")
            .GetValue(null)
            ?? throw new InvalidOperationException(
                "Toolkit preferences were unavailable.");
        for (int attempt = 0;
             attempt < 5
             && (float)AccessTools.Property(
                     preferences.GetType(),
                     "UiScale").GetValue(preferences)! < 1.99f;
             attempt++)
        {
            AccessTools.Method(runtime, "CycleUiScale").Invoke(null, null);
            preferences = AccessTools.Property(runtime, "Preferences")
                .GetValue(null)!;
        }

        AccessTools.Method(runtime, "ToggleSound").Invoke(null, null);
        AccessTools.Method(runtime, "ToggleSound").Invoke(null, null);
        AccessTools.Method(runtime, "ToggleReducedMotion").Invoke(null, null);
        AccessTools.Method(runtime, "ToggleReducedMotion").Invoke(null, null);
        AccessTools.Method(runtime, "ToggleHighContrast").Invoke(null, null);
        AccessTools.Method(runtime, "ToggleHighContrast").Invoke(null, null);
        AccessTools.Method(node.GetType(), "Refresh").Invoke(node, null);
        Godot.Button soundButton = (Godot.Button)AccessTools.Field(
                node.GetType(),
                "_soundButton").GetValue(node)!;
        Godot.Theme localTheme = (Godot.Theme)AccessTools.Field(
                node.GetType(),
                "_localTheme").GetValue(node)!;
        string preferencesPath = Godot.ProjectSettings.GlobalizePath(
            "user://CoopGuard/preferences.json");
        if (!soundButton.Visible
            || localTheme.DefaultFontSize < 36
            || !File.Exists(preferencesPath)
            || new FileInfo(preferencesPath).Length > 4096
            || File.Exists(preferencesPath + ".tmp"))
        {
            throw new InvalidOperationException(
                "Toolkit preference controls, 200% scale or atomic persistence failed: "
                + $"visible={soundButton.Visible}; "
                + $"font={localTheme.DefaultFontSize}; "
                + $"exists={File.Exists(preferencesPath)}; "
                + $"bytes={(File.Exists(preferencesPath) ? new FileInfo(preferencesPath).Length : -1)}; "
                + $"temp={File.Exists(preferencesPath + ".tmp")}.");
        }
        AccessTools.Method(node.GetType(), "SetControlMode").Invoke(
            node,
            [false]);
        Godot.Button originalFocus = new()
        {
            Text = "focus-probe",
            FocusMode = Godot.Control.FocusModeEnum.All
        };
        game.AddChild(originalFocus);
        originalFocus.GrabFocus();
        AccessTools.Method(runtime, "TogglePanel").Invoke(null, [game]);
        bool openedFocus =
            game.GetViewport().GuiGetFocusOwner() == soundButton;
        AccessTools.Method(runtime, "TogglePanel").Invoke(null, [game]);
        bool restoredFocus =
            game.GetViewport().GuiGetFocusOwner() == originalFocus;
        originalFocus.QueueFree();
        if (!openedFocus || !restoredFocus || soundButton.Visible)
        {
            throw new InvalidOperationException(
                $"Toolkit focus lifecycle failed: opened={openedFocus}; restored={restoredFocus}; closedVisible={soundButton.Visible}.");
        }

        string environment = (string?)AccessTools.Method(
                runtime,
                "CreateEnvironmentLockfile").Invoke(null, null)
            ?? string.Empty;
        Type doctorType = AccessTools.TypeByName(
                "CoopGuard.LocalModDoctor")
            ?? throw new InvalidOperationException(
                "LocalModDoctor was not found.");
        object dependencyRecords = AccessTools.Method(
                doctorType,
                "CurrentDependencyRecords").Invoke(null, [null])
            ?? throw new InvalidOperationException(
                "Dependency records were unavailable.");
        Type plannerType = AccessTools.TypeByName(
                "CoopGuard.ModBisectPlanner")
            ?? throw new InvalidOperationException(
                "ModBisectPlanner was not found.");
        object bisect = AccessTools.Method(
                plannerType,
                "Create").Invoke(
                null,
                [dependencyRecords, new string[] { ModId }])
            ?? throw new InvalidOperationException(
                "A/B plan was unavailable.");
        bool bisectAvailable = (bool)AccessTools.Property(
                bisect.GetType(),
                "Available").GetValue(bisect)!;
        string bisectPlan = (string?)AccessTools.Property(
                bisect.GetType(),
                "Text").GetValue(bisect)
            ?? string.Empty;
        string environmentAgain = (string?)AccessTools.Method(
                runtime,
                "CreateEnvironmentLockfile").Invoke(null, null)
            ?? string.Empty;
        if (environment != environmentAgain
            || System.Text.Encoding.UTF8.GetByteCount(environment)
                > 512 * 1024
            || environment.Contains(":\\", StringComparison.Ordinal)
            || !environment.Contains(
                "\"GuardProtocol\":4",
                StringComparison.Ordinal)
            || !environment.Contains(
                "\"Categories\"",
                StringComparison.Ordinal)
            || !bisectPlan.Contains(
                "Read-only",
                StringComparison.Ordinal)
            || !bisectAvailable)
        {
            throw new InvalidOperationException(
                "Environment lockfile or dependency-aware A/B plan was unsafe or incomplete.");
        }

        Type codec = AccessTools.TypeByName(
                "CoopGuard.EnvironmentLockCodec")
            ?? throw new InvalidOperationException(
                "EnvironmentLockCodec was not found.");
        object?[] importedArguments =
        [
            environment,
            null,
            string.Empty
        ];
        object?[] currentArguments =
        [
            environmentAgain,
            null,
            string.Empty
        ];
        bool parsedImported = (bool?)AccessTools.Method(
                codec,
                "TryParse").Invoke(null, importedArguments)
            ?? false;
        bool parsedCurrent = (bool?)AccessTools.Method(
                codec,
                "TryParse").Invoke(null, currentArguments)
            ?? false;
        int differenceCount = parsedImported && parsedCurrent
            ? CountItems(AccessTools.Method(codec, "Compare").Invoke(
                null,
                [currentArguments[1], importedArguments[1]]))
            : -1;
        string comparison = differenceCount == 0
            ? "Environment confirmed identical."
            : "Environment comparison failed.";
        object?[] parseArguments =
        [
            "{\"Schema\":999}",
            null,
            string.Empty
        ];
        bool acceptedInvalidSchema = (bool?)AccessTools.Method(
                codec,
                "TryParse").Invoke(null, parseArguments)
            ?? true;
        bool acceptedTruncated = AcceptedLockfile(
            codec,
            environment[..^1]);
        bool acceptedOversized = AcceptedLockfile(
            codec,
            new string('x', 512 * 1024 + 1));
        bool acceptedUnknownCategory = AcceptedLockfile(
            codec,
            environment.Replace(
                "\"manifest\"",
                "\"unknown-category\"",
                StringComparison.Ordinal));
        string doctor = (string?)AccessTools.Method(
                AccessTools.TypeByName("CoopGuard.LocalModDoctor"),
                "BuildReport").Invoke(null, null)
            ?? string.Empty;
        string harmony = (string?)AccessTools.Method(
                AccessTools.TypeByName("CoopGuard.HarmonyConflictReport"),
                "Build").Invoke(null, null)
            ?? string.Empty;
        string knownIssues = (string?)AccessTools.Method(
                AccessTools.TypeByName("CoopGuard.KnownIssueCatalog"),
                "BuildReport").Invoke(null, [NGame.GetGameVersion()])
            ?? string.Empty;
        Type catalog = AccessTools.TypeByName(
                "CoopGuard.KnownIssueCatalog")
            ?? throw new InvalidOperationException(
                "KnownIssueCatalog was not found.");
        Type queryType = AccessTools.TypeByName(
                "CoopGuard.KnownIssueQuery")
            ?? throw new InvalidOperationException(
                "KnownIssueQuery was not found.");
        object exactQuery = Activator.CreateInstance(
                queryType,
                [NGame.GetGameVersion(), null, null, "Timeout"])
            ?? throw new InvalidOperationException(
                "Could not create an exact known-issue query.");
        object nearQuery = Activator.CreateInstance(
                queryType,
                ["v0.109.10", null, null, "Timeout"])
            ?? throw new InvalidOperationException(
                "Could not create a near-miss known-issue query.");
        int exactRuleCount = CountItems(
            AccessTools.Method(catalog, "Match").Invoke(
                null,
                [exactQuery]));
        int nearRuleCount = CountItems(
            AccessTools.Method(catalog, "Match").Invoke(
                null,
                [nearQuery]));
        if (!comparison.Contains(
                "confirmed identical",
                StringComparison.Ordinal)
            || acceptedInvalidSchema
            || acceptedTruncated
            || acceptedOversized
            || acceptedUnknownCategory
            || !environment.Contains(
                "\"settings\"",
                StringComparison.Ordinal)
            || environment.Contains(
                "mode=stable",
                StringComparison.Ordinal)
            || !doctor.Contains("Read-only", StringComparison.Ordinal)
            || doctor.Contains(":\\", StringComparison.Ordinal)
            || !harmony.Contains(
                "did not unpatch",
                StringComparison.Ordinal)
            || !knownIssues.Contains(
                "Local static data only",
                StringComparison.Ordinal)
            || exactRuleCount != 1
            || nearRuleCount != 0)
        {
            throw new InvalidOperationException(
                "A local environment-governance contract failed: "
                + $"comparison={comparison}; invalidSchema={acceptedInvalidSchema}; "
                + $"truncated={acceptedTruncated}; oversized={acceptedOversized}; "
                + $"unknownCategory={acceptedUnknownCategory}; "
                + $"hasSettings={environment.Contains("\"settings\"", StringComparison.Ordinal)}; "
                + $"rawSetting={environment.Contains("mode=stable", StringComparison.Ordinal)}; "
                + $"doctorReadOnly={doctor.Contains("Read-only", StringComparison.Ordinal)}; "
                + $"doctorPath={doctor.Contains(":\\", StringComparison.Ordinal)}; "
                + $"harmonyReadOnly={harmony.Contains("did not unpatch", StringComparison.Ordinal)}; "
                + $"knownStatic={knownIssues.Contains("Local static data only", StringComparison.Ordinal)}; "
                + $"exactRules={exactRuleCount}; nearRules={nearRuleCount}.");
        }

        Log.Info(
            $"TOOLKIT_COCKPIT_OK hudBytes={hud.Length} overviewBytes={overview.Length} environmentBytes={environment.Length}");
        game.GetTree().Quit();
    }

    private static bool AcceptedLockfile(Type codec, string json)
    {
        object?[] arguments = [json, null, string.Empty];
        return (bool?)AccessTools.Method(codec, "TryParse")
                .Invoke(null, arguments)
            ?? true;
    }

    private static int CountItems(object? value)
    {
        int count = 0;
        if (value is not System.Collections.IEnumerable items)
        {
            return -1;
        }

        foreach (object? _ in items)
        {
            count++;
        }

        return count;
    }

    internal static async void ScheduleToolkitVerification()
    {
        NGame? game = NGame.Instance;
        if (!VerifyToolkit
            || game == null
            || Interlocked.Exchange(ref _toolkitScheduled, 1) != 0)
        {
            return;
        }

        try
        {
            for (int frame = 0; frame < 5; frame++)
            {
                await game.ToSignal(
                    game.GetTree(),
                    Godot.SceneTree.SignalName.ProcessFrame);
            }

            TryVerifyToolkit(game);
        }
        catch (Exception ex)
        {
            Log.Error($"TOOLKIT_COCKPIT_FAILED {ex}");
            game.GetTree().Quit(1);
        }
    }

    internal static void TryReady(StartRunLobby? lobby)
    {
        if (lobby is null
            || !lobby.NetService.IsConnected
            || !lobby.NetService.Type.IsMultiplayer()
            || lobby.Players.Count < ExpectedPlayers
            || lobby.LocalPlayer.isReady)
        {
            return;
        }

        ScheduleProtocolVerification();
        Log.Info($"fresh auto-ready id={lobby.NetService.NetId} peers={lobby.Players.Count}");
        lobby.SetReady(ready: true);
    }

    internal static void TryReady(LoadRunLobby? lobby)
    {
        if (lobby is null
            || !lobby.NetService.IsConnected
            || !lobby.NetService.Type.IsMultiplayer()
            || lobby.ConnectedPlayerIds.Count < ExpectedPlayers
            || lobby.IsPlayerReady(lobby.NetService.NetId))
        {
            return;
        }

        ScheduleProtocolVerification();
        Log.Info($"load auto-ready id={lobby.NetService.NetId} peers={lobby.ConnectedPlayerIds.Count}");
        lobby.SetReady(ready: true);
    }

    private static async void ScheduleProtocolVerification()
    {
        NGame? game = NGame.Instance;
        if (!VerifyProtocol
            || game == null
            || Interlocked.Exchange(ref _protocolScheduled, 1) != 0)
        {
            return;
        }

        try
        {
            Type protocol = Type.GetType(
                    "CoopGuard.ToolkitDiagnosticsRuntime, CoopGuard",
                    throwOnError: true)
                ?? throw new InvalidOperationException(
                    "Could not find diagnostics runtime.");
            string session = "unavailable";
            string matrix = string.Empty;
            string hud = string.Empty;
            string previousSession = string.Empty;
            int matrixRows = 0;
            int stableSamples = 0;
            MethodInfo canSendQuickStatus = AccessTools.Method(
                protocol,
                "CanSendQuickStatus");
            for (int second = 0; second < 30; second++)
            {
                await game.ToSignal(
                    game.GetTree().CreateTimer(1),
                    Godot.SceneTreeTimer.SignalName.Timeout);
                session = (string?)AccessTools.Method(
                        protocol,
                        "FullSessionId").Invoke(null, null)
                    ?? "unavailable";
                matrix = (string?)AccessTools.Method(
                        protocol,
                        "BuildMatrix").Invoke(null, [false])
                    ?? string.Empty;
                hud = (string?)AccessTools.Method(
                        protocol,
                        "HudStatus").Invoke(null, [false])
                    ?? string.Empty;
                matrixRows = Enumerable.Range(1, ExpectedPlayers)
                    .Count(index => matrix.Contains(
                        $"P{index}:",
                        StringComparison.Ordinal));
                bool ready = session.Length == 32
                    && session != "unavailable"
                    && matrixRows == ExpectedPlayers
                    && matrix.Contains("supported", StringComparison.Ordinal)
                    && (bool)canSendQuickStatus.Invoke(null, null)!
                    && !hud.Contains(
                        "environment code unavailable",
                        StringComparison.Ordinal);
                stableSamples = ready && session == previousSession
                    ? stableSamples + 1
                    : ready
                        ? 1
                        : 0;
                previousSession = session;
                if (stableSamples == 2)
                {
                    break;
                }
            }

            if (stableSamples != 2)
            {
                throw new InvalidOperationException(
                    $"Protocol negotiation incomplete: sessionLength={session.Length}; matrix={matrix}; hud={hud}.");
            }

            string token = Convert.ToHexString(
                    SHA256.HashData(
                        Encoding.UTF8.GetBytes(
                            "protocol-fixture\n" + session)))
                [..16];
            Log.Info(
                $"TOOLKIT_PROTOCOL_OK sessionToken={token} matrixRows={matrixRows}");
            if (VerifyCollaboration)
            {
                MethodInfo sendQuickStatus = AccessTools.Method(
                    protocol,
                    "SendQuickStatus");
                Type quickStatusType =
                    sendQuickStatus.GetParameters()[0].ParameterType;
                if (RunManager.Instance.NetService.Type
                    == NetGameType.Host)
                {
                    string sendResult = (string?)sendQuickStatus.Invoke(
                            null,
                            [Enum.ToObject(quickStatusType, 1)])
                        ?? string.Empty;
                    if (sendResult.Contains(
                            "unavailable",
                            StringComparison.OrdinalIgnoreCase)
                        || sendResult.Contains(
                            "not sent",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "C1 all-peer capability gate rejected a fully supported session: "
                            + sendResult);
                    }
                }

                string recentStatus = string.Empty;
                for (int second = 0; second < 5; second++)
                {
                    await game.ToSignal(
                        game.GetTree().CreateTimer(1),
                        Godot.SceneTreeTimer.SignalName.Timeout);
                    recentStatus = (string?)AccessTools.Method(
                            protocol,
                            "RecentQuickStatuses").Invoke(
                            null,
                            [false])
                        ?? string.Empty;
                    if (recentStatus.Contains(
                            "Please wait",
                            StringComparison.Ordinal))
                    {
                        break;
                    }
                }

                if (!recentStatus.Contains(
                        "Please wait",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "C1 fixed status did not converge on every peer.");
                }

                AccessTools.Method(
                    protocol,
                    "ToggleHandSharingConsent").Invoke(null, null);
                AccessTools.Method(
                    protocol,
                    "ToggleForensicsConsent").Invoke(null, null);
                AccessTools.Method(
                    protocol,
                    "ToggleContributions").Invoke(null, null);
                AccessTools.Method(
                    protocol,
                    "ToggleContributionSharing").Invoke(null, null);
                string collaboration = string.Empty;
                for (int second = 0; second < 30; second++)
                {
                    await game.ToSignal(
                        game.GetTree().CreateTimer(1),
                        Godot.SceneTreeTimer.SignalName.Timeout);
                    collaboration = (string?)AccessTools.Method(
                            protocol,
                            "CollaborationStatus").Invoke(
                            null,
                            [false])
                        ?? string.Empty;
                    if (collaboration.Contains(
                            "Hand sharing active",
                            StringComparison.Ordinal)
                        && collaboration.Contains(
                            "Forensics: active",
                            StringComparison.Ordinal)
                        && collaboration.Contains(
                            "sharing separately enabled",
                            StringComparison.Ordinal))
                    {
                        byte[] stateDigest = SHA256.HashData(
                            Encoding.UTF8.GetBytes(
                                "optional-state-fixture-v1"));
                        if (global::CoopGuard.CoopGuardApi.PublishStateDigest(
                                "../" + ModId,
                                1,
                                1,
                                stateDigest)
                            || !global::CoopGuard.CoopGuardApi.PublishStateDigest(
                                ModId,
                                1,
                                1,
                                stateDigest)
                            || !global::CoopGuard.CoopGuardApi.RecordPublicDiagnosticEvent(
                                ModId,
                                7)
                            || global::CoopGuard.CoopGuardApi.RecordPublicDiagnosticEvent(
                                ModId,
                                8))
                        {
                            throw new InvalidOperationException(
                                "Optional Mod-author API identity, valid push or 2/s rate contract failed.");
                        }

                        Log.Info("TOOLKIT_COLLABORATION_OK unanimous=1 contributionSharing=1 f6=1");
                        return;
                    }
                }

                throw new InvalidOperationException(
                    "Collaboration consent did not converge: "
                    + collaboration);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"TOOLKIT_PROTOCOL_FAILED {ex}");
            game.GetTree().Quit(1);
        }
    }

    internal static async Task<RunState> SaveAfterFreshRun(
        Task<RunState> startTask)
    {
        RunState state = await startTask;
        RunManager run = RunManager.Instance;
        if (ExpectedPlayers >= 2
            && run.IsInProgress
            && run.ShouldSave
            && run.NetService.Type == NetGameType.Host)
        {
            Log.Info("Requesting native multiplayer save after embark.");
            await SaveManager.Instance.SaveRun(
                null,
                saveProgress: false);
        }

        if (DisconnectClientAfterFreshRun
            && run.NetService.Type == NetGameType.Client)
        {
            Log.Info("Requesting native client disconnect after embark.");
            run.NetService.Disconnect(NetError.Quit);
            await Task.Delay(250);
            if (Godot.Engine.GetMainLoop() is Godot.SceneTree tree)
            {
                tree.Quit();
            }
        }

        return state;
    }

    internal static void TryInjectPackageChangeAtInitialInfo()
    {
        if (!InjectPackageChangeAtInitialInfo)
        {
            return;
        }

        InjectPackageChangeAtInitialInfo = false;
        Mod guard = ModManager.Mods.Single(
            mod => mod.manifest?.id == "CoopGuard");
        File.WriteAllText(
            Path.Combine(guard.path, "cgtest-toctou.marker"),
            "Injected after client preflight.");
        Log.Info("Injected a package change immediately before CoopGuard's initial-info gate.");
    }

    internal static void VerifyDiagnosticPopup()
    {
        Log.Error(
            "privacy-probe 76561198824432109 [2001:db8::1]:443 "
                + "Authorization: Bearer bearer-secret "
                + "\"password\":\"json-secret\" "
                + "\\\\server\\share\\save.dat "
                + "CoopGuard-package-v4-aaaaaaaa "
                + new string('b', 64));
        NErrorPopup? popup;
        string reasonLabel;
        bool alreadyAdded = false;
        if (string.Equals(
                DiagnosticReason,
                "ManualHealth",
                StringComparison.Ordinal))
        {
            DiagnosticReason = null;
            Type reporter = Type.GetType(
                    "CoopGuard.FatalIncidentReporter, CoopGuard",
                    throwOnError: true)
                ?? throw new InvalidOperationException(
                    "Could not find CoopGuard incident reporter.");
            AccessTools.Method(reporter, "ShowManualSnapshot").Invoke(
                null,
                null);
            popup = NModalContainer.Instance?.OpenModal as NErrorPopup;
            reasonLabel = "ManualHealth";
            alreadyAdded = true;
        }
        else if (string.Equals(
                DiagnosticReason,
                "InternalMissingMethod",
                StringComparison.Ordinal))
        {
            DiagnosticReason = null;
            Type reporter = Type.GetType(
                    "CoopGuard.FatalIncidentReporter, CoopGuard",
                    throwOnError: true)
                ?? throw new InvalidOperationException(
                    "Could not find CoopGuard incident reporter.");
            AccessTools.Method(reporter, "RememberInternalError").Invoke(
                null,
                [new MissingMethodException("RemovedGameApi")]);
            popup = NErrorPopup.Create(
                new LocString("main_menu_ui", "INTERNAL_ERROR.title"),
                new LocString("main_menu_ui", "INTERNAL_ERROR.description"),
                null,
                showReportBugButton: true);
            reasonLabel = "InternalMissingMethod";
        }
        else if (Enum.TryParse(DiagnosticReason, out NetError reason))
        {
            DiagnosticReason = null;
            popup = NErrorPopup.Create(
                new NetErrorInfo(reason, selfInitiated: false));
            reasonLabel = reason.ToString();
        }
        else
        {
            return;
        }

        NErrorPopup verifiedPopup = popup
            ?? throw new InvalidOperationException(
                "Diagnostic popup creation returned null.");
        string title = (string?)AccessTools.Field(
                typeof(NErrorPopup),
                "_title")
            .GetValue(verifiedPopup)
            ?? throw new InvalidOperationException(
                "Diagnostic popup has no title.");
        string body = (string?)AccessTools.Field(
                typeof(NErrorPopup),
                "_body")
            .GetValue(verifiedPopup)
            ?? throw new InvalidOperationException(
                "Diagnostic popup has no body.");
        bool chinese = body.Contains("根因", StringComparison.Ordinal);
        bool english = body.Contains("Root cause", StringComparison.Ordinal);
        if (!chinese && !english)
        {
            throw new InvalidOperationException(
                "Diagnostic popup was not bilingual-aware.");
        }

        NModalContainer container = NModalContainer.Instance
            ?? throw new InvalidOperationException(
                "Native modal container is unavailable.");
        if (!alreadyAdded)
        {
            container.Add(verifiedPopup);
        }

        if (!ReferenceEquals(container.OpenModal, verifiedPopup))
        {
            throw new InvalidOperationException(
                "Diagnostic popup was not added to the native modal container.");
        }

        NPopupYesNoButton copyButton = verifiedPopup
            .GetNode<NVerticalPopup>("VerticalPopup")
            .YesButton;
        Godot.Label copyLabel = (Godot.Label?)AccessTools.Field(
                typeof(NPopupYesNoButton),
                "_label")
            .GetValue(copyButton)
            ?? throw new InvalidOperationException(
                "The native diagnosis button has no label.");
        string expectedCopyLabel = chinese ? "复制诊断" : "Copy diagnosis";
        if (!string.Equals(
                copyLabel.Text,
                expectedCopyLabel,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected diagnosis button '{expectedCopyLabel}', got '{copyLabel.Text}'.");
        }

        AccessTools.Method(typeof(NErrorPopup), "OnReportBugButtonPressed")
            .Invoke(
                verifiedPopup,
                [copyButton]);
        string copied = Godot.DisplayServer.ClipboardGet();
        if (!copied.Contains(
                "CoopGuard diagnostic report",
                StringComparison.Ordinal)
            || !copied.Contains(
                "CoopGuard version: 0.3.3",
                StringComparison.Ordinal)
            || !copied.Contains(
                "Report format: 2",
                StringComparison.Ordinal)
            || !copied.Contains(
                body.Contains("CG-HEALTHY", StringComparison.Ordinal)
                    ? "[CG-HEALTHY]"
                    : "[CG-",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The native diagnosis button did not copy a CoopGuard report.");
        }

        string[] forbidden =
        [
            "76561198824432109",
            "2001:db8::1",
            "bearer-secret",
            "json-secret",
            "\\\\server\\share",
            "CoopGuard-package-v4-aaaaaaaa",
            new string('b', 64)
        ];
        if (forbidden.Any(value =>
                copied.Contains(value, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The copied diagnosis leaked a privacy probe.");
        }

        if (string.Equals(reasonLabel, "ManualHealth", StringComparison.Ordinal)
            && !body.Contains(
                chinese ? "没有重新读取全部文件" : "did not reread every file byte",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The manual snapshot overstated its quick verification.");
        }

        if (string.Equals(
                reasonLabel,
                "InternalMissingMethod",
                StringComparison.Ordinal)
            && (!body.Contains("RemovedGameApi", StringComparison.Ordinal)
                || !body.Contains(
                    chinese ? "来源未确定" : "source unknown",
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "The missing API detail or unknown attribution was not shown.");
        }

        Log.Info(
            $"DIAGNOSIS_POPUP_OK reason={reasonLabel} language={(chinese ? "zh" : "en")} title={title}");
        Log.Info($"DIAGNOSIS_COPY_OK reason={reasonLabel} bytes={copied.Length}");
    }

    internal static void VerifyMismatchPopup(NErrorPopup popup)
    {
        if (string.IsNullOrEmpty(ExpectedMismatchMod))
        {
            return;
        }

        string body = (string?)AccessTools.Field(
                typeof(NErrorPopup),
                "_body")
            .GetValue(popup)
            ?? string.Empty;
        if (!body.Contains("CG-MOD-MISMATCH", StringComparison.Ordinal))
        {
            return;
        }

        if (!body.Contains(ExpectedMismatchMod, StringComparison.Ordinal)
            || body.Contains(
                "CoopGuard-component-v4-",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The mismatch popup did not safely name the differing Mod.");
        }

        Log.Info($"DIAGNOSIS_MISMATCH_OK mod={ExpectedMismatchMod}");
        ExpectedMismatchMod = null;
        if (Godot.Engine.GetMainLoop() is Godot.SceneTree tree)
        {
            tree.Quit();
        }
    }
}

#if DEBUG
[HarmonyPatch]
internal static class DiagnosticsHandlerThrowOnceFault
{
    private static int _remaining = 1;

    private static bool Prepare() => string.Equals(
        Main.FaultId,
        "diagnostics-handler-throw-once",
        StringComparison.Ordinal);

    private static MethodBase TargetMethod() =>
        AccessTools.Method(
            AccessTools.TypeByName(
                "CoopGuard.ToolkitDiagnosticsRuntime")
            ?? throw new MissingMemberException(
                "ToolkitDiagnosticsRuntime"),
            "HandleMessageCore")
        ?? throw new MissingMethodException(
            "ToolkitDiagnosticsRuntime.HandleMessageCore");

    private static void Prefix()
    {
        if (Interlocked.Exchange(ref _remaining, 0) == 1)
        {
            Main.LogFaultInjected();
            throw new InvalidOperationException(
                "CGTEST injected diagnostics handler failure.");
        }
    }
}
#endif

[HarmonyPatch(
    typeof(MegaCrit.Sts2.Core.Multiplayer.NetHostGameService),
    nameof(MegaCrit.Sts2.Core.Multiplayer.NetHostGameService.StartENetHost))]
internal static class TestHostPortPatch
{
    private static void Prefix(ref ushort port) =>
        port = Main.TestPort;
}

[HarmonyPatch(
    typeof(MegaCrit.Sts2.Core.Multiplayer.Connection.ENetClientConnectionInitializer),
    MethodType.Constructor,
    [typeof(ulong), typeof(string), typeof(ushort)])]
internal static class TestClientPortPatch
{
    private static void Prefix(ref ushort port) =>
        port = Main.TestPort;
}

[HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen._Process))]
internal static class FreshLobbyProcessPatch
{
    private static void Postfix(NCharacterSelectScreen __instance) => Main.TryReady(__instance.Lobby);
}

[HarmonyPatch(typeof(NMultiplayerLoadGameScreen), nameof(NMultiplayerLoadGameScreen._Process))]
internal static class LoadLobbyProcessPatch
{
    private static void Postfix(LoadRunLobby? ____runLobby) => Main.TryReady(____runLobby);
}

[HarmonyPatch(typeof(NGame), nameof(NGame.StartNewMultiplayerRun))]
internal static class MultiplayerSaveAfterEmbarkPatch
{
    private static void Postfix(ref Task<RunState> __result) =>
        __result = Main.SaveAfterFreshRun(__result);
}

[HarmonyPatch(
    typeof(JoinFlow),
    "HandleInitialGameInfoMessage",
    [typeof(InitialGameInfoMessage), typeof(ulong)])]
[HarmonyBefore("CoopGuard")]
internal static class InitialInfoToctouPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix() => Main.TryInjectPackageChangeAtInitialInfo();
}

[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
internal static class DiagnosticPopupPatch
{
    private static void Postfix()
    {
        Main.TryPublishLateSettingsChange();
        Main.VerifyDiagnosticPopup();
        Main.ScheduleToolkitVerification();
    }
}


[HarmonyPatch(typeof(NErrorPopup), nameof(NErrorPopup._Ready))]
internal static class MismatchPopupPatch
{
    private static void Postfix(NErrorPopup __instance) =>
        Main.VerifyMismatchPopup(__instance);
}
