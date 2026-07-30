using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CoopGuard;

public enum ObservationSource
{
    NativeAuthoritative,
    NativeObserved,
    NativeStatistic,
    PeerDeclared,
    LocalInference
}

public enum ObservationConfidence
{
    Unknown,
    Low,
    Medium,
    High
}

public readonly record struct Observation<T>(
    T Value,
    ObservationSource Source,
    ObservationConfidence Confidence,
    long CapturedAtTicks,
    long MaxAgeTicks,
    long Sequence)
{
    public static Observation<T> Capture(
        T value,
        ObservationSource source,
        ObservationConfidence confidence,
        TimeSpan maxAge,
        long sequence,
        long? capturedAtTicks = null)
    {
        if (maxAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAge));
        }

        double ticks = maxAge.TotalSeconds * Stopwatch.Frequency;
        if (ticks > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAge));
        }

        return new Observation<T>(
            value,
            source,
            confidence,
            capturedAtTicks ?? Stopwatch.GetTimestamp(),
            (long)ticks,
            sequence);
    }

    public bool IsFresh(long nowTicks)
    {
        long age = nowTicks - CapturedAtTicks;
        return age >= 0 && age <= MaxAgeTicks;
    }
}

public enum TimelineEventKind
{
    Lifecycle,
    Network,
    Progress,
    Warning,
    Error,
    PublicAction,
    Checkpoint
}

public sealed record TimelineEntry(
    long Sequence,
    long CapturedAtTicks,
    TimelineEventKind Kind,
    string Code,
    string Subject,
    string Message,
    int Count);

public sealed class FlightRecorder
{
    public const int Capacity = 1024;
    public const int MaxEntryUtf8Bytes = 512;
    private const int MaxCodeUtf8Bytes = 64;
    private const int MaxSubjectUtf8Bytes = 96;

    private readonly object _sync = new();
    private readonly TimelineEntry?[] _entries = new TimelineEntry[Capacity];
    private int _next;
    private int _count;
    private long _sequence;
    private long _dropped;

