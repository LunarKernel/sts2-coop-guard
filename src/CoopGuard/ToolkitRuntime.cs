using System.Diagnostics;
using System.Globalization;
using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Audio;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Quality;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace CoopGuard;

internal enum ToolkitSessionPhase
{
    None,
    NewRunLobby,
    LoadedRunLobby,
    Running
}

internal enum ToolkitJoinStage
{
    Idle,
    ConnectingPlatform,
    WaitingInitialInfo,
    ValidatingEnvironment,
    WaitingNewRunLobby,
    WaitingLoadedRunLobby,
    RunInProgressUnsupported,
    Complete,
    Failed
}

internal enum ToolkitCueCategory
{
    Ready,
    Network,
    Rejoin,
    Result
}

internal static class ToolkitRuntime
{
    private enum SoundCue
    {
        AllReady,
        WaitingLocal,
        Disconnected,
        Rejoined,
        ReconnectResult
    }

    private sealed class PeerRuntime(PeerIdentity identity)
    {
        public PeerIdentity Identity { get; } = identity;
        public NetworkHistory History { get; } = new();
        public NetworkSample LastSample { get; set; }
        public bool HasSample { get; set; }
        public long? LoadingSinceTicks { get; set; }
        public double? LastLoadingSeconds { get; set; }
    }

    private sealed class PublicActionView(
        GameAction action,
        string category,
        string owner,
        long startedTicks)
    {
        public GameAction Action { get; } = action;
        public string Category { get; } = category;
        public string Owner { get; } = owner;
        public long StartedTicks { get; } = startedTicks;
        public long? CompletedTicks { get; set; }
    }

    private const long TicketTtlSeconds = 10 * 60;
    private static readonly object Sync = new();
    private static readonly FlightRecorder Recorder = new();
    private static readonly ToolkitSession Session = new();
    private static readonly OptionalModuleFuse RuntimeFuse = new();
    private static readonly PeerIdentityMap Identities = new();
    private static readonly Dictionary<ulong, PeerRuntime> Peers = [];
    private static readonly HashSet<ulong> PlayersReadyToEndTurn = [];
    private static readonly Dictionary<ulong, int> RemoteChoiceWaits = [];
    private static readonly HashSet<ulong> MapSubmitted = [];
    private static readonly List<PublicActionView> PublicActions = [];
    private static bool _mapTracking;
    private static readonly ProgressLease Progress = new();
    private static readonly AlertCenter Alerts = new();
    private static readonly CueRateLimiter CueLimiter = new();
    private static WeakReference<NMainMenu>? _mainMenu;
    private static WeakReference<ToolkitNode>? _toolkitNode;
    private static WeakReference<Control>? _previousFocus;
    private static StartRunLobby? _startLobby;
    private static LoadRunLobby? _loadLobby;
    private static RunLobby? _runLobby;
    private static INetGameService? _service;
    private static ToolkitSessionPhase _phase;
    private static bool _wasConnected;
    private static long _observationSequence;
    private static long _nextSampleTicks;
    private static long _nextHandSampleTicks;
    private static long _nextPackageObservationTicks;
    private static long _lastWaitAlertTicks;
    private static long _localLoadingSinceTicks;
    private static double? _lastLocalLoadingSeconds;
    private static readonly Queue<double> LocalLoadingHistory = new();
    private static Observation<string>? _networkState;
    private static WaitAssessment _wait;
    private static ToolkitJoinStage _joinStage;
    private static long _joinStageSinceTicks;
    private static string _joinFailure = string.Empty;
    private static ulong? _candidateLobbyTicket;
    private static ulong? _reconnectTicket;
    private static long _reconnectTicketExpiryTicks;
    private static string _reconnectStatus = "Unavailable";
    private static int _reconnectInProgress;
    private static long _reconnectCooldownUntilTicks;
    private static CrashReview? _crashReview;
    private static string _manualSaveStatus = "No report saved this session";
    private static string _packageObservation = "Package observation: not checked";
    private static string _saveSealStatus =
        "No multiplayer save has been observed.";
    private static string _loadWarningToken = string.Empty;
    private static long _loadWarningUntilTicks;
    private static ToolkitPreferences _preferences = ToolkitPreferences.Default;
    private static bool _panelOpened;

    public static void Attach(NGame game)
    {
        if (game.GetNodeOrNull<ToolkitNode>("CoopGuardToolkit")
            is ToolkitNode existing)
        {
            existing.Initialize();
            _toolkitNode = new WeakReference<ToolkitNode>(existing);
            return;
        }

        ToolkitNode node = new()
        {
            Name = "CoopGuardToolkit",
            ProcessMode = Node.ProcessModeEnum.Always
        };
        game.AddChild(node);
        node.Initialize();
        _toolkitNode = new WeakReference<ToolkitNode>(node);
        Recorder.Record(
            TimelineEventKind.Lifecycle,
            "CG-LIFECYCLE-ATTACHED",
            "local",
            "Toolkit runtime attached.");
    }

    public static void InitializePersistence(
        string dataRoot,
        string logPath)
    {
        try
        {
            ToolkitReportHistory.Configure(
                Path.Combine(dataRoot, "reports"));
            EnvironmentLockStore.Configure(dataRoot);
            SaveEnvironmentSidecars.Configure(dataRoot);
            ToolkitPreferences preferences =
                ToolkitPreferenceStore.ConfigureAndLoad(dataRoot);
            CrashReview? review = ToolkitCrashMarker.Start(
                dataRoot,
                logPath);
            lock (Sync)
            {
                _preferences = preferences;
                _crashReview = review;
                if (review != null)
                {
                    string message = review.Confidence
                        == CrashReviewConfidence.High
                            ? "Previous session ended unexpectedly with a native crash signature."
                            : "Previous session may have ended unexpectedly; evidence is insufficient.";
                    Recorder.Record(
                        TimelineEventKind.Warning,
                        "CG-PREVIOUS-UNEXPECTED-EXIT",
                        "local",
                        message);
                    Alerts.Add(
                        AlertSeverity.Warning,
                        "CG-PREVIOUS-UNEXPECTED-EXIT",
                        "local",
                        message,
                        Stopwatch.GetTimestamp());
                }
            }
        }
        catch (Exception ex)
        {
            Main.Log.Error(
                $"Optional Toolkit persistence was disabled: {ex}");
        }
    }

    internal static void RecordExternalDiagnostic(
        string modId,
        ushort eventCode)
    {
        Recorder.Record(
            TimelineEventKind.PublicAction,
            "CG-MOD-EVENT-" + eventCode.ToString(
                CultureInfo.InvariantCulture),
            FlightRecorder.BoundedText(modId, 64),
            "A Mod published a fixed public diagnostic event code.");
    }

    internal static void RecordNaturalCheckpoint(
        uint checkpointId,
        string context)
    {
        Recorder.Record(
            TimelineEventKind.Checkpoint,
            "CG-CHECKPOINT",
            "local",
            "Natural checkpoint "
                + checkpointId.ToString(CultureInfo.InvariantCulture)
                + "; context="
                + FlightRecorder.BoundedText(context, 64));
    }

    public static void ObserveStartLobby(StartRunLobby lobby)
    {
        lock (Sync)
        {
            _startLobby = lobby;
            _loadLobby = null;
            _runLobby = null;
            _phase = ToolkitSessionPhase.NewRunLobby;
            SetService(lobby.NetService);
        }
    }

    public static void ObserveLoadLobby(LoadRunLobby lobby)
    {
        lock (Sync)
        {
            _startLobby = null;
            _loadLobby = lobby;
            _runLobby = null;
            _phase = ToolkitSessionPhase.LoadedRunLobby;
            SetService(lobby.NetService);
        }
    }

    public static void ObserveRunLobby(
        RunLobby lobby,
        INetGameService service)
    {
        lock (Sync)
        {
            _startLobby = null;
            _loadLobby = null;
            _runLobby = lobby;
            _phase = ToolkitSessionPhase.Running;
            _candidateLobbyTicket = null;
            SetService(service);
            Recorder.Record(
                TimelineEventKind.Lifecycle,
                "CG-RUN-START",
                "local",
                "Native run lobby became active.");
        }
    }

    public static void ReleaseLobby(object lobby)
    {
        lock (Sync)
        {
            if (ReferenceEquals(_startLobby, lobby))
            {
                _startLobby = null;
            }

            if (ReferenceEquals(_loadLobby, lobby))
            {
                _loadLobby = null;
            }
        }
    }

    public static void AttachMainMenu(NMainMenu menu)
    {
        lock (Sync)
        {
            _mainMenu = new WeakReference<NMainMenu>(menu);
        }
    }

    public static void ReadyChanged(ulong playerId, bool ready)
    {
        lock (Sync)
        {
            PeerIdentity identity = Identities.GetOrAdd(playerId);
            Recorder.Record(
                TimelineEventKind.Progress,
                ready ? "CG-PLAYER-READY" : "CG-PLAYER-NOT-READY",
                identity.Label,
                ready ? "Ready" : "Not ready");
            bool allReady = ready
                && ((_startLobby != null
                        && _startLobby.Players.All(player => player.isReady))
                    || (_loadLobby != null
                        && _loadLobby.ConnectedPlayerIds.Count > 0
                        && _loadLobby.ConnectedPlayerIds.All(
                            _loadLobby.IsPlayerReady)));
            if (allReady)
            {
                Recorder.Record(
                    TimelineEventKind.Progress,
                    "CG-ALL-READY",
                    "local",
                    "All players are ready.");
                TryPlayCue(SoundCue.AllReady);
            }
        }
    }

    public static void PlayerTurnReadyChanged(ulong playerId, bool ready)
    {
        lock (Sync)
        {
            if (ready)
            {
                PlayersReadyToEndTurn.Add(playerId);
            }
            else
            {
                PlayersReadyToEndTurn.Remove(playerId);
            }

            PeerIdentity identity = Identities.GetOrAdd(playerId);
            Recorder.Record(
                TimelineEventKind.Progress,
                ready ? "CG-TURN-READY" : "CG-TURN-RESUMED",
                identity.Label,
                ready ? "Turn complete" : "Turn active");
        }
    }

    public static void ResetTurnReadiness()
    {
        lock (Sync)
        {
            PlayersReadyToEndTurn.Clear();
        }
    }

    public static void RemoteChoiceStarted(ulong playerId)
    {
        lock (Sync)
        {
            RemoteChoiceWaits[playerId] =
                RemoteChoiceWaits.GetValueOrDefault(playerId) + 1;
            PeerIdentity identity = Identities.GetOrAdd(playerId);
            Recorder.Record(
                TimelineEventKind.Progress,
                "CG-CHOICE-WAIT-START",
                identity.Label,
                "Waiting for a public remote choice; contents hidden.");
        }
    }

    public static void RemoteChoiceCompleted(ulong playerId)
    {
        lock (Sync)
        {
            if (RemoteChoiceWaits.GetValueOrDefault(playerId) <= 1)
            {
                RemoteChoiceWaits.Remove(playerId);
            }
            else
            {
                RemoteChoiceWaits[playerId]--;
            }

            PeerIdentity identity = Identities.GetOrAdd(playerId);
            Recorder.Record(
                TimelineEventKind.Progress,
                "CG-CHOICE-WAIT-END",
                identity.Label,
                "Remote choice wait completed; contents hidden.");
        }
    }

    public static void MapVoteChanged(ulong playerId, bool submitted)
    {
        lock (Sync)
        {
            _mapTracking = true;
            if (submitted)
            {
                MapSubmitted.Add(playerId);
            }
            else
            {
                MapSubmitted.Remove(playerId);
            }

            PeerIdentity identity = Identities.GetOrAdd(playerId);
            Recorder.Record(
                TimelineEventKind.Progress,
                submitted ? "CG-MAP-SUBMITTED" : "CG-MAP-CANCELLED",
                identity.Label,
                submitted
                    ? "Map selection submitted; destination hidden."
                    : "Map selection cancelled.");
        }
    }

    public static void MapVotesCleared()
    {
        lock (Sync)
        {
            MapSubmitted.Clear();
            _mapTracking = true;
            Recorder.Record(
                TimelineEventKind.Progress,
                "CG-MAP-RESET",
                "local",
                "Map selection progress reset.");
        }
    }

    public static void PublicActionStarted(GameAction action)
    {
        lock (Sync)
        {
            PrunePublicActions(Stopwatch.GetTimestamp());
            string category = PublicActionCategory(action);
            string owner = Identities.GetOrAdd(action.OwnerId).Label;
            PublicActionView view = new(
                action,
                category,
                owner,
                Stopwatch.GetTimestamp());
            PublicActions.Add(view);
            while (PublicActions.Count > 8)
            {
                PublicActions.RemoveAt(0);
            }

            Recorder.Record(
                TimelineEventKind.PublicAction,
                "CG-ACTION-START-" + category.ToUpperInvariant(),
                owner,
                "Accepted public action started.");
        }
    }

    public static void PublicActionCompleted(GameAction action)
    {
        lock (Sync)
        {
            long now = Stopwatch.GetTimestamp();
            PublicActionView? view = PublicActions.LastOrDefault(
                item => ReferenceEquals(item.Action, action));
            if (view != null)
            {
                view.CompletedTicks = now;
            }

            Recorder.Record(
                TimelineEventKind.PublicAction,
                "CG-ACTION-END-"
                    + PublicActionCategory(action).ToUpperInvariant(),
                Identities.GetOrAdd(action.OwnerId).Label,
                "Accepted public action completed.");
            if (action.Id.HasValue
                && ToolkitDiagnosticsRuntime.TryGetOrdinal(
                    action.OwnerId,
                    out byte ordinal))
            {
                ToolkitDiagnosticsRuntime.RecordContribution(
                    ordinal,
                    (1UL << 60) | ((ulong)action.Id.Value + 1),
                    completedActions: 1);
            }

            PrunePublicActions(now);
        }
    }

