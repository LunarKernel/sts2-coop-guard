using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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

internal readonly record struct ToolkitTextEntry(
    long ReceivedTicks,
    byte OriginOrdinal,
    string Text,
    int Utf8Bytes);

internal readonly record struct ToolkitRollbackTransition(
    ToolkitRunControl Control,
    ulong HostId,
    bool IsHost);

internal static class ToolkitDiagnosticsRuntime
{
  private enum RollbackStage
  {
    Preparing,
    ActivationPending,
    Activated,
    CommitPending,
    Committed,
    Failed
  }

  private sealed class RollbackState(
      ToolkitRunControl control,
      ulong hostId,
      bool isHost,
      ulong[] expectedRoster,
      long deadlineTicks)
  {
    public ToolkitRunControl Control { get; } = control;
    public ulong HostId { get; } = hostId;
    public bool IsHost { get; } = isHost;
    public ulong[] ExpectedRoster { get; } = expectedRoster;
    public long DeadlineTicks { get; set; } = deadlineTicks;
    public RollbackStage Stage { get; set; } = RollbackStage.Preparing;
    public HashSet<ulong> ReadyPeers { get; } = [];
    public HashSet<ulong> VerifiedPeers { get; } = [];
    public bool AssessmentPending { get; set; } = !isHost;
    public bool ActivationPending { get; set; }
    public bool TransitionPending { get; set; }
    public bool VerificationSent { get; set; }
    public bool CommitPending { get; set; }
    public bool NativeActivated { get; set; }
    public bool FailureTransitionPending { get; set; }
    public string Failure { get; set; } = string.Empty;
  }

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
    public Queue<byte[]> PendingTexts { get; } = [];
    public byte WatchedHandOrdinal { get; set; }
    public byte[]? PendingHandWatch { get; set; }
    public byte[]? PendingRngSummary { get; set; }
    public byte[]? PendingLimitBreakCapability { get; set; }
    public Queue<byte[]> PendingRunControls { get; } = [];
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
  private const int MaxTextHistory = 100;
  private const int MaxTextHistoryBytes = 64 * 1024;
  private static readonly object Sync = new();
  private static readonly Dictionary<ulong, Peer> Peers = [];
  private static readonly ToolkitSendLimiter SendLimiter = new();
  private static readonly ToolkitSendLimiter TextSenderLimiter = new(
      ToolkitLimits.MaxPlayers,
      tokensPerSecond: 1,
      burst: 2);
  private static readonly ToolkitSendLimiter TextGlobalLimiter = new(
      maxPeers: 1,
      tokensPerSecond: 2,
      burst: 6);
  private static readonly ToolkitSendLimiter RunControlLimiter = new(
      ToolkitLimits.MaxPlayers,
      tokensPerSecond: 4,
      burst: 8);
  private static readonly ToolkitQuickStatusLimiter QuickStatusLimiter =
      new();
  private static readonly Queue<(long Ticks, byte Ordinal,
      ToolkitQuickStatus Status)> Statuses = new();
  private static readonly ConsentState HandConsent = new();
  private static readonly ConsentState ForensicsConsent = new();
  private static readonly Dictionary<byte, ToolkitHandSnapshot> Hands = [];
  private static readonly Dictionary<byte, long> HandSeenTicks = [];
  private static readonly Queue<ToolkitTextEntry> TextHistory = new();
  private static readonly Dictionary<byte, ToolkitRngSummary>
      RngSummaries = [];
  private static readonly Dictionary<byte, ToolkitLimitBreakCapability>
      LimitBreakCapabilities = [];
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
  private static bool _runControlEnabled;
  private static uint _membershipEpoch;
  private static uint _rollbackEpoch;
  private static byte[] _rosterDigest = [];
  private static uint _handRevision;
  private static byte _watchedHandOrdinal;
  private static byte[] _lastHandPayload = [];
  private static long _lastHandSendTicks;
  private static int _textHistoryBytes;
  private static bool _textMuted;
  private static byte[] _lastLimitBreakPayload = [];
  private static long _lastLimitBreakSendTicks;
  private static byte[] _lastContributionPayload = [];
  private static long _lastContributionSendTicks;
  private static RollbackState? _rollback;
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
      | ToolkitFeature.HandWatch
      | ToolkitFeature.FreeText
      | ToolkitFeature.LimitBreakProbe
      | (_runControlEnabled
          ? ToolkitFeature.RunControl
          : ToolkitFeature.None)
      | (ToolkitRngCounter.IsAvailable
          ? ToolkitFeature.RngAnalysis
          : ToolkitFeature.None)
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
      FlushPendingHandWatchLocked(now);
      FlushPendingHandsLocked(now);
      FlushPendingTextsLocked(now);
      FlushPendingRngSummariesLocked(now);
      FlushPendingLimitBreakCapabilitiesLocked(now);
      FlushPendingRunControlsLocked(now);
      FlushPendingContributionsLocked(now);
      PublishLimitBreakCapabilityLocked(now);
      TickRollbackLocked(now);
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

  public static void SetRunControlEnabled(bool enabled)
  {
    lock (Sync)
    {
      if (_service == null)
      {
        _runControlEnabled = enabled;
      }
    }
  }

  public static bool CanSendText()
  {
    lock (Sync)
    {
      if (_service is not
        {
          IsConnected: true,
          Type: NetGameType.Host or NetGameType.Client
        }
          || _sessionId == Guid.Empty
          || _sessionInvalid)
      {
        return false;
      }

      return _service.Type == NetGameType.Host
          ? Peers.Values.Any(peer =>
              peer.Acknowledged
              && !peer.Disabled
              && peer.Features.HasFlag(ToolkitFeature.FreeText))
          : _hostId.HasValue
              && Peers.TryGetValue(_hostId.Value, out Peer? host)
              && host.Acknowledged
              && !host.Disabled
              && host.Features.HasFlag(ToolkitFeature.FreeText);
    }
  }

  public static string SendText(string text)
  {
    lock (Sync)
    {
      if (!ToolkitTextCodec.TryEncode(0, text, out byte[] submitted)
          || !ToolkitTextCodec.TryDecode(
              submitted,
              out ToolkitTextMessage normalized))
      {
        return "Text rejected: use printable Unicode, at most 384 UTF-8 bytes and four lines.";
      }

      if (!CanSendTextLocked() || _service == null)
      {
        return "Text unavailable: peer negotiation is incomplete.";
      }

      long now = Stopwatch.GetTimestamp();
      byte localOrdinal = OrdinalOf(_service.NetId);
      if (!TextSenderLimiter.TryConsume(localOrdinal, now))
      {
        return "Text rate limit reached; wait a moment.";
      }

      if (_service.Type == NetGameType.Host)
      {
        if (!TextGlobalLimiter.TryConsume(1, now)
            || !ToolkitTextCodec.TryEncode(
                localOrdinal,
                normalized.Text,
                out byte[] delivered))
        {
          return "Text room budget is busy; wait a moment.";
        }

        RememberTextLocked(localOrdinal, normalized.Text, now);
        foreach (Peer target in Peers.Values.Where(peer =>
                     peer.Acknowledged
                     && !peer.Disabled
                     && peer.Features.HasFlag(ToolkitFeature.FreeText)))
        {
          QueueOrSendTextLocked(target, delivered);
        }
      }
      else if (_hostId.HasValue
               && Peers.TryGetValue(_hostId.Value, out Peer? host))
      {
        QueueOrSendTextLocked(host, submitted);
      }
      else
      {
        return "Text unavailable: host capability is unknown.";
      }

      return "Text sent.";
    }
  }

  public static string ToggleTextMute()
  {
    lock (Sync)
    {
      _textMuted = !_textMuted;
      if (_textMuted)
      {
        TextHistory.Clear();
        _textHistoryBytes = 0;
      }

      return _textMuted
          ? "Peer text muted and in-memory history cleared."
          : "Peer text unmuted.";
    }
  }

  public static string TextStatus(bool chinese)
  {
    lock (Sync)
    {
      if (_textMuted)
      {
        return chinese ? "联机文本：已静音" : "Peer text: muted";
      }

      if (TextHistory.Count == 0)
      {
        return chinese ? "联机文本：暂无消息" : "Peer text: no messages";
      }

      return string.Join(
          '\n',
          TextHistory.TakeLast(20).Select(entry =>
              $"P{entry.OriginOrdinal}: {entry.Text}"));
    }
  }