    public long DroppedCount
    {
        get
        {
            lock (_sync)
            {
                return _dropped;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    public TimelineEntry Record(
        TimelineEventKind kind,
        string code,
        string? subject,
        string? message,
        long? capturedAtTicks = null)
    {
        string safeCode = BoundedText(code, MaxCodeUtf8Bytes);
        string safeSubject = BoundedText(subject, MaxSubjectUtf8Bytes);
        int remaining = MaxEntryUtf8Bytes
            - Encoding.UTF8.GetByteCount(safeCode)
            - Encoding.UTF8.GetByteCount(safeSubject);
        string safeMessage = BoundedText(message, Math.Max(0, remaining));
        long now = capturedAtTicks ?? Stopwatch.GetTimestamp();

        lock (_sync)
        {
            PruneExpired(now);
            if (_count > 0)
            {
                int latestIndex = (_next - 1 + Capacity) % Capacity;
                TimelineEntry? latest = _entries[latestIndex];
                if (latest != null
                    && now - latest.CapturedAtTicks <= Stopwatch.Frequency
                    && latest.Kind == kind
                    && latest.Code == safeCode
                    && latest.Subject == safeSubject
                    && latest.Message == safeMessage)
                {
                    TimelineEntry merged = latest with
                    {
                        Count = latest.Count + 1
                    };
                    _entries[latestIndex] = merged;
                    return merged;
                }
            }

            TimelineEntry entry = new(
                ++_sequence,
                now,
                kind,
                safeCode,
                safeSubject,
                safeMessage,
                1);
            if (_count == Capacity)
            {
                _dropped++;
            }

            _entries[_next] = entry;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity)
            {
                _count++;
            }

            return entry;
        }
    }

    public IReadOnlyList<TimelineEntry> Snapshot(int limit = 100)
    {
        if (limit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        lock (_sync)
        {
            PruneExpired(Stopwatch.GetTimestamp());
            int take = Math.Min(limit, _count);
            List<TimelineEntry> result = new(take);
            int start = (_next - take + Capacity) % Capacity;
            for (int index = 0; index < take; index++)
            {
                TimelineEntry? entry = _entries[(start + index) % Capacity];
                if (entry != null)
                {
                    result.Add(entry);
                }
            }

            return result;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            Array.Clear(_entries);
            _next = 0;
            _count = 0;
            _dropped = 0;
        }
    }

    public static string BoundedText(string? value, int maxUtf8Bytes)
    {
        if (string.IsNullOrEmpty(value) || maxUtf8Bytes <= 0)
        {
            return string.Empty;
        }

        StringBuilder result = new(Math.Min(value.Length, maxUtf8Bytes));
        int used = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            Rune safe = Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control
                ? new Rune(' ')
                : rune;
            int bytes = safe.Utf8SequenceLength;
            if (bytes > maxUtf8Bytes - used)
            {
                break;
            }

            result.Append(safe);
            used += bytes;
        }

        return result.ToString();
    }

    private void PruneExpired(long nowTicks)
    {
        long retention = 15L * 60 * Stopwatch.Frequency;
        while (_count > 0)
        {
            int oldest = (_next - _count + Capacity) % Capacity;
            TimelineEntry? entry = _entries[oldest];
            if (entry != null
                && nowTicks - entry.CapturedAtTicks <= retention)
            {
                break;
            }

            _entries[oldest] = null;
            _count--;
            _dropped++;
        }
    }
}

public readonly record struct NetworkSample(
    long CapturedAtTicks,
    float? PingMsec,
    float? PacketLoss,
    double? HeartbeatAgeSeconds,
    bool RemoteIsLoading,
    bool Connected);

public sealed class NetworkHistory
{
    public const int Capacity = 60;
    private readonly NetworkSample[] _samples = new NetworkSample[Capacity];
    private int _next;
    private int _count;

    public int Count => _count;

    public void Add(NetworkSample sample)
    {
        _samples[_next] = sample;
        _next = (_next + 1) % Capacity;
        if (_count < Capacity)
        {
            _count++;
        }
    }

    public IReadOnlyList<NetworkSample> Snapshot()
    {
        List<NetworkSample> result = new(_count);
        int start = (_next - _count + Capacity) % Capacity;
        for (int index = 0; index < _count; index++)
        {
            result.Add(_samples[(start + index) % Capacity]);
        }

        return result;
    }
}

public enum WaitReasonCode
{
    None,
    NetworkDisconnected,
    RemoteLoading,
    PlayerTurn,
    PlayerChoice,
    ActionExecuting,
    PhaseSync,
    Unknown
}

public enum StallLevel
{
    None,
    Notice,
    Suspected,
    HighlySuspected
}

public readonly record struct WaitAssessment(
    WaitReasonCode Reason,
    ObservationConfidence Confidence,
    long DurationTicks,
    StallLevel Stall,
    string Evidence);

public sealed class ProgressLease
{
    private string _progressToken = string.Empty;
    private WaitReasonCode _reason;
    private long _reasonSince;
    private long _lastProgress;

    public WaitAssessment Observe(
        string progressToken,
        WaitReasonCode reason,
        ObservationConfidence confidence,
        string evidence,
        bool canFlagStall,
        long nowTicks)
    {
        string safeToken = FlightRecorder.BoundedText(progressToken, 256);
        if (!string.Equals(_progressToken, safeToken, StringComparison.Ordinal))
        {
            _progressToken = safeToken;
            _lastProgress = nowTicks;
        }

        if (_reason != reason)
        {
            _reason = reason;
            _reasonSince = nowTicks;
        }

        long duration = Math.Max(0, nowTicks - _reasonSince);
        if (duration < Stopwatch.Frequency / 2)
        {
            duration = 0;
        }

        long stalled = Math.Max(0, nowTicks - _lastProgress);
        StallLevel level = !canFlagStall
            ? StallLevel.None
            : stalled >= 90L * Stopwatch.Frequency
                ? StallLevel.HighlySuspected
                : stalled >= 60L * Stopwatch.Frequency
                    ? StallLevel.Suspected
                    : stalled >= 30L * Stopwatch.Frequency
                        ? StallLevel.Notice
                        : StallLevel.None;
        return new WaitAssessment(
            reason,
            confidence,
            duration,
            level,
            FlightRecorder.BoundedText(evidence, 256));
    }

    public void Reset(long nowTicks)
    {
        _progressToken = string.Empty;
        _reason = WaitReasonCode.None;
        _reasonSince = nowTicks;
        _lastProgress = nowTicks;
    }
}

public enum AlertSeverity
{
    Info,
    Warning,
    Fatal
}

public sealed record ToolkitAlert(
    long Sequence,
    long FirstSeenTicks,
    long LastSeenTicks,
    AlertSeverity Severity,
    string Code,
    string Subject,
    string Message,
    int Count,
    bool Acknowledged);

public sealed class AlertCenter
{
    public const int Capacity = 50;
    private readonly List<ToolkitAlert> _alerts = new(Capacity);
    private long _sequence;

    public IReadOnlyList<ToolkitAlert> Snapshot() => _alerts.ToArray();

    public void Add(
        AlertSeverity severity,
        string code,
        string subject,
        string message,
        long nowTicks)
    {
        string safeCode = FlightRecorder.BoundedText(code, 64);
        string safeSubject = FlightRecorder.BoundedText(subject, 96);
        string safeMessage = FlightRecorder.BoundedText(message, 352);
        int existing = _alerts.FindLastIndex(alert =>
            alert.Code == safeCode
            && alert.Subject == safeSubject
            && nowTicks - alert.LastSeenTicks <= 5L * Stopwatch.Frequency);
        if (existing >= 0)
        {
            ToolkitAlert current = _alerts[existing];
            _alerts[existing] = current with
            {
                LastSeenTicks = nowTicks,
                Severity = severity > current.Severity
                    ? severity
                    : current.Severity,
                Count = current.Count + 1,
                Acknowledged = false
            };
            return;
        }

        if (_alerts.Count == Capacity)
        {
            int discard = _alerts.FindIndex(alert =>
                alert.Severity == AlertSeverity.Info
                && alert.Acknowledged);
            if (discard < 0)
            {
                discard = _alerts.FindIndex(alert =>
                    alert.Severity != AlertSeverity.Fatal);
            }

            if (discard < 0)
            {
                if (severity != AlertSeverity.Fatal)
                {
                    return;
                }

                discard = 0;
            }

            _alerts.RemoveAt(discard);
        }

        _alerts.Add(new ToolkitAlert(
            ++_sequence,
            nowTicks,
            nowTicks,
            severity,
            safeCode,
            safeSubject,
            safeMessage,
            1,
            false));
    }

    public void Acknowledge(long sequence)
    {
        int index = _alerts.FindIndex(alert => alert.Sequence == sequence);
        if (index >= 0)
        {
            _alerts[index] = _alerts[index] with { Acknowledged = true };
        }
    }

    public void AcknowledgeAll()
    {
        for (int index = 0; index < _alerts.Count; index++)
        {
            _alerts[index] = _alerts[index] with { Acknowledged = true };
        }
    }

    public void Clear() => _alerts.Clear();
}

public sealed record PeerIdentity(
    int Ordinal,
    string Label,
    string Shape,
    string Color);

public sealed class PeerIdentityMap
{
    private static readonly string[] Shapes =
        ["●", "■", "▲", "◆", "★", "⬟", "✚", "✦"];
    private static readonly string[] Colors =
        ["#0072B2", "#D55E00", "#009E73", "#CC79A7",
         "#E69F00", "#56B4E9", "#F0E442", "#999999"];
    private readonly Dictionary<ulong, PeerIdentity> _identities = [];

    public PeerIdentity GetOrAdd(ulong peerKey)
    {
        if (_identities.TryGetValue(peerKey, out PeerIdentity? identity))
        {
            return identity;
        }

        int ordinal = _identities.Count + 1;
        int palette = (ordinal - 1) % Shapes.Length;
        identity = new PeerIdentity(
            ordinal,
            "P" + ordinal.ToString(CultureInfo.InvariantCulture),
            Shapes[palette],
            Colors[palette]);
        _identities.Add(peerKey, identity);
        return identity;
    }

    public void Clear() => _identities.Clear();
}

public sealed class OptionalModuleFuse
{
    private int _disabled;

    public bool IsDisabled => Volatile.Read(ref _disabled) != 0;

    public string? Failure { get; private set; }

    public bool TryRun(Action action, Action<Exception>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsDisabled)
        {
            return false;
        }

        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            if (Interlocked.CompareExchange(ref _disabled, 1, 0) == 0)
            {
                Failure = FlightRecorder.BoundedText(
                    ex.GetType().Name + ": " + ex.Message,
                    256);
                try
                {
                    onFailure?.Invoke(ex);
                }
                catch
                {
                    // An optional failure callback must never escape to the game.
                }
            }

            return false;
        }
    }