    public static void BeginJoin()
    {
        SetJoinStage(ToolkitJoinStage.ConnectingPlatform);
    }

    public static void InitialInfo(RunSessionState state)
    {
        SetJoinStage(ToolkitJoinStage.ValidatingEnvironment);
        if (state == RunSessionState.Running)
        {
            SetJoinStage(ToolkitJoinStage.RunInProgressUnsupported);
        }
    }

    public static void WaitingNewRunLobby() =>
        SetJoinStage(ToolkitJoinStage.WaitingNewRunLobby);

    public static void WaitingLoadedRunLobby() =>
        SetJoinStage(ToolkitJoinStage.WaitingLoadedRunLobby);

    public static void WaitingRunningResponse() =>
        SetJoinStage(ToolkitJoinStage.RunInProgressUnsupported);

    public static void JoinCompleted(RunSessionState state)
    {
        SetJoinStage(
            state == RunSessionState.Running
                ? ToolkitJoinStage.RunInProgressUnsupported
                : ToolkitJoinStage.Complete);
    }

    public static void JoinFailed(Exception exception)
    {
        lock (Sync)
        {
            _joinFailure = FlightRecorder.BoundedText(
                exception.GetBaseException().GetType().Name,
                96);
        }

        SetJoinStage(ToolkitJoinStage.Failed);
    }

    public static void Tick()
    {
        RuntimeFuse.TryRun(
            TickCore,
            ex =>
            {
                lock (Sync)
                {
                    Recorder.Record(
                        TimelineEventKind.Error,
                        "CG-TOOLKIT-DISABLED",
                        "local",
                        ex.GetType().Name);
                    Alerts.Add(
                        AlertSeverity.Warning,
                        "CG-TOOLKIT-DISABLED",
                        "local",
                        "Optional cockpit disabled; Guard remains active.",
                        Stopwatch.GetTimestamp());
                }

                Main.Log.Error(
                    $"Optional Toolkit runtime disabled for this session: {ex}");
            });
    }

    public static void Frame()
    {
        ToolkitNode? node = null;
        lock (Sync)
        {
            _toolkitNode?.TryGetTarget(out node);
        }

        node?.Refresh();
    }

    public static string HudText(bool chinese)
    {
        lock (Sync)
        {
            if (RuntimeFuse.IsDisabled)
            {
                return chinese
                    ? "CoopGuard 工具已停用；Guard 仍正常"
                    : "CoopGuard Toolkit disabled; Guard remains active";
            }

            List<string> lines =
            [
                chinese
                    ? "CoopGuard 联机驾驶舱"
                    : "CoopGuard Multiplayer Cockpit"
            ];
            if (_networkState is { } observation
                && observation.IsFresh(Stopwatch.GetTimestamp()))
            {
                lines.Add(observation.Value);
            }
            else
            {
                lines.Add(chinese ? "状态：未知" : "State: Unknown");
            }

            if (_wasConnected)
            {
                lines.Add(ToolkitDiagnosticsRuntime.HudStatus(chinese));
            }

            if (_wait.Reason != WaitReasonCode.None)
            {
                double seconds = _wait.DurationTicks
                    / (double)Stopwatch.Frequency;
                lines.Add(
                    (chinese ? "等待：" : "Waiting: ")
                    + WaitLabel(_wait.Reason, chinese)
                    + (seconds > 0 ? $" {seconds:F1}s" : string.Empty)
                    + $" [{_wait.Confidence}]");
            }

            foreach (PeerRuntime peer in Peers.Values
                         .OrderBy(peer => peer.Identity.Ordinal)
                         .Take(4))
            {
                lines.Add(PeerLine(peer, chinese));
            }

            int activeAlerts = Alerts.Snapshot()
                .Count(alert => !alert.Acknowledged);
            if (activeAlerts > 0)
            {
                lines.Add(
                    chinese
                        ? $"警报：{activeAlerts}（Ctrl+F8 查看）"
                        : $"Alerts: {activeAlerts} (Ctrl+F8 for details)");
            }

            if (CanReconnectLocked())
            {
                lines.Add(
                    chinese
                        ? "可安全重进开局前大厅"
                        : "Pre-run lobby re-entry is available");
            }

            return string.Join('\n', lines);
        }
    }

    public static bool ShouldShowHud
    {
        get
        {
            lock (Sync)
            {
                return _wasConnected
                    || _panelOpened
                    || _reconnectTicket.HasValue
                    || Alerts.Snapshot().Count > 0;
            }
        }
    }

    public static bool PanelOpened
    {
        get
        {
            lock (Sync)
            {
                return _panelOpened;
            }
        }
    }

    public static void TogglePanel(NGame game)
    {
        bool opened;
        ToolkitNode? node = null;
        Control? restore = null;
        lock (Sync)
        {
            _panelOpened = !_panelOpened;
            opened = _panelOpened;
            _toolkitNode?.TryGetTarget(out node);
            if (opened)
            {
                Control? focused = game.GetViewport().GuiGetFocusOwner();
                _previousFocus = focused == null
                    ? null
                    : new WeakReference<Control>(focused);
            }
            else if (_previousFocus?.TryGetTarget(out restore) == true)
            {
                _previousFocus = null;
            }
        }

        node?.SetControlMode(opened);
        if (!opened
            && restore != null
            && GodotObject.IsInstanceValid(restore)
            && restore.IsInsideTree())
        {
            restore.GrabFocus();
        }
    }

    public static bool CanReconnect
    {
        get
        {
            lock (Sync)
            {
                return CanReconnectLocked();
            }
        }
    }

    public static int ActiveAlertCount
    {
        get
        {
            lock (Sync)
            {
                return Alerts.Snapshot()
                    .Count(alert => !alert.Acknowledged);
            }
        }
    }

    public static void AcknowledgeAlerts()
    {
        lock (Sync)
        {
            Alerts.AcknowledgeAll();
            Recorder.Record(
                TimelineEventKind.Progress,
                "CG-ALERTS-ACKNOWLEDGED",
                "local",
                "User acknowledged current Toolkit alerts.");
        }
    }

    public static bool SoundEnabled
    {
        get
        {
            lock (Sync)
            {
                return _preferences.SoundEnabled;
            }
        }
    }

    public static void ToggleSound()
    {
        lock (Sync)
        {
            _preferences = _preferences with
            {
                SoundEnabled = !_preferences.SoundEnabled
            };
            _ = ToolkitPreferenceStore.TrySave(_preferences, out _);
            Recorder.Record(
                TimelineEventKind.Progress,
                "CG-SOUND-TOGGLED",
                "local",
                _preferences.SoundEnabled
                    ? "Toolkit sound cues enabled."
                    : "Toolkit sound cues muted.");
        }

        RefreshPreferencesUi();
    }

    public static ToolkitPreferences Preferences
    {
        get
        {
            lock (Sync)
            {
                return _preferences;
            }
        }
    }

    public static void CycleSoundVolume()
    {
        lock (Sync)
        {
            float volume = _preferences.SoundVolume switch
            {
                >= 0.99f => 0.75f,
                >= 0.74f => 0.5f,
                >= 0.49f => 0.25f,
                _ => 1f
            };
            _preferences = _preferences with { SoundVolume = volume };
            _ = ToolkitPreferenceStore.TrySave(_preferences, out _);
        }

        RefreshPreferencesUi();
    }

    public static void ToggleCue(ToolkitCueCategory category)
    {
        lock (Sync)
        {
            _preferences = category switch
            {
                ToolkitCueCategory.Ready => _preferences with
                {
                    ReadyCues = !_preferences.ReadyCues
                },
                ToolkitCueCategory.Network => _preferences with
                {
                    NetworkCues = !_preferences.NetworkCues
                },
                ToolkitCueCategory.Rejoin => _preferences with
                {
                    RejoinCues = !_preferences.RejoinCues
                },
                _ => _preferences with
                {
                    ResultCues = !_preferences.ResultCues
                }
            };
            _ = ToolkitPreferenceStore.TrySave(_preferences, out _);
        }

        RefreshPreferencesUi();
    }

    public static void CycleUiScale()
    {
        lock (Sync)
        {
            float scale = _preferences.UiScale switch
            {
                < 1.24f => 1.25f,
                < 1.49f => 1.5f,
                < 1.74f => 1.75f,
                < 1.99f => 2f,
                _ => 1f
            };
            _preferences = _preferences with { UiScale = scale };
            _ = ToolkitPreferenceStore.TrySave(_preferences, out _);
        }

        RefreshPreferencesUi();
    }

    public static void ToggleReducedMotion()
    {
        lock (Sync)
        {
            _preferences = _preferences with
            {
                ReducedMotion = !_preferences.ReducedMotion
            };
            _ = ToolkitPreferenceStore.TrySave(_preferences, out _);
        }

        RefreshPreferencesUi();
    }

    public static void ToggleHighContrast()
    {
        lock (Sync)
        {
            _preferences = _preferences with
            {
                HighContrast = !_preferences.HighContrast
            };
            _ = ToolkitPreferenceStore.TrySave(_preferences, out _);
        }

        RefreshPreferencesUi();
    }

    private static void RefreshPreferencesUi()
    {
        if (_toolkitNode?.TryGetTarget(out ToolkitNode? node) == true)
        {
            node.ApplyPreferencesNow();
        }
    }

    public static string CreateEnvironmentLockfile()
    {
        FingerprintSnapshot snapshot = ModFingerprint.ValidateQuick();
        return EnvironmentLockCodec.Create(
            snapshot,
            NGame.GetGameVersion(),
            typeof(Main).Assembly.GetName().Version?.ToString()
                ?? "unknown");
    }

    public static string SaveEnvironmentLockfile()
    {
        string json = CreateEnvironmentLockfile();
        return EnvironmentLockStore.TrySave(json, out string status)
            ? "Saved " + status
            : status;
    }

    public static string CopyEnvironmentLockfile()
    {
        string json = CreateEnvironmentLockfile();
        DisplayServer.ClipboardSet(json);
        return "Environment lockfile copied.";
    }

    public static string CompareClipboardEnvironment()
    {
        string imported = DisplayServer.ClipboardGet();
        if (!EnvironmentLockCodec.TryParse(
                imported,
                out EnvironmentLock? other,
                out string error))
        {
            return error;
        }

        string currentJson = CreateEnvironmentLockfile();
        if (!EnvironmentLockCodec.TryParse(
                currentJson,
                out EnvironmentLock? current,
                out error))
        {
            return "Current environment could not be parsed: " + error;
        }

        IReadOnlyList<string> differences =
            EnvironmentLockCodec.Compare(current!, other!);
        return differences.Count == 0
            ? "Environment confirmed identical."
            : "Environment differences: "
                + string.Join(", ", differences.Take(20))
                + (differences.Count > 20
                    ? $" (+{differences.Count - 20} more)"
                    : string.Empty);
    }

    public static string BuildBisectPlanFromClipboard()
    {
        try
        {
            string input = DisplayServer.ClipboardGet();
            if (Encoding.UTF8.GetByteCount(input) > 16 * 1024)
            {
                return "CoopGuard dependency-aware manual A/B plan\n"
                    + "Plan unavailable: candidate input exceeds 16 KiB.\n"
                    + "No Mod or setting was changed.";
            }

            string[] candidates = input.Split(
                    ['\r', '\n', ',', ';'],
                    StringSplitOptions.RemoveEmptyEntries
                        | StringSplitOptions.TrimEntries)
                .Take(ModDependencyDoctor.MaxMods + 1)
                .ToArray();
            ModBisectPlan plan = ModBisectPlanner.Create(
                LocalModDoctor.CurrentDependencyRecords(),
                candidates);
            if (plan.Available)
            {
                DisplayServer.ClipboardSet(plan.Text);
            }

            return plan.Text
                + (plan.Available
                    ? "\nPlan copied to the clipboard."
                    : string.Empty);
        }
        catch (Exception ex)
        {
            return "CoopGuard dependency-aware manual A/B plan\n"
                + "Plan unavailable: "
                + FlightRecorder.BoundedText(ex.GetType().Name, 100)
                + ".\nNo Mod or setting was changed.";
        }
    }

    public static async Task SealAfterNativeMultiplayerSave(
        Task nativeSave,
        RunSaveManager manager,
        ISaveStore saveStore)
    {
        await nativeSave.ConfigureAwait(false);
        string status;
        bool sealedSave = false;
        try
        {
            SaveEnvironmentStamp environment =
                EnvironmentLockCodec.CreateStamp(
                    ModFingerprint.ValidateQuick(),
                    NGame.GetGameVersion());
            string relativePath = (string?)AccessTools.PropertyGetter(
                    typeof(RunSaveManager),
                    "CurrentMultiplayerRunSavePath")
                ?.Invoke(manager, null)
                ?? throw new MissingMemberException(
                    "RunSaveManager.CurrentMultiplayerRunSavePath");
            string path = NativeSavePath(saveStore, relativePath);
            sealedSave = SaveEnvironmentSidecars.TrySeal(
                path,
                environment,
                out status);
        }
        catch (Exception ex)
        {
            status = "Native save succeeded, but environment sealing failed: "
                + FlightRecorder.BoundedText(ex.GetType().Name, 100);
        }

        lock (Sync)
        {
            _saveSealStatus = status;
            Recorder.Record(
                sealedSave
                    ? TimelineEventKind.Lifecycle
                    : TimelineEventKind.Warning,
                sealedSave
                    ? "CG-SAVE-SEALED"
                    : "CG-SAVE-SEAL-FAILED",
                "local",
                status);
            if (!sealedSave)
            {
                Alerts.Add(
                    AlertSeverity.Warning,
                    "CG-SAVE-SEAL-FAILED",
                    "local",
                    status,
                    Stopwatch.GetTimestamp());
            }
        }

        if (sealedSave)
        {
            Main.Log.Info($"CG-SAVE-SEALED: {status}");
        }
        else
        {
            Main.Log.Warn($"CG-SAVE-SEAL-FAILED: {status}");
        }
    }

