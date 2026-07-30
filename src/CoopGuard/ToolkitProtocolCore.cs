using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace CoopGuard;

[Flags]
public enum ToolkitFeature : ulong
{
    None = 0,
    HealthMatrix = 1UL << 0,
    ChoiceProgress = 1UL << 1,
    EnvironmentCode = 1UL << 2,
    QuickStatus = 1UL << 3,
    MapProgress = 1UL << 4,
    HandSharing = 1UL << 5,
    Contributions = 1UL << 6,
    Checkpoints = 1UL << 7,
    Heartbeat = 1UL << 8,
    HostDiagnostics = 1UL << 9
}

public enum ToolkitMessageType : byte
{
    Hello = 1,
    HelloAck = 2,
    State = 3,
    QuickStatus = 4,
    Consent = 5,
    HandSnapshot = 6,
    Checkpoint = 7,
    CheckpointResult = 8,
    Contribution = 9
}

public sealed record ToolkitEnvelope(
    byte Major,
    byte Minor,
    ToolkitMessageType Type,
    byte Flags,
    Guid SessionId,
    uint Sequence,
    byte[] Payload);

public static class ToolkitEnvelopeCodec
{
    public const byte Major = 1;
    public const byte Minor = 0;
    public const int FixedBytes = 30;
    public const int MaxPayloadBytes = 4096;
    public const int MaxEncodedBytes = FixedBytes + MaxPayloadBytes;
    private const uint Magic = 0x31444743;

    public static bool TryEncode(
        ToolkitEnvelope envelope,
        out byte[] encoded)
    {
        encoded = [];
        if (envelope.Major == 0
            || envelope.Type == 0
            || envelope.SessionId == Guid.Empty
            || envelope.Sequence == 0
            || envelope.Payload.Length > MaxPayloadBytes)
        {
            return false;
        }

        encoded = new byte[FixedBytes + envelope.Payload.Length];
        Span<byte> destination = encoded;
        BinaryPrimitives.WriteUInt32LittleEndian(destination, Magic);
        destination[4] = envelope.Major;
        destination[5] = envelope.Minor;
        destination[6] = (byte)envelope.Type;
        destination[7] = envelope.Flags;
        envelope.SessionId.TryWriteBytes(destination[8..24]);
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[24..28],
            envelope.Sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination[28..30],
            (ushort)envelope.Payload.Length);
        envelope.Payload.CopyTo(destination[FixedBytes..]);
        return true;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> encoded,
        out ToolkitEnvelope envelope,
        out string error)
    {
        envelope = null!;
        error = string.Empty;
        if (encoded.Length < FixedBytes
            || encoded.Length > MaxEncodedBytes)
        {
            error = "Envelope length is outside the fixed bound.";
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(encoded) != Magic)
        {
            error = "Envelope magic is invalid.";
            return false;
        }

        ushort payloadLength =
            BinaryPrimitives.ReadUInt16LittleEndian(encoded[28..30]);
        if (payloadLength > MaxPayloadBytes
            || encoded.Length != FixedBytes + payloadLength)
        {
            error = "Envelope payload length is invalid.";
            return false;
        }

        byte major = encoded[4];
        ToolkitMessageType type = (ToolkitMessageType)encoded[6];
        Guid sessionId = new(encoded[8..24]);
        uint sequence =
            BinaryPrimitives.ReadUInt32LittleEndian(encoded[24..28]);
        if (major == 0
            || type == 0
            || sessionId == Guid.Empty
            || sequence == 0)
        {
            error = "Envelope contains an invalid fixed field.";
            return false;
        }

        envelope = new ToolkitEnvelope(
            major,
            encoded[5],
            type,
            encoded[7],
            sessionId,
            sequence,
            encoded[FixedBytes..].ToArray());
        return true;
    }
}

public static class ToolkitHelloCodec
{
    public const int PayloadBytes = 10;

    public static byte[] Encode(ToolkitFeature features, byte locale)
    {
        byte[] payload = new byte[PayloadBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload,
            (ulong)features);
        payload[8] = locale;
        payload[9] = 0;
        return payload;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> payload,
        out ToolkitFeature features,
        out byte locale)
    {
        features = ToolkitFeature.None;
        locale = 0;
        if (payload.Length != PayloadBytes || payload[9] != 0)
        {
            return false;
        }

        ulong bits = BinaryPrimitives.ReadUInt64LittleEndian(payload);
        ulong known = (ulong)(ToolkitFeature.HealthMatrix
            | ToolkitFeature.ChoiceProgress
            | ToolkitFeature.EnvironmentCode
            | ToolkitFeature.QuickStatus
            | ToolkitFeature.MapProgress
            | ToolkitFeature.HandSharing
            | ToolkitFeature.Contributions
            | ToolkitFeature.Checkpoints
            | ToolkitFeature.Heartbeat
            | ToolkitFeature.HostDiagnostics);
        features = (ToolkitFeature)(bits & known);
        locale = payload[8];
        return locale <= 2;
    }
}

