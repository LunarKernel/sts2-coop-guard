using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Platform;

namespace BetterCoop;

internal readonly record struct ToolkitPeerObservation(
    ulong NetId,
    bool Connected,
    float? PingMsec,
    float? PacketLoss,
    bool Loading,
    bool Choosing,
    bool? MapSubmitted);

internal static class ToolkitDiagnosticsRuntime
{
    private sealed class Peer(ulong netId, byte ordinal)
    {
        public ulong NetId { get; } = netId;
        public byte Ordinal { get; set; } = ordinal;
        public ToolkitFeature Features { get; set; }
        public bool Acknowledged { get; set; }
        public uint LastSequence { get; set; }
        public long LastSeenTicks { get; set; }
        public int HelloAttempts { get; set; }
        public long NextHelloTicks { get; set; }
        public int ParseErrors { get; set; }
        public long ErrorWindowTicks { get; set; }
        public bool Disabled { get; set; }
        public ToolkitStateRow? DeclaredState { get; set; }
        public Dictionary<ToolkitConsentFeature, ToolkitConsent>
            PendingConsent
        { get; } = [];
        public Queue<byte[]> PendingQuickStatuses { get; } = [];
        public Dictionary<byte, byte[]> PendingCheckpointResults { get; } = [];
        public Dictionary<byte, byte[]> PendingContributions { get; } = [];
        public Dictionary<byte, byte[]> PendingHands { get; } = [];
        public Dictionary<ulong, byte[]> PendingCheckpoints { get; } = [];
    }

    private sealed class ConsentState
    {
        public bool LocalOptIn { get; set; }
        public bool Active { get; set; }
        public HashSet<ulong> Opted { get; } = [];
        public HashSet<ulong> Acknowledged { get; } = [];
        public byte[] CommitToken { get; set; } = [];

        public void Clear()
        {
            LocalOptIn = false;
            Active = false;
            Opted.Clear();
            Acknowledged.Clear();
            CommitToken = [];
        }
    }

    private const long StaleSeconds = 5;
    private static readonly object Sync = new();
    private static readonly Dictionary<ulong, Peer> Peers = [];
    private static readonly ToolkitSendLimiter SendLimiter = new();
    private static readonly ToolkitQuickStatusLimiter QuickStatusLimiter =
        new();
    private static readonly Queue<(long Ticks, byte Ordinal,
        ToolkitQuickStatus Status)> Statuses = new();
    private static readonly ConsentState HandConsent = new();
    private static readonly ConsentState ForensicsConsent = new();
    private static readonly Dictionary<byte, ToolkitHandSnapshot> Hands = [];
    private static readonly Dictionary<byte, Dictionary<ulong, F1Checkpoint>>
        Checkpoints = [];
    private static readonly F2History DivergenceHistory = new();
    private static readonly ToolkitContributionLedger Contributions = new();
    private static INetGameService? _service;
    private static ulong[] _roster = [];
    private static Guid _sessionId;
    private static ulong? _hostId;
    private static uint _outSequence;
    private static long _lastStateTicks;
    private static ToolkitStateRow _localState;
    private static Dictionary<ulong, ToolkitPeerObservation>
        _nativeObservations = [];
    private static ToolkitStateRow[] _receivedRows = [];
    private static int _transportRejects;
    private static bool _sessionInvalid;
    private static bool _transportValidated;
    private static bool _statusMuted;
    private static bool _contributionsEnabled;
    private static bool _contributionSharingEnabled;
    private static uint _membershipEpoch;
    private static byte[] _rosterDigest = [];
    private static uint _handRevision;
    private static byte[] _lastHandPayload = [];
    private static long _lastHandSendTicks;
    private static byte[] _lastContributionPayload = [];
    private static long _lastContributionSendTicks;
    private static string? _environmentCode;
    private static long _nextEnvironmentCodeRetryTicks;
    private static string _forensicsStatus =
        "Forensics: disabled (unanimous consent required).";

    private static ToolkitFeature LocalFeatures =>
        ToolkitFeature.HealthMatrix
        | ToolkitFeature.ChoiceProgress
        | ToolkitFeature.EnvironmentCode
        | ToolkitFeature.QuickStatus
        | ToolkitFeature.MapProgress
        | ToolkitFeature.HandSharing
        | ToolkitFeature.Contributions
        | (ToolkitForensicsRuntime.CheckpointObserverAvailable
            ? ToolkitFeature.Checkpoints
            : ToolkitFeature.None)
        | ToolkitFeature.Heartbeat
        | ToolkitFeature.HostDiagnostics;

    public static void SetService(INetGameService? service)
    {
        lock (Sync)
        {
            if (ReferenceEquals(_service, service))
            {
                return;
            }

            if (_service != null)
            {
                try
                {
                    _service.UnregisterMessageHandler<ToolkitEnvelopeMessage>(
                        HandleMessage);
                }
                catch
                {
                    // Optional protocol teardown cannot affect native networking.
                }
            }

            ResetLocked();
            _service = _transportValidated ? service : null;
            if (_service == null)
            {
                return;
            }

            try
            {
                _service.RegisterMessageHandler<ToolkitEnvelopeMessage>(
                    HandleMessage);
            }
            catch (Exception ex)
            {
                _service = null;
                Main.Log.Error(
                    "Optional diagnostics protocol was disabled without affecting Guard: "
                    + ex.GetType().Name);
            }
        }
    }

    public static void EnableTransportValidation()
    {
        lock (Sync)
        {
            _transportValidated = true;
        }
    }

    public static void Tick(
        IReadOnlyList<ulong> roster,
        ToolkitStateRow localState,
        IReadOnlyList<ToolkitPeerObservation> nativeObservations)
    {
        lock (Sync)
        {
            if (_service is not
                {
                    IsConnected: true,
                    Type: NetGameType.Host or NetGameType.Client
                })
            {
                return;
            }

            ulong[] current = roster
                .Append(_service.NetId)
                .Distinct()
                .Order()
                .Take(ToolkitStateCodec.MaxRows + 1)
                .ToArray();
            if (current.Length is < 1 or > ToolkitStateCodec.MaxRows)
            {
                _sessionInvalid = true;
                return;
            }

            if (!_roster.SequenceEqual(current))
            {
                BeginRosterLocked(current);
            }

            _localState = localState with
            {
                Ordinal = OrdinalOf(_service.NetId)
            };
            _nativeObservations = nativeObservations
                .Take(ToolkitStateCodec.MaxRows)
                .ToDictionary(value => value.NetId);
            long now = Stopwatch.GetTimestamp();
            FlushPendingConsentLocked(now);
            FlushPendingQuickStatusesLocked(now);
            FlushPendingCheckpointResultsLocked(now);
            FlushPendingCheckpointsLocked(now);
            FlushPendingHandsLocked(now);
            FlushPendingContributionsLocked(now);
            if (_service.Type == NetGameType.Host)
            {
                TickHostLocked(now);
            }
            else
            {
                TickClientLocked(now);
            }

            TickContributionSharingLocked(now);
        }
    }

    public static string HudStatus(bool chinese)
    {
        lock (Sync)
        {
            if (_sessionInvalid)
            {
                return chinese
                    ? "诊断会话：无效（不影响 Guard）"
                    : "Diagnostics session: invalid (Guard unaffected)";
            }

            if (_sessionId == Guid.Empty)
            {
                return chinese
                    ? "诊断会话：不可用"
                    : "Diagnostics session: unavailable";
            }

            string session = ShortSession(_sessionId);
            string environment = EnvironmentCodeLocked();
            return chinese
                ? $"会话 {session}；环境短码 {environment}"
                : $"Session {session}; environment code {environment}";
        }
    }