    public static bool AllowNativeMultiplayerLoad()
    {
        try
        {
            SaveManager saveManager = SaveManager.Instance;
            RunSaveManager runSaveManager =
                (RunSaveManager?)AccessTools.Field(
                        typeof(SaveManager),
                        "_runSaveManager")
                    .GetValue(saveManager)
                ?? throw new MissingMemberException(
                    "SaveManager._runSaveManager");
            ISaveStore saveStore =
                (ISaveStore?)AccessTools.Field(
                        typeof(RunSaveManager),
                        "_saveStore")
                    .GetValue(runSaveManager)
                ?? throw new MissingMemberException(
                    "RunSaveManager._saveStore");
            string relativePath = (string?)AccessTools.PropertyGetter(
                    typeof(RunSaveManager),
                    "CurrentMultiplayerRunSavePath")
                ?.Invoke(runSaveManager, null)
                ?? throw new MissingMemberException(
                    "RunSaveManager.CurrentMultiplayerRunSavePath");
            SaveEnvironmentStamp current =
                EnvironmentLockCodec.CreateStamp(
                    ModFingerprint.ValidateQuick(),
                    NGame.GetGameVersion());
            SaveSealReview review = SaveEnvironmentSidecars.Review(
                NativeSavePath(saveStore, relativePath),
                current);
            string status = review.Evidence
                + (review.Differences.Count == 0
                    ? string.Empty
                    : " Differences: "
                        + string.Join(
                            ", ",
                            review.Differences.Take(100)));
            lock (Sync)
            {
                _saveSealStatus = status;
            }

            if (review.State == SaveSealState.MatchingLocalRecord)
            {
                return true;
            }

            string token = review.State
                + ":"
                + string.Join(",", review.Differences);
            long now = Stopwatch.GetTimestamp();
            lock (Sync)
            {
                if (_loadWarningToken == token
                    && now <= _loadWarningUntilTicks)
                {
                    _loadWarningToken = string.Empty;
                    _loadWarningUntilTicks = 0;
                    return true;
                }

                _loadWarningToken = token;
                _loadWarningUntilTicks =
                    now + 60 * Stopwatch.Frequency;
            }

            ShowLoadEnvironmentWarning(status);
            return false;
        }
        catch (Exception ex)
        {
            string status =
                "Save environment evidence is unavailable: "
                + FlightRecorder.BoundedText(ex.GetType().Name, 100);
            string token = "unavailable:" + ex.GetType().Name;
            long now = Stopwatch.GetTimestamp();
            lock (Sync)
            {
                _saveSealStatus = status;
                if (_loadWarningToken == token
                    && now <= _loadWarningUntilTicks)
                {
                    _loadWarningToken = string.Empty;
                    _loadWarningUntilTicks = 0;
                    return true;
                }

                _loadWarningToken = token;
                _loadWarningUntilTicks =
                    now + 60 * Stopwatch.Frequency;
            }

            ShowLoadEnvironmentWarning(status);
            return false;
        }
    }

    private static string NativeSavePath(
        ISaveStore saveStore,
        string relativePath)
    {
        string storePath = saveStore.GetFullPath(relativePath);
        if (storePath.StartsWith("user://", StringComparison.Ordinal))
        {
            return Path.GetFullPath(
                ProjectSettings.GlobalizePath(storePath));
        }

        if (!Path.IsPathFullyQualified(storePath))
        {
            throw new InvalidDataException(
                "Native save store returned an unqualified path.");
        }

        return Path.GetFullPath(storePath);
    }

    private static void ShowLoadEnvironmentWarning(string evidence)
    {
        NErrorPopup? popup = NErrorPopup.Create(
            "CoopGuard save environment warning / 存档环境警告",
            "[CG-SAVE-ENVIRONMENT-WARNING]\n"
            + FlightRecorder.BoundedText(evidence, 2048)
            + "\n\nThis is a local warning, not proof of compatibility. "
            + "Loading was paused. Close this popup to cancel, or press Load again within 60 seconds to continue. "
            + "Current peer package checks will still run.\n\n"
            + "这是本机警告，不代表兼容性证明。加载已暂停；关闭弹窗即取消，"
            + "或在 60 秒内再次点击“加载”继续。当前联机包校验仍会照常执行。",
            showReportBugButton: false);
        if (popup != null && NModalContainer.Instance != null)
        {
            NModalContainer.Instance.Add(popup);
        }
    }

    public static string BuildOverview(bool chinese)
    {
        lock (Sync)
        {
            if (RuntimeFuse.IsDisabled)
            {
                return chinese
                    ? $"联机驾驶舱：本局已安全停用（{RuntimeFuse.Failure ?? "未知错误"}）；Guard 仍保持工作。"
                    : $"Multiplayer cockpit: safely disabled for this session ({RuntimeFuse.Failure ?? "unknown error"}); Guard remains active.";
            }

            List<string> lines =
            [
                chinese ? "联机驾驶舱" : "Multiplayer cockpit",
                _networkState?.Value
                    ?? (chinese ? "状态：未知" : "State: Unknown"),
                (chinese ? "加入阶段：" : "Join stage: ")
                    + JoinStageLabel(_joinStage, chinese)
                    + JoinStageDuration(),
                (chinese ? "重连：" : "Reconnect: ") + _reconnectStatus,
                (chinese ? "包更新观察：" : "Package update observation: ")
                    + _packageObservation,
                (chinese ? "存档环境封印：" : "Save environment seal: ")
                    + _saveSealStatus,
                _localLoadingSinceTicks != 0
                    ? (chinese ? "本地加载：" : "Local loading: ")
                        + $"{(Stopwatch.GetTimestamp() - _localLoadingSinceTicks) / (double)Stopwatch.Frequency:F1}s"
                    : _lastLocalLoadingSeconds.HasValue
                        ? (chinese ? "最近本地加载：" : "Last local load: ")
                            + $"{_lastLocalLoadingSeconds.Value:F1}s"
                        : (chinese ? "本地加载：无数据" : "Local loading: no data"),
                LocalLoadingHistory.Count == 0
                    ? (chinese ? "最近加载记录：无" : "Recent local loads: none")
                    : (chinese ? "最近加载记录：" : "Recent local loads: ")
                        + string.Join(
                            ", ",
                            LocalLoadingHistory.Select(
                                value => $"{value:F1}s")),
                (chinese ? "等待原因：" : "Wait reason: ")
                    + WaitLabel(_wait.Reason, chinese)
                    + $" [{_wait.Confidence}]",
                (chinese ? "证据：" : "Evidence: ")
                    + (string.IsNullOrEmpty(_wait.Evidence)
                        ? (chinese ? "无" : "none")
                        : _wait.Evidence)
            ];

            foreach (PeerRuntime peer in Peers.Values
                         .OrderBy(peer => peer.Identity.Ordinal))
            {
                lines.Add(PeerLine(peer, chinese));
                string summary = NetworkSummary(peer, chinese);
                if (!string.IsNullOrEmpty(summary))
                {
                    lines.Add("  " + summary);
                }
            }

            IReadOnlyList<TimelineEntry> timeline = Recorder.Snapshot(10);
            lines.Add(
                chinese
                    ? $"时间线：{Recorder.Count}/{FlightRecorder.Capacity}，丢弃 {Recorder.DroppedCount}"
                    : $"Timeline: {Recorder.Count}/{FlightRecorder.Capacity}, dropped {Recorder.DroppedCount}");
            foreach (TimelineEntry entry in timeline)
            {
                lines.Add(
                    $"  {entry.Kind} {entry.Code} {entry.Subject}"
                    + (entry.Count > 1 ? $" x{entry.Count}" : string.Empty));
            }

            IReadOnlyList<ToolkitAlert> alerts = Alerts.Snapshot();
            lines.Add(
                chinese
                    ? $"警报中心：{alerts.Count}/{AlertCenter.Capacity}"
                    : $"Alert center: {alerts.Count}/{AlertCenter.Capacity}");
            foreach (ToolkitAlert alert in alerts.TakeLast(5))
            {
                lines.Add(
                    $"  [{alert.Severity}] {alert.Code}"
                    + (alert.Count > 1 ? $" x{alert.Count}" : string.Empty));
            }

            lines.Add(ToolkitDiagnosticsRuntime.BuildMatrix(chinese));
            lines.Add(
                ToolkitDiagnosticsRuntime.RecentQuickStatuses(chinese));
            lines.Add(
                ToolkitDiagnosticsRuntime.CollaborationStatus(chinese));
            lines.Add(
                RemoteChoiceWaits.Count == 0
                    ? (chinese
                        ? "选择进度：无已确认远端等待"
                        : "Choice progress: no confirmed remote wait")
                    : (chinese ? "选择进度：" : "Choice progress: ")
                        + string.Join(
                            ", ",
                            RemoteChoiceWaits.Keys.Select(id =>
                                Identities.GetOrAdd(id).Label
                                + " choosing (contents hidden)")));
            int rosterCount = CurrentPeerIds().Distinct().Count();
            lines.Add(
                !_mapTracking
                    ? (chinese
                        ? "地图选择：未知"
                        : "Map selection: unknown")
                    : (chinese ? "地图选择：" : "Map selection: ")
                        + $"{MapSubmitted.Count}/{rosterCount} submitted; destinations hidden");
            PrunePublicActions(Stopwatch.GetTimestamp());
            lines.Add(chinese ? "公开行动流：" : "Public action flow:");
            lines.AddRange(PublicActions.Count == 0
                ? ["  <none>"]
                : PublicActions.Select(action =>
                    $"  {action.Owner} {action.Category}: "
                    + (action.CompletedTicks.HasValue
                        ? "completed"
                        : "executing")));

            if (_crashReview != null)
            {
                lines.Add(
                    chinese
                        ? $"上次会话：可能异常终止；置信度 {_crashReview.Confidence}；签名 {_crashReview.Signature}；最后阶段 {_crashReview.LastStage}"
                        : $"Previous session: possible unexpected exit; confidence {_crashReview.Confidence}; signature {_crashReview.Signature}; last stage {_crashReview.LastStage}");
            }

            lines.Add(
                (chinese ? "报告历史：" : "Report history: ")
                + $"{ToolkitReportHistory.ReportCount()}/{ToolkitReportHistory.MaxReports}; "
                + _manualSaveStatus);
            lines.Add(
                chinese
                    ? "网络数值均标注本机视角；可选通道只发送已协商的有界状态，不主动探测。运行中恢复在 STS2 v0.109.1 为 Unsupported。"
                    : "Network metrics are local-view; the optional plane sends only negotiated bounded state, never active probes. Running rejoin is Unsupported on STS2 v0.109.1.");
            return string.Join('\n', lines);
        }
    }

    public static IReadOnlyList<string> BuildReportDetails()
    {
        lock (Sync)
        {
            string role = _service?.Type switch
            {
                NetGameType.Host => "Host",
                NetGameType.Client => "Client",
                _ when _service == null => "Offline",
                _ => "Unknown"
            };
            List<string> lines =
            [
                "Session ID: "
                    + ToolkitDiagnosticsRuntime.FullSessionId(),
                "Role: " + role,
                "Network: local"
                    + $"|connected={(_service?.IsConnected == true ? 1 : 0)}"
                    + $"|type={role}"
            ];
            foreach (PeerRuntime peer in Peers.Values
                         .OrderBy(peer => peer.Identity.Ordinal))
            {
                NetworkSample sample = peer.LastSample;
                lines.Add(
                    $"Network: {peer.Identity.Label}"
                    + $"|connected={(peer.HasSample && sample.Connected ? 1 : 0)}"
                    + $"|pingMs={Invariant(sample.PingMsec)}"
                    + $"|loss={Invariant(sample.PacketLoss)}"
                    + $"|heartbeatSec={Invariant(sample.HeartbeatAgeSeconds)}"
                    + $"|loading={(peer.HasSample && sample.RemoteIsLoading ? 1 : 0)}");
            }

            long now = Stopwatch.GetTimestamp();
            IReadOnlyList<TimelineEntry> timeline =
                Recorder.Snapshot(FlightRecorder.Capacity);
            if (timeline.Count == 0)
            {
                lines.Add("Timeline: unavailable");
            }
            else
            {
                foreach (TimelineEntry entry in timeline)
                {
                    long ageMilliseconds = Math.Max(
                        0,
                        (long)((now - entry.CapturedAtTicks)
                            * 1000d
                            / Stopwatch.Frequency));
                    lines.Add(
                        $"Timeline: {ageMilliseconds}"
                        + $"|{entry.Kind}|{entry.Code}|{entry.Subject}|{entry.Count}");
                }
            }

            return lines;
        }
    }

    public static void SaveLatestReport()
    {
        bool saved = ToolkitReportHistory.TrySaveLatest(out string status);
        lock (Sync)
        {
            _manualSaveStatus = status;
            Recorder.Record(
                saved ? TimelineEventKind.Progress : TimelineEventKind.Warning,
                saved ? "CG-REPORT-SAVED" : "CG-REPORT-SAVE-FAILED",
                "local",
                status);
            Alerts.Add(
                saved ? AlertSeverity.Info : AlertSeverity.Warning,
                saved ? "CG-REPORT-SAVED" : "CG-REPORT-SAVE-FAILED",
                "local",
                status,
                Stopwatch.GetTimestamp());
        }
    }

