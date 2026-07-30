using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CoopGuard;

public enum ToolkitConsentFeature : byte
{
    HandSharing = 1,
    Forensics = 2
}

public enum ToolkitConsentAction : byte
{
    OptIn = 1,
    Revoke = 2,
    Commit = 3,
    Acknowledge = 4,
    Active = 5
}

public readonly record struct ToolkitConsent(
    ToolkitConsentFeature Feature,
    ToolkitConsentAction Action,
    uint MembershipEpoch,
    byte[] RosterDigest,
    byte[] CommitToken);

public static class ToolkitConsentCodec
{
    public const int PayloadBytes = 38;

    public static bool TryEncode(
        ToolkitConsent value,
        out byte[] payload)
    {
        payload = [];
        if (!Valid(value))
        {
            return false;
        }

        payload = new byte[PayloadBytes];
        payload[0] = (byte)value.Feature;
        payload[1] = (byte)value.Action;
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(2),
            value.MembershipEpoch);
        value.RosterDigest.CopyTo(payload, 6);
        value.CommitToken.CopyTo(payload, 22);
        return true;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> payload,
        out ToolkitConsent value)
    {
        value = default;
        if (payload.Length != PayloadBytes)
        {
            return false;
        }

        value = new(
            (ToolkitConsentFeature)payload[0],
            (ToolkitConsentAction)payload[1],
            BinaryPrimitives.ReadUInt32LittleEndian(payload[2..]),
            payload.Slice(6, 16).ToArray(),
            payload.Slice(22, 16).ToArray());
        return Valid(value);
    }

    public static byte[] RosterDigest(
        Guid sessionId,
        uint epoch,
        IEnumerable<ulong> roster)
    {
        ulong[] ids = roster.Distinct().Order().Take(17).ToArray();
        if (sessionId == Guid.Empty
            || epoch == 0
            || ids.Length is < 1 or > 16)
        {
            return [];
        }

        byte[] material = new byte[20 + ids.Length * 8];
        sessionId.TryWriteBytes(material);
        BinaryPrimitives.WriteUInt32LittleEndian(
            material.AsSpan(16),
            epoch);
        for (int index = 0; index < ids.Length; index++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                material.AsSpan(20 + index * 8),
                ids[index]);
        }

        return SHA256.HashData(material)[..16];
    }

    public static byte[] CommitToken(
        Guid sessionId,
        ToolkitConsentFeature feature,
        uint epoch,
        ReadOnlySpan<byte> rosterDigest)
    {
        if (sessionId == Guid.Empty
            || !Enum.IsDefined(feature)
            || epoch == 0
            || rosterDigest.Length != 16)
        {
            return [];
        }

        byte[] material = new byte[46];
        "CGCONSENT"u8.CopyTo(material);
        sessionId.TryWriteBytes(material.AsSpan(9));
        material[25] = (byte)feature;
        BinaryPrimitives.WriteUInt32LittleEndian(
            material.AsSpan(26),
            epoch);
        rosterDigest.CopyTo(material.AsSpan(30));
        return SHA256.HashData(material)[..16];
    }

    private static bool Valid(ToolkitConsent value)
    {
        if (!Enum.IsDefined(value.Feature)
            || !Enum.IsDefined(value.Action)
            || value.MembershipEpoch == 0
            || value.RosterDigest.Length != 16
            || value.CommitToken.Length != 16
            || value.RosterDigest.All(item => item == 0))
        {
            return false;
        }

        bool tokenRequired = value.Action is ToolkitConsentAction.Commit
            or ToolkitConsentAction.Acknowledge
            or ToolkitConsentAction.Active;
        return tokenRequired
            ? value.CommitToken.Any(item => item != 0)
            : value.CommitToken.All(item => item == 0);
    }
}

public readonly record struct ToolkitHandCard(
    string ModelId,
    byte Upgrade,
    short PublicCost);