[Flags]
public enum ToolkitStateFlags : ushort
{
    None = 0,
    ToolkitAvailable = 1 << 0,
    GuardHealthy = 1 << 1,
    Ready = 1 << 2,
    Choosing = 1 << 3,
    MapSubmitted = 1 << 4,
    Loading = 1 << 5,
    Connected = 1 << 6,
    PeerDeclared = 1 << 7,
    ReadyKnown = 1 << 8,
    ChoosingKnown = 1 << 9,
    MapKnown = 1 << 10
}

public readonly record struct ToolkitStateRow(
    byte Ordinal,
    ToolkitStateFlags Flags,
    byte WaitReason,
    byte WaitConfidence,
    ushort ProgressAgeSeconds,
    ushort PingMsec,
    ushort LossPermille);

public static class ToolkitStateCodec
{
    public const int MaxRows = 16;
    public const int RowBytes = 12;

    public static bool TryEncode(
        IReadOnlyList<ToolkitStateRow> rows,
        out byte[] payload)
    {
        payload = [];
        if (rows.Count is < 1 or > MaxRows)
        {
            return false;
        }

        payload = new byte[1 + rows.Count * RowBytes];
        payload[0] = (byte)rows.Count;
        for (int index = 0; index < rows.Count; index++)
        {
            ToolkitStateRow row = rows[index];
            if (!Valid(row))
            {
                payload = [];
                return false;
            }

            Span<byte> destination =
                payload.AsSpan(1 + index * RowBytes, RowBytes);
            destination[0] = row.Ordinal;
            BinaryPrimitives.WriteUInt16LittleEndian(
                destination[1..3],
                (ushort)row.Flags);
            destination[3] = row.WaitReason;
            destination[4] = row.WaitConfidence;
            BinaryPrimitives.WriteUInt16LittleEndian(
                destination[5..7],
                row.ProgressAgeSeconds);
            BinaryPrimitives.WriteUInt16LittleEndian(
                destination[7..9],
                row.PingMsec);
            BinaryPrimitives.WriteUInt16LittleEndian(
                destination[9..11],
                row.LossPermille);
            destination[11] = 0;
        }

        return true;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> payload,
        out ToolkitStateRow[] rows)
    {
        rows = [];
        if (payload.Length < 1)
        {
            return false;
        }

        int count = payload[0];
        if (count is < 1 or > MaxRows
            || payload.Length != 1 + count * RowBytes)
        {
            return false;
        }

        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> source =
                payload.Slice(1 + index * RowBytes, RowBytes);
            ToolkitStateRow row = new(
                source[0],
                (ToolkitStateFlags)
                    BinaryPrimitives.ReadUInt16LittleEndian(source[1..3]),
                source[3],
                source[4],
                BinaryPrimitives.ReadUInt16LittleEndian(source[5..7]),
                BinaryPrimitives.ReadUInt16LittleEndian(source[7..9]),
                BinaryPrimitives.ReadUInt16LittleEndian(source[9..11]));
            if (source[11] != 0 || !Valid(row))
            {
                return false;
            }
        }

        rows = new ToolkitStateRow[count];
        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> source =
                payload.Slice(1 + index * RowBytes, RowBytes);
            rows[index] = new ToolkitStateRow(
                source[0],
                (ToolkitStateFlags)
                    BinaryPrimitives.ReadUInt16LittleEndian(source[1..3]),
                source[3],
                source[4],
                BinaryPrimitives.ReadUInt16LittleEndian(source[5..7]),
                BinaryPrimitives.ReadUInt16LittleEndian(source[7..9]),
                BinaryPrimitives.ReadUInt16LittleEndian(source[9..11]));
        }

        return true;
    }

    private static bool Valid(ToolkitStateRow row)
    {
        const ToolkitStateFlags known = ToolkitStateFlags.ToolkitAvailable
            | ToolkitStateFlags.GuardHealthy
            | ToolkitStateFlags.Ready
            | ToolkitStateFlags.Choosing
            | ToolkitStateFlags.MapSubmitted
            | ToolkitStateFlags.Loading
            | ToolkitStateFlags.Connected
            | ToolkitStateFlags.PeerDeclared
            | ToolkitStateFlags.ReadyKnown
            | ToolkitStateFlags.ChoosingKnown
            | ToolkitStateFlags.MapKnown;
        return row.Ordinal <= MaxRows
            && (row.Flags & ~known) == 0
            && row.WaitReason <= 7
            && row.WaitConfidence <= 3
            && (row.PingMsec == ushort.MaxValue
                || row.PingMsec <= 60_000)
            && (row.LossPermille == ushort.MaxValue
                || row.LossPermille <= 1000);
    }
}