    public static async Task RequestReconnect()
    {
        ulong ticket;
        NMainMenu menu;
        lock (Sync)
        {
            if (!CanReconnectLocked()
                || Interlocked.CompareExchange(
                    ref _reconnectInProgress,
                    1,
                    0) != 0)
            {
                return;
            }

            if (!_mainMenu!.TryGetTarget(out menu!))
            {
                _reconnectStatus = "Main menu unavailable";
                Volatile.Write(ref _reconnectInProgress, 0);
                return;
            }

            ticket = _reconnectTicket!.Value;
            _reconnectTicket = null;
            _reconnectStatus = "Validating local environment";
            Recorder.Record(
                TimelineEventKind.Lifecycle,
                "CG-RECONNECT-CLICKED",
                "local",
                "User requested one native lobby re-entry.");
        }

        try
        {
            if (!CompatibilityGate.CanProceed(out string reason))
            {
                CompatibilityGate.ReportBlocked("manual lobby re-entry", reason);
                lock (Sync)
                {
                    _reconnectStatus = "Blocked by local Guard verification";
                }
                return;
            }

            lock (Sync)
            {
                _reconnectStatus = "Connecting through native Steam lobby flow";
            }

            await menu.JoinGame(
                SteamClientConnectionInitializer.FromLobby(ticket));
            lock (Sync)
            {
                _reconnectStatus = "Native lobby re-entry completed";
                Recorder.Record(
                    TimelineEventKind.Lifecycle,
                    "CG-RECONNECT-COMPLETE",
                    "local",
                    "Native lobby flow returned.");
                TryPlayCue(SoundCue.ReconnectResult);
            }
        }
        catch (Exception ex)
        {
            lock (Sync)
            {
                _reconnectStatus = "Failed: "
                    + FlightRecorder.BoundedText(
                        ex.GetBaseException().GetType().Name,
                        64);
                _reconnectCooldownUntilTicks = Stopwatch.GetTimestamp()
                    + 5L * Stopwatch.Frequency;
                Recorder.Record(
                    TimelineEventKind.Error,
                    "CG-RECONNECT-FAILED",
                    "local",
                    ex.GetBaseException().GetType().Name);
                Alerts.Add(
                    AlertSeverity.Warning,
                    "CG-RECONNECT-FAILED",
                    "local",
                    "Native lobby re-entry failed.",
                    Stopwatch.GetTimestamp());
                TryPlayCue(SoundCue.ReconnectResult);
            }
        }
        finally
        {
            Volatile.Write(ref _reconnectInProgress, 0);
        }
    }

    public static void Detach()
    {
        lock (Sync)
        {
            UnsubscribeService();
            Session.End();
            Recorder.Clear();
            Alerts.Clear();
            Identities.Clear();
            Peers.Clear();
            PlayersReadyToEndTurn.Clear();
            RemoteChoiceWaits.Clear();
            MapSubmitted.Clear();
            PublicActions.Clear();
            _mapTracking = false;
            CueLimiter.Clear();
            RuntimeFuse.Reset();
            Progress.Reset(Stopwatch.GetTimestamp());
            _startLobby = null;
            _loadLobby = null;
            _runLobby = null;
            _networkState = null;
            _candidateLobbyTicket = null;
            _reconnectTicket = null;
            _wasConnected = false;
            _phase = ToolkitSessionPhase.None;
            _toolkitNode = null;
            _panelOpened = false;
        }

        ToolkitCrashMarker.Finish();
    }

    private static void TickCore()
    {
        long now = Stopwatch.GetTimestamp();
        if (now >= _nextHandSampleTicks)
        {
            _nextHandSampleTicks =
                now + Stopwatch.Frequency / 4;
            ToolkitForensicsRuntime.SampleLocalHand();
        }

        if (now < _nextSampleTicks)
        {
            return;
        }

        _nextSampleTicks = now + Stopwatch.Frequency;
        lock (Sync)
        {
            INetGameService? discovered = DiscoverService();
            if (!ReferenceEquals(discovered, _service))
            {
                SetService(discovered);
            }

            bool connected = _service is
            {
                IsConnected: true,
                Type: NetGameType.Host or NetGameType.Client
            };
            if (connected && !_wasConnected)
            {
                bool recovery = _reconnectStatus.Contains(
                    "Connecting",
                    StringComparison.OrdinalIgnoreCase);
                if (!recovery)
                {
                    Recorder.Clear();
                    Alerts.Clear();
                    Identities.Clear();
                    Peers.Clear();
                    RemoteChoiceWaits.Clear();
                    MapSubmitted.Clear();
                    PublicActions.Clear();
                    _mapTracking = false;
                    LocalLoadingHistory.Clear();
                    _lastLocalLoadingSeconds = null;
                    _localLoadingSinceTicks = 0;
                }

                Session.Begin();
                Progress.Reset(now);
                Recorder.Record(
                    TimelineEventKind.Lifecycle,
                    recovery ? "CG-SESSION-REJOINED" : "CG-SESSION-START",
                    "local",
                    _service!.Type.ToString());
                if (recovery)
                {
                    TryPlayCue(SoundCue.Rejoined);
                }
            }

            _wasConnected = connected;
            if (connected
                && _joinStage == ToolkitJoinStage.ConnectingPlatform)
            {
                SetJoinStage(ToolkitJoinStage.WaitingInitialInfo);
            }
            SampleLoading(now, connected);
            SamplePeers(now, connected);
            UpdateWait(now, connected);

            string value = connected
                ? $"{_service!.Type}; connected; phase={_phase}; local-loading={_service.IsGameLoading}"
                : "Not in a connected multiplayer session";
            _networkState = Observation<string>.Capture(
                value,
                ObservationSource.NativeAuthoritative,
                ObservationConfidence.High,
                TimeSpan.FromSeconds(1.25),
                ++_observationSequence,
                now);

            if (_reconnectTicket.HasValue
                && now > _reconnectTicketExpiryTicks)
            {
                _reconnectTicket = null;
                _reconnectStatus = "Expired";
            }

            ObservePackageChanges(now);
            TickDiagnostics();
        }
    }

    private static void TickDiagnostics()
    {
        if (_service == null)
        {
            return;
        }

        ToolkitStateFlags flags = ToolkitStateFlags.ToolkitAvailable;
        if (_service.IsConnected)
        {
            flags |= ToolkitStateFlags.Connected;
        }

        if (_packageObservation == "stable")
        {
            flags |= ToolkitStateFlags.GuardHealthy;
        }

        bool readyKnown = false;
        bool ready = false;
        if (_startLobby != null)
        {
            LobbyPlayer localPlayer = _startLobby.LocalPlayer;
            readyKnown = localPlayer.id == _service.NetId;
            ready = readyKnown && localPlayer.isReady;
        }
        else if (_loadLobby != null)
        {
            readyKnown = _loadLobby.ConnectedPlayerIds.Contains(
                _service.NetId);
            ready = readyKnown
                && _loadLobby.IsPlayerReady(_service.NetId);
        }
        else if (_runLobby != null)
        {
            readyKnown = true;
            ready = PlayersReadyToEndTurn.Contains(_service.NetId);
        }

        if (readyKnown)
        {
            flags |= ToolkitStateFlags.ReadyKnown;
            if (ready)
            {
                flags |= ToolkitStateFlags.Ready;
            }
        }

        if (_mapTracking)
        {
            flags |= ToolkitStateFlags.MapKnown;
            if (MapSubmitted.Contains(_service.NetId))
            {
                flags |= ToolkitStateFlags.MapSubmitted;
            }
        }

        ToolkitStateRow localState = new(
            0,
            flags,
            (byte)_wait.Reason,
            (byte)_wait.Confidence,
            (ushort)Math.Min(
                ushort.MaxValue,
                _wait.DurationTicks / Stopwatch.Frequency),
            ushort.MaxValue,
            ushort.MaxValue);
        ToolkitPeerObservation[] peers = Peers
            .Select(pair => new ToolkitPeerObservation(
                pair.Key,
                pair.Value.HasSample
                    && pair.Value.LastSample.Connected,
                pair.Value.HasSample
                    ? pair.Value.LastSample.PingMsec
                    : null,
                pair.Value.HasSample
                    ? pair.Value.LastSample.PacketLoss
                    : null,
                pair.Value.HasSample
                    && pair.Value.LastSample.RemoteIsLoading,
                RemoteChoiceWaits.ContainsKey(pair.Key),
                _mapTracking
                    ? MapSubmitted.Contains(pair.Key)
                    : null))
            .ToArray();
        ToolkitDiagnosticsRuntime.Tick(
            CurrentPeerIds().ToArray(),
            localState,
            peers);
    }

    private static void ObservePackageChanges(long now)
    {
        if (now < _nextPackageObservationTicks)
        {
            return;
        }

        _nextPackageObservationTicks = now + 5L * Stopwatch.Frequency;
        string previous = _packageObservation;
        try
        {
            FingerprintSnapshot snapshot = ModFingerprint.ValidateQuick();
            _packageObservation = snapshot.Errors.Count == 0
                ? "stable"
                : FlightRecorder.BoundedText(
                    string.Join("; ", snapshot.Errors),
                    512);
        }
        catch (Exception ex)
        {
            _packageObservation =
                "unknown/unavailable (" + ex.GetType().Name + ")";
        }

        if (!string.Equals(
                previous,
                _packageObservation,
                StringComparison.Ordinal)
            && _packageObservation != "stable")
        {
            Recorder.Record(
                TimelineEventKind.Warning,
                "CG-PACKAGE-CHANGED",
                "local",
                _packageObservation);
            Alerts.Add(
                AlertSeverity.Warning,
                "CG-PACKAGE-CHANGED",
                "local",
                _packageObservation,
                now);
        }
    }

    private static INetGameService? DiscoverService()
    {
        if (_startLobby != null)
        {
            return _startLobby.NetService;
        }

        if (_loadLobby != null)
        {
            return _loadLobby.NetService;
        }

        try
        {
            INetGameService? runService = RunManager.Instance.NetService;
            if (runService != null)
            {
                return runService;
            }
        }
        catch
        {
        }

        return null;
    }

    private static void SetService(INetGameService? service)
    {
        if (ReferenceEquals(_service, service))
        {
            CaptureTicketCandidate();
            return;
        }

        UnsubscribeService();
        _service = service;
        if (_service == null)
        {
            return;
        }

        _service.Disconnected += OnDisconnected;
        ToolkitDiagnosticsRuntime.SetService(_service);
        CaptureTicketCandidate();
    }

    private static void CaptureTicketCandidate()
    {
        _candidateLobbyTicket = null;
        if (_service is
            {
                Type: NetGameType.Client,
                Platform: PlatformType.Steam,
                IsConnected: true
            }
            && _phase is ToolkitSessionPhase.NewRunLobby
                or ToolkitSessionPhase.LoadedRunLobby
            && ulong.TryParse(
                _service.GetRawLobbyIdentifier(),
                out ulong lobbyId))
        {
            _candidateLobbyTicket = lobbyId;
        }
    }

    private static void UnsubscribeService()
    {
        if (_service != null)
        {
            _service.Disconnected -= OnDisconnected;
        }

        ToolkitDiagnosticsRuntime.SetService(null);
        _service = null;
    }

    private static void OnDisconnected(NetErrorInfo info)
    {
        try
        {
            lock (Sync)
            {
                string reason = info.GetReason().ToString();
                bool safePreRunTimeout = reason == "Timeout"
                    && _phase is ToolkitSessionPhase.NewRunLobby
                        or ToolkitSessionPhase.LoadedRunLobby
                    && _candidateLobbyTicket.HasValue;
                if (safePreRunTimeout)
                {
                    _reconnectTicket = _candidateLobbyTicket;
                    _reconnectTicketExpiryTicks = Stopwatch.GetTimestamp()
                        + TicketTtlSeconds * Stopwatch.Frequency;
                    _reconnectStatus = "Available for pre-run lobby";
                }
                else
                {
                    _reconnectTicket = null;
                    _reconnectStatus = _phase == ToolkitSessionPhase.Running
                        ? "RunInProgress / Unsupported"
                        : $"Unavailable ({FlightRecorder.BoundedText(reason, 64)})";
                }

                _candidateLobbyTicket = null;
                _wasConnected = false;
                Session.End();
                RemoteChoiceWaits.Clear();
                MapSubmitted.Clear();
                PublicActions.Clear();
                _mapTracking = false;
                ToolkitDiagnosticsRuntime.OnDisconnected();
                Recorder.Record(
                    TimelineEventKind.Network,
                    "CG-DISCONNECTED",
                    "local",
                    reason);
                Alerts.Add(
                    AlertSeverity.Warning,
                    "CG-DISCONNECTED",
                    "local",
                    "Native multiplayer disconnected.",
                    Stopwatch.GetTimestamp());
                TryPlayCue(SoundCue.Disconnected);
            }
        }
        catch (Exception ex)
        {
            Main.Log.Error(
                $"Toolkit disconnect observation was contained: {ex}");
        }
    }

    private static void SampleLoading(long now, bool connected)
    {
        bool localLoading = connected && _service!.IsGameLoading;
        if (localLoading && _localLoadingSinceTicks == 0)
        {
            _localLoadingSinceTicks = now;
            Recorder.Record(
                TimelineEventKind.Progress,
                "CG-LOCAL-LOADING-START",
                "local",
                "Native loading flag enabled.");
        }
        else if (!localLoading && _localLoadingSinceTicks != 0)
        {
            _lastLocalLoadingSeconds =
                (now - _localLoadingSinceTicks) / (double)Stopwatch.Frequency;
            LocalLoadingHistory.Enqueue(
                _lastLocalLoadingSeconds.Value);
            while (LocalLoadingHistory.Count > 5)
            {
                LocalLoadingHistory.Dequeue();
            }
            _localLoadingSinceTicks = 0;
            Recorder.Record(
                TimelineEventKind.Progress,
                "CG-LOCAL-LOADING-END",
                "local",
                $"duration={_lastLocalLoadingSeconds:F1}s");
        }
    }