    public void Reset()
    {
        Failure = null;
        Volatile.Write(ref _disabled, 0);
    }
}

public sealed class CueRateLimiter
{
    private readonly Dictionary<int, long> _lastByCue = [];
    private readonly Queue<long> _recent = new();

    public bool TryTake(int cue, long nowTicks)
    {
        while (_recent.Count > 0
            && nowTicks - _recent.Peek() >= 10L * Stopwatch.Frequency)
        {
            _recent.Dequeue();
        }

        if (_recent.Count >= 3
            || (_lastByCue.TryGetValue(cue, out long previous)
                && nowTicks - previous < 2L * Stopwatch.Frequency))
        {
            return false;
        }

        _lastByCue[cue] = nowTicks;
        _recent.Enqueue(nowTicks);
        return true;
    }

    public void Clear()
    {
        _lastByCue.Clear();
        _recent.Clear();
    }
}

public sealed class ToolkitSession
{
    public Guid Id { get; private set; }

    public ulong MembershipEpoch { get; private set; }

    public bool IsActive => Id != Guid.Empty;

    public void Begin()
    {
        Id = Guid.NewGuid();
        MembershipEpoch = 1;
    }

    public void MembershipChanged()
    {
        if (!IsActive)
        {
            return;
        }

        MembershipEpoch++;
    }

    public void End()
    {
        Id = Guid.Empty;
        MembershipEpoch = 0;
    }
}