  public static string CycleWatchedHand(int direction)
  {
    lock (Sync)
    {
      if (!HandConsent.Active || _service == null)
      {
        return "Hand shelf unavailable: unanimous sharing is not active.";
      }

      byte local = OrdinalOf(_service.NetId);
      byte[] candidates = Enumerable.Range(1, _roster.Length)
          .Select(value => (byte)value)
          .Where(value => value != local)
          .ToArray();
      if (candidates.Length == 0)
      {
        return "Hand shelf unavailable: no teammate is present.";
      }

      int current = Array.IndexOf(candidates, _watchedHandOrdinal);
      int step = direction < 0 ? -1 : 1;
      int next = current < 0
          ? 0
          : (current + step + candidates.Length) % candidates.Length;
      SetWatchedHandLocked(candidates[next]);
      return $"Watching P{_watchedHandOrdinal}.";
    }
  }

  public static string HandShelfStatus(bool chinese)
  {
    lock (Sync)
    {
      if (!HandConsent.Active)
      {
        return chinese
            ? "队友手牌：需要全员同意"
            : "Teammate hand: unanimous consent required";
      }

      if (_watchedHandOrdinal == 0)
      {
        return chinese
            ? "队友手牌：没有可查看的队友"
            : "Teammate hand: no teammate available";
      }

      string prefix = chinese
          ? $"正在查看 P{_watchedHandOrdinal}"
          : $"Watching P{_watchedHandOrdinal}";
      if (!Hands.TryGetValue(
              _watchedHandOrdinal,
              out ToolkitHandSnapshot? hand)
          || !HandSeenTicks.TryGetValue(
              _watchedHandOrdinal,
              out long seen))
      {
        return prefix + (chinese ? "：等待快照" : ": waiting for snapshot");
      }

      long age = Stopwatch.GetTimestamp() - seen;
      if (age > 3L * Stopwatch.Frequency)
      {
        return prefix + (chinese ? "：数据已过期" : ": data expired");
      }

      string stale = age > Stopwatch.Frequency
          ? chinese ? "（数据较旧）" : " (stale)"
          : string.Empty;
      string cards = hand.Cards.Length == 0
          ? chinese ? "空" : "empty"
          : string.Join(
              ", ",
              hand.Cards.Select(card =>
                  $"{DisplayCardId(card.ModelId)}+{card.Upgrade} [{card.PublicCost}]"));
      return $"{prefix}{stale}: {cards}";
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
      bool contentChanged =
          !_lastHandPayload.AsSpan().SequenceEqual(content);
      if (!contentChanged
          && now - _lastHandSendTicks < 2L * Stopwatch.Frequency)
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
      HandSeenTicks[ordinal] = now;
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

  internal static void PublishRngSummary(ulong checkpointId)
  {
    lock (Sync)
    {
      if (_service == null
          || _sessionId == Guid.Empty
          || _membershipEpoch == 0
          || _rollbackEpoch == 0)
      {
        return;
      }

      byte ordinal = OrdinalOf(_service.NetId);
      if (ordinal == 0
          || !ToolkitForensicsRuntime.TryCaptureRngSummary(
              _sessionId,
              ordinal,
              _membershipEpoch,
              _rollbackEpoch,
              checkpointId,
              out ToolkitRngSummary summary))
      {
        return;
      }

      RngSummaries[ordinal] = summary;
      ToolkitRngSummary outbound = _service.Type == NetGameType.Client
          ? summary with
          {
            OriginOrdinal = 0
          }
          : summary;
      if (!ToolkitRngSummaryCodec.TryEncode(
              outbound,
              out byte[] payload))
      {
        return;
      }

      if (_service.Type == NetGameType.Host)
      {
        RelayRngSummaryLocked(payload, relayFrom: null);
      }
      else if (_hostId.HasValue
               && Peers.TryGetValue(_hostId.Value, out Peer? host)
               && host.Acknowledged
               && !host.Disabled
               && host.Features.HasFlag(ToolkitFeature.RngAnalysis))
      {
        QueueOrSendRngSummaryLocked(host, payload);
      }
    }
  }

  internal static bool TryGetRollbackEvidence(
      out uint rollbackEpoch,
      out string rosterDigest,
      out string seedTag,
      out string rngDigest)
  {
    lock (Sync)
    {
      rollbackEpoch = _rollbackEpoch;
      rosterDigest = string.Empty;
      seedTag = string.Empty;
      rngDigest = string.Empty;
      if (_service == null
          || _sessionId == Guid.Empty
          || _membershipEpoch == 0
          || _rollbackEpoch == 0
          || _rosterDigest.Length != 16)
      {
        return false;
      }

      byte ordinal = OrdinalOf(_service.NetId);
      if (ordinal == 0
          || !ToolkitForensicsRuntime.TryCaptureRngSummary(
              _sessionId,
              ordinal,
              _membershipEpoch,
              _rollbackEpoch,
              checkpointId: 1,
              out ToolkitRngSummary summary)
          || !ToolkitRngSummaryCodec.TryEncode(
              summary,
              out byte[] encoded))
      {
        return false;
      }

      rosterDigest = Convert.ToHexString(
              StableRosterDigestLocked())
          .ToLowerInvariant();
      seedTag = Convert.ToHexString(summary.SeedTag)
          .ToLowerInvariant();
      rngDigest = Convert.ToHexString(SHA256.HashData(encoded))
          .ToLowerInvariant();
      return true;
    }
  }

  public static string RngStatus(bool chinese, bool revealRaw)
  {
    lock (Sync)
    {
      string local = ToolkitForensicsRuntime.LocalRngStatus(
          _sessionId,
          revealRaw,
          chinese);
      if (_service == null || _sessionId == Guid.Empty)
      {
        return local;
      }

      byte localOrdinal = OrdinalOf(_service.NetId);
      if (!RngSummaries.TryGetValue(
              localOrdinal,
              out ToolkitRngSummary? baseline))
      {
        return local
            + (chinese
                ? "\n对端比较：等待自然校验点"
                : "\nPeer comparison: waiting for a natural checkpoint");
      }

      List<string> peerLines = [];
      foreach (byte ordinal in Enumerable.Range(1, _roster.Length)
                   .Select(value => (byte)value)
                   .Where(value => value != localOrdinal))
      {
        if (!RngSummaries.TryGetValue(
                ordinal,
                out ToolkitRngSummary? remote))
        {
          peerLines.Add(chinese
              ? $"P{ordinal}：等待摘要"
              : $"P{ordinal}: waiting for summary");
          continue;
        }

        if (remote.MembershipEpoch != baseline.MembershipEpoch
            || remote.RollbackEpoch != baseline.RollbackEpoch
            || remote.CheckpointId != baseline.CheckpointId)
        {
          peerLines.Add(chinese
              ? $"P{ordinal}：校验点/epoch 不可比较"
              : $"P{ordinal}: checkpoint/epoch not comparable");
          continue;
        }

        if (!remote.SeedTag.AsSpan().SequenceEqual(baseline.SeedTag))
        {
          peerLines.Add(chinese
              ? $"P{ordinal}：seed 不一致"
              : $"P{ordinal}: seed mismatch");
          continue;
        }

        int first = Enumerable.Range(
                0,
                ToolkitRngSummaryCodec.StreamCount)
            .FirstOrDefault(
                index => remote.Streams[index]
                    != baseline.Streams[index],
                -1);
        peerLines.Add(first < 0
            ? chinese
                ? $"P{ordinal}：一致"
                : $"P{ordinal}: match"
            : chinese
                ? $"P{ordinal}：首次不同 stream="
                    + ToolkitRngAnalyzer.StreamIds[first]
                : $"P{ordinal}: first differing stream="
                    + ToolkitRngAnalyzer.StreamIds[first]);
      }

      return local + "\n" + string.Join('\n', peerLines);
    }
  }

  public static bool CanUseLimitBreak(
      int playerCount,
      out string reason) =>
      CanUseLimitBreak(
          playerCount,
          requireSettingsSynchronized: true,
          out reason);

  public static bool CanReadyWithLimitBreak(
      int playerCount,
      out string reason) =>
      CanUseLimitBreak(
          playerCount,
          requireSettingsSynchronized: false,
          out reason);

  private static bool CanUseLimitBreak(
      int playerCount,
      bool requireSettingsSynchronized,
      out string reason)
  {
    lock (Sync)
    {
      if (playerCount <= 4)
      {
        reason = string.Empty;
        return true;
      }

      if (playerCount > ToolkitLimits.MaxPlayers)
      {
        reason =
            $"roster has {playerCount} players; maximum is {ToolkitLimits.MaxPlayers}";
        return false;
      }

      if (_service == null
          || _membershipEpoch == 0
          || _roster.Length != playerCount)
      {
        reason =
            $"BetterCoop roster proof is pending ({_roster.Length}/{playerCount})";
        return false;
      }

      byte localOrdinal = OrdinalOf(_service.NetId);
      if (localOrdinal == 0)
      {
        reason = "local player ordinal is unavailable";
        return false;
      }

      bool localCompatible = requireSettingsSynchronized
          ? LimitBreakAdapter.TryCapture(
              _service,
              localOrdinal,
              _membershipEpoch,
              out ToolkitLimitBreakCapability local,
              out string localReason)
          : LimitBreakAdapter.TryCaptureForReady(
              _service,
              localOrdinal,
              _membershipEpoch,
              out local,
              out localReason);
      if (!localCompatible)
      {
        reason = "local Limit Break: " + localReason;
        return false;
      }

      LimitBreakCapabilities[localOrdinal] = local;
      IEnumerable<Peer> transportPeers =
          _service.Type == NetGameType.Host
              ? Peers.Values
              : _hostId.HasValue
                  && Peers.TryGetValue(
                      _hostId.Value,
                      out Peer? host)
                      ? [host]
                      : [];
      foreach (Peer peer in transportPeers.OrderBy(
                   value => value.Ordinal))
      {
        if (!peer.Acknowledged
            || peer.Disabled
            || !peer.Features.HasFlag(
                ToolkitFeature.LimitBreakProbe))
        {
          reason =
              $"P{peer.Ordinal}: BetterCoop Limit Break proof is unavailable";
          return false;
        }
      }

      for (byte ordinal = 1;
           ordinal <= _roster.Length;
           ordinal++)
      {
        if (ordinal == localOrdinal)
        {
          continue;
        }

        if (!LimitBreakCapabilities.TryGetValue(
                ordinal,
                out ToolkitLimitBreakCapability? remote))
        {
          reason = $"P{ordinal}: Limit Break proof is pending";
          return false;
        }

        bool remoteCompatible = requireSettingsSynchronized
            ? ToolkitLimitBreakContract.IsCompatible(
                remote,
                out string remoteReason)
            : ToolkitLimitBreakContract.IsReadyCompatible(
                remote,
                out remoteReason);
        if (!remoteCompatible)
        {
          reason = $"P{ordinal}: {remoteReason}";
          return false;
        }

        if (!remote.ContractDigest.AsSpan().SequenceEqual(
                local.ContractDigest))
        {
          reason =
              $"P{ordinal}: Limit Break/RitsuLib contract differs";
          return false;
        }

        if (!remote.SettingsDigest.AsSpan().SequenceEqual(
                local.SettingsDigest))
        {
          reason =
              $"P{ordinal}: Limit Break enabled/scaling settings differ";
          return false;
        }
      }

      reason = string.Empty;
      return true;
    }
  }

  public static string LimitBreakStatus(bool chinese)
  {
    lock (Sync)
    {
      if (_roster.Length <= 4)
      {
        return chinese
            ? "Limit Break：2–4 人无需兼容门禁"
            : "Limit Break: compatibility gate not required for 2-4 players";
      }

      bool compatible = CanUseLimitBreak(
          _roster.Length,
          out string reason);
      return compatible
          ? chinese
              ? $"Limit Break：{_roster.Length} 人能力证明一致"
              : $"Limit Break: {_roster.Length}-player capability proofs match"
          : (chinese
              ? "Limit Break：阻止开局 — "
              : "Limit Break: start blocked — ")
            + reason;
    }
  }

  public static bool TryStartRollback(
      RollbackCheckpointMetadata checkpoint,
      out Guid transactionId,
      out string status)
  {
    lock (Sync)
    {
      transactionId = Guid.Empty;
      if (!_runControlEnabled)
      {
        status = "Rollback is disabled in local BetterCoop settings.";
        return false;
      }

      if (_service is not
        {
          IsConnected: true,
          Type: NetGameType.Host
        }
          || _sessionId == Guid.Empty
          || _membershipEpoch == 0
          || _rollback != null)
      {
        status =
            "Rollback unavailable: host session or transaction state is not ready.";
        return false;
      }

      if (Peers.Count != _roster.Length - 1
          || Peers.Values.Any(peer =>
              !peer.Acknowledged
              || peer.Disabled
              || !peer.Features.HasFlag(ToolkitFeature.RunControl)))
      {
        status =
            "Rollback unavailable until every current peer enables BetterCoop Run Control.";
        return false;
      }

      if (!Guid.TryParseExact(
              checkpoint.CheckpointId,
              "N",
              out Guid checkpointId))
      {
        status = "Rollback checkpoint ID is invalid.";
        return false;
      }

      byte[] digest = RollbackCheckpointIdentity.Create(checkpoint);

      uint nextEpoch = unchecked(_rollbackEpoch + 1);
      byte ordinal = OrdinalOf(_service.NetId);
      transactionId = NewSessionId();
      ToolkitRunControl control = new(
          ToolkitRunControlAction.Prepare,
          ordinal,
          ToolkitRunControlResult.None,
          _membershipEpoch,
          nextEpoch,
          transactionId,
          checkpointId,
          checkpoint.VisitIndex,
          digest);
      if (nextEpoch == 0
          || ordinal == 0
          || !ToolkitRunControlCodec.TryEncode(
              control,
              out byte[] payload))
      {
        transactionId = Guid.Empty;
        status = "Rollback control payload could not be created.";
        return false;
      }

      long now = Stopwatch.GetTimestamp();
      _rollback = new(
          control,
          _service.NetId,
          isHost: true,
          _roster.ToArray(),
          now + 10L * Stopwatch.Frequency);
      _rollback.ReadyPeers.Add(_service.NetId);
      BroadcastRunControlLocked(payload);
      status =
          "Rollback Prepare sent; waiting up to 10 seconds for every peer.";
      return true;
    }
  }

  public static string CancelRollback()
  {
    lock (Sync)
    {
      if (_service?.Type != NetGameType.Host
          || _rollback is not
          {
            Stage: RollbackStage.Preparing
                or RollbackStage.ActivationPending
          } state)
      {
        return "No cancellable rollback transaction exists.";
      }

      AbortRollbackLocked(
          state,
          ToolkitRunControlResult.Cancelled,
          "Rollback cancelled by host.");
      _rollback = null;
      return "Rollback cancelled before native save activation.";
    }
  }

  internal static bool TryTakeRollbackAssessment(
      out ToolkitRunControl control)
  {
    lock (Sync)
    {
      if (_rollback is not
        {
          Stage: RollbackStage.Preparing,
          AssessmentPending: true
        } state)
      {
        control = null!;
        return false;
      }

      state.AssessmentPending = false;
      control = state.Control;
      return true;
    }
  }

  internal static void ReplyRollbackAssessment(
      Guid transactionId,
      ToolkitRunControlResult result)
  {
    lock (Sync)
    {
      if (_service?.Type != NetGameType.Client
          || _rollback is not
          {
            Stage: RollbackStage.Preparing
          } state
          || state.Control.TransactionId != transactionId
          || !_hostId.HasValue
          || !Peers.TryGetValue(_hostId.Value, out Peer? host))
      {
        return;
      }

      ToolkitRunControl reply = state.Control with
      {
        Action = ToolkitRunControlAction.Ready,
        OriginOrdinal = 0,
        Result = result,
        MembershipEpoch = _membershipEpoch
      };
      if (ToolkitRunControlCodec.TryEncode(
              reply,
              out byte[] payload))
      {
        QueueOrSendRunControlLocked(host, payload);
      }

    }
  }

  internal static bool TryTakeRollbackActivation(
      out ToolkitRunControl control)
  {
    lock (Sync)
    {
      if (_rollback is not
        {
          IsHost: true,
          Stage: RollbackStage.ActivationPending,
          ActivationPending: true
        } state)
      {
        control = null!;
        return false;
      }

      state.ActivationPending = false;
      control = state.Control;
      return true;
    }
  }

  internal static void CompleteRollbackActivation(
      Guid transactionId,
      bool succeeded)
  {
    lock (Sync)
    {
      if (_service?.Type != NetGameType.Host
          || _rollback is not
          {
            IsHost: true,
            Stage: RollbackStage.ActivationPending
          } state
          || state.Control.TransactionId != transactionId)
      {
        return;
      }

      if (!succeeded)
      {
        AbortRollbackLocked(
            state,
            ToolkitRunControlResult.ActivationFailed,
            "Host could not atomically activate the selected native save.");
        return;
      }

      ToolkitRunControl activate = state.Control with
      {
        Action = ToolkitRunControlAction.Activate,
        Result = ToolkitRunControlResult.None
      };
      if (!ToolkitRunControlCodec.TryEncode(
              activate,
              out byte[] payload))
      {
        AbortRollbackLocked(
            state,
            ToolkitRunControlResult.ActivationFailed,
            "Host could not encode Rollback Activate.");
        return;
      }

      BroadcastRunControlLocked(payload);
      state.Stage = RollbackStage.Activated;
      state.NativeActivated = true;
      state.TransitionPending = true;
      state.DeadlineTicks =
          Stopwatch.GetTimestamp() + 60L * Stopwatch.Frequency;
      _rollbackEpoch = state.Control.RollbackEpoch;
      ClearPostRollbackEvidenceLocked();
    }
  }

  internal static bool TryTakeRollbackTransition(
      out ToolkitRollbackTransition transition)
  {
    lock (Sync)
    {
      if (_rollback is not
        {
          Stage: RollbackStage.Activated,
          TransitionPending: true
        } state)
      {
        transition = default;
        return false;
      }

      state.TransitionPending = false;
      transition = new(
          state.Control,
          state.HostId,
          state.IsHost);
      return true;
    }
  }

  internal static bool TrySubmitRollbackVerification(
      bool checkpointMatches,
      out string status)
  {
    lock (Sync)
    {
      if (_service is not
        {
          IsConnected: true,
          Type: NetGameType.Host or NetGameType.Client
        }
          || _sessionId == Guid.Empty
          || _rollback is not
          {
            Stage: RollbackStage.Activated,
            VerificationSent: false
          } state
          || !ToolkitRunControlRoster.IsExact(
              state.ExpectedRoster,
              _roster))
      {
        status =
            "Rollback verification is waiting for the complete original roster.";
        return false;
      }

      ToolkitRunControl verification = state.Control with
      {
        Action = ToolkitRunControlAction.Verify,
        OriginOrdinal = _service.Type == NetGameType.Host
            ? OrdinalOf(_service.NetId)
            : (byte)0,
        Result = checkpointMatches
            ? ToolkitRunControlResult.Ready
            : ToolkitRunControlResult.DigestMismatch,
        MembershipEpoch = _membershipEpoch
      };
      if (!ToolkitRunControlCodec.TryEncode(
              verification,
              out byte[] payload))
      {
        status = "Rollback verification payload was rejected locally.";
        return false;
      }

      state.VerificationSent = true;
      if (_service.Type == NetGameType.Host)
      {
        if (checkpointMatches)
        {
          state.VerifiedPeers.Add(_service.NetId);
          TryCommitRollbackLocked(state);
          status = "Host checkpoint verified; waiting for every peer.";
        }
        else
        {
          FailActivatedRollbackLocked(
              state,
              "Host loaded checkpoint digest differs.",
              ToolkitRunControlResult.DigestMismatch);
          status = state.Failure;
        }

        return true;
      }

      if (!_hostId.HasValue
          || !Peers.TryGetValue(_hostId.Value, out Peer? host)
          || !host.Acknowledged
          || host.Disabled
          || !host.Features.HasFlag(ToolkitFeature.RunControl))
      {
        state.VerificationSent = false;
        status = "Rollback verification is waiting for host negotiation.";
        return false;
      }

      QueueOrSendRunControlLocked(host, payload);
      status = checkpointMatches
          ? "Loaded checkpoint verified and reported to host."
          : "Loaded checkpoint digest differs; host was notified.";
      return true;
    }
  }

  internal static bool TryTakeRollbackCommit(
      out Guid transactionId,
      out bool isHost)
  {
    lock (Sync)
    {
      if (_rollback is not
        {
          Stage: RollbackStage.CommitPending
              or RollbackStage.Committed,
          CommitPending: true
        } state)
      {
        transactionId = Guid.Empty;
        isHost = false;
        return false;
      }

      state.CommitPending = false;
      transactionId = state.Control.TransactionId;
      isHost = state.IsHost;
      return true;
    }
  }

  internal static bool CompleteRollbackCommitPersistence(
      Guid transactionId,
      bool succeeded,
      out string status)
  {
    lock (Sync)
    {
      if (_service?.Type != NetGameType.Host
          || _rollback is not
          {
            IsHost: true,
            Stage: RollbackStage.CommitPending
          } state
          || state.Control.TransactionId != transactionId)
      {
        status = "Rollback commit transaction is no longer current.";
        return false;
      }

      if (!succeeded)
      {
        FailActivatedRollbackLocked(
            state,
            "Host could not persist rollback Commit.");
        status = state.Failure;
        return false;
      }

      ToolkitRunControl commit = state.Control with
      {
        Action = ToolkitRunControlAction.Commit,
        OriginOrdinal = OrdinalOf(_service.NetId),
        Result = ToolkitRunControlResult.None,
        MembershipEpoch = _membershipEpoch
      };
      if (!ToolkitRunControlCodec.TryEncode(
              commit,
              out byte[] payload))
      {
        FailActivatedRollbackLocked(
            state,
            "Host could not encode Rollback Commit.");
        status = state.Failure;
        return false;
      }

      BroadcastRunControlLocked(payload);
      state.Stage = RollbackStage.Committed;
      status = "Rollback committed after every peer verified.";
      return true;
    }
  }

  internal static void FinishRollbackCommit(Guid transactionId)
  {
    lock (Sync)
    {
      if (_rollback?.Control.TransactionId == transactionId
          && _rollback.Stage == RollbackStage.Committed)
      {
        _rollback = null;
      }
    }
  }

  internal static bool CanReadyAfterRollback(out string reason)
  {
    lock (Sync)
    {
      if (_rollback == null
          || _rollback.Stage == RollbackStage.Committed)
      {
        reason = string.Empty;
        return true;
      }

      reason = _rollback.Stage == RollbackStage.Failed
          ? "rollback recovery required: " + _rollback.Failure
          : "rollback verification has not committed";
      return false;
    }
  }

  internal static bool TryTakeRollbackFailureTransition(
      out bool isHost)
  {
    lock (Sync)
    {
      if (_rollback is not
        {
          Stage: RollbackStage.Failed,
          NativeActivated: true,
          FailureTransitionPending: true
        } state)
      {
        isHost = false;
        return false;
      }

      state.FailureTransitionPending = false;
      isHost = state.IsHost;
      if (!state.IsHost)
      {
        _rollback = null;
      }

      return true;
    }
  }

  internal static void CompleteRollbackRecovery(Guid transactionId)
  {
    lock (Sync)
    {
      if (_rollback is
        {
          Stage: RollbackStage.Failed
        } state
          && state.Control.TransactionId == transactionId)
      {
        _rollback = null;
      }
    }
  }

  internal static bool RunControlSettingMatches(out string reason)
  {
    lock (Sync)
    {
      if (_service == null || _roster.Length < 2)
      {
        if (_runControlEnabled)
        {
          reason =
              "Run Control is enabled locally but peer negotiation is incomplete";
          return false;
        }

        reason = string.Empty;
        return true;
      }

      foreach (Peer peer in Peers.Values)
      {
        if (!peer.Acknowledged)
        {
          if (_runControlEnabled)
          {
            reason =
                $"P{peer.Ordinal}: Run Control setting proof is pending";
            return false;
          }

          continue;
        }

        bool remote = peer.Features.HasFlag(
            ToolkitFeature.RunControl);
        if (remote != _runControlEnabled)
        {
          reason =
              $"P{peer.Ordinal}: Run Control enabled setting differs";
          return false;
        }
      }

      reason = string.Empty;
      return true;
    }
  }

  public static string RollbackStatus(bool chinese)
  {
    lock (Sync)
    {
      if (!_runControlEnabled)
      {
        return chinese
            ? "节点回溯：已关闭（默认）"
            : "Node rollback: disabled (default)";
      }

      if (_rollback == null)
      {
        return chinese
            ? "节点回溯：空闲"
            : "Node rollback: idle";
      }

      string transaction =
          _rollback.Control.TransactionId.ToString("N")[..8];
      return (chinese ? "节点回溯：" : "Node rollback: ")
          + _rollback.Stage
          + " "
          + transaction
          + (_rollback.Failure.Length == 0
              ? string.Empty
              : " — " + _rollback.Failure);
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
      RunControlLimiter.Clear();
      QuickStatusLimiter.Clear();
      Statuses.Clear();
      _receivedRows = [];
      _sessionInvalid = false;
      ClearCollaborativeStateLocked();
      _membershipEpoch = 0;
      _rollbackEpoch = 0;
      _rosterDigest = [];
    }
  }

  public static void RejectTransportPacket(string reason)
  {
    int rejected = Interlocked.Increment(ref _transportRejects);
    if (rejected is 1 or 10 or 100)
    {
      Main.Log.Warn(
          "Dropped malformed or sender-spoofed optional diagnostics packet ("
          + FlightRecorder.BoundedText(reason, 64)
          + "); Guard and native networking were unchanged.");
    }
  }

  private static void BeginRosterLocked(ulong[] roster)
  {
    if (_rollback is
      {
        NativeActivated: false,
        Stage: not RollbackStage.Committed
      } interrupted)
    {
      if (interrupted.IsHost)
      {
        AbortRollbackLocked(
            interrupted,
            ToolkitRunControlResult.Cancelled,
            "Roster changed before native rollback activation.");
      }

      _rollback = null;
    }

    if (_rollback is
      {
        NativeActivated: true,
        Stage: RollbackStage.Activated
      } activeRollback
        && roster.Any(id =>
            !activeRollback.ExpectedRoster.Contains(id)))
    {
      FailActivatedRollbackLocked(
          activeRollback,
          "Loaded lobby contains a player outside the original roster.");
    }

    ClearCollaborativeStateLocked();
    _roster = roster;
    Peers.Clear();
    SendLimiter.Clear();
    TextSenderLimiter.Clear();
    TextGlobalLimiter.Clear();
    RunControlLimiter.Clear();
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
    _rollbackEpoch = _rollback is { NativeActivated: true } active
        ? active.Control.RollbackEpoch
        : 1;
    if (_rollback is
      {
        NativeActivated: true,
        Stage: RollbackStage.Activated
      } verifying)
    {
      verifying.VerificationSent = false;
      verifying.VerifiedPeers.Clear();
    }

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
        case ToolkitMessageType.Text:
          HandleTextLocked(peer, envelope.Payload);
          break;
        case ToolkitMessageType.HandWatch:
          HandleHandWatchLocked(peer, envelope.Payload);
          break;
        case ToolkitMessageType.RngSummary:
          HandleRngSummaryLocked(peer, envelope.Payload);
          break;
        case ToolkitMessageType.LimitBreakCapability:
          HandleLimitBreakCapabilityLocked(peer, envelope.Payload);
          break;
        case ToolkitMessageType.RunControl:
          HandleRunControlLocked(peer, envelope.Payload);
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
        else
        {
          EnsureWatchedHandLocked();
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
    else
    {
      EnsureWatchedHandLocked();
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
      HandSeenTicks.Clear();
      _watchedHandOrdinal = 0;
      foreach (Peer peer in Peers.Values)
      {
        peer.PendingHands.Clear();
        peer.WatchedHandOrdinal = 0;
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
          ? ToolkitFeature.HandSharing | ToolkitFeature.HandWatch
          : ToolkitFeature.Checkpoints;

  private static void HandleTextLocked(Peer peer, byte[] payload)
  {
    if (!peer.Features.HasFlag(ToolkitFeature.FreeText)
        || !ToolkitTextCodec.TryDecode(
            payload,
            out ToolkitTextMessage message))
    {
      RejectPeerMessageLocked(peer);
      return;
    }

    long now = Stopwatch.GetTimestamp();
    if (_service?.Type == NetGameType.Host)
    {
      if (message.OriginOrdinal != 0)
      {
        RejectPeerMessageLocked(peer);
        return;
      }

      if (!TextSenderLimiter.TryConsume(peer.Ordinal, now)
          || !TextGlobalLimiter.TryConsume(1, now))
      {
        return;
      }

      if (!ToolkitTextCodec.TryEncode(
              peer.Ordinal,
              message.Text,
              out byte[] delivered))
      {
        RejectPeerMessageLocked(peer);
        return;
      }

      RememberTextLocked(peer.Ordinal, message.Text, now);
      foreach (Peer target in Peers.Values.Where(value =>
                   value.Acknowledged
                   && !value.Disabled
                   && value.Features.HasFlag(ToolkitFeature.FreeText)))
      {
        QueueOrSendTextLocked(target, delivered);
      }
    }
    else if (_hostId == peer.NetId
             && message.OriginOrdinal is >= 1
                 and <= ToolkitLimits.MaxPlayers
             && message.OriginOrdinal <= _roster.Length)
    {
      RememberTextLocked(
          message.OriginOrdinal,
          message.Text,
          now);
    }
    else
    {
      RejectPeerMessageLocked(peer);
    }
  }

  private static void HandleHandWatchLocked(Peer peer, byte[] payload)
  {
    if (_service?.Type != NetGameType.Host
        || !HandConsent.Active
        || !peer.Features.HasFlag(ToolkitFeature.HandWatch)
        || !ToolkitHandWatchCodec.TryDecode(
            payload,
            out ToolkitHandWatch watch)
        || watch.MembershipEpoch != _membershipEpoch
        || watch.OwnerOrdinal > _roster.Length
        || watch.OwnerOrdinal == peer.Ordinal)
    {
      RejectPeerMessageLocked(peer);
      return;
    }

    peer.WatchedHandOrdinal = watch.OwnerOrdinal;
    peer.PendingHands.Clear();
    if (Hands.TryGetValue(
            watch.OwnerOrdinal,
            out ToolkitHandSnapshot? hand)
        && ToolkitHandSnapshotCodec.TryEncode(
            hand,
            out byte[] snapshot))
    {
      QueueOrSendHandLocked(peer, watch.OwnerOrdinal, snapshot);
    }
  }

  private static void HandleRngSummaryLocked(Peer peer, byte[] payload)
  {
    if (!peer.Features.HasFlag(ToolkitFeature.RngAnalysis)
        || !ToolkitRngSummaryCodec.TryDecode(
            payload,
            out ToolkitRngSummary summary)
        || summary.MembershipEpoch != _membershipEpoch
        || summary.RollbackEpoch != _rollbackEpoch)
    {
      RejectPeerMessageLocked(peer);
      return;
    }

    if (_service?.Type == NetGameType.Host)
    {
      if (summary.OriginOrdinal != 0)
      {
        RejectPeerMessageLocked(peer);
        return;
      }

      ToolkitRngSummary delivered = summary with
      {
        OriginOrdinal = peer.Ordinal
      };
      if (!ToolkitRngSummaryCodec.TryEncode(
              delivered,
              out byte[] relayPayload))
      {
        RejectPeerMessageLocked(peer);
        return;
      }

      RngSummaries[peer.Ordinal] = delivered;
      RelayRngSummaryLocked(relayPayload, peer.NetId);
    }
    else if (_hostId == peer.NetId
             && summary.OriginOrdinal is >= 1
                 and <= ToolkitLimits.MaxPlayers
             && summary.OriginOrdinal <= _roster.Length)
    {
      RngSummaries[summary.OriginOrdinal] = summary;
    }
    else
    {
      RejectPeerMessageLocked(peer);
    }
  }

  private static void HandleLimitBreakCapabilityLocked(
      Peer peer,
      byte[] payload)
  {
    if (!peer.Features.HasFlag(ToolkitFeature.LimitBreakProbe)
        || !ToolkitLimitBreakCapabilityCodec.TryDecode(
            payload,
            out ToolkitLimitBreakCapability capability)
        || capability.MembershipEpoch != _membershipEpoch)
    {
      RejectPeerMessageLocked(peer);
      return;
    }

    if (_service?.Type == NetGameType.Host)
    {
      if (capability.OriginOrdinal != 0)
      {
        RejectPeerMessageLocked(peer);
        return;
      }

      ToolkitLimitBreakCapability delivered = capability with
      {
        OriginOrdinal = peer.Ordinal
      };
      if (!ToolkitLimitBreakCapabilityCodec.TryEncode(
              delivered,
              out byte[] relayPayload))
      {
        RejectPeerMessageLocked(peer);
        return;
      }

      LimitBreakCapabilities[peer.Ordinal] = delivered;
      RelayLimitBreakCapabilityLocked(
          relayPayload,
          peer.NetId);
    }
    else if (_hostId == peer.NetId
             && capability.OriginOrdinal is >= 1
                 and <= ToolkitLimits.MaxPlayers
             && capability.OriginOrdinal <= _roster.Length)
    {
      LimitBreakCapabilities[capability.OriginOrdinal] =
          capability;
    }
    else
    {
      RejectPeerMessageLocked(peer);
    }
  }

  private static void HandleRunControlLocked(
      Peer peer,
      byte[] payload)
  {
    if (!peer.Features.HasFlag(ToolkitFeature.RunControl)
        || !ToolkitRunControlCodec.TryDecode(
            payload,
            out ToolkitRunControl control)
        || control.MembershipEpoch != _membershipEpoch)
    {
      RejectPeerMessageLocked(peer);
      return;
    }

    if (_service?.Type == NetGameType.Host)
    {
      HandleClientRunControlLocked(peer, control);
      return;
    }

    if (_service?.Type != NetGameType.Client
        || _hostId != peer.NetId
        || control.OriginOrdinal != peer.Ordinal)
    {
      RejectPeerMessageLocked(peer);
      return;
    }

    if (control.Action == ToolkitRunControlAction.Prepare)
    {
      if (_rollback != null
          || control.RollbackEpoch != unchecked(_rollbackEpoch + 1))
      {
        RejectPeerMessageLocked(peer);
        return;
      }

      _rollback = new(
          control,
          peer.NetId,
          isHost: false,
          _roster.ToArray(),
          Stopwatch.GetTimestamp() + 10L * Stopwatch.Frequency);
      return;
    }

    if (_rollback is not { IsHost: false } state
        || !SameRollbackTarget(state.Control, control))
    {
      RejectPeerMessageLocked(peer);
      return;
    }

    switch (control.Action)
    {
      case ToolkitRunControlAction.Activate
          when state.Stage == RollbackStage.Preparing:
        state.Stage = RollbackStage.Activated;
        state.NativeActivated = true;
        state.TransitionPending = true;
        state.DeadlineTicks =
            Stopwatch.GetTimestamp() + 60L * Stopwatch.Frequency;
        _rollbackEpoch = control.RollbackEpoch;
        ClearPostRollbackEvidenceLocked();
        break;
      case ToolkitRunControlAction.Abort
          when state.Stage is RollbackStage.Preparing
              or RollbackStage.ActivationPending
              or RollbackStage.Activated:
        if (state.NativeActivated)
        {
          state.Stage = RollbackStage.Failed;
          state.FailureTransitionPending = true;
          state.Failure = "Host aborted rollback after activation: "
              + control.Result;
        }
        else
        {
          _rollback = null;
        }
        break;
      case ToolkitRunControlAction.Commit
          when state.Stage == RollbackStage.Activated:
        state.Stage = RollbackStage.Committed;
        state.CommitPending = true;
        break;
      default:
        RejectPeerMessageLocked(peer);
        break;
    }
  }

  private static void HandleClientRunControlLocked(
      Peer peer,
      ToolkitRunControl control)
  {
    if (control.OriginOrdinal != 0
        || control.Action is not (
            ToolkitRunControlAction.Ready
                or ToolkitRunControlAction.Verify)
        || _rollback is not { IsHost: true } state
        || !SameRollbackTarget(state.Control, control))
    {
      RejectPeerMessageLocked(peer);
      return;
    }

    if (control.Action == ToolkitRunControlAction.Ready
        && state.Stage == RollbackStage.Preparing)
    {
      if (control.Result == ToolkitRunControlResult.Ready)
      {
        state.ReadyPeers.Add(peer.NetId);
        TryAdvanceRollbackPrepareLocked(state);
      }
      else
      {
        AbortRollbackLocked(
            state,
            control.Result,
            $"P{peer.Ordinal} rejected rollback Prepare: {control.Result}.");
        _rollback = null;
      }

      return;
    }

    if (control.Action == ToolkitRunControlAction.Verify
        && state.Stage == RollbackStage.Activated)
    {
      if (control.Result == ToolkitRunControlResult.Ready)
      {
        state.VerifiedPeers.Add(peer.NetId);
        TryCommitRollbackLocked(state);
      }
      else
      {
        FailActivatedRollbackLocked(
            state,
            $"P{peer.Ordinal} loaded checkpoint digest differs.",
            ToolkitRunControlResult.DigestMismatch);
      }

      return;
    }

    RejectPeerMessageLocked(peer);
  }

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
      HandSeenTicks[snapshot.OwnerOrdinal] = Stopwatch.GetTimestamp();
      SendHandLocked(
          payload,
          snapshot.OwnerOrdinal,
          relayFrom: peer.NetId);
    }
    else if (_service != null
             && _hostId == peer.NetId
             && snapshot.OwnerOrdinal != OrdinalOf(_service.NetId)
             && snapshot.OwnerOrdinal == _watchedHandOrdinal)
    {
      if (!Hands.TryGetValue(
              snapshot.OwnerOrdinal,
              out ToolkitHandSnapshot? current)
          || ToolkitSequence.IsNewer(
              snapshot.Revision,
              current.Revision))
      {
        Hands[snapshot.OwnerOrdinal] = snapshot;
        HandSeenTicks[snapshot.OwnerOrdinal] =
            Stopwatch.GetTimestamp();
      }
    }
    else if (_service?.Type != NetGameType.Client
             || _hostId != peer.NetId)
    {
      RejectPeerMessageLocked(peer);
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
            + (result.LastCommonCheckpointId?.ToString(
                CultureInfo.InvariantCulture) ?? "unknown")
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

  private static bool CanSendTextLocked()
  {
    if (_service is not
      {
        IsConnected: true,
        Type: NetGameType.Host or NetGameType.Client
      }
        || _sessionId == Guid.Empty
        || _sessionInvalid)
    {
      return false;
    }

    return _service.Type == NetGameType.Host
        ? Peers.Values.Any(peer =>
            peer.Acknowledged
            && !peer.Disabled
            && peer.Features.HasFlag(ToolkitFeature.FreeText))
        : _hostId.HasValue
            && Peers.TryGetValue(_hostId.Value, out Peer? host)
            && host.Acknowledged
            && !host.Disabled
            && host.Features.HasFlag(ToolkitFeature.FreeText);
  }

  private static void QueueOrSendTextLocked(Peer peer, byte[] payload)
  {
    long now = Stopwatch.GetTimestamp();
    if (peer.PendingTexts.Count == 0
        && SendLimiter.TryConsume(peer.Ordinal, now)
        && SendLocked(peer, ToolkitMessageType.Text, payload))
    {
      return;
    }

    if (peer.Disabled)
    {
      return;
    }

    while (peer.PendingTexts.Count >= 8)
    {
      peer.PendingTexts.Dequeue();
    }

    peer.PendingTexts.Enqueue(payload);
  }

  private static void FlushPendingTextsLocked(long now)
  {
    foreach (Peer peer in Peers.Values)
    {
      while (peer.PendingTexts.Count > 0
             && peer.Acknowledged
             && !peer.Disabled
             && peer.Features.HasFlag(ToolkitFeature.FreeText)
             && SendLimiter.TryConsume(peer.Ordinal, now))
      {
        if (!SendLocked(
                peer,
                ToolkitMessageType.Text,
                peer.PendingTexts.Peek()))
        {
          break;
        }

        peer.PendingTexts.Dequeue();
      }
    }
  }

  private static void RememberTextLocked(
      byte origin,
      string text,
      long now)
  {
    if (_textMuted)
    {
      return;
    }

    int bytes = Encoding.UTF8.GetByteCount(text);
    TextHistory.Enqueue(new(now, origin, text, bytes));
    _textHistoryBytes += bytes;
    while (TextHistory.Count > MaxTextHistory
           || _textHistoryBytes > MaxTextHistoryBytes)
    {
      _textHistoryBytes -= TextHistory.Dequeue().Utf8Bytes;
    }
  }

  private static void EnsureWatchedHandLocked()
  {
    if (!HandConsent.Active || _service == null)
    {
      return;
    }

    byte local = OrdinalOf(_service.NetId);
    byte first = Enumerable.Range(1, _roster.Length)
        .Select(value => (byte)value)
        .FirstOrDefault(value => value != local);
    if (first != 0)
    {
      SetWatchedHandLocked(first);
    }
  }

  private static void SetWatchedHandLocked(byte owner)
  {
    if (_service == null
        || !HandConsent.Active
        || owner is < 1 or > ToolkitLimits.MaxPlayers
        || owner > _roster.Length
        || owner == OrdinalOf(_service.NetId))
    {
      return;
    }

    _watchedHandOrdinal = owner;
    byte local = OrdinalOf(_service.NetId);
    foreach (byte existing in Hands.Keys
                 .Where(value => value != local && value != owner)
                 .ToArray())
    {
      Hands.Remove(existing);
      HandSeenTicks.Remove(existing);
    }

    if (_service.Type == NetGameType.Host)
    {
      return;
    }

    if (!_hostId.HasValue
        || !Peers.TryGetValue(_hostId.Value, out Peer? host)
        || !host.Acknowledged
        || host.Disabled
        || !host.Features.HasFlag(ToolkitFeature.HandWatch))
    {
      return;
    }

    byte[] payload = ToolkitHandWatchCodec.Encode(new(
        _membershipEpoch,
        owner));
    if (payload.Length != 0)
    {
      bool sent = SendLimiter.TryConsume(
          host.Ordinal,
          Stopwatch.GetTimestamp())
          && SendLocked(
              host,
              ToolkitMessageType.HandWatch,
              payload);
      host.PendingHandWatch = sent ? null : payload;
    }
  }

  private static void FlushPendingHandWatchLocked(long now)
  {
    if (_service?.Type != NetGameType.Client
        || !_hostId.HasValue
        || !Peers.TryGetValue(_hostId.Value, out Peer? host)
        || host.PendingHandWatch == null
        || !host.Acknowledged
        || host.Disabled
        || !SendLimiter.TryConsume(host.Ordinal, now)
        || !SendLocked(
            host,
            ToolkitMessageType.HandWatch,
            host.PendingHandWatch))
    {
      return;
    }

    host.PendingHandWatch = null;
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
                   && value.WatchedHandOrdinal == subjectOrdinal
                   && value.Features.HasFlag(
                       ToolkitFeature.HandSharing)
                   && value.Features.HasFlag(
                       ToolkitFeature.HandWatch)))
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

  private static void RelayRngSummaryLocked(
      byte[] payload,
      ulong? relayFrom)
  {
    if (_service?.Type != NetGameType.Host)
    {
      return;
    }

    foreach (Peer peer in Peers.Values.Where(value =>
                 value.NetId != relayFrom
                 && value.Acknowledged
                 && !value.Disabled
                 && value.Features.HasFlag(
                     ToolkitFeature.RngAnalysis)))
    {
      QueueOrSendRngSummaryLocked(peer, payload);
    }
  }

  private static void QueueOrSendRngSummaryLocked(
      Peer peer,
      byte[] payload)
  {
    if (SendLimiter.TryConsume(
            peer.Ordinal,
            Stopwatch.GetTimestamp())
        && SendLocked(
            peer,
            ToolkitMessageType.RngSummary,
            payload))
    {
      peer.PendingRngSummary = null;
      return;
    }

    if (!peer.Disabled)
    {
      peer.PendingRngSummary = payload;
    }
  }

  private static void FlushPendingRngSummariesLocked(long now)
  {
    foreach (Peer peer in Peers.Values)
    {
      if (peer.PendingRngSummary == null
          || !peer.Acknowledged
          || peer.Disabled
          || !peer.Features.HasFlag(ToolkitFeature.RngAnalysis)
          || !SendLimiter.TryConsume(peer.Ordinal, now)
          || !SendLocked(
              peer,
              ToolkitMessageType.RngSummary,
              peer.PendingRngSummary))
      {
        continue;
      }

      peer.PendingRngSummary = null;
    }
  }

  private static void PublishLimitBreakCapabilityLocked(long now)
  {
    if (_roster.Length <= 4
        || _service == null
        || _membershipEpoch == 0
        || (_lastLimitBreakSendTicks != 0
            && now - _lastLimitBreakSendTicks
                < 2L * Stopwatch.Frequency))
    {
      return;
    }

    byte ordinal = OrdinalOf(_service.NetId);
    if (ordinal == 0)
    {
      return;
    }

    LimitBreakAdapter.TryCapture(
        _service,
        ordinal,
        _membershipEpoch,
        out ToolkitLimitBreakCapability capability,
        out _);
    LimitBreakCapabilities[ordinal] = capability;
    ToolkitLimitBreakCapability outbound =
        _service.Type == NetGameType.Client
            ? capability with
            {
              OriginOrdinal = 0
            }
            : capability;
    if (!ToolkitLimitBreakCapabilityCodec.TryEncode(
            outbound,
            out byte[] payload))
    {
      return;
    }

    bool changed =
        !_lastLimitBreakPayload.AsSpan().SequenceEqual(payload);
    if (!changed
        && _lastLimitBreakSendTicks != 0
        && now - _lastLimitBreakSendTicks
            < 5L * Stopwatch.Frequency)
    {
      return;
    }

    _lastLimitBreakPayload = payload;
    _lastLimitBreakSendTicks = now;
    if (_service.Type == NetGameType.Host)
    {
      RelayLimitBreakCapabilityLocked(
          payload,
          relayFrom: null);
    }
    else if (_hostId.HasValue
             && Peers.TryGetValue(_hostId.Value, out Peer? host)
             && host.Acknowledged
             && !host.Disabled
             && host.Features.HasFlag(
                 ToolkitFeature.LimitBreakProbe))
    {
      QueueOrSendLimitBreakCapabilityLocked(host, payload);
    }
  }

  private static void RelayLimitBreakCapabilityLocked(
      byte[] payload,
      ulong? relayFrom)
  {
    if (_service?.Type != NetGameType.Host)
    {
      return;
    }

    foreach (Peer peer in Peers.Values.Where(value =>
                 value.NetId != relayFrom
                 && value.Acknowledged
                 && !value.Disabled
                 && value.Features.HasFlag(
                     ToolkitFeature.LimitBreakProbe)))
    {
      QueueOrSendLimitBreakCapabilityLocked(peer, payload);
    }
  }

  private static void QueueOrSendLimitBreakCapabilityLocked(
      Peer peer,
      byte[] payload)
  {
    if (SendLimiter.TryConsume(
            peer.Ordinal,
            Stopwatch.GetTimestamp())
        && SendLocked(
            peer,
            ToolkitMessageType.LimitBreakCapability,
            payload))
    {
      peer.PendingLimitBreakCapability = null;
      return;
    }

    if (!peer.Disabled)
    {
      peer.PendingLimitBreakCapability = payload;
    }
  }

  private static void FlushPendingLimitBreakCapabilitiesLocked(
      long now)
  {
    foreach (Peer peer in Peers.Values)
    {
      if (peer.PendingLimitBreakCapability == null
          || !peer.Acknowledged
          || peer.Disabled
          || !peer.Features.HasFlag(
              ToolkitFeature.LimitBreakProbe)
          || !SendLimiter.TryConsume(peer.Ordinal, now)
          || !SendLocked(
              peer,
              ToolkitMessageType.LimitBreakCapability,
              peer.PendingLimitBreakCapability))
      {
        continue;
      }

      peer.PendingLimitBreakCapability = null;
    }
  }

  private static void BroadcastRunControlLocked(byte[] payload)
  {
    if (_service?.Type != NetGameType.Host)
    {
      return;
    }

    foreach (Peer peer in Peers.Values.Where(value =>
                 value.Acknowledged
                 && !value.Disabled
                 && value.Features.HasFlag(
                     ToolkitFeature.RunControl)))
    {
      QueueOrSendRunControlLocked(peer, payload);
    }
  }

  private static void QueueOrSendRunControlLocked(
      Peer peer,
      byte[] payload)
  {
    long now = Stopwatch.GetTimestamp();
    if (peer.PendingRunControls.Count == 0
        && RunControlLimiter.TryConsume(peer.Ordinal, now)
        && SendLocked(
            peer,
            ToolkitMessageType.RunControl,
            payload))
    {
      return;
    }

    if (peer.Disabled)
    {
      return;
    }

    if (peer.PendingRunControls.Count >= 8)
    {
      peer.PendingRunControls.Dequeue();
    }

    peer.PendingRunControls.Enqueue(payload);
  }

  private static void FlushPendingRunControlsLocked(long now)
  {
    foreach (Peer peer in Peers.Values)
    {
      while (peer.PendingRunControls.Count > 0
             && peer.Acknowledged
             && !peer.Disabled
             && peer.Features.HasFlag(ToolkitFeature.RunControl)
             && RunControlLimiter.TryConsume(peer.Ordinal, now))
      {
        if (!SendLocked(
                peer,
                ToolkitMessageType.RunControl,
                peer.PendingRunControls.Peek()))
        {
          break;
        }

        peer.PendingRunControls.Dequeue();
      }
    }
  }

  private static void TickRollbackLocked(long now)
  {
    if (_rollback == null)
    {
      return;
    }

    if (_rollback.IsHost
        && _rollback.Stage == RollbackStage.Preparing)
    {
      TryAdvanceRollbackPrepareLocked(_rollback);
    }

    if (_rollback.Stage is RollbackStage.Committed
        or RollbackStage.Failed
        || now < _rollback.DeadlineTicks)
    {
      return;
    }

    if (_rollback.IsHost
        && _rollback.Stage is RollbackStage.Preparing
            or RollbackStage.ActivationPending)
    {
      RollbackState timedOut = _rollback;
      AbortRollbackLocked(
          timedOut,
          ToolkitRunControlResult.Timeout,
          "Rollback Prepare timed out before every peer acknowledged.");
      _rollback = null;
      return;
    }

    if (_rollback.IsHost
        && _rollback.Stage == RollbackStage.Activated)
    {
      FailActivatedRollbackLocked(
          _rollback,
          "Rollback loaded-lobby verification timed out.",
          ToolkitRunControlResult.Timeout);
      return;
    }

    if (!_rollback.NativeActivated)
    {
      _rollback = null;
      return;
    }

    _rollback.Stage = RollbackStage.Failed;
    _rollback.Failure =
        "Rollback host did not complete the current stage before timeout.";
  }

  private static void TryAdvanceRollbackPrepareLocked(
      RollbackState state)
  {
    if (!state.IsHost
        || state.Stage != RollbackStage.Preparing
        || _service?.Type != NetGameType.Host
        || !ToolkitRunControlRoster.AllResponded(
            state.ExpectedRoster,
            _roster,
            state.ReadyPeers))
    {
      return;
    }

    state.Stage = RollbackStage.ActivationPending;
    state.ActivationPending = true;
  }

  private static void TryCommitRollbackLocked(
      RollbackState state)
  {
    if (!state.IsHost
        || state.Stage != RollbackStage.Activated
        || _service?.Type != NetGameType.Host
        || !ToolkitRunControlRoster.AllResponded(
            state.ExpectedRoster,
            _roster,
            state.VerifiedPeers))
    {
      return;
    }

    state.Stage = RollbackStage.CommitPending;
    state.CommitPending = true;
  }

  private static void AbortRollbackLocked(
      RollbackState state,
      ToolkitRunControlResult result,
      string failure)
  {
    ToolkitRunControl abort = state.Control with
    {
      Action = ToolkitRunControlAction.Abort,
      OriginOrdinal = _service == null
          ? state.Control.OriginOrdinal
          : OrdinalOf(_service.NetId),
      Result = result,
      MembershipEpoch = _membershipEpoch
    };
    if (ToolkitRunControlCodec.TryEncode(
            abort,
            out byte[] payload))
    {
      BroadcastRunControlLocked(payload);
    }

    state.Stage = RollbackStage.Failed;
    state.Failure = failure;
  }

  private static void FailActivatedRollbackLocked(
      RollbackState state,
      string failure,
      ToolkitRunControlResult result =
          ToolkitRunControlResult.ActivationFailed)
  {
    AbortRollbackLocked(
        state,
        result,
        failure);
    state.FailureTransitionPending = true;
  }

  private static bool SameRollbackTarget(
      ToolkitRunControl left,
      ToolkitRunControl right) =>
      left.TransactionId == right.TransactionId
      && left.CheckpointId == right.CheckpointId
      && left.RollbackEpoch == right.RollbackEpoch
      && left.VisitIndex == right.VisitIndex
      && left.CheckpointDigest.AsSpan().SequenceEqual(
          right.CheckpointDigest);

  private static byte[] StableRosterDigestLocked()
  {
    byte[] canonical = new byte[_roster.Length * sizeof(ulong)];
    for (int index = 0; index < _roster.Length; index++)
    {
      BinaryPrimitives.WriteUInt64BigEndian(
          canonical.AsSpan(index * sizeof(ulong)),
          _roster[index]);
    }

    return SHA256.HashData(canonical)[..16];
  }

  private static void ClearPostRollbackEvidenceLocked() =>
      ClearCollaborativeStateLocked();

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
            + (localization.LastCommon?.ToString(
                CultureInfo.InvariantCulture) ?? "unknown")
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
    peer.PendingTexts.Clear();
    peer.PendingHandWatch = null;
    peer.PendingRngSummary = null;
    peer.PendingLimitBreakCapability = null;
    peer.PendingRunControls.Clear();
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
    TextSenderLimiter.Clear();
    TextGlobalLimiter.Clear();
    RunControlLimiter.Clear();
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
    _rollbackEpoch = 0;
    _rosterDigest = [];
    _environmentCode = null;
    _nextEnvironmentCodeRetryTicks = 0;
  }

  private static void ClearCollaborativeStateLocked()
  {
    HandConsent.Clear();
    ForensicsConsent.Clear();
    Hands.Clear();
    HandSeenTicks.Clear();
    TextHistory.Clear();
    RngSummaries.Clear();
    LimitBreakCapabilities.Clear();
    Checkpoints.Clear();
    DivergenceHistory.Clear();
    Contributions.Clear();
    OptionalDiagnosticsRegistry.SetEnabled(false);
    _contributionsEnabled = false;
    _contributionSharingEnabled = false;
    _handRevision = 0;
    _watchedHandOrdinal = 0;
    _lastHandPayload = [];
    _lastHandSendTicks = 0;
    _textHistoryBytes = 0;
    _textMuted = false;
    _lastLimitBreakPayload = [];
    _lastLimitBreakSendTicks = 0;
    _lastContributionPayload = [];
    _lastContributionSendTicks = 0;
    _forensicsStatus =
        "Forensics: disabled (unanimous consent required).";
    foreach (Peer peer in Peers.Values)
    {
      peer.PendingTexts.Clear();
      peer.PendingHandWatch = null;
      peer.PendingRngSummary = null;
      peer.PendingLimitBreakCapability = null;
      peer.WatchedHandOrdinal = 0;
    }
  }
}