    private static void SamplePeers(long now, bool connected)
    {
        HashSet<ulong> observed = [];
        if (connected)
        {
            foreach (ulong peerId in CurrentPeerIds())
            {
                if (peerId == _service!.NetId)
                {
                    continue;
                }

                observed.Add(peerId);
                PeerIdentity identity = Identities.GetOrAdd(peerId);
                if (!Peers.TryGetValue(peerId, out PeerRuntime? peer))
                {
                    peer = new PeerRuntime(identity);
                    Peers.Add(peerId, peer);
                    Recorder.Record(
                        TimelineEventKind.Lifecycle,
                        "CG-PEER-OBSERVED",
                        identity.Label,
                        "Peer entered the local observation set.");
                }

                ConnectionStats? stats = null;
                try
                {
                    stats = _service.GetStatsForPeer(peerId);
                }
                catch (Exception ex)
                {
                    Recorder.Record(
                        TimelineEventKind.Warning,
                        "CG-NET-STATS-UNAVAILABLE",
                        identity.Label,
                        ex.GetType().Name);
                }

                float? ping = stats != null
                    && float.IsFinite(stats.PingMsec)
                    && stats.PingMsec >= 0
                        ? stats.PingMsec
                        : null;
                float? loss = stats != null
                    && float.IsFinite(stats.PacketLoss)
                    && stats.PacketLoss is >= 0 and <= 1
                        ? stats.PacketLoss
                        : null;
                double? heartbeatAge = null;
                if (stats?.LastReceivedTime is ulong lastReceived)
                {
                    ulong nativeNow = Godot.Time.GetTicksMsec();
                    if (nativeNow >= lastReceived)
                    {
                        heartbeatAge = (nativeNow - lastReceived) / 1000d;
                    }
                }

                NetworkSample sample = new(
                    now,
                    ping,
                    loss,
                    heartbeatAge,
                    stats?.RemoteIsLoading ?? false,
                    Connected: true);
                peer.History.Add(sample);
                peer.LastSample = sample;
                peer.HasSample = true;
                if (sample.RemoteIsLoading && !peer.LoadingSinceTicks.HasValue)
                {
                    peer.LoadingSinceTicks = now;
                }
                else if (!sample.RemoteIsLoading
                    && peer.LoadingSinceTicks is long loadingSince)
                {
                    peer.LastLoadingSeconds =
                        (now - loadingSince) / (double)Stopwatch.Frequency;
                    peer.LoadingSinceTicks = null;
                }

                if (loss > 0.25f
                    || heartbeatAge > (sample.RemoteIsLoading ? 8 : 3))
                {
                    Alerts.Add(
                        AlertSeverity.Warning,
                        "CG-NETWORK-DEGRADED",
                        identity.Label,
                        "Native connection statistics degraded.",
                        now);
                }
            }
        }

        foreach ((ulong peerId, PeerRuntime peer) in Peers)
        {
            if (!observed.Contains(peerId))
            {
                peer.History.Add(new NetworkSample(
                    now,
                    null,
                    null,
                    null,
                    RemoteIsLoading: false,
                    Connected: false));
                peer.LastSample = peer.History.Snapshot()[^1];
                peer.HasSample = true;
            }
        }
    }

    private static IEnumerable<ulong> CurrentPeerIds()
    {
        if (_startLobby != null)
        {
            return _startLobby.Players.Select(player => player.id).ToArray();
        }

        if (_loadLobby != null)
        {
            return _loadLobby.ConnectedPlayerIds.ToArray();
        }

        if (_runLobby != null)
        {
            return _runLobby.ConnectedPlayerIds.ToArray();
        }

        return [];
    }

    private static void UpdateWait(long now, bool connected)
    {
        WaitReasonCode reason;
        ObservationConfidence confidence;
        string evidence;
        bool canFlagStall = false;

        if (!connected)
        {
            reason = WaitReasonCode.NetworkDisconnected;
            confidence = ObservationConfidence.High;
            evidence = "native service disconnected";
        }
        else if (_service!.IsGameLoading)
        {
            reason = WaitReasonCode.RemoteLoading;
            confidence = ObservationConfidence.High;
            evidence = "native local loading flag";
        }
        else
        {
            PeerRuntime? loadingPeer = Peers.Values.FirstOrDefault(
                peer => peer.HasSample
                    && peer.LastSample.RemoteIsLoading);
            if (loadingPeer != null)
            {
                reason = WaitReasonCode.RemoteLoading;
                confidence = ObservationConfidence.High;
                evidence = $"{loadingPeer.Identity.Label} native remote-loading flag";
            }
            else if (_phase == ToolkitSessionPhase.Running)
            {
                GameAction? action = null;
                string syncState = "Unknown";
                try
                {
                    action = RunManager.Instance.ActionExecutor
                        ?.CurrentlyRunningAction;
                    syncState = RunManager.Instance.ActionQueueSynchronizer
                        ?.CombatState.ToString()
                        ?? "Unknown";
                }
                catch
                {
                }

                if (RemoteChoiceWaits.Count > 0)
                {
                    ulong waiting = RemoteChoiceWaits.Keys.First();
                    reason = WaitReasonCode.PlayerChoice;
                    confidence = ObservationConfidence.High;
                    evidence = Identities.GetOrAdd(waiting).Label
                        + " is in an observed native remote-choice wait; contents hidden";
                }
                else if (action != null)
                {
                    reason = WaitReasonCode.ActionExecuting;
                    confidence = ObservationConfidence.Medium;
                    evidence = "native action executor is active";
                }
                else if (syncState == "PlayPhase")
                {
                    ulong? waitingPeer = CurrentPeerIds()
                        .FirstOrDefault(peerId =>
                            !PlayersReadyToEndTurn.Contains(peerId));
                    if (waitingPeer.HasValue && waitingPeer.Value != 0)
                    {
                        PeerIdentity identity = Identities.GetOrAdd(
                            waitingPeer.Value);
                        reason = WaitReasonCode.PlayerTurn;
                        confidence = ObservationConfidence.High;
                        evidence = $"{identity.Label} not ready to end turn";
                    }
                    else
                    {
                        reason = WaitReasonCode.Unknown;
                        confidence = ObservationConfidence.Low;
                        evidence = "play phase; no active native action";
                        canFlagStall = true;
                    }
                }
                else
                {
                    reason = WaitReasonCode.PhaseSync;
                    confidence = ObservationConfidence.Medium;
                    evidence = "native combat sync phase " + syncState;
                    canFlagStall = true;
                }
            }
            else
            {
                reason = WaitReasonCode.PhaseSync;
                confidence = ObservationConfidence.Medium;
                evidence = "pre-run lobby";
            }
        }

        bool degradedNetwork = connected
            && Peers.Values.Any(peer =>
                peer.HasSample
                && (peer.LastSample.PacketLoss is > 0.25f
                    || peer.LastSample.HeartbeatAgeSeconds
                        > (peer.LastSample.RemoteIsLoading ? 8 : 3)));
        if (degradedNetwork
            && reason is WaitReasonCode.ActionExecuting
                or WaitReasonCode.PhaseSync
                or WaitReasonCode.Unknown)
        {
            confidence = ObservationConfidence.Low;
            evidence = "conflicting evidence: "
                + evidence
                + "; native connection statistics are also degraded";
            canFlagStall = false;
        }

        string progressToken = string.Join(
            '|',
            _phase,
            _joinStage,
            connected,
            _service?.IsGameLoading ?? false,
            Peers.Count,
            PlayersReadyToEndTurn.Count,
            RemoteChoiceWaits.Count,
            MapSubmitted.Count,
            evidence);
        WaitAssessment previous = _wait;
        _wait = Progress.Observe(
            progressToken,
            reason,
            confidence,
            evidence,
            canFlagStall,
            now);
        if (previous.Reason != _wait.Reason)
        {
            Recorder.Record(
                TimelineEventKind.Progress,
                "CG-WAIT-REASON",
                "local",
                _wait.Reason.ToString());
            if (_wait.Reason == WaitReasonCode.PlayerTurn
                && _service != null
                && !PlayersReadyToEndTurn.Contains(_service.NetId))
            {
                TryPlayCue(SoundCue.WaitingLocal);
            }
        }

        if (_wait.DurationTicks >= 15L * Stopwatch.Frequency
            && now - _lastWaitAlertTicks >= 60L * Stopwatch.Frequency)
        {
            _lastWaitAlertTicks = now;
            Alerts.Add(
                AlertSeverity.Warning,
                "CG-WAIT-LONG",
                "local",
                "Waiting longer than the fixed 15-second notice threshold.",
                now);
        }

        if (_wait.Stall is StallLevel.Suspected
            or StallLevel.HighlySuspected)
        {
            Alerts.Add(
                AlertSeverity.Warning,
                "CG-POSSIBLE-STALL",
                "local",
                "Read-only progress lease has not advanced; this is not proof of a soft lock.",
                now);
        }
    }

    private static string PeerLine(PeerRuntime peer, bool chinese)
    {
        NetworkSample sample = peer.LastSample;
        string ping = !peer.HasSample || !sample.Connected
            ? (chinese ? "未知" : "unknown")
            : sample.PingMsec.HasValue
                ? $"{sample.PingMsec.Value:F0} ms"
                : (chinese ? "等待数据" : "waiting for data");
        string loss = sample.PacketLoss.HasValue
            ? $"{sample.PacketLoss.Value * 100:F1}%"
            : (chinese ? "未知" : "unknown");
        string heartbeat = sample.HeartbeatAgeSeconds.HasValue
            ? $"{sample.HeartbeatAgeSeconds.Value:F1}s"
            : (chinese ? "未知" : "unknown");
        string loading = sample.RemoteIsLoading
            ? (chinese ? "；加载中" : "; loading")
            : string.Empty;
        return $"{peer.Identity.Shape} {peer.Identity.Label} "
            + $"[{peer.Identity.Color}]: "
            + (chinese
                ? $"RTT {ping}；丢包 {loss}；心跳 {heartbeat}"
                : $"RTT {ping}; loss {loss}; heartbeat {heartbeat}")
            + loading;
    }

    private static void TryPlayCue(SoundCue cue)
    {
        if (!_preferences.SoundEnabled
            || _preferences.SoundVolume <= 0f
            || (cue is SoundCue.AllReady or SoundCue.WaitingLocal)
                && !_preferences.ReadyCues
            || cue == SoundCue.Disconnected && !_preferences.NetworkCues
            || cue == SoundCue.Rejoined && !_preferences.RejoinCues
            || cue == SoundCue.ReconnectResult && !_preferences.ResultCues)
        {
            return;
        }

        if (!CueLimiter.TryTake(
                (int)cue,
                Stopwatch.GetTimestamp()))
        {
            return;
        }

        try
        {
            string path = cue switch
            {
                SoundCue.AllReady => FmodSfx.uiTickboxOn,
                SoundCue.WaitingLocal => FmodSfx.uiClick,
                SoundCue.Disconnected => FmodSfx.backButton,
                SoundCue.Rejoined => FmodSfx.timelineUnlock,
                _ => FmodSfx.uiClick
            };
            NAudioManager.Instance?.PlayOneShot(
                path,
                _preferences.SoundVolume);
        }
        catch (Exception ex)
        {
            Recorder.Record(
                TimelineEventKind.Warning,
                "CG-SOUND-UNAVAILABLE",
                "local",
                ex.GetType().Name);
        }
    }

    private static string NetworkSummary(
        PeerRuntime peer,
        bool chinese)
    {
        float[] pings = peer.History.Snapshot()
            .Where(sample => sample.Connected && sample.PingMsec.HasValue)
            .Select(sample => sample.PingMsec!.Value)
            .Order()
            .ToArray();
        if (pings.Length < 2)
        {
            return chinese ? "近 60 秒：数据不足" : "Last 60s: insufficient data";
        }

        int p95Index = (int)Math.Ceiling(pings.Length * 0.95) - 1;
        float peakLoss = peer.History.Snapshot()
            .Where(sample => sample.PacketLoss.HasValue)
            .Select(sample => sample.PacketLoss!.Value)
            .DefaultIfEmpty()
            .Max();
        return chinese
            ? $"近 60 秒：p95 RTT {pings[p95Index]:F0} ms，丢包峰值 {peakLoss * 100:F1}%"
            : $"Last 60s: p95 RTT {pings[p95Index]:F0} ms, peak loss {peakLoss * 100:F1}%";
    }

    private static string Invariant<T>(T? value)
        where T : struct, IFormattable =>
        value.HasValue
            ? value.Value.ToString(null, CultureInfo.InvariantCulture)
            : "unavailable";

    private static void PrunePublicActions(long now)
    {
        PublicActions.RemoveAll(action =>
            action.CompletedTicks is long completed
            && now - completed > 15L * Stopwatch.Frequency);
    }

    private static string PublicActionCategory(GameAction action) =>
        action.GetType().Name switch
        {
            "PlayCardAction" or "NetPlayCardAction" => "play-card",
            "UsePotionAction" or "NetUsePotionAction"
                or "DiscardPotionGameAction"
                or "NetDiscardPotionGameAction" => "potion",
            "EndPlayerTurnAction" or "NetEndPlayerTurnAction"
                or "UndoEndPlayerTurnAction"
                or "NetUndoEndPlayerTurnAction"
                or "ReadyToBeginEnemyTurnAction"
                or "NetReadyToBeginEnemyTurnAction" => "end-turn",
            "MoveToMapCoordAction" or "NetMoveToMapCoordAction"
                or "VoteForMapCoordAction"
                or "NetVoteForMapCoordAction"
                or "VoteToMoveToNextActAction"
                or "NetVoteToMoveToNextActAction" => "system",
            _ => "custom-action"
        };