    public static string BuildMatrix(bool chinese)
    {
        lock (Sync)
        {
            List<string> lines =
            [
                chinese
                    ? "可选诊断通道（不参与 Guard/ready/start）"
                    : "Optional diagnostics plane (not used by Guard/ready/start)",
                HudStatus(chinese)
            ];
            long now = Stopwatch.GetTimestamp();
            IEnumerable<ToolkitStateRow> rows =
                _service?.Type == NetGameType.Host
                    ? AggregateRowsLocked()
                    : _receivedRows;
            foreach (ToolkitStateRow row in rows.OrderBy(row => row.Ordinal))
            {
                Peer? peer = Peers.Values.FirstOrDefault(
                    value => value.Ordinal == row.Ordinal);
                bool local = _service != null
                    && row.Ordinal == OrdinalOf(_service.NetId);
                Peer? source = _service?.Type == NetGameType.Client
                    && _hostId.HasValue
                    && Peers.TryGetValue(_hostId.Value, out Peer? host)
                        ? host
                        : peer;
                bool stale = !local
                    && (source == null
                        || now - source.LastSeenTicks
                            > StaleSeconds * Stopwatch.Frequency);
                string toolkit = peer?.Disabled == true
                    ? "invalid"
                    : stale
                        ? "stale/unknown"
                        : local
                            || row.Flags.HasFlag(
                                ToolkitStateFlags.ToolkitAvailable)
                            ? "supported"
                            : "unknown";
                string guard = row.Flags.HasFlag(
                    ToolkitStateFlags.GuardHealthy)
                        ? row.Flags.HasFlag(ToolkitStateFlags.PeerDeclared)
                            ? "peer-declared healthy"
                            : "local/native-list inferred healthy"
                        : "unknown";
                string ready = row.Flags.HasFlag(
                    ToolkitStateFlags.ReadyKnown)
                        ? row.Flags.HasFlag(ToolkitStateFlags.Ready)
                            ? "ready"
                            : "not ready"
                        : "unknown";
                string choosing = row.Flags.HasFlag(
                    ToolkitStateFlags.ChoosingKnown)
                        ? row.Flags.HasFlag(ToolkitStateFlags.Choosing)
                            ? "choosing"
                            : "not choosing"
                        : "unknown";
                string map = row.Flags.HasFlag(ToolkitStateFlags.MapKnown)
                    ? row.Flags.HasFlag(ToolkitStateFlags.MapSubmitted)
                        ? "submitted"
                        : "pending"
                    : "unknown";
                lines.Add(
                    $"P{row.Ordinal}: connected="
                    + (row.Flags.HasFlag(ToolkitStateFlags.Connected)
                        ? "yes"
                        : "unknown")
                    + $"; toolkit={toolkit}; guard={guard}; ready={ready}; "
                    + $"choice={choosing}; map={map}; "
                    + $"wait={row.WaitReason}/{row.WaitConfidence}; "
                    + $"network={Metric(row.PingMsec, "ms")}, "
                    + $"loss={Metric(row.LossPermille, "permille")}; "
                    + (stale ? "source stale" : "updated <5s"));
            }

            if (lines.Count == 2)
            {
                lines.Add(
                    chinese
                        ? "玩家诊断状态：未知"
                        : "Peer diagnostics: unknown");
            }

            return string.Join('\n', lines);
        }
    }

    public static string FullSessionId()
    {
        lock (Sync)
        {
            return _sessionInvalid || _sessionId == Guid.Empty
                ? "unavailable"
                : _sessionId.ToString("N");
        }
    }

    public static string SendQuickStatus(ToolkitQuickStatus status)
    {
        lock (Sync)
        {
            long now = Stopwatch.GetTimestamp();
            if (_service is not
                {
                    IsConnected: true,
                    Type: NetGameType.Host or NetGameType.Client
                }
                || _sessionId == Guid.Empty)
            {
                return "Quick status unavailable: diagnostics negotiation is incomplete.";
            }

            if (!CanSendQuickStatusLocked())
            {
                return "Quick status unavailable until every current peer confirms Toolkit support.";
            }

            if (!QuickStatusLimiter.TryTake(now))
            {
                return "Quick status cooling down; no packet was sent.";
            }

            if (_service.Type == NetGameType.Host)
            {
                byte origin = OrdinalOf(_service.NetId);
                if (!ToolkitQuickStatusCodec.TryEncode(
                        origin,
                        status,
                        out byte[] payload))
                {
                    return "Quick status rejected locally.";
                }

                RememberStatusLocked(origin, status, now);
                int accepted = 0;
                foreach (Peer peer in Peers.Values.Where(value =>
                             value.Acknowledged
                             && !value.Disabled
                             && value.Features.HasFlag(
                                 ToolkitFeature.QuickStatus)))
                {
                    SendOrQueueQuickStatusLocked(peer, payload, now);
                    accepted++;
                }

                return $"Quick status shown locally and accepted for {accepted} peer(s).";
            }

            if (!_hostId.HasValue
                || !Peers.TryGetValue(_hostId.Value, out Peer? host)
                || !host.Acknowledged
                || !host.Features.HasFlag(ToolkitFeature.QuickStatus)
                || !ToolkitQuickStatusCodec.TryEncode(
                    0,
                    status,
                    out byte[] clientPayload))
            {
                return "Quick status was not sent; host capability is unavailable.";
            }

            SendOrQueueQuickStatusLocked(host, clientPayload, now);
            RememberStatusLocked(
                OrdinalOf(_service.NetId),
                status,
                now);
            return "Quick status accepted.";
        }
    }

    public static bool CanSendQuickStatus()
    {
        lock (Sync)
        {
            return CanSendQuickStatusLocked();
        }
    }

    private static bool CanSendQuickStatusLocked()
    {
        if (_service is not
            {
                IsConnected: true,
                Type: NetGameType.Host or NetGameType.Client
            }
            || _sessionId == Guid.Empty
            || _roster.Length < 2)
        {
            return false;
        }

        if (_service.Type == NetGameType.Host)
        {
            return Peers.Count == _roster.Length - 1
                && Peers.Values.All(peer =>
                    peer.Acknowledged
                    && !peer.Disabled
                    && peer.Features.HasFlag(
                        ToolkitFeature.QuickStatus));
        }

        return _receivedRows.Length == _roster.Length
            && _receivedRows.All(row =>
                row.Flags.HasFlag(ToolkitStateFlags.ToolkitAvailable));
    }

    public static string ToggleQuickStatusMute()
    {
        lock (Sync)
        {
            _statusMuted = !_statusMuted;
            if (_statusMuted)
            {
                Statuses.Clear();
            }

            return _statusMuted
                ? "Quick status messages muted."
                : "Quick status messages unmuted.";
        }
    }

    public static string RecentQuickStatuses(bool chinese)
    {
        lock (Sync)
        {
            PruneStatusesLocked(Stopwatch.GetTimestamp());
            if (_statusMuted)
            {
                return chinese
                    ? "快捷状态：已静音"
                    : "Quick status: muted";
            }

            if (Statuses.Count == 0)
            {
                return chinese
                    ? "快捷状态：无"
                    : "Quick status: none";
            }

            return string.Join(
                '\n',
                Statuses.Select(item =>
                    $"P{item.Ordinal}: "
                    + StatusLabel(item.Status, chinese)));
        }
    }

    public static string ToggleHandSharingConsent() =>
        ToggleConsent(ToolkitConsentFeature.HandSharing);

    public static string ToggleForensicsConsent() =>
        ToggleConsent(ToolkitConsentFeature.Forensics);

    public static string ToggleContributions()
    {
        lock (Sync)
        {
            _contributionsEnabled = !_contributionsEnabled;
            if (!_contributionsEnabled)
            {
                DisableContributionSharingLocked();
                Contributions.Clear();
            }

            return _contributionsEnabled
                ? ToolkitForensicsRuntime.ContributionObserversAvailable
                    ? "Contribution counters enabled locally; entertainment only."
                    : "Contribution counters enabled with completed-action counts only; effect observers are unavailable."
                : "Contribution counters disabled and cleared.";
        }
    }

    public static string ToggleContributionSharing()
    {
        lock (Sync)
        {
            if (!_contributionsEnabled)
            {
                return "Enable contribution counters before sharing.";
            }

            _contributionSharingEnabled = !_contributionSharingEnabled;
            _lastContributionPayload = [];
            _lastContributionSendTicks = 0;
            if (!_contributionSharingEnabled)
            {
                DisableContributionSharingLocked();
            }

            return _contributionSharingEnabled
                ? "Contribution sharing enabled separately; public counters only."
                : "Contribution sharing disabled; remote counters cleared.";
        }
    }