public sealed record ToolkitHandSnapshot(
    uint MembershipEpoch,
    uint Revision,
    byte OwnerOrdinal,
    ToolkitHandCard[] Cards);

public static class ToolkitHandSnapshotCodec
{
    public const int MaxCards = 64;
    public const int MaxModelBytes = 48;
    public const int MaxPayloadBytes = 4096;
    private const int HeaderBytes = 10;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool TryEncode(
        ToolkitHandSnapshot value,
        out byte[] payload)
    {
        payload = [];
        if (value.MembershipEpoch == 0
            || value.Revision == 0
            || value.OwnerOrdinal is < 1 or > 16
            || value.Cards.Length > MaxCards)
        {
            return false;
        }

        List<byte[]> ids = new(value.Cards.Length);
        int bytes = HeaderBytes;
        try
        {
            foreach (ToolkitHandCard card in value.Cards)
            {
                if (string.IsNullOrEmpty(card.ModelId)
                    || card.ModelId.Any(char.IsControl))
                {
                    return false;
                }

                byte[] id = StrictUtf8.GetBytes(card.ModelId);
                if (id.Length is < 1 or > MaxModelBytes)
                {
                    return false;
                }

                bytes = checked(bytes + 1 + id.Length + 3);
                if (bytes > MaxPayloadBytes)
                {
                    return false;
                }

                ids.Add(id);
            }
        }
        catch (Exception ex) when (
            ex is EncoderFallbackException or OverflowException)
        {
            return false;
        }

        payload = new byte[bytes];
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload,
            value.MembershipEpoch);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(4),
            value.Revision);
        payload[8] = value.OwnerOrdinal;
        payload[9] = (byte)value.Cards.Length;
        int offset = HeaderBytes;
        for (int index = 0; index < value.Cards.Length; index++)
        {
            byte[] id = ids[index];
            payload[offset++] = (byte)id.Length;
            id.CopyTo(payload, offset);
            offset += id.Length;
            payload[offset++] = value.Cards[index].Upgrade;
            BinaryPrimitives.WriteInt16LittleEndian(
                payload.AsSpan(offset),
                value.Cards[index].PublicCost);
            offset += 2;
        }

        return true;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> payload,
        out ToolkitHandSnapshot snapshot)
    {
        snapshot = null!;
        if (payload.Length is < HeaderBytes or > MaxPayloadBytes)
        {
            return false;
        }

        uint epoch = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        uint revision =
            BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        byte owner = payload[8];
        int count = payload[9];
        if (epoch == 0
            || revision == 0
            || owner is < 1 or > 16
            || count > MaxCards)
        {
            return false;
        }

        int offset = HeaderBytes;
        for (int index = 0; index < count; index++)
        {
            if (offset >= payload.Length)
            {
                return false;
            }

            int length = payload[offset++];
            if (length is < 1 or > MaxModelBytes
                || offset + length + 3 > payload.Length
                || !ValidUtf8(payload.Slice(offset, length)))
            {
                return false;
            }

            offset += length + 3;
        }

        if (offset != payload.Length)
        {
            return false;
        }

        ToolkitHandCard[] cards = new ToolkitHandCard[count];
        offset = HeaderBytes;
        for (int index = 0; index < count; index++)
        {
            int length = payload[offset++];
            string id = StrictUtf8.GetString(
                payload.Slice(offset, length));
            offset += length;
            byte upgrade = payload[offset++];
            short cost =
                BinaryPrimitives.ReadInt16LittleEndian(payload[offset..]);
            offset += 2;
            cards[index] = new(id, upgrade, cost);
        }

        snapshot = new(epoch, revision, owner, cards);
        return true;
    }

    private static bool ValidUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            string value = StrictUtf8.GetString(bytes);
            return !string.IsNullOrEmpty(value)
                && !value.Any(char.IsControl);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}

public readonly record struct ToolkitContributionSnapshot(
    byte Ordinal,
    ulong Damage,
    ulong DefenseGiven,
    ulong HealingGiven,
    ulong Kills,
    ulong CompletedActions);