    private static bool CanReconnectLocked()
    {
        long now = Stopwatch.GetTimestamp();
        return _reconnectTicket.HasValue
            && now <= _reconnectTicketExpiryTicks
            && now >= _reconnectCooldownUntilTicks
            && Volatile.Read(ref _reconnectInProgress) == 0
            && _mainMenu != null
            && _mainMenu.TryGetTarget(out _);
    }

    private static void SetJoinStage(ToolkitJoinStage stage)
    {
        lock (Sync)
        {
            if (_joinStage == stage)
            {
                return;
            }

            _joinStage = stage;
            _joinStageSinceTicks = Stopwatch.GetTimestamp();
            ToolkitCrashMarker.UpdateStage(stage.ToString());
            Recorder.Record(
                TimelineEventKind.Progress,
                "CG-JOIN-STAGE",
                "local",
                stage.ToString());
        }
    }

    private static string JoinStageDuration()
    {
        if (_joinStageSinceTicks == 0)
        {
            return string.Empty;
        }

        double seconds = (Stopwatch.GetTimestamp() - _joinStageSinceTicks)
            / (double)Stopwatch.Frequency;
        return $" ({seconds:F1}s)"
            + (_joinStage == ToolkitJoinStage.Failed
                && !string.IsNullOrEmpty(_joinFailure)
                    ? $" {_joinFailure}"
                    : string.Empty);
    }

    private static string WaitLabel(WaitReasonCode reason, bool chinese) =>
        (reason, chinese) switch
        {
            (WaitReasonCode.None, true) => "无",
            (WaitReasonCode.None, false) => "none",
            (WaitReasonCode.NetworkDisconnected, true) => "网络已断开",
            (WaitReasonCode.NetworkDisconnected, false) => "network disconnected",
            (WaitReasonCode.RemoteLoading, true) => "远端加载中",
            (WaitReasonCode.RemoteLoading, false) => "loading",
            (WaitReasonCode.PlayerTurn, true) => "玩家结束回合",
            (WaitReasonCode.PlayerTurn, false) => "player to end turn",
            (WaitReasonCode.PlayerChoice, true) => "玩家选择",
            (WaitReasonCode.PlayerChoice, false) => "player choice",
            (WaitReasonCode.ActionExecuting, true) => "原生行动执行",
            (WaitReasonCode.ActionExecuting, false) => "native action",
            (WaitReasonCode.PhaseSync, true) => "阶段同步",
            (WaitReasonCode.PhaseSync, false) => "phase synchronization",
            (_, true) => "原因未知",
            _ => "unknown reason"
        };

    private static string JoinStageLabel(
        ToolkitJoinStage stage,
        bool chinese) =>
        (stage, chinese) switch
        {
            (ToolkitJoinStage.Idle, true) => "空闲",
            (ToolkitJoinStage.Idle, false) => "idle",
            (ToolkitJoinStage.ConnectingPlatform, true) => "连接平台",
            (ToolkitJoinStage.ConnectingPlatform, false) => "connecting platform",
            (ToolkitJoinStage.WaitingInitialInfo, true) => "等待初始信息",
            (ToolkitJoinStage.WaitingInitialInfo, false) => "waiting for initial info",
            (ToolkitJoinStage.ValidatingEnvironment, true) => "验证版本/Mod/ModelDb",
            (ToolkitJoinStage.ValidatingEnvironment, false) => "validating version/Mods/ModelDb",
            (ToolkitJoinStage.WaitingNewRunLobby, true) => "等待新局大厅",
            (ToolkitJoinStage.WaitingNewRunLobby, false) => "waiting for new-run lobby",
            (ToolkitJoinStage.WaitingLoadedRunLobby, true) => "等待读档大厅",
            (ToolkitJoinStage.WaitingLoadedRunLobby, false) => "waiting for loaded-run lobby",
            (ToolkitJoinStage.RunInProgressUnsupported, true) => "RunInProgress / Unsupported",
            (ToolkitJoinStage.RunInProgressUnsupported, false) => "RunInProgress / Unsupported",
            (ToolkitJoinStage.Complete, true) => "完成",
            (ToolkitJoinStage.Complete, false) => "complete",
            (ToolkitJoinStage.Failed, true) => "失败",
            _ => "failed"
        };
}

internal sealed class ToolkitNode : Node
{
    private CanvasLayer? _layer;
    private PanelContainer? _panel;
    private ScrollContainer? _scroll;
    private VBoxContainer? _content;
    private Theme? _localTheme;
    private float _appliedUiScale;
    private bool _appliedHighContrast;
    private Label? _label;
    private Button? _reconnectButton;
    private Button? _saveReportButton;
    private Button? _acknowledgeButton;
    private Button? _soundButton;
    private Button? _soundVolumeButton;
    private Button? _readyCueButton;
    private Button? _networkCueButton;
    private Button? _rejoinCueButton;
    private Button? _resultCueButton;
    private Button? _uiScaleButton;
    private Button? _reducedMotionButton;
    private Button? _highContrastButton;
    private OptionButton? _historySelect;
    private Button? _viewReportButton;
    private Button? _copyReportButton;
    private Button? _markReportAButton;
    private Button? _compareReportsButton;
    private Button? _deleteReportButton;
    private Button? _clearReportsButton;
    private Button? _saveEnvironmentButton;
    private Button? _copyEnvironmentButton;
    private Button? _compareEnvironmentButton;
    private Button? _modDoctorButton;
    private Button? _bisectPlanButton;
    private Button? _harmonyReportButton;
    private Button? _knownIssuesButton;
    private OptionButton? _quickStatusSelect;
    private Button? _sendQuickStatusButton;
    private Button? _muteQuickStatusButton;
    private Button? _handConsentButton;
    private Button? _forensicsConsentButton;
    private Button? _contributionsButton;
    private Button? _contributionSharingButton;
    private Label? _historyStatus;
    private string _historySignature = string.Empty;
    private string? _reportAName;
    private long _clearConfirmUntilTicks;
    private long _nextUiTicks;
    private bool _initialized;

    public override void _Ready() => Initialize();