    public static string CollaborationStatus(bool chinese)
    {
        lock (Sync)
        {
            List<string> lines =
            [
                HandConsent.Active
                    ? chinese
                        ? "手牌共享中（全员同意；可立即撤回）"
                        : "Hand sharing active (unanimous; revoke any time)"
                    : HandConsent.LocalOptIn
                        ? chinese
                            ? "手牌共享：已同意，等待全员确认"
                            : "Hand sharing: opted in; waiting for unanimous commit"
                        : chinese
                            ? "手牌共享：关闭"
                            : "Hand sharing: off",
                ForensicsConsent.Active
                    ? chinese
                        ? "分层取证：已启用（仅自然 checkpoint）"
                        : "Forensics: active (natural checkpoints only)"
                    : chinese
                        ? "分层取证：关闭/等待全员同意"
                        : _forensicsStatus,
                _contributionsEnabled
                    ? chinese
                        ? "贡献统计：本会话内存中"
                            + (_contributionSharingEnabled
                                ? "；已单独开启共享"
                                : "；仅本机")
                            + "；娱乐统计，不代表责任或技术健康"
                        : "Contributions: in-memory this session; "
                            + (_contributionSharingEnabled
                                ? "sharing separately enabled"
                                : "local only")
                            + "; entertainment only, not responsibility/health"
                    : chinese
                        ? "贡献统计：关闭"
                        : "Contributions: off"
            ];

            if (HandConsent.Active)
            {
                foreach (ToolkitHandSnapshot hand in Hands.Values
                             .OrderBy(value => value.OwnerOrdinal))
                {
                    string cards = hand.Cards.Length == 0
                        ? "(empty)"
                        : string.Join(
                            ", ",
                            hand.Cards.Take(64).Select(card =>
                                $"{DisplayCardId(card.ModelId)}+{card.Upgrade} [{card.PublicCost}]"));
                    lines.Add($"P{hand.OwnerOrdinal} hand: {cards}");
                }
            }

            if (_contributionsEnabled)
            {
                if (!ToolkitForensicsRuntime.ContributionObserversAvailable)
                {
                    lines.Add(
                        chinese
                            ? "伤害/防御/治疗/击杀观察器不可用；仅显示已完成行动数。"
                            : "Damage/defense/healing/kill observers unavailable; completed actions only.");
                }

                lines.Add(
                    chinese
                        ? "定义：伤害/击杀=公开伤害结果；给予防御=对他人的公开格挡；给予治疗=原生行动所有者对他人的实际 HP 增量；行动=已完成公开行动。"
                        : "Definitions: damage/kills=public damage result; defense=public block given to another player; healing=native action owner actual HP granted to another player; actions=completed public actions.");
                foreach (ToolkitContributionSnapshot row in
                         Contributions.Snapshot())
                {
                    lines.Add(
                        $"P{row.Ordinal}: damage={row.Damage}; "
                        + $"defense-given={row.DefenseGiven}; "
                        + $"healing-given={row.HealingGiven}; "
                        + $"kills={row.Kills}; actions={row.CompletedActions}");
                }
            }

            return string.Join('\n', lines);
        }
    }

    internal static bool TryGetOrdinal(ulong netId, out byte ordinal)
    {
        lock (Sync)
        {
            ordinal = OrdinalOf(netId);
            return ordinal != 0;
        }
    }

    internal static bool TryGetLocalIdentity(
        out ulong netId,
        out byte ordinal)
    {
        lock (Sync)
        {
            netId = _service?.NetId ?? 0;
            ordinal = netId == 0 ? (byte)0 : OrdinalOf(netId);
            return netId != 0 && ordinal != 0;
        }
    }

    internal static void PublishHand(
        IReadOnlyList<ToolkitHandCard> cards)
    {
        lock (Sync)
        {
            if (!HandConsent.Active
                || _service == null
                || _membershipEpoch == 0
                || cards.Count > ToolkitHandSnapshotCodec.MaxCards)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            byte ordinal = OrdinalOf(_service.NetId);
            uint revision = unchecked(++_handRevision);
            if (revision == 0)
            {
                revision = ++_handRevision;
            }

            ToolkitHandSnapshot value = new(
                _membershipEpoch,
                revision,
                ordinal,
                cards.ToArray());
            if (!ToolkitHandSnapshotCodec.TryEncode(
                    value,
                    out byte[] payload))
            {
                DeactivateLocked(
                    ToolkitConsentFeature.HandSharing,
                    broadcast: true);
                return;
            }

            byte[] content = payload[10..];
            if (_lastHandPayload.AsSpan().SequenceEqual(content))
            {
                return;
            }

            if (now - _lastHandSendTicks
                < Stopwatch.Frequency / 2)
            {
                return;
            }

            _lastHandPayload = content;
            _lastHandSendTicks = now;
            Hands[ordinal] = value;
            SendHandLocked(payload, ordinal, relayFrom: null);
        }
    }

    internal static void PublishNaturalCheckpoint(
        ulong checkpointId,
        string context,
        IReadOnlyList<F1CategoryDigest> categories)
    {
        lock (Sync)
        {
            if (!ForensicsConsent.Active
                || _service == null
                || !F1SchemaV1.TryCreateCheckpoint(
                    _sessionId,
                    checkpointId,
                    context,
                    categories,
                    out F1Checkpoint checkpoint)
                || !F1CheckpointCodec.TryEncode(
                    checkpoint,
                    out byte[] payload))
            {
                return;
            }

            byte ordinal = OrdinalOf(_service.NetId);
            RememberCheckpointLocked(ordinal, checkpoint);
            if (_service.Type == NetGameType.Client)
            {
                QueueCheckpointToHostLocked(checkpointId, payload);
            }
            else
            {
                CompareHostCheckpointLocked(checkpointId);
            }
        }
    }

    internal static void RecordContribution(
        byte ordinal,
        ulong eventId,
        ulong damage = 0,
        ulong defenseGiven = 0,
        ulong healingGiven = 0,
        ulong kills = 0,
        ulong completedActions = 0)
    {
        lock (Sync)
        {
            if (!_contributionsEnabled)
            {
                return;
            }

            byte localOrdinal = OrdinalOf(_service?.NetId ?? 0);
            if (ordinal == 0 || ordinal != localOrdinal)
            {
                return;
            }

            Contributions.Add(
                ordinal,
                eventId,
                damage,
                defenseGiven,
                healingGiven,
                kills,
                completedActions);
        }
    }

    public static void OnDisconnected()
    {
        lock (Sync)
        {
            _roster = [];
            _sessionId = Guid.Empty;
            _hostId = null;
            Peers.Clear();
            SendLimiter.Clear();
            QuickStatusLimiter.Clear();
            Statuses.Clear();
            _receivedRows = [];
            _sessionInvalid = false;
            ClearCollaborativeStateLocked();
            _membershipEpoch = 0;
            _rosterDigest = [];
        }
    }

    public static void RejectTransportPacket()
    {
        int rejected = Interlocked.Increment(ref _transportRejects);
        if (rejected is 1 or 10 or 100)
        {
            Main.Log.Warn(
                "Dropped malformed or sender-spoofed optional diagnostics packet; Guard and native networking were unchanged.");
        }
    }

    private static void BeginRosterLocked(ulong[] roster)
    {
        ClearCollaborativeStateLocked();
        _roster = roster;
        Peers.Clear();
        SendLimiter.Clear();
        QuickStatusLimiter.Clear();
        Statuses.Clear();
        _receivedRows = [];
        _hostId = null;
        _outSequence = 0;
        _lastStateTicks = 0;
        _sessionInvalid = false;
        _environmentCode = null;
        _nextEnvironmentCodeRetryTicks = 0;
        for (int index = 0; index < roster.Length; index++)
        {
            if (_service != null && roster[index] != _service.NetId)
            {
                Peers.Add(
                    roster[index],
                    new Peer(roster[index], (byte)(index + 1)));
            }
        }

        // Each roster change creates a fresh diagnostics session; epoch 1 is
        // therefore unambiguous within that session.
        _membershipEpoch = 1;

        _sessionId = _service?.Type == NetGameType.Host
            ? NewSessionId()
            : Guid.Empty;
        _rosterDigest = _sessionId == Guid.Empty
            ? []
            : ToolkitConsentCodec.RosterDigest(
                _sessionId,
                _membershipEpoch,
                _roster);
    }

    private static void TickHostLocked(long now)
    {
        foreach (Peer peer in Peers.Values.OrderBy(value => value.Ordinal))
        {
            if (peer.Disabled || peer.Acknowledged)
            {
                continue;
            }

            if (peer.HelloAttempts < 3
                && now >= peer.NextHelloTicks
                && SendLimiter.TryConsume(peer.Ordinal, now))
            {
                if (SendLocked(
                        peer,
                        ToolkitMessageType.Hello,
                        ToolkitHelloCodec.Encode(LocalFeatures, 0)))
                {
                    int delay = 1 << peer.HelloAttempts;
                    peer.HelloAttempts++;
                    peer.NextHelloTicks =
                        now + delay * Stopwatch.Frequency;
                }
            }
        }

        if (now - _lastStateTicks < 2L * Stopwatch.Frequency)
        {
            return;
        }

        _lastStateTicks = now;
        ToolkitStateRow[] rows = AggregateRowsLocked();
        if (!ToolkitStateCodec.TryEncode(rows, out byte[] payload))
        {
            return;
        }

        foreach (Peer peer in Peers.Values
                     .Where(value => value.Acknowledged && !value.Disabled)
                     .OrderBy(value => value.Ordinal))
        {
            if (SendLimiter.TryConsume(peer.Ordinal, now))
            {
                SendLocked(peer, ToolkitMessageType.State, payload);
            }
        }
    }