public enum ToolkitQuickStatus : byte
{
    PleaseWait = 1,
    ReadyToStart = 2,
    Choosing = 3,
    NeedReconnect = 4,
    ViewingMap = 5
}

public static class ToolkitQuickStatusCodec
{
    public static bool TryEncode(
        byte originOrdinal,
        ToolkitQuickStatus status,
        out byte[] payload)
    {
        payload = [];
        if (originOrdinal > ToolkitStateCodec.MaxRows
            || !Enum.IsDefined(status))
        {
            return false;
        }

        payload = [originOrdinal, (byte)status];
        return true;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> payload,
        out byte originOrdinal,
        out ToolkitQuickStatus status)
    {
        originOrdinal = 0;
        status = 0;
        if (payload.Length != 2
            || payload[0] > ToolkitStateCodec.MaxRows
            || !Enum.IsDefined((ToolkitQuickStatus)payload[1]))
        {
            return false;
        }

        originOrdinal = payload[0];
        status = (ToolkitQuickStatus)payload[1];
        return true;
    }
}

public static class ToolkitEnvironmentCode
{
    private const string Base32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static bool TryCreate(
        Guid sessionId,
        string gameVersion,
        int guardProtocol,
        string packageDigest,
        out string code)
    {
        code = string.Empty;
        if (sessionId == Guid.Empty
            || guardProtocol <= 0
            || packageDigest.Length != 64)
        {
            return false;
        }

        byte[] digest;
        try
        {
            digest = Convert.FromHexString(packageDigest);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] session = sessionId.ToByteArray();
        byte[] context = Encoding.UTF8.GetBytes(
            gameVersion + "\n" + guardProtocol.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        byte[] material =
            new byte[session.Length + digest.Length + context.Length];
        session.CopyTo(material, 0);
        digest.CopyTo(material, session.Length);
        context.CopyTo(material, session.Length + digest.Length);
        byte[] hash = SHA256.HashData(material);
        Span<char> result = stackalloc char[10];
        ulong bits = BinaryPrimitives.ReadUInt64BigEndian(hash);
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = Base32[(int)((bits >> (59 - index * 5)) & 31)];
        }

        code = new string(result);
        return true;
    }
}

public sealed class ToolkitSendLimiter
{
    public const int MaxPeers = 16;
    public const int Burst = 4;
    private sealed record PeerBudget(double Tokens, long LastTicks);
    private readonly Dictionary<int, PeerBudget> _peers = [];

    public bool TryConsume(int peerOrdinal, long nowTicks)
    {
        if (peerOrdinal is < 1 or > MaxPeers || nowTicks < 0)
        {
            return false;
        }

        if (!_peers.TryGetValue(peerOrdinal, out PeerBudget? budget))
        {
            if (_peers.Count == MaxPeers)
            {
                return false;
            }

            budget = new PeerBudget(Burst, nowTicks);
        }

        long elapsed = Math.Max(0, nowTicks - budget.LastTicks);
        double tokens = Math.Min(
            Burst,
            budget.Tokens + elapsed / (double)Stopwatch.Frequency);
        if (tokens < 1)
        {
            _peers[peerOrdinal] = budget with
            {
                Tokens = tokens,
                LastTicks = nowTicks
            };
            return false;
        }

        _peers[peerOrdinal] = new PeerBudget(tokens - 1, nowTicks);
        return true;
    }

    public void Clear() => _peers.Clear();
}

public sealed class ToolkitQuickStatusLimiter
{
    private readonly Queue<long> _sent = new();
    private long _cooldownUntil;

    public bool TryTake(long nowTicks)
    {
        while (_sent.Count > 0
            && nowTicks - _sent.Peek() >= 3L * Stopwatch.Frequency)
        {
            _sent.Dequeue();
        }

        if (nowTicks < _cooldownUntil || _sent.Count >= 3)
        {
            return false;
        }

        _sent.Enqueue(nowTicks);
        if (_sent.Count == 3)
        {
            _cooldownUntil = nowTicks + 3L * Stopwatch.Frequency;
        }

        return true;
    }

    public void Clear()
    {
        _sent.Clear();
        _cooldownUntil = 0;
    }
}

public static class ToolkitSequence
{
    public static bool IsNewer(uint candidate, uint current) =>
        candidate != 0
        && candidate != current
        && (current == 0 || unchecked(candidate - current) < 0x80000000U);
}