    internal void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            _layer = new CanvasLayer
            {
                Layer = 100
            };
            _panel = new PanelContainer
            {
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            _localTheme = new Theme();
            _panel.Theme = _localTheme;
            _panel.AnchorLeft = 1;
            _panel.AnchorRight = 1;
            _panel.AnchorBottom = 1;
            _panel.OffsetLeft = -540;
            _panel.OffsetRight = -20;
            _panel.OffsetTop = 20;
            _panel.OffsetBottom = -20;

            _scroll = new ScrollContainer
            {
                CustomMinimumSize = new Vector2(500, 0),
                SizeFlagsVertical = Control.SizeFlags.ExpandFill
            };
            _content = new VBoxContainer
            {
                CustomMinimumSize = new Vector2(480, 0),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            _label = new Label
            {
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(480, 0),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            _reconnectButton = new Button
            {
                Text = "Reconnect / 重进大厅",
                FocusMode = Control.FocusModeEnum.All,
                Visible = false
            };
            _reconnectButton.Pressed += OnReconnectPressed;
            _saveReportButton = new Button
            {
                Text = "Save latest report / 保存最近报告",
                FocusMode = Control.FocusModeEnum.All,
                Visible = false
            };
            _saveReportButton.Pressed += OnSaveReportPressed;
            _acknowledgeButton = new Button
            {
                Text = "Acknowledge alerts / 确认警报",
                FocusMode = Control.FocusModeEnum.All,
                Visible = false
            };
            _acknowledgeButton.Pressed += OnAcknowledgePressed;
            _soundButton = new Button
            {
                FocusMode = Control.FocusModeEnum.All
            };
            _soundButton.Pressed += OnSoundPressed;
            _soundVolumeButton = HistoryButton(
                string.Empty,
                ToolkitRuntime.CycleSoundVolume);
            _readyCueButton = HistoryButton(
                string.Empty,
                () => ToolkitRuntime.ToggleCue(ToolkitCueCategory.Ready));
            _networkCueButton = HistoryButton(
                string.Empty,
                () => ToolkitRuntime.ToggleCue(ToolkitCueCategory.Network));
            _rejoinCueButton = HistoryButton(
                string.Empty,
                () => ToolkitRuntime.ToggleCue(ToolkitCueCategory.Rejoin));
            _resultCueButton = HistoryButton(
                string.Empty,
                () => ToolkitRuntime.ToggleCue(ToolkitCueCategory.Result));
            _uiScaleButton = HistoryButton(
                string.Empty,
                ToolkitRuntime.CycleUiScale);
            _reducedMotionButton = HistoryButton(
                string.Empty,
                ToolkitRuntime.ToggleReducedMotion);
            _highContrastButton = HistoryButton(
                string.Empty,
                ToolkitRuntime.ToggleHighContrast);
            _historySelect = new OptionButton
            {
                FocusMode = Control.FocusModeEnum.All,
                Visible = false
            };
            _viewReportButton = HistoryButton(
                "View report / 查看报告",
                OnViewReportPressed);
            _copyReportButton = HistoryButton(
                "Copy report / 复制报告",
                OnCopyReportPressed);
            _markReportAButton = HistoryButton(
                "Mark selected as A / 将所选标为 A",
                OnMarkReportAPressed);
            _compareReportsButton = HistoryButton(
                "Compare A/B or clipboard pair / 对比 A/B 或剪贴板",
                OnCompareReportsPressed);
            _deleteReportButton = HistoryButton(
                "Delete selected / 删除所选",
                OnDeleteReportPressed);
            _clearReportsButton = HistoryButton(
                "Clear history / 清空历史",
                OnClearReportsPressed);
            _historyStatus = new Label
            {
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            _saveEnvironmentButton = HistoryButton(
                "Save environment / 保存环境",
                () => SetHistoryStatus(
                    ToolkitRuntime.SaveEnvironmentLockfile()));
            _copyEnvironmentButton = HistoryButton(
                "Copy environment / 复制环境",
                () => SetHistoryStatus(
                    ToolkitRuntime.CopyEnvironmentLockfile()));
            _compareEnvironmentButton = HistoryButton(
                "Compare clipboard / 对比剪贴板",
                () => SetHistoryStatus(
                    ToolkitRuntime.CompareClipboardEnvironment()));
            _modDoctorButton = HistoryButton(
                "Open Mod Doctor / 打开 Mod 检查",
                () => ShowReadOnlyReport(
                    "CoopGuard Local Mod Doctor",
                    LocalModDoctor.BuildReport()));
            _bisectPlanButton = HistoryButton(
                "A/B plan from clipboard IDs / 从剪贴板生成二分计划",
                () => ShowReadOnlyReport(
                    "CoopGuard dependency-aware manual A/B plan",
                    ToolkitRuntime.BuildBisectPlanFromClipboard()));
            _harmonyReportButton = HistoryButton(
                "Harmony conflict map / Harmony 冲突图",
                () => ShowReadOnlyReport(
                    "CoopGuard Harmony conflict map",
                    HarmonyConflictReport.Build()));
            _knownIssuesButton = HistoryButton(
                "Built-in known issues / 内置已知问题",
                () => ShowReadOnlyReport(
                    "CoopGuard built-in known issues",
                    KnownIssueCatalog.BuildReport(NGame.GetGameVersion())));
            _quickStatusSelect = new OptionButton
            {
                FocusMode = Control.FocusModeEnum.All,
                Visible = false
            };
            foreach (string status in new[]
                     {
                         "Please wait / 稍等",
                         "Ready to start / 可以开始",
                         "Choosing / 正在选择",
                         "Need to reconnect / 需要重连",
                         "Viewing map / 正在看地图"
                     })
            {
                _quickStatusSelect.AddItem(status);
            }

            _sendQuickStatusButton = HistoryButton(
                "Send fixed status / 发送固定状态",
                OnSendQuickStatusPressed);
            _muteQuickStatusButton = HistoryButton(
                "Mute/unmute statuses / 静音或恢复状态消息",
                () => SetHistoryStatus(
                    ToolkitDiagnosticsRuntime.ToggleQuickStatusMute()));
            _handConsentButton = HistoryButton(
                "Opt in/revoke hand sharing / 同意或撤回手牌共享",
                () => SetHistoryStatus(
                    ToolkitDiagnosticsRuntime.ToggleHandSharingConsent()));
            _forensicsConsentButton = HistoryButton(
                "Opt in/revoke forensics / 同意或撤回分层取证",
                () => SetHistoryStatus(
                    ToolkitDiagnosticsRuntime.ToggleForensicsConsent()));
            _contributionsButton = HistoryButton(
                "Toggle contribution counters / 切换贡献统计",
                () => SetHistoryStatus(
                    ToolkitDiagnosticsRuntime.ToggleContributions()));
            _contributionSharingButton = HistoryButton(
                "Toggle contribution sharing / 切换贡献共享",
                () => SetHistoryStatus(
                    ToolkitDiagnosticsRuntime.ToggleContributionSharing()));
            _content.AddChild(_label);
            _content.AddChild(_reconnectButton);
            _content.AddChild(_saveReportButton);
            _content.AddChild(_acknowledgeButton);
            _content.AddChild(_soundButton);
            _content.AddChild(_soundVolumeButton);
            _content.AddChild(_readyCueButton);
            _content.AddChild(_networkCueButton);
            _content.AddChild(_rejoinCueButton);
            _content.AddChild(_resultCueButton);
            _content.AddChild(_uiScaleButton);
            _content.AddChild(_reducedMotionButton);
            _content.AddChild(_highContrastButton);
            _content.AddChild(_saveEnvironmentButton);
            _content.AddChild(_copyEnvironmentButton);
            _content.AddChild(_compareEnvironmentButton);
            _content.AddChild(_modDoctorButton);
            _content.AddChild(_bisectPlanButton);
            _content.AddChild(_harmonyReportButton);
            _content.AddChild(_knownIssuesButton);
            _content.AddChild(_quickStatusSelect);
            _content.AddChild(_sendQuickStatusButton);
            _content.AddChild(_muteQuickStatusButton);
            _content.AddChild(_handConsentButton);
            _content.AddChild(_forensicsConsentButton);
            _content.AddChild(_contributionsButton);
            _content.AddChild(_contributionSharingButton);
            _content.AddChild(_historySelect);
            _content.AddChild(_viewReportButton);
            _content.AddChild(_copyReportButton);
            _content.AddChild(_markReportAButton);
            _content.AddChild(_compareReportsButton);
            _content.AddChild(_deleteReportButton);
            _content.AddChild(_clearReportsButton);
            _content.AddChild(_historyStatus);
            _scroll.AddChild(_content);
            _panel.AddChild(_scroll);
            _layer.AddChild(_panel);
            AddChild(_layer);

            string userRoot = ProjectSettings.GlobalizePath("user://");
            ToolkitRuntime.InitializePersistence(
                Path.Combine(userRoot, "CoopGuard"),
                Path.Combine(userRoot, "logs", "godot.log"));
            SetControlMode(ToolkitRuntime.PanelOpened);
            ApplyPreferences();
            Main.Log.Info("Toolkit cockpit UI initialized.");
        }
        catch (Exception ex)
        {
            _layer?.QueueFree();
            _layer = null;
            _panel = null;
            _scroll = null;
            _content = null;
            _localTheme = null;
            _label = null;
            _reconnectButton = null;
            _saveReportButton = null;
            _acknowledgeButton = null;
            _soundButton = null;
            _soundVolumeButton = null;
            _readyCueButton = null;
            _networkCueButton = null;
            _rejoinCueButton = null;
            _resultCueButton = null;
            _uiScaleButton = null;
            _reducedMotionButton = null;
            _highContrastButton = null;
            _historySelect = null;
            _viewReportButton = null;
            _copyReportButton = null;
            _markReportAButton = null;
            _compareReportsButton = null;
            _deleteReportButton = null;
            _clearReportsButton = null;
            _saveEnvironmentButton = null;
            _copyEnvironmentButton = null;
            _compareEnvironmentButton = null;
            _modDoctorButton = null;
            _bisectPlanButton = null;
            _harmonyReportButton = null;
            _knownIssuesButton = null;
            _quickStatusSelect = null;
            _sendQuickStatusButton = null;
            _muteQuickStatusButton = null;
            _handConsentButton = null;
            _forensicsConsentButton = null;
            _contributionsButton = null;
            _contributionSharingButton = null;
            _historyStatus = null;
            Main.Log.Error(
                $"Optional cockpit HUD failed open; Guard remains active: {ex}");
        }
    }

    public override void _Process(double delta) => Refresh();

    internal void Refresh()
    {
        Initialize();
        try
        {
            ToolkitRuntime.Tick();
            long now = Stopwatch.GetTimestamp();
            if (now < _nextUiTicks)
            {
                return;
            }

            _nextUiTicks = now + Stopwatch.Frequency / 4;
            if (_panel != null)
            {
                _panel.Visible = ToolkitRuntime.ShouldShowHud;
            }

            if (_label != null)
            {
                string text = ToolkitRuntime.HudText(IsChinese());
                if (_label.Text != text)
                {
                    _label.Text = text;
                }
            }
            bool quickStatusAvailable =
                ToolkitDiagnosticsRuntime.CanSendQuickStatus();
            if (_sendQuickStatusButton != null)
            {
                _sendQuickStatusButton.Visible =
                    ToolkitRuntime.PanelOpened && quickStatusAvailable;
            }

            if (_quickStatusSelect != null)
            {
                _quickStatusSelect.Visible =
                    ToolkitRuntime.PanelOpened && quickStatusAvailable;
            }

            if (_reconnectButton != null)
            {
                _reconnectButton.Visible = ToolkitRuntime.CanReconnect;
                _reconnectButton.Disabled = !ToolkitRuntime.CanReconnect;
            }

            if (_saveReportButton != null)
            {
                _saveReportButton.Visible = ToolkitReportHistory.HasLatest;
            }

            if (_acknowledgeButton != null)
            {
                _acknowledgeButton.Visible =
                    ToolkitRuntime.ActiveAlertCount > 0;
            }

            if (_soundButton != null)
            {
                _soundButton.Text = ToolkitRuntime.SoundEnabled
                    ? "Sound: On / 声音：开"
                    : "Sound: Off / 声音：关";
            }

            ApplyPreferences();
            RefreshReportHistory();
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Toolkit process callback was contained: {ex}");
        }
    }

    public override void _ExitTree()
    {
        try
        {
            if (_reconnectButton != null)
            {
                _reconnectButton.Pressed -= OnReconnectPressed;
            }

            if (_saveReportButton != null)
            {
                _saveReportButton.Pressed -= OnSaveReportPressed;
            }

            if (_acknowledgeButton != null)
            {
                _acknowledgeButton.Pressed -= OnAcknowledgePressed;
            }

            if (_soundButton != null)
            {
                _soundButton.Pressed -= OnSoundPressed;
            }

            ToolkitRuntime.Detach();
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Toolkit detach callback was contained: {ex}");
        }
    }

    private async void OnReconnectPressed()
    {
        try
        {
            await ToolkitRuntime.RequestReconnect();
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Reconnect button callback was contained: {ex}");
        }
    }

    private static void OnSaveReportPressed()
    {
        try
        {
            ToolkitRuntime.SaveLatestReport();
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Report-save callback was contained: {ex}");
        }
    }

    private static void OnAcknowledgePressed()
    {
        try
        {
            ToolkitRuntime.AcknowledgeAlerts();
        }
        catch (Exception ex)
        {
            Main.Log.Error(
                $"Alert acknowledgement callback was contained: {ex}");
        }
    }

    private static void OnSoundPressed()
    {
        try
        {
            ToolkitRuntime.ToggleSound();
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Sound-toggle callback was contained: {ex}");
        }
    }

    private Button HistoryButton(string text, Action callback)
    {
        Button button = new()
        {
            Text = text,
            FocusMode = Control.FocusModeEnum.All,
            Visible = false
        };
        button.Pressed += () =>
        {
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                SetHistoryStatus(
                    "Report history action failed: "
                    + ex.GetType().Name);
                Main.Log.Error(
                    $"Report history callback was contained: {ex}");
            }
        };
        return button;
    }

    internal void SetControlMode(bool opened)
    {
        Button?[] controls =
        [
            _soundButton,
            _soundVolumeButton,
            _readyCueButton,
            _networkCueButton,
            _rejoinCueButton,
            _resultCueButton,
            _uiScaleButton,
            _reducedMotionButton,
            _highContrastButton,
            _contributionSharingButton,
            _saveEnvironmentButton,
            _copyEnvironmentButton,
            _compareEnvironmentButton,
            _modDoctorButton,
            _bisectPlanButton,
            _harmonyReportButton,
            _knownIssuesButton,
            _sendQuickStatusButton,
            _muteQuickStatusButton,
            _handConsentButton,
            _forensicsConsentButton,
            _contributionsButton,
            _viewReportButton,
            _copyReportButton,
            _markReportAButton,
            _compareReportsButton,
            _deleteReportButton,
            _clearReportsButton
        ];
        foreach (Button? control in controls)
        {
            if (control != null)
            {
                control.Visible = opened;
            }
        }

        if (_historyStatus != null)
        {
            _historyStatus.Visible = opened;
        }

        if (_quickStatusSelect != null)
        {
            _quickStatusSelect.Visible = opened;
        }

        RefreshReportHistory();
        if (opened && _soundButton != null)
        {
            _soundButton.GrabFocus();
        }
    }

    private void ApplyPreferences()
    {
        ToolkitPreferences preferences = ToolkitRuntime.Preferences;
        if (_soundVolumeButton != null)
        {
            _soundVolumeButton.Text =
                $"Sound volume: {preferences.SoundVolume:P0} / 声音音量";
            _readyCueButton!.Text =
                $"Ready cues: {(preferences.ReadyCues ? "On" : "Off")} / 准备提示";
            _networkCueButton!.Text =
                $"Network cues: {(preferences.NetworkCues ? "On" : "Off")} / 网络提示";
            _rejoinCueButton!.Text =
                $"Rejoin cues: {(preferences.RejoinCues ? "On" : "Off")} / 重进提示";
            _resultCueButton!.Text =
                $"Result cues: {(preferences.ResultCues ? "On" : "Off")} / 结果提示";
            _uiScaleButton!.Text =
                $"Text/UI scale: {preferences.UiScale:P0} / 界面缩放";
            _reducedMotionButton!.Text =
                $"Reduced motion: {(preferences.ReducedMotion ? "On" : "Off")} / 减少动态";
            _highContrastButton!.Text =
                $"High contrast: {(preferences.HighContrast ? "On" : "Off")} / 高对比";
        }

        if (_localTheme == null
            || (_appliedUiScale == preferences.UiScale
                && _appliedHighContrast == preferences.HighContrast))
        {
            return;
        }

        bool disableHighContrast =
            _appliedHighContrast && !preferences.HighContrast;
        _appliedUiScale = preferences.UiScale;
        _appliedHighContrast = preferences.HighContrast;
        _localTheme.DefaultFontSize =
            (int)Math.Round(18 * preferences.UiScale);
        string[] types = ["Label", "Button", "OptionButton"];
        if (preferences.HighContrast)
        {
            foreach (string type in types)
            {
                _localTheme.SetColor("font_color", type, Colors.White);
                _localTheme.SetColor(
                    "font_outline_color",
                    type,
                    Colors.Black);
                _localTheme.SetConstant("outline_size", type, 2);
            }
        }
        else if (disableHighContrast)
        {
            foreach (string type in types)
            {
                _localTheme.ClearColor("font_color", type);
                _localTheme.ClearColor("font_outline_color", type);
                _localTheme.ClearConstant("outline_size", type);
            }
        }
    }

    internal void ApplyPreferencesNow() => ApplyPreferences();

    private void RefreshReportHistory()
    {
        if (_historySelect == null)
        {
            return;
        }

        IReadOnlyList<string> names = ToolkitReportHistory.ReportNames();
        string signature = string.Join('\n', names);
        if (_historySignature != signature)
        {
            _historySignature = signature;
            _historySelect.Clear();
            foreach (string name in names)
            {
                _historySelect.AddItem(name);
            }

            if (names.Count > 0)
            {
                _historySelect.Select(0);
            }
        }

        bool visible = names.Count > 0 && ToolkitRuntime.PanelOpened;
        if (_reportAName != null && !names.Contains(_reportAName))
        {
            _reportAName = null;
        }

        _historySelect.Visible = visible;
        _viewReportButton!.Visible = visible;
        _copyReportButton!.Visible = visible;
        _markReportAButton!.Visible = visible;
        _compareReportsButton!.Visible = ToolkitRuntime.PanelOpened;
        _deleteReportButton!.Visible = visible;
        _clearReportsButton!.Visible = visible;
    }

    private string? SelectedReportName()
    {
        if (_historySelect == null || _historySelect.ItemCount == 0)
        {
            return null;
        }

        return _historySelect.GetItemText(_historySelect.Selected);
    }

    private void OnViewReportPressed()
    {
        string? name = SelectedReportName();
        if (name == null
            || !ToolkitReportHistory.TryRead(
                name,
                out string report,
                out string status))
        {
            SetHistoryStatus(status: name ?? "No report selected.");
            return;
        }

        ShowReadOnlyReport(name, report);
        SetHistoryStatus(status);
    }

    private void ShowReadOnlyReport(string title, string report)
    {
        string body = FlightRecorder.BoundedText(report, 32 * 1024);
        if (body.Length < report.Length)
        {
            body += "\n\n[Display truncated; copied/exported reports retain their configured bound.]";
        }

        NErrorPopup? popup = NErrorPopup.Create(title, body, false);
        NModalContainer? container = NModalContainer.Instance;
        if (popup != null && container != null && container.OpenModal == null)
        {
            container.Add(popup);
            SetHistoryStatus("Opened " + title + ".");
        }
        else
        {
            popup?.QueueFree();
            SetHistoryStatus("Another modal is open.");
        }
    }

    private void OnCopyReportPressed()
    {
        string? name = SelectedReportName();
        if (name != null
            && ToolkitReportHistory.TryRead(
                name,
                out string report,
                out string status))
        {
            DisplayServer.ClipboardSet(report);
            SetHistoryStatus("Copied " + status);
        }
        else
        {
            SetHistoryStatus(name ?? "No report selected.");
        }
    }

    private void OnMarkReportAPressed()
    {
        _reportAName = SelectedReportName();
        SetHistoryStatus(
            _reportAName == null
                ? "No report selected."
                : "Report A: " + _reportAName
                    + ". Select another report and press Compare.");
    }

    private void OnCompareReportsPressed()
    {
        string? reportBName = SelectedReportName();
        string first;
        string second;
        if (_reportAName != null
            && reportBName != null
            && _reportAName != reportBName
            && ToolkitReportHistory.TryRead(
                _reportAName,
                out first,
                out string firstStatus)
            && ToolkitReportHistory.TryRead(
                reportBName,
                out second,
                out string secondStatus))
        {
            ShowComparison(first, second);
            SetHistoryStatus(
                $"Compared {firstStatus} with {secondStatus}.");
            return;
        }

        string clipboard = DisplayServer.ClipboardGet();
        if (!ToolkitReportComparison.TrySplitClipboard(
                clipboard,
                out first,
                out second,
                out string error))
        {
            SetHistoryStatus(
                _reportAName == reportBName && _reportAName != null
                    ? "Select a different report B, or " + error
                    : error);
            return;
        }

        ShowComparison(first, second);
        SetHistoryStatus("Compared two bounded clipboard reports offline.");
    }

    private void OnSendQuickStatusPressed()
    {
        if (_quickStatusSelect == null
            || _quickStatusSelect.Selected < 0)
        {
            SetHistoryStatus("No fixed quick status selected.");
            return;
        }

        SetHistoryStatus(
            ToolkitDiagnosticsRuntime.SendQuickStatus(
                (ToolkitQuickStatus)(_quickStatusSelect.Selected + 1)));
    }

    private void ShowComparison(string first, string second)
    {
        ToolkitReportComparisonResult comparison =
            ToolkitReportComparison.Compare(first, second);
        ShowReadOnlyReport(
            "CoopGuard offline report comparison / 离线报告对比",
            comparison.Text);
    }

    private void OnDeleteReportPressed()
    {
        string? name = SelectedReportName();
        if (name == null)
        {
            SetHistoryStatus("No report selected.");
            return;
        }

        bool deleted = ToolkitReportHistory.TryDelete(
            name,
            out string status);
        SetHistoryStatus(deleted ? "Deleted " + status : status);
        _historySignature = string.Empty;
        RefreshReportHistory();
    }

    private void OnClearReportsPressed()
    {
        long now = Stopwatch.GetTimestamp();
        if (now > _clearConfirmUntilTicks)
        {
            _clearConfirmUntilTicks = now + 5L * Stopwatch.Frequency;
            SetHistoryStatus(
                "Press Clear history again within 5 seconds to confirm.");
            return;
        }

        int deleted = ToolkitReportHistory.Clear();
        _clearConfirmUntilTicks = 0;
        _reportAName = null;
        SetHistoryStatus($"Cleared {deleted} report(s).");
        _historySignature = string.Empty;
        RefreshReportHistory();
    }

    private void SetHistoryStatus(string status)
    {
        if (_historyStatus != null)
        {
            _historyStatus.Text = FlightRecorder.BoundedText(status, 256);
        }
    }

    private static bool IsChinese()
    {
        try
        {
            string? language = MegaCrit.Sts2.Core.Localization.LocManager
                .Instance?.Language;
            return language != null
                && (language.StartsWith(
                        "zh",
                        StringComparison.OrdinalIgnoreCase)
                    || language.Equals(
                        "zhs",
                        StringComparison.OrdinalIgnoreCase)
                    || language.Equals(
                        "zht",
                        StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}

[HarmonyPatch(typeof(NGame), nameof(NGame._Ready))]
internal static class ToolkitAttachPatch
{
    private static void Postfix(NGame __instance)
    {
        try
        {
            ToolkitRuntime.Attach(__instance);
        }
        catch (Exception ex)
        {
            Main.Log.Error(
                $"Optional Toolkit node was not attached; Guard remains active: {ex}");
        }
    }
}

[HarmonyPatch(
    typeof(NControllerManager),
    nameof(NControllerManager._Process))]
internal static class ToolkitFramePatch
{
    private static void Postfix()
    {
        try
        {
            ToolkitRuntime.Frame();
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Toolkit frame callback was contained: {ex}");
        }
    }
}

[HarmonyPatch(typeof(NGame), nameof(NGame._Input))]
internal static class ToolkitPanelHotkeyPatch
{
    private static void Postfix(NGame __instance, InputEvent inputEvent)
    {
        try
        {
            if (inputEvent is InputEventKey
                {
                    Pressed: true,
                    Echo: false,
                    CtrlPressed: true,
                    Keycode: Key.F7
                })
            {
                ToolkitRuntime.TogglePanel(__instance);
            }
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Toolkit panel hotkey was contained: {ex}");
        }
    }
}

[HarmonyPatch(
    typeof(RunSaveManager),
    nameof(RunSaveManager.SaveRun),
    [typeof(SerializableRun), typeof(bool)])]
internal static class ToolkitMultiplayerSavePatch
{
    private static void Postfix(
        RunSaveManager __instance,
        bool isMultiplayer,
        ISaveStore ____saveStore,
        ref Task __result)
    {
        if (isMultiplayer)
        {
            __result = ToolkitRuntime.SealAfterNativeMultiplayerSave(
                __result,
                __instance,
                ____saveStore);
        }
    }
}

[HarmonyPatch(typeof(NMultiplayerSubmenu), "StartLoad")]
internal static class ToolkitMultiplayerLoadWarningPatch
{
    private static bool Prefix() =>
        ToolkitRuntime.AllowNativeMultiplayerLoad();
}

[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
internal static class ToolkitMainMenuPatch
{
    private static void Postfix(NMainMenu __instance)
    {
        try
        {
            ToolkitRuntime.AttachMainMenu(__instance);
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Main-menu observation was contained: {ex}");
        }
    }
}

[HarmonyPatch(
    typeof(StartRunLobby),
    MethodType.Constructor,
    [typeof(GameMode), typeof(INetGameService), typeof(IStartRunLobbyListener), typeof(int)])]
internal static class ToolkitStartLobbyPatch
{
    private static void Postfix(StartRunLobby __instance)
    {
        try
        {
            ToolkitRuntime.ObserveStartLobby(__instance);
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Start-lobby observation was contained: {ex}");
        }
    }
}

[HarmonyPatch(
    typeof(LoadRunLobby),
    MethodType.Constructor,
    [typeof(INetGameService), typeof(ILoadRunLobbyListener), typeof(SerializableRun)])]
internal static class ToolkitLoadLobbyPatch
{
    private static void Postfix(LoadRunLobby __instance)
    {
        try
        {
            ToolkitRuntime.ObserveLoadLobby(__instance);
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Loaded-lobby observation was contained: {ex}");
        }
    }
}

[HarmonyPatch(
    typeof(RunLobby),
    MethodType.Constructor,
    [
        typeof(GameMode),
        typeof(INetGameService),
        typeof(IRunLobbyListener),
        typeof(IPlayerCollection),
        typeof(IEnumerable<ulong>)
    ])]
internal static class ToolkitRunLobbyPatch
{
    private static void Postfix(
        RunLobby __instance,
        INetGameService netService)
    {
        try
        {
            ToolkitRuntime.ObserveRunLobby(__instance, netService);
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Run-lobby observation was contained: {ex}");
        }
    }
}

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.CleanUp))]
internal static class ToolkitStartLobbyCleanupPatch
{
    private static void Postfix(StartRunLobby __instance)
    {
        try
        {
            ToolkitRuntime.ReleaseLobby(__instance);
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Start-lobby cleanup observation was contained: {ex}");
        }
    }
}

[HarmonyPatch(typeof(LoadRunLobby), nameof(LoadRunLobby.CleanUp))]
internal static class ToolkitLoadLobbyCleanupPatch
{
    private static void Postfix(LoadRunLobby __instance)
    {
        try
        {
            ToolkitRuntime.ReleaseLobby(__instance);
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Load-lobby cleanup observation was contained: {ex}");
        }
    }
}

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.SetReady))]
internal static class ToolkitStartReadyPatch
{
    private static void Postfix(StartRunLobby __instance, bool ready)
    {
        try
        {
            ToolkitRuntime.ReadyChanged(
                __instance.NetService.NetId,
                ready);
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Start-lobby ready observation was contained: {ex}");
        }
    }
}