    private static void TickClientLocked(long now)
    {
        if (_sessionId == Guid.Empty
            || !_hostId.HasValue
            || !Peers.TryGetValue(_hostId.Value, out Peer? host)
            || !host.Acknowledged
            || now - _lastStateTicks < 2L * Stopwatch.Frequency
            || !SendLimiter.TryConsume(host.Ordinal, now)
            || !ToolkitStateCodec.TryEncode(
                [_localState with { Ordinal = 0 }],
                out byte[] payload))
        {
            return;
        }

        _lastStateTicks = now;
        SendLocked(host, ToolkitMessageType.State, payload);
    }

    private static void TickContributionSharingLocked(long now)
    {
        if (!_contributionsEnabled
            || !_contributionSharingEnabled
            || _service == null
            || now - _lastContributionSendTicks
                < 5L * Stopwatch.Frequency)
        {
            return;
        }

        byte ordinal = OrdinalOf(_service.NetId);
        if (!Contributions.TryGet(
                ordinal,
                out ToolkitContributionSnapshot value)
            || !ToolkitContributionCodec.TryEncode(
                value,
                out byte[] payload)
            || payload.AsSpan().SequenceEqual(_lastContributionPayload)
            || !Peers.Values.Any(peer =>
                peer.Acknowledged
                && !peer.Disabled
                && peer.Features.HasFlag(ToolkitFeature.Contributions)))
        {
            return;
        }

        SendContributionLocked(payload, ordinal, relayFrom: null);
        _lastContributionPayload = payload;
        _lastContributionSendTicks = now;
    }

    private static void HandleMessage(
        ToolkitEnvelopeMessage message,
        ulong senderId)
    {
        try
        {
            HandleMessageCore(message, senderId);
        }
        catch (Exception ex)
        {
            Main.Log.Error(
                "Optional diagnostics handler failed open without affecting Guard/native networking: "
                + ex.GetType().Name);
        }
    }

    private static void HandleMessageCore(
        ToolkitEnvelopeMessage message,
        ulong senderId)
    {
        lock (Sync)
        {
            if (_service == null
                || !_roster.Contains(senderId)
                || !Peers.TryGetValue(senderId, out Peer? peer)
                || peer.Disabled)
            {
                return;
            }

            ToolkitEnvelope envelope = message.Envelope;
            if (envelope.Major != ToolkitEnvelopeCodec.Major)
            {
                DisablePeerLocked(peer);
                return;
            }

            if (envelope.Type == ToolkitMessageType.Hello)
            {
                HandleHelloLocked(peer, envelope);
                return;
            }

            if (_sessionInvalid
                || _sessionId == Guid.Empty
                || envelope.SessionId != _sessionId
                || !ToolkitSequence.IsNewer(
                    envelope.Sequence,
                    peer.LastSequence))
            {
                RejectPeerMessageLocked(peer);
                return;
            }

            peer.LastSequence = envelope.Sequence;
            peer.LastSeenTicks = Stopwatch.GetTimestamp();
            switch (envelope.Type)
            {
                case ToolkitMessageType.HelloAck:
                    HandleAckLocked(peer, envelope.Payload);
                    break;
                case ToolkitMessageType.State:
                    HandleStateLocked(peer, envelope.Payload);
                    break;
                case ToolkitMessageType.QuickStatus:
                    HandleQuickStatusLocked(peer, envelope.Payload);
                    break;
                case ToolkitMessageType.Consent:
                    HandleConsentLocked(peer, envelope.Payload);
                    break;
                case ToolkitMessageType.HandSnapshot:
                    HandleHandSnapshotLocked(peer, envelope.Payload);
                    break;
                case ToolkitMessageType.Checkpoint:
                    HandleCheckpointLocked(peer, envelope.Payload);
                    break;
                case ToolkitMessageType.CheckpointResult:
                    HandleCheckpointResultLocked(peer, envelope.Payload);
                    break;
                case ToolkitMessageType.Contribution:
                    HandleContributionLocked(peer, envelope.Payload);
                    break;
                default:
                    // Unknown minor/type is intentionally ignored.
                    break;
            }
        }
    }

    private static void HandleHelloLocked(
        Peer peer,
        ToolkitEnvelope envelope)
    {
        if (_service?.Type != NetGameType.Client
            || !ToolkitHelloCodec.TryDecode(
                envelope.Payload,
                out ToolkitFeature features,
                out _))
        {
            RejectPeerMessageLocked(peer);
            return;
        }

        if (_sessionId != Guid.Empty
            && envelope.SessionId != _sessionId)
        {
            _sessionInvalid = true;
            _sessionId = Guid.Empty;
            _receivedRows = [];
            return;
        }

        _hostId = peer.NetId;
        _sessionId = envelope.SessionId;
        _rosterDigest = ToolkitConsentCodec.RosterDigest(
            _sessionId,
            _membershipEpoch,
            _roster);
        peer.Features = features;
        peer.LastSequence = envelope.Sequence;
        peer.LastSeenTicks = Stopwatch.GetTimestamp();
        peer.Acknowledged = true;
        if (SendLimiter.TryConsume(peer.Ordinal, peer.LastSeenTicks))
        {
            SendLocked(
                peer,
                ToolkitMessageType.HelloAck,
                ToolkitHelloCodec.Encode(LocalFeatures, 0));
        }
    }

    private static void HandleAckLocked(Peer peer, byte[] payload)
    {
        if (_service?.Type != NetGameType.Host
            || !ToolkitHelloCodec.TryDecode(
                payload,
                out ToolkitFeature features,
                out _))
        {
            RejectPeerMessageLocked(peer);
            return;
        }

        peer.Features = features;
        peer.Acknowledged = true;
    }

    private static void HandleStateLocked(Peer peer, byte[] payload)
    {
        if (!ToolkitStateCodec.TryDecode(
                payload,
                out ToolkitStateRow[] rows))
        {
            RejectPeerMessageLocked(peer);
            return;
        }

        if (_service?.Type == NetGameType.Host)
        {
            if (rows.Length != 1 || rows[0].Ordinal != 0)
            {
                RejectPeerMessageLocked(peer);
                return;
            }

            peer.DeclaredState = rows[0] with
            {
                Ordinal = peer.Ordinal,
                Flags = rows[0].Flags | ToolkitStateFlags.PeerDeclared
            };
        }
        else
        {
            if (_hostId != peer.NetId
                || rows.Select(row => row.Ordinal).Distinct().Count()
                    != rows.Length)
            {
                RejectPeerMessageLocked(peer);
                return;
            }

            _receivedRows = rows;
        }
    }