public static class ToolkitContributionCodec
{
    public const int PayloadBytes = 41;

    public static bool TryEncode(
        ToolkitContributionSnapshot value,
        out byte[] payload)
    {
        payload = [];
        if (value.Ordinal is < 1 or > 16)
        {
            return false;
        }

        payload = new byte[PayloadBytes];
        payload[0] = value.Ordinal;
        ulong[] values =
        [
            value.Damage,
            value.DefenseGiven,
            value.HealingGiven,
            value.Kills,
            value.CompletedActions
        ];
        for (int index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                payload.AsSpan(1 + index * 8),
                values[index]);
        }

        return true;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> payload,
        out ToolkitContributionSnapshot value)
    {
        value = default;
        if (payload.Length != PayloadBytes
            || payload[0] is < 1 or > 16)
        {
            return false;
        }

        value = new(
            payload[0],
            BinaryPrimitives.ReadUInt64LittleEndian(payload[1..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[9..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[17..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[25..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[33..]));
        return true;
    }
}

public sealed class ToolkitContributionLedger
{
    public const int MaxPeers = 16;
    public const int MaxRememberedEvents = 1024;
    private readonly Dictionary<byte, ToolkitContributionSnapshot> _rows = [];
    private readonly Queue<ulong> _eventOrder = [];
    private readonly HashSet<ulong> _events = [];

    public bool Add(
        byte ordinal,
        ulong eventId,
        ulong damage = 0,
        ulong defenseGiven = 0,
        ulong healingGiven = 0,
        ulong kills = 0,
        ulong completedActions = 0)
    {
        if (ordinal is < 1 or > MaxPeers
            || eventId == 0
            || !_events.Add(eventId))
        {
            return false;
        }

        while (_eventOrder.Count >= MaxRememberedEvents)
        {
            _events.Remove(_eventOrder.Dequeue());
        }

        _eventOrder.Enqueue(eventId);
        ToolkitContributionSnapshot row = _rows.GetValueOrDefault(
            ordinal,
            new ToolkitContributionSnapshot(ordinal, 0, 0, 0, 0, 0));
        try
        {
            _rows[ordinal] = row with
            {
                Damage = checked(row.Damage + damage),
                DefenseGiven = checked(
                    row.DefenseGiven + defenseGiven),
                HealingGiven = checked(
                    row.HealingGiven + healingGiven),
                Kills = checked(row.Kills + kills),
                CompletedActions = checked(
                    row.CompletedActions + completedActions)
            };
            return true;
        }
        catch (OverflowException)
        {
            _rows.Remove(ordinal);
            return false;
        }
    }

    public ToolkitContributionSnapshot[] Snapshot() =>
        _rows.Values.OrderBy(row => row.Ordinal).ToArray();

    public bool TryGet(
        byte ordinal,
        out ToolkitContributionSnapshot value) =>
        _rows.TryGetValue(ordinal, out value);

    public bool TryApplySnapshot(ToolkitContributionSnapshot value)
    {
        if (value.Ordinal is < 1 or > MaxPeers)
        {
            return false;
        }

        if (_rows.TryGetValue(
                value.Ordinal,
                out ToolkitContributionSnapshot current)
            && (value.Damage < current.Damage
                || value.DefenseGiven < current.DefenseGiven
                || value.HealingGiven < current.HealingGiven
                || value.Kills < current.Kills
                || value.CompletedActions < current.CompletedActions))
        {
            return false;
        }

        _rows[value.Ordinal] = value;
        return true;
    }

    public void KeepOnly(byte ordinal)
    {
        ToolkitContributionSnapshot? local =
            _rows.TryGetValue(ordinal, out ToolkitContributionSnapshot value)
                ? value
                : null;
        _rows.Clear();
        if (local.HasValue)
        {
            _rows[ordinal] = local.Value;
        }
    }

    public void Clear()
    {
        _rows.Clear();
        _events.Clear();
        _eventOrder.Clear();
    }
}