[HarmonyPatch(typeof(LoadRunLobby), nameof(LoadRunLobby.SetReady))]
internal static class ToolkitLoadReadyPatch
{
    private static void Postfix(LoadRunLobby __instance, bool ready)
    {
        try
        {
            ToolkitRuntime.ReadyChanged(
                __instance.NetService.NetId,
                ready);
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Load-lobby ready observation was contained: {ex}");
        }
    }
}

[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetReadyToEndTurn))]
internal static class ToolkitTurnReadyPatch
{
    private static void Postfix(
        MegaCrit.Sts2.Core.Entities.Players.Player player)
    {
        try
        {
            ToolkitRuntime.PlayerTurnReadyChanged(player.NetId, ready: true);
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Turn-ready observation was contained: {ex}");
        }
    }
}

[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.UndoReadyToEndTurn))]
internal static class ToolkitTurnUndoPatch
{
    private static void Postfix(
        MegaCrit.Sts2.Core.Entities.Players.Player player)
    {
        try
        {
            ToolkitRuntime.PlayerTurnReadyChanged(player.NetId, ready: false);
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Turn-undo observation was contained: {ex}");
        }
    }
}

[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetUpCombat))]
internal static class ToolkitCombatStartPatch
{
    private static void Prefix()
    {
        try
        {
            ToolkitRuntime.ResetTurnReadiness();
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Combat-start observation was contained: {ex}");
        }
    }
}

[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.Reset))]
internal static class ToolkitCombatResetPatch
{
    private static void Prefix()
    {
        try
        {
            ToolkitRuntime.ResetTurnReadiness();
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Combat-reset observation was contained: {ex}");
        }
    }
}

[HarmonyPatch(
    typeof(PlayerChoiceSynchronizer),
    nameof(PlayerChoiceSynchronizer.WaitForRemoteChoice))]
internal static class ToolkitRemoteChoicePatch
{
    private static void Prefix(Player player, out ulong __state)
    {
        __state = player.NetId;
        ToolkitRuntime.RemoteChoiceStarted(__state);
    }

    private static void Postfix(
        Task<PlayerChoiceResult> __result,
        ulong __state)
    {
        _ = __result.ContinueWith(
            _ => ToolkitRuntime.RemoteChoiceCompleted(__state),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static Exception? Finalizer(
        Exception? __exception,
        ulong __state)
    {
        if (__exception != null)
        {
            ToolkitRuntime.RemoteChoiceCompleted(__state);
        }

        return __exception;
    }
}

[HarmonyPatch(
    typeof(MapSelectionSynchronizer),
    nameof(MapSelectionSynchronizer.PlayerVotedForMapCoord))]
internal static class ToolkitMapVotePatch
{
    private static void Postfix(Player player, MapVote? destination) =>
        ToolkitRuntime.MapVoteChanged(
            player.NetId,
            destination.HasValue);
}

[HarmonyPatch(
    typeof(MapSelectionSynchronizer),
    nameof(MapSelectionSynchronizer.OnLocationChanged))]
internal static class ToolkitMapResetPatch
{
    private static void Postfix() => ToolkitRuntime.MapVotesCleared();
}

[HarmonyPatch(
    typeof(ActionExecutor),
    MethodType.Constructor,
    [typeof(ActionQueueSet)])]
internal static class ToolkitActionObserverPatch
{
    private static void Postfix(ActionExecutor __instance)
    {
        __instance.BeforeActionExecuted +=
            ToolkitRuntime.PublicActionStarted;
        __instance.AfterActionExecuted +=
            ToolkitRuntime.PublicActionCompleted;
    }
}

[HarmonyPatch(typeof(JoinFlow), nameof(JoinFlow.Begin))]
internal static class ToolkitJoinBeginPatch
{
    private static void Prefix()
    {
        try
        {
            ToolkitRuntime.BeginJoin();
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Join-begin observation was contained: {ex}");
        }
    }

    private static void Postfix(ref Task<JoinResult> __result)
    {
        __result = Observe(__result);
    }

    private static async Task<JoinResult> Observe(Task<JoinResult> original)
    {
        try
        {
            JoinResult result = await original;
            if (result.sessionState.HasValue)
            {
                ToolkitRuntime.JoinCompleted(result.sessionState.Value);
            }
            else
            {
                ToolkitRuntime.JoinFailed(
                    new InvalidDataException(
                        "Native join result omitted its session state."));
            }
            return result;
        }
        catch (Exception ex)
        {
            ToolkitRuntime.JoinFailed(ex);
            throw;
        }
    }
}

[HarmonyPatch(
    typeof(JoinFlow),
    "HandleInitialGameInfoMessage",
    [typeof(InitialGameInfoMessage), typeof(ulong)])]
internal static class ToolkitInitialInfoPatch
{
    private static void Prefix(InitialGameInfoMessage message)
    {
        try
        {
            ToolkitRuntime.InitialInfo(message.sessionState);
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Initial-info observation was contained: {ex}");
        }
    }
}

[HarmonyPatch(typeof(JoinFlow), "AttemptJoin")]
internal static class ToolkitAttemptJoinPatch
{
    private static void Prefix()
    {
        try
        {
            ToolkitRuntime.WaitingNewRunLobby();
        }
        catch (Exception ex)
        {
            Main.Log.Error($"New-run join observation was contained: {ex}");
        }
    }
}

[HarmonyPatch(typeof(JoinFlow), "AttemptLoadJoin")]
internal static class ToolkitAttemptLoadJoinPatch
{
    private static void Prefix()
    {
        try
        {
            ToolkitRuntime.WaitingLoadedRunLobby();
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Loaded-run join observation was contained: {ex}");
        }
    }
}

[HarmonyPatch(typeof(JoinFlow), "AttemptRejoin")]
internal static class ToolkitAttemptRunningPatch
{
    private static void Prefix()
    {
        try
        {
            ToolkitRuntime.WaitingRunningResponse();
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Running-rejoin observation was contained: {ex}");
        }
    }
}