    private static void HandleQuickStatusLocked(
        Peer peer,
        byte[] payload)
    {
        if (!ToolkitQuickStatusCodec.TryDecode(
                payload,
                out byte origin,
                out ToolkitQuickStatus status))
        {
            RejectPeerMessageLocked(peer);
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (_service?.Type == NetGameType.Host)
        {
            if (!CanSendQuickStatusLocked() || origin != 0)
            {
                RejectPeerMessageLocked(peer);
                return;
            }

            origin = peer.Ordinal;
            RememberStatusLocked(origin, status, now);
            if (!ToolkitQuickStatusCodec.TryEncode(
                    origin,
                    status,
                    out byte[] relay))
            {
                return;
            }

            foreach (Peer target in Peers.Values.Where(value =>
                         value.NetId != peer.NetId
                         && value.Acknowledged
                         && !value.Disabled
                         && value.Features.HasFlag(
                             ToolkitFeature.QuickStatus)))
            {
                SendOrQueueQuickStatusLocked(target, relay, now);
            }
        }
        else if (_hostId == peer.NetId
                 && peer.Acknowledged
                 && peer.Features.HasFlag(ToolkitFeature.QuickStatus)
                 && origin != 0)
        {
            RememberStatusLocked(origin, status, now);
        }
        else
        {
            RejectPeerMessageLocked(peer);
        }
    }

    private static string ToggleConsent(
        ToolkitConsentFeature feature)
    {
        lock (Sync)
        {
            ConsentState state = ConsentFor(feature);
            if (_service is not
                {
                    IsConnected: true,
                    Type: NetGameType.Host or NetGameType.Client
                }
                || _sessionId == Guid.Empty
                || _membershipEpoch == 0
                || _rosterDigest.Length != 16)
            {
                return "Consent unavailable: diagnostics negotiation is incomplete.";
            }

            if (state.LocalOptIn)
            {
                DeactivateLocked(feature, broadcast: true);
                return feature == ToolkitConsentFeature.HandSharing
                    ? "Hand-sharing consent revoked; cached hands cleared."
                    : "Forensics consent revoked; peer evidence cleared.";
            }

            state.LocalOptIn = true;
            state.Opted.Add(_service.NetId);
            ToolkitConsent value = new(
                feature,
                ToolkitConsentAction.OptIn,
                _membershipEpoch,
                _rosterDigest,
                new byte[16]);
            if (_service.Type == NetGameType.Host)
            {
                TryBeginCommitLocked(feature);
            }
            else
            {
                SendConsentToHostLocked(value);
            }

            return feature == ToolkitConsentFeature.HandSharing
                ? "Hand-sharing consent recorded for this session; waiting for everyone."
                : "Forensics consent recorded for this session; waiting for everyone.";
        }
    }

    private static void HandleConsentLocked(Peer peer, byte[] payload)
    {
        if (!ToolkitConsentCodec.TryDecode(
                payload,
                out ToolkitConsent value)
            || value.MembershipEpoch != _membershipEpoch
            || !_rosterDigest.AsSpan().SequenceEqual(value.RosterDigest))
        {
            RejectPeerMessageLocked(peer);
            return;
        }

        ConsentState state = ConsentFor(value.Feature);
        if (_service?.Type == NetGameType.Host)
        {
            switch (value.Action)
            {
                case ToolkitConsentAction.OptIn:
                    state.Opted.Add(peer.NetId);
                    TryBeginCommitLocked(value.Feature);
                    return;
                case ToolkitConsentAction.Revoke:
                    DeactivateLocked(value.Feature, broadcast: true);
                    return;
                case ToolkitConsentAction.Acknowledge:
                    if (state.CommitToken.Length == 16
                        && state.CommitToken.AsSpan()
                            .SequenceEqual(value.CommitToken))
                    {
                        state.Acknowledged.Add(peer.NetId);
                        TryActivateLocked(value.Feature);
                        return;
                    }

                    break;
            }

            RejectPeerMessageLocked(peer);
            return;
        }

        if (_hostId != peer.NetId)
        {
            RejectPeerMessageLocked(peer);
            return;
        }

        switch (value.Action)
        {
            case ToolkitConsentAction.Commit
                when state.LocalOptIn
                     && ToolkitConsentCodec.CommitToken(
                            _sessionId,
                            value.Feature,
                            _membershipEpoch,
                            _rosterDigest)
                         .AsSpan().SequenceEqual(value.CommitToken):
                state.CommitToken = value.CommitToken;
                SendConsentToHostLocked(value with
                {
                    Action = ToolkitConsentAction.Acknowledge
                });
                return;
            case ToolkitConsentAction.Active
                when state.LocalOptIn
                     && state.CommitToken.AsSpan()
                         .SequenceEqual(value.CommitToken):
                state.Active = true;
                if (value.Feature == ToolkitConsentFeature.Forensics)
                {
                    ToolkitRngCounter.Clear();
                    OptionalDiagnosticsRegistry.SetEnabled(true);
                    _forensicsStatus =
                        "Forensics: active; waiting for a natural checkpoint.";
                }

                return;
            case ToolkitConsentAction.Revoke:
                DeactivateLocked(value.Feature, broadcast: false);
                return;
            default:
                RejectPeerMessageLocked(peer);
                return;
        }
    }

    private static void TryBeginCommitLocked(
        ToolkitConsentFeature feature)
    {
        if (_service?.Type != NetGameType.Host)
        {
            return;
        }

        ConsentState state = ConsentFor(feature);
        ToolkitFeature capability = CapabilityFor(feature);
        if (!state.LocalOptIn
            || Peers.Values.Any(peer =>
                !peer.Acknowledged
                || peer.Disabled
                || !peer.Features.HasFlag(capability))
            || !_roster.All(state.Opted.Contains))
        {
            return;
        }

        byte[] token = ToolkitConsentCodec.CommitToken(
            _sessionId,
            feature,
            _membershipEpoch,
            _rosterDigest);
        if (token.Length != 16)
        {
            return;
        }

        state.CommitToken = token;
        state.Acknowledged.Clear();
        state.Acknowledged.Add(_service.NetId);
        BroadcastConsentLocked(new(
            feature,
            ToolkitConsentAction.Commit,
            _membershipEpoch,
            _rosterDigest,
            token));
        TryActivateLocked(feature);
    }

    private static void TryActivateLocked(
        ToolkitConsentFeature feature)
    {
        if (_service?.Type != NetGameType.Host)
        {
            return;
        }

        ConsentState state = ConsentFor(feature);
        if (state.CommitToken.Length != 16
            || !_roster.All(state.Acknowledged.Contains))
        {
            return;
        }

        state.Active = true;
        if (feature == ToolkitConsentFeature.Forensics)
        {
            ToolkitRngCounter.Clear();
            OptionalDiagnosticsRegistry.SetEnabled(true);
            _forensicsStatus =
                "Forensics: active; waiting for a natural checkpoint.";
        }

        BroadcastConsentLocked(new(
            feature,
            ToolkitConsentAction.Active,
            _membershipEpoch,
            _rosterDigest,
            state.CommitToken));
    }

    private static void DeactivateLocked(
        ToolkitConsentFeature feature,
        bool broadcast)
    {
        ConsentState state = ConsentFor(feature);
        if (broadcast
            && _service != null
            && _sessionId != Guid.Empty
            && _rosterDigest.Length == 16)
        {
            ToolkitConsent revoke = new(
                feature,
                ToolkitConsentAction.Revoke,
                _membershipEpoch,
                _rosterDigest,
                new byte[16]);
            if (_service.Type == NetGameType.Host)
            {
                BroadcastConsentLocked(revoke);
            }
            else
            {
                SendConsentToHostLocked(revoke);
            }
        }

        state.Clear();
        if (feature == ToolkitConsentFeature.HandSharing)
        {
            Hands.Clear();
            foreach (Peer peer in Peers.Values)
            {
                peer.PendingHands.Clear();
            }
            _lastHandPayload = [];
            _handRevision = 0;
            _lastHandSendTicks = 0;
        }
        else
        {
            Checkpoints.Clear();
            DivergenceHistory.Clear();
            foreach (Peer peer in Peers.Values)
            {
                peer.PendingCheckpointResults.Clear();
                peer.PendingCheckpoints.Clear();
            }
            OptionalDiagnosticsRegistry.SetEnabled(false);
            ToolkitRngCounter.Clear();
            _forensicsStatus =
                "Forensics: disabled (unanimous consent required).";
        }
    }

    private static void BroadcastConsentLocked(ToolkitConsent consent)
    {
        ToolkitFeature capability = CapabilityFor(consent.Feature);
        foreach (Peer peer in Peers.Values.Where(value =>
                     value.Acknowledged
                     && !value.Disabled
                     && value.Features.HasFlag(capability)))
        {
            QueueOrSendConsentLocked(peer, consent);
        }
    }

    private static void SendConsentToHostLocked(ToolkitConsent consent)
    {
        if (_hostId.HasValue
            && Peers.TryGetValue(_hostId.Value, out Peer? host)
            && host.Acknowledged)
        {
            QueueOrSendConsentLocked(host, consent);
        }
    }

    private static void QueueOrSendConsentLocked(
        Peer peer,
        ToolkitConsent consent)
    {
        if (!ToolkitConsentCodec.TryEncode(consent, out byte[] payload))
        {
            return;
        }

        if (!SendLimiter.TryConsume(
                peer.Ordinal,
                Stopwatch.GetTimestamp())
            || !SendLocked(peer, ToolkitMessageType.Consent, payload))
        {
            peer.PendingConsent[consent.Feature] = consent;
            return;
        }

        peer.PendingConsent.Remove(consent.Feature);
    }

    private static void FlushPendingConsentLocked(long now)
    {
        foreach (Peer peer in Peers.Values)
        {
            foreach (ToolkitConsent consent in
                     peer.PendingConsent.Values.ToArray())
            {
                if (!peer.Acknowledged
                    || peer.Disabled
                    || !SendLimiter.TryConsume(peer.Ordinal, now)
                    || !ToolkitConsentCodec.TryEncode(
                        consent,
                        out byte[] payload)
                    || !SendLocked(
                        peer,
                        ToolkitMessageType.Consent,
                        payload))
                {
                    continue;
                }

                peer.PendingConsent.Remove(consent.Feature);
            }
        }
    }

    private static void SendOrQueueQuickStatusLocked(
        Peer peer,
        byte[] payload,
        long now)
    {
        if (peer.PendingQuickStatuses.Count == 0
            && SendLimiter.TryConsume(peer.Ordinal, now)
            && SendLocked(
                peer,
                ToolkitMessageType.QuickStatus,
                payload))
        {
            return;
        }

        if (peer.Disabled)
        {
            return;
        }

        if (peer.PendingQuickStatuses.Count == 3)
        {
            peer.PendingQuickStatuses.Dequeue();
        }

        peer.PendingQuickStatuses.Enqueue(payload);
    }

    private static void FlushPendingQuickStatusesLocked(long now)
    {
        foreach (Peer peer in Peers.Values)
        {
            while (peer.PendingQuickStatuses.Count > 0
                   && peer.Acknowledged
                   && !peer.Disabled
                   && SendLimiter.TryConsume(peer.Ordinal, now))
            {
                if (!SendLocked(
                        peer,
                        ToolkitMessageType.QuickStatus,
                        peer.PendingQuickStatuses.Peek()))
                {
                    break;
                }

                peer.PendingQuickStatuses.Dequeue();
            }
        }
    }

    private static ConsentState ConsentFor(
        ToolkitConsentFeature feature) =>
        feature == ToolkitConsentFeature.HandSharing
            ? HandConsent
            : ForensicsConsent;

    private static ToolkitFeature CapabilityFor(
        ToolkitConsentFeature feature) =>
        feature == ToolkitConsentFeature.HandSharing
            ? ToolkitFeature.HandSharing
            : ToolkitFeature.Checkpoints;

    private static void HandleHandSnapshotLocked(
        Peer peer,
        byte[] payload)
    {
        if (!HandConsent.Active
            || payload.Length < 4
            || BinaryPrimitives.ReadUInt32LittleEndian(payload)
                != _membershipEpoch
            || !ToolkitHandSnapshotCodec.TryDecode(
                payload,
                out ToolkitHandSnapshot snapshot))
        {
            DeactivateLocked(
                ToolkitConsentFeature.HandSharing,
                broadcast: _service?.Type == NetGameType.Host);
            return;
        }

        if (_service?.Type == NetGameType.Host)
        {
            if (snapshot.OwnerOrdinal != peer.Ordinal)
            {
                DeactivateLocked(
                    ToolkitConsentFeature.HandSharing,
                    broadcast: true);
                return;
            }

            if (Hands.TryGetValue(
                    snapshot.OwnerOrdinal,
                    out ToolkitHandSnapshot? current)
                && !ToolkitSequence.IsNewer(
                    snapshot.Revision,
                    current.Revision))
            {
                return;
            }

            Hands[snapshot.OwnerOrdinal] = snapshot;
            SendHandLocked(
                payload,
                snapshot.OwnerOrdinal,
                relayFrom: peer.NetId);
        }
        else if (_service != null
                 && _hostId == peer.NetId
                 && snapshot.OwnerOrdinal != OrdinalOf(_service.NetId))
        {
            if (!Hands.TryGetValue(
                    snapshot.OwnerOrdinal,
                    out ToolkitHandSnapshot? current)
                || ToolkitSequence.IsNewer(
                    snapshot.Revision,
                    current.Revision))
            {
                Hands[snapshot.OwnerOrdinal] = snapshot;
            }
        }
        else
        {
            DeactivateLocked(
                ToolkitConsentFeature.HandSharing,
                broadcast: false);
        }
    }

    private static void HandleCheckpointLocked(
        Peer peer,
        byte[] payload)
    {
        if (!ForensicsConsent.Active
            || _service?.Type != NetGameType.Host
            || !F1CheckpointCodec.TryDecode(
                payload,
                out F1Checkpoint checkpoint))
        {
            RejectPeerMessageLocked(peer);
            return;
        }

        RememberCheckpointLocked(peer.Ordinal, checkpoint);
        CompareHostCheckpointLocked(checkpoint.CheckpointId);
    }

    private static void HandleCheckpointResultLocked(
        Peer peer,
        byte[] payload)
    {
        if (!ForensicsConsent.Active
            || _service?.Type != NetGameType.Client
            || _hostId != peer.NetId
            || !F1CheckpointResultCodec.TryDecode(
                payload,
                out F1CheckpointResult result)
            || result.SubjectOrdinal > _roster.Length)
        {
            RejectPeerMessageLocked(peer);
            return;
        }

        _forensicsStatus = result.MismatchMask == 0
            ? $"Forensics: P{result.SubjectOrdinal} checkpoint {result.CheckpointId} comparable categories match."
            : $"Forensics: P{result.SubjectOrdinal} first observed divergence at checkpoint {result.CheckpointId}; "
                + "last common="
                + (result.LastCommonCheckpointId?.ToString() ?? "unknown")
                + "; "
                + "categories="
                + CategoryLabels(result.MismatchMask)
                + "; cause/player/Mod not established.";
    }

    private static void HandleContributionLocked(
        Peer peer,
        byte[] payload)
    {
        if (!_contributionsEnabled || !_contributionSharingEnabled)
        {
            return;
        }

        if (!ToolkitContributionCodec.TryDecode(
                payload,
                out ToolkitContributionSnapshot value))
        {
            DisableContributionSharingLocked();
            return;
        }

        bool valid = _service?.Type == NetGameType.Host
            ? value.Ordinal == peer.Ordinal
            : _hostId == peer.NetId;
        if (!valid || !Contributions.TryApplySnapshot(value))
        {
            DisableContributionSharingLocked();
            return;
        }

        if (_service?.Type == NetGameType.Host)
        {
            SendContributionLocked(
                payload,
                value.Ordinal,
                relayFrom: peer.NetId);
        }
    }

    private static void DisableContributionSharingLocked()
    {
        _contributionSharingEnabled = false;
        _lastContributionPayload = [];
        _lastContributionSendTicks = 0;
        Contributions.KeepOnly(OrdinalOf(_service?.NetId ?? 0));
        foreach (Peer peer in Peers.Values)
        {
            peer.PendingContributions.Clear();
        }
    }

    private static void SendContributionLocked(
        byte[] payload,
        byte subjectOrdinal,
        ulong? relayFrom)
    {
        if (_service?.Type == NetGameType.Host)
        {
            foreach (Peer peer in Peers.Values.Where(value =>
                         value.NetId != relayFrom
                         && value.Acknowledged
                         && !value.Disabled
                         && value.Features.HasFlag(
                             ToolkitFeature.Contributions)))
            {
                QueueOrSendContributionLocked(
                    peer,
                    subjectOrdinal,
                    payload);
            }
        }
        else if (_hostId.HasValue
                 && Peers.TryGetValue(_hostId.Value, out Peer? host)
                 && host.Acknowledged
                 && !host.Disabled
                 && host.Features.HasFlag(
                     ToolkitFeature.Contributions))
        {
            QueueOrSendContributionLocked(
                host,
                subjectOrdinal,
                payload);
        }
    }

    private static void QueueOrSendContributionLocked(
        Peer peer,
        byte subjectOrdinal,
        byte[] payload)
    {
        if (!SendLimiter.TryConsume(
                peer.Ordinal,
                Stopwatch.GetTimestamp())
            || !SendLocked(
                peer,
                ToolkitMessageType.Contribution,
                payload))
        {
            peer.PendingContributions[subjectOrdinal] = payload;
            return;
        }

        peer.PendingContributions.Remove(subjectOrdinal);
    }

    private static void FlushPendingContributionsLocked(long now)
    {
        if (!_contributionSharingEnabled)
        {
            foreach (Peer peer in Peers.Values)
            {
                peer.PendingContributions.Clear();
            }
            return;
        }

        foreach (Peer peer in Peers.Values)
        {
            foreach ((byte subject, byte[] payload) in
                     peer.PendingContributions.ToArray())
            {
                if (!peer.Acknowledged
                    || peer.Disabled
                    || !SendLimiter.TryConsume(peer.Ordinal, now)
                    || !SendLocked(
                        peer,
                        ToolkitMessageType.Contribution,
                        payload))
                {
                    continue;
                }

                peer.PendingContributions.Remove(subject);
            }
        }
    }

    private static void SendHandLocked(
        byte[] payload,
        byte subjectOrdinal,
        ulong? relayFrom)
    {
        if (_service?.Type == NetGameType.Host)
        {
            foreach (Peer peer in Peers.Values.Where(value =>
                         value.NetId != relayFrom
                         && value.Acknowledged
                         && !value.Disabled
                         && value.Features.HasFlag(
                             ToolkitFeature.HandSharing)))
            {
                QueueOrSendHandLocked(
                    peer,
                    subjectOrdinal,
                    payload);
            }
        }
        else if (_hostId.HasValue
                 && Peers.TryGetValue(_hostId.Value, out Peer? host)
                 && host.Acknowledged
                 && !host.Disabled
                 && host.Features.HasFlag(ToolkitFeature.HandSharing))
        {
            QueueOrSendHandLocked(host, subjectOrdinal, payload);
        }
    }

    private static void QueueOrSendHandLocked(
        Peer peer,
        byte subjectOrdinal,
        byte[] payload)
    {
        if (!SendLimiter.TryConsume(
                peer.Ordinal,
                Stopwatch.GetTimestamp())
            || !SendLocked(
                peer,
                ToolkitMessageType.HandSnapshot,
                payload))
        {
            peer.PendingHands[subjectOrdinal] = payload;
            return;
        }

        peer.PendingHands.Remove(subjectOrdinal);
    }

    private static void FlushPendingHandsLocked(long now)
    {
        if (!HandConsent.Active)
        {
            foreach (Peer peer in Peers.Values)
            {
                peer.PendingHands.Clear();
            }
            return;
        }

        foreach (Peer peer in Peers.Values)
        {
            foreach ((byte subject, byte[] payload) in
                     peer.PendingHands.ToArray())
            {
                if (!peer.Acknowledged
                    || peer.Disabled
                    || !SendLimiter.TryConsume(peer.Ordinal, now)
                    || !SendLocked(
                        peer,
                        ToolkitMessageType.HandSnapshot,
                        payload))
                {
                    continue;
                }

                peer.PendingHands.Remove(subject);
            }
        }
    }

    private static void QueueCheckpointToHostLocked(
        ulong checkpointId,
        byte[] payload)
    {
        if (!_hostId.HasValue
            || !Peers.TryGetValue(_hostId.Value, out Peer? host)
            || !host.Acknowledged
            || host.Disabled
            || !host.Features.HasFlag(ToolkitFeature.Checkpoints))
        {
            return;
        }

        if (SendLimiter.TryConsume(
                host.Ordinal,
                Stopwatch.GetTimestamp())
            && SendLocked(
                host,
                ToolkitMessageType.Checkpoint,
                payload))
        {
            host.PendingCheckpoints.Remove(checkpointId);
            return;
        }

        while (host.PendingCheckpoints.Count
               >= F2History.MaxCheckpoints
               && !host.PendingCheckpoints.ContainsKey(checkpointId))
        {
            host.PendingCheckpoints.Remove(
                host.PendingCheckpoints.Keys.Min());
        }
        host.PendingCheckpoints[checkpointId] = payload;
    }

    private static void FlushPendingCheckpointsLocked(long now)
    {
        if (!ForensicsConsent.Active)
        {
            foreach (Peer peer in Peers.Values)
            {
                peer.PendingCheckpoints.Clear();
            }
            return;
        }

        foreach (Peer peer in Peers.Values)
        {
            foreach ((ulong checkpointId, byte[] payload) in
                     peer.PendingCheckpoints
                         .OrderBy(item => item.Key)
                         .ToArray())
            {
                if (!peer.Acknowledged
                    || peer.Disabled
                    || !SendLimiter.TryConsume(peer.Ordinal, now)
                    || !SendLocked(
                        peer,
                        ToolkitMessageType.Checkpoint,
                        payload))
                {
                    break;
                }

                peer.PendingCheckpoints.Remove(checkpointId);
            }
        }
    }

    private static void RememberCheckpointLocked(
        byte ordinal,
        F1Checkpoint checkpoint)
    {
        if (!Checkpoints.TryGetValue(
                ordinal,
                out Dictionary<ulong, F1Checkpoint>? history))
        {
            history = [];
            Checkpoints[ordinal] = history;
        }

        while (history.Count >= F2History.MaxCheckpoints
               && !history.ContainsKey(checkpoint.CheckpointId))
        {
            history.Remove(history.Keys.Min());
        }

        history[checkpoint.CheckpointId] = checkpoint;
        DivergenceHistory.Add(ordinal, checkpoint);
        _forensicsStatus =
            $"Forensics: observed natural checkpoint {checkpoint.CheckpointId}.";
    }

    private static void CompareHostCheckpointLocked(ulong checkpointId)
    {
        if (_service?.Type != NetGameType.Host)
        {
            return;
        }

        byte hostOrdinal = OrdinalOf(_service.NetId);
        if (!Checkpoints.TryGetValue(
                hostOrdinal,
                out Dictionary<ulong, F1Checkpoint>? hostHistory)
            || !hostHistory.TryGetValue(
                checkpointId,
                out F1Checkpoint? host))
        {
            return;
        }

        foreach (Peer peer in Peers.Values)
        {
            if (!Checkpoints.TryGetValue(
                    peer.Ordinal,
                    out Dictionary<ulong, F1Checkpoint>? peerHistory)
                || !peerHistory.TryGetValue(
                    checkpointId,
                    out F1Checkpoint? remote)
                || host.Schema != remote.Schema
                || !host.ContextTag.AsSpan()
                    .SequenceEqual(remote.ContextTag))
            {
                continue;
            }

            F2Result localization = DivergenceHistory.Compare(
                hostOrdinal,
                peer.Ordinal);
            ulong reportId =
                localization.FirstObservedDivergent ?? checkpointId;
            F1Checkpoint reportHost = host;
            F1Checkpoint reportRemote = remote;
            if (reportId != checkpointId)
            {
                if (hostHistory.TryGetValue(
                        reportId,
                        out F1Checkpoint? priorHost)
                    && peerHistory.TryGetValue(
                        reportId,
                        out F1Checkpoint? priorRemote)
                    && priorHost != null
                    && priorRemote != null)
                {
                    reportHost = priorHost;
                    reportRemote = priorRemote;
                }
                else
                {
                    reportId = checkpointId;
                }
            }

            CompareMasks(
                reportHost,
                reportRemote,
                out byte comparable,
                out byte mismatch);
            byte[] result = F1CheckpointResultCodec.Encode(
                new(
                    peer.Ordinal,
                    reportId,
                    localization.LastCommon,
                    comparable,
                    mismatch));
            if (result.Length != 0)
            {
                foreach (Peer target in Peers.Values.Where(value =>
                             value.Acknowledged
                             && !value.Disabled
                             && value.Features.HasFlag(
                                 ToolkitFeature.Checkpoints)))
                {
                    QueueOrSendCheckpointResultLocked(
                        target,
                        peer.Ordinal,
                        result);
                }
            }

            if (mismatch != 0)
            {
                _forensicsStatus =
                    $"Forensics: first observed divergence at checkpoint {reportId}; "
                    + "last common="
                    + (localization.LastCommon?.ToString() ?? "unknown")
                    + "; "
                    + "categories="
                    + CategoryLabels(mismatch)
                    + "; cause/player/Mod not established.";
            }
        }
    }

    private static void CompareMasks(
        F1Checkpoint left,
        F1Checkpoint right,
        out byte comparable,
        out byte mismatch)
    {
        comparable = left.Schema == right.Schema
            && left.ContextTag.AsSpan().SequenceEqual(right.ContextTag)
                ? (byte)(left.AvailabilityMask & right.AvailabilityMask)
                : (byte)0;
        mismatch = 0;
        for (int index = 0; index < 8; index++)
        {
            if ((comparable & (1 << index)) != 0
                && !left.Tags[index].AsSpan()
                    .SequenceEqual(right.Tags[index]))
            {
                mismatch |= (byte)(1 << index);
            }
        }
    }

    private static void QueueOrSendCheckpointResultLocked(
        Peer peer,
        byte subjectOrdinal,
        byte[] payload)
    {
        if (!SendLimiter.TryConsume(
                peer.Ordinal,
                Stopwatch.GetTimestamp())
            || !SendLocked(
                peer,
                ToolkitMessageType.CheckpointResult,
                payload))
        {
            if (!peer.PendingCheckpointResults.TryGetValue(
                    subjectOrdinal,
                    out byte[]? existing)
                || !F1CheckpointResultCodec.TryDecode(
                    existing,
                    out F1CheckpointResult prior)
                || F1CheckpointResultCodec.TryDecode(
                    payload,
                    out F1CheckpointResult next)
                    && prior.MismatchMask == 0
                    && next.MismatchMask != 0)
            {
                peer.PendingCheckpointResults[subjectOrdinal] = payload;
            }
            return;
        }

        peer.PendingCheckpointResults.Remove(subjectOrdinal);
    }

    private static void FlushPendingCheckpointResultsLocked(long now)
    {
        foreach (Peer peer in Peers.Values)
        {
            foreach ((byte subject, byte[] payload) in
                     peer.PendingCheckpointResults.ToArray())
            {
                if (!peer.Acknowledged
                    || peer.Disabled
                    || !SendLimiter.TryConsume(peer.Ordinal, now)
                    || !SendLocked(
                        peer,
                        ToolkitMessageType.CheckpointResult,
                        payload))
                {
                    continue;
                }

                peer.PendingCheckpointResults.Remove(subject);
            }
        }
    }

    private static string CategoryLabels(byte mask) =>
        string.Join(
            ",",
            Enumerable.Range(0, 8)
                .Where(index => (mask & (1 << index)) != 0)
                .Select(index => ((F1Category)(index + 1)).ToString()));

    private static string DisplayCardId(string modelId)
    {
        try
        {
            return ModelDb.GetByIdOrNull<CardModel>(
                    ModelId.Deserialize(modelId)) != null
                ? modelId
                : "unknown-card";
        }
        catch
        {
            return "unknown-card";
        }
    }

    private static bool SendLocked(
        Peer peer,
        ToolkitMessageType type,
        byte[] payload)
    {
        if (_service == null
            || _sessionId == Guid.Empty
            || peer.Disabled)
        {
            return false;
        }

        uint sequence = unchecked(++_outSequence);
        if (sequence == 0)
        {
            sequence = ++_outSequence;
        }

        ToolkitEnvelopeMessage message = new(
            new ToolkitEnvelope(
                ToolkitEnvelopeCodec.Major,
                ToolkitEnvelopeCodec.Minor,
                type,
                0,
                _sessionId,
                sequence,
                payload));
        try
        {
            if (_service.Type == NetGameType.Host)
            {
                _service.SendMessage(message, peer.NetId);
            }
            else
            {
                _service.SendMessage(message);
            }

            return true;
        }
        catch
        {
            RejectPeerMessageLocked(peer);
            return false;
        }
    }

    private static ToolkitStateRow[] AggregateRowsLocked()
    {
        if (_service == null)
        {
            return [];
        }

        List<ToolkitStateRow> rows = [_localState];
        foreach (Peer peer in Peers.Values.OrderBy(value => value.Ordinal))
        {
            ToolkitStateRow row = peer.DeclaredState
                ?? new ToolkitStateRow(
                    peer.Ordinal,
                    ToolkitStateFlags.None,
                    0,
                    0,
                    0,
                    ushort.MaxValue,
                    ushort.MaxValue);
            if (_nativeObservations.TryGetValue(
                    peer.NetId,
                    out ToolkitPeerObservation native))
            {
                ToolkitStateFlags flags = row.Flags;
                if (native.Connected)
                {
                    flags |= ToolkitStateFlags.Connected;
                }

                if (native.Loading)
                {
                    flags |= ToolkitStateFlags.Loading;
                }

                if (native.Choosing)
                {
                    flags |= ToolkitStateFlags.ChoosingKnown
                        | ToolkitStateFlags.Choosing;
                }

                if (native.MapSubmitted.HasValue)
                {
                    flags |= ToolkitStateFlags.MapKnown;
                    if (native.MapSubmitted.Value)
                    {
                        flags |= ToolkitStateFlags.MapSubmitted;
                    }
                }

                row = row with
                {
                    Flags = flags,
                    PingMsec = ToUShort(native.PingMsec, 60_000),
                    LossPermille = native.PacketLoss.HasValue
                        ? ToUShort(native.PacketLoss.Value * 1000, 1000)
                        : ushort.MaxValue
                };
            }

            rows.Add(row);
        }

        return rows.Take(ToolkitStateCodec.MaxRows).ToArray();
    }

    private static void RejectPeerMessageLocked(Peer peer)
    {
        long now = Stopwatch.GetTimestamp();
        if (now - peer.ErrorWindowTicks > 60L * Stopwatch.Frequency)
        {
            peer.ErrorWindowTicks = now;
            peer.ParseErrors = 0;
        }

        peer.ParseErrors++;
        if (peer.ParseErrors >= 3)
        {
            DisablePeerLocked(peer);
        }
    }

    private static void DisablePeerLocked(Peer peer)
    {
        peer.Disabled = true;
        peer.Acknowledged = false;
        peer.DeclaredState = null;
        peer.PendingConsent.Clear();
        peer.PendingQuickStatuses.Clear();
        peer.PendingCheckpointResults.Clear();
        peer.PendingContributions.Clear();
        peer.PendingHands.Clear();
        peer.PendingCheckpoints.Clear();
    }

    private static byte OrdinalOf(ulong netId)
    {
        int index = Array.IndexOf(_roster, netId);
        return index < 0 ? (byte)0 : (byte)(index + 1);
    }

    private static Guid NewSessionId()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return new Guid(bytes);
    }

    private static string EnvironmentCodeLocked()
    {
        if (_environmentCode != null)
        {
            return _environmentCode;
        }

        long now = Stopwatch.GetTimestamp();
        if (now < _nextEnvironmentCodeRetryTicks)
        {
            return "unavailable";
        }

        _nextEnvironmentCodeRetryTicks =
            now + 5L * Stopwatch.Frequency;
        try
        {
            FingerprintSnapshot snapshot = ModFingerprint.ValidateQuick();
            string code = snapshot.Errors.Count == 0
                && ToolkitEnvironmentCode.TryCreate(
                    _sessionId,
                    MegaCrit.Sts2.Core.Nodes.NGame.GetGameVersion(),
                    FingerprintCodec.ProtocolVersion,
                    snapshot.Digest,
                    out string derivedCode)
                        ? derivedCode
                        : "unavailable";
            if (code != "unavailable")
            {
                _environmentCode = code;
            }
            return code;
        }
        catch
        {
            return "unavailable";
        }
    }

    private static string ShortSession(Guid sessionId) =>
        sessionId.ToString("N")[..13].ToUpperInvariant();

    private static string Metric(ushort value, string suffix) =>
        value == ushort.MaxValue ? "unknown" : value + " " + suffix;

    private static ushort ToUShort(float? value, int max)
    {
        if (!value.HasValue
            || !float.IsFinite(value.Value)
            || value.Value < 0)
        {
            return ushort.MaxValue;
        }

        return (ushort)Math.Min(max, Math.Round(value.Value));
    }

    private static void RememberStatusLocked(
        byte ordinal,
        ToolkitQuickStatus status,
        long now)
    {
        if (_statusMuted)
        {
            return;
        }

        PruneStatusesLocked(now);
        while (Statuses.Count >= 8)
        {
            Statuses.Dequeue();
        }

        Statuses.Enqueue((now, ordinal, status));
    }

    private static void PruneStatusesLocked(long now)
    {
        while (Statuses.Count > 0
            && now - Statuses.Peek().Ticks
                > 15L * Stopwatch.Frequency)
        {
            Statuses.Dequeue();
        }
    }

    private static string StatusLabel(
        ToolkitQuickStatus status,
        bool chinese) =>
        (status, chinese) switch
        {
            (ToolkitQuickStatus.PleaseWait, true) => "稍等",
            (ToolkitQuickStatus.PleaseWait, false) => "Please wait",
            (ToolkitQuickStatus.ReadyToStart, true) => "可以开始",
            (ToolkitQuickStatus.ReadyToStart, false) => "Ready to start",
            (ToolkitQuickStatus.Choosing, true) => "正在选择",
            (ToolkitQuickStatus.Choosing, false) => "Choosing",
            (ToolkitQuickStatus.NeedReconnect, true) => "需要重连",
            (ToolkitQuickStatus.NeedReconnect, false) => "Need to reconnect",
            (ToolkitQuickStatus.ViewingMap, true) => "正在看地图",
            _ => "Viewing map"
        };

    private static void ResetLocked()
    {
        ClearCollaborativeStateLocked();
        Peers.Clear();
        SendLimiter.Clear();
        QuickStatusLimiter.Clear();
        Statuses.Clear();
        _roster = [];
        _sessionId = Guid.Empty;
        _hostId = null;
        _outSequence = 0;
        _lastStateTicks = 0;
        _receivedRows = [];
        _nativeObservations.Clear();
        _sessionInvalid = false;
        _membershipEpoch = 0;
        _rosterDigest = [];
        _environmentCode = null;
        _nextEnvironmentCodeRetryTicks = 0;
    }

    private static void ClearCollaborativeStateLocked()
    {
        HandConsent.Clear();
        ForensicsConsent.Clear();
        Hands.Clear();
        Checkpoints.Clear();
        DivergenceHistory.Clear();
        Contributions.Clear();
        OptionalDiagnosticsRegistry.SetEnabled(false);
        _contributionsEnabled = false;
        _contributionSharingEnabled = false;
        _handRevision = 0;
        _lastHandPayload = [];
        _lastHandSendTicks = 0;
        _lastContributionPayload = [];
        _lastContributionSendTicks = 0;
        _forensicsStatus =
            "Forensics: disabled (unanimous consent required).";
    }
}
