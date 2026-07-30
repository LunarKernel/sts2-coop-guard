using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BetterCoop;

public enum F1Category : byte
{
    RunPublic = 1,
    CombatPublic = 2,
    PlayersPublic = 3,
    MonstersPublic = 4,
    PublicCardCounts = 5,
    PublicEffects = 6,
    RngConsumptionCounts = 7,
    ModContributions = 8
}

public readonly record struct F1Run(
    ushort Mask,
    int Act,
    int Floor,
    ushort RoomKind);

public readonly record struct F1Combat(
    ushort Mask,
    int Round,
    ushort Phase,
    bool ActionRunning);

public readonly record struct F1Player(
    ushort Mask,
    byte Ordinal,
    int Hp,
    int MaxHp,
    int Block,
    int Energy,
    bool Ready,
    ushort HandCount);

public readonly record struct F1Monster(
    ushort Mask,
    ulong EntityId,
    string ModelId,
    int Hp,
    int MaxHp,
    int Block);

public readonly record struct F1CardCounts(
    ushort Mask,
    byte Ordinal,
    ushort Hand,
    ushort Draw,
    ushort Discard,
    ushort Exhaust);

public readonly record struct F1Effect(
    ushort Mask,
    byte OwnerKind,
    ulong OwnerId,
    byte EffectKind,
    string ModelId,
    int Stack,
    ushort Multiplicity);

public readonly record struct F1RngCount(
    ushort Mask,
    string StreamId,
    ulong Consumed);

public readonly record struct F1ModContribution(
    ushort Mask,
    string ModId,
    ushort ProviderSchema,
    ulong SourceRevision,
    byte[] Digest);

public sealed record F1CategoryDigest(
    F1Category Category,
    bool Available,
    byte[] Digest);

public sealed record F1Checkpoint(
    ushort Schema,
    ulong CheckpointId,
    byte[] ContextTag,
    byte AvailabilityMask,
    byte[][] Tags);

public sealed record F2Result(
    ulong? LastCommon,
    ulong? FirstObservedDivergent,
    F1Category? LowestDifferingCategory,
    string Status);

public static class F1SchemaV1
{
    public const ushort Schema = 1;
    public const int MaxBlobBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 =
        new(false, true);

    public static F1CategoryDigest Unsupported(F1Category category) =>
        new(category, false, []);

    public static F1CategoryDigest Run(F1Run? value) =>
        !value.HasValue
            ? Unsupported(F1Category.RunPublic)
            : Encode(F1Category.RunPublic, 1, writer =>
        {
            F1Run item = value.GetValueOrDefault();
            if ((item.Mask & ~0x7) != 0)
            {
                return false;
            }

            writer.Write(item.Mask);
            WriteIf(writer, item.Mask, 0, item.Act);
            WriteIf(writer, item.Mask, 1, item.Floor);
            WriteIf(writer, item.Mask, 2, RoomKind(item.RoomKind));
            return true;
        });

    public static F1CategoryDigest Combat(F1Combat? value) =>
        !value.HasValue
            ? Unsupported(F1Category.CombatPublic)
            : Encode(F1Category.CombatPublic, 1, writer =>
        {
            F1Combat item = value.GetValueOrDefault();
            if ((item.Mask & ~0x7) != 0)
            {
                return false;
            }

            writer.Write(item.Mask);
            WriteIf(writer, item.Mask, 0, item.Round);
            WriteIf(writer, item.Mask, 1, CombatPhase(item.Phase));
            if (Has(item.Mask, 2))
            {
                writer.Write((byte)(item.ActionRunning ? 1 : 0));
            }

            return true;
        });

    public static F1CategoryDigest Players(
        IReadOnlyList<F1Player>? values) =>
        EncodeList(
            F1Category.PlayersPublic,
            values,
            16,
            item => item.Ordinal,
            (writer, item) =>
            {
                if (item.Ordinal is < 1 or > 16
                    || (item.Mask & ~0x7F) != 0
                    || !Has(item.Mask, 0))
                {
                    return false;
                }

                writer.Write(item.Mask);
                writer.Write(item.Ordinal);
                WriteIf(writer, item.Mask, 1, item.Hp);
                WriteIf(writer, item.Mask, 2, item.MaxHp);
                WriteIf(writer, item.Mask, 3, item.Block);
                WriteIf(writer, item.Mask, 4, item.Energy);
                if (Has(item.Mask, 5))
                {
                    writer.Write((byte)(item.Ready ? 1 : 0));
                }

                WriteIf(writer, item.Mask, 6, item.HandCount);
                return true;
            });

    public static F1CategoryDigest Monsters(
        IReadOnlyList<F1Monster>? values) =>
        EncodeList(
            F1Category.MonstersPublic,
            values,
            32,
            item => (
                item.EntityId,
                Utf8SortKey(item.ModelId)),
            (writer, item) =>
            {
                if (item.EntityId == 0
                    || (item.Mask & ~0x1F) != 0
                    || !Has(item.Mask, 0)
                    || !Has(item.Mask, 1))
                {
                    return false;
                }

                writer.Write(item.Mask);
                writer.Write(item.EntityId);
                if (!WriteString(writer, item.ModelId))
                {
                    return false;
                }

                WriteIf(writer, item.Mask, 2, item.Hp);
                WriteIf(writer, item.Mask, 3, item.MaxHp);
                WriteIf(writer, item.Mask, 4, item.Block);
                return true;
            });

    public static F1CategoryDigest CardCounts(
        IReadOnlyList<F1CardCounts>? values) =>
        EncodeList(
            F1Category.PublicCardCounts,
            values,
            16,
            item => item.Ordinal,
            (writer, item) =>
            {
                if (item.Ordinal is < 1 or > 16
                    || (item.Mask & ~0x1F) != 0
                    || !Has(item.Mask, 0))
                {
                    return false;
                }

                writer.Write(item.Mask);
                writer.Write(item.Ordinal);
                WriteIf(writer, item.Mask, 1, item.Hand);
                WriteIf(writer, item.Mask, 2, item.Draw);
                WriteIf(writer, item.Mask, 3, item.Discard);
                WriteIf(writer, item.Mask, 4, item.Exhaust);
                return true;
            });

    public static F1CategoryDigest Effects(
        IReadOnlyList<F1Effect>? values) =>
        EncodeList(
            F1Category.PublicEffects,
            values,
            256,
            item => (
                item.OwnerKind,
                item.OwnerId,
                item.EffectKind,
                Utf8SortKey(item.ModelId),
                item.Stack),
            (writer, item) =>
            {
                if (item.OwnerKind is not (1 or 2 or 3)
                    || item.EffectKind is not (1 or 2)
                    || item.Multiplicity == 0
                    || (item.Mask & ~0x3F) != 0
                    || (item.Mask & 0xF) != 0xF)
                {
                    return false;
                }

                writer.Write(item.Mask);
                writer.Write(item.OwnerKind);
                writer.Write(item.OwnerId);
                writer.Write(item.EffectKind);
                if (!WriteString(writer, item.ModelId))
                {
                    return false;
                }

                WriteIf(writer, item.Mask, 4, item.Stack);
                WriteIf(writer, item.Mask, 5, item.Multiplicity);
                return true;
            });

    public static F1CategoryDigest RngCounts(
        IReadOnlyList<F1RngCount>? values) =>
        EncodeList(
            F1Category.RngConsumptionCounts,
            values,
            64,
            item => Utf8SortKey(item.StreamId),
            (writer, item) =>
            {
                if ((item.Mask & ~0x3) != 0
                    || (item.Mask & 0x1) == 0
                    || !ToolkitRngCounter.AllowedStreams.Contains(
                        item.StreamId,
                        StringComparer.Ordinal)
                    || !WriteMaskAndString(
                        writer,
                        item.Mask,
                        item.StreamId))
                {
                    return false;
                }

                WriteIf(writer, item.Mask, 1, item.Consumed);
                return true;
            });

    public static F1CategoryDigest Contributions(
        IReadOnlyList<F1ModContribution>? values) =>
        EncodeList(
            F1Category.ModContributions,
            values,
            256,
            item => Utf8SortKey(item.ModId),
            (writer, item) =>
            {
                if ((item.Mask & ~0xF) != 0
                    || (item.Mask & 0x1) == 0
                    || item.ProviderSchema == 0
                    || item.Digest.Length != 32
                    || !WriteMaskAndString(
                        writer,
                        item.Mask,
                        item.ModId))
                {
                    return false;
                }

                WriteIf(writer, item.Mask, 1, item.ProviderSchema);
                WriteIf(writer, item.Mask, 2, item.SourceRevision);
                if (Has(item.Mask, 3))
                {
                    writer.Write(item.Digest);
                }

                return true;
            });

    public static bool TryCreateCheckpoint(
        Guid sessionId,
        ulong checkpointId,
        string context,
        IReadOnlyList<F1CategoryDigest> categories,
        out F1Checkpoint checkpoint)
    {
        checkpoint = null!;
        if (sessionId == Guid.Empty
            || categories.Count != 8
            || categories.Select(item => item.Category).Distinct().Count() != 8
            || !TryStrictUtf8(context, 64, out byte[] contextBytes))
        {
            return false;
        }

        byte[] contextMaterial =
            "CGF1CTX"u8.ToArray().Concat(contextBytes).ToArray();
        byte[] contextTag = SHA256.HashData(contextMaterial)[..16];
        byte[][] tags = new byte[8][];
        byte availability = 0;
        foreach (F1CategoryDigest category in categories)
        {
            int index = (int)category.Category - 1;
            if (index is < 0 or > 7
                || (category.Available && category.Digest.Length != 32)
                || (!category.Available && category.Digest.Length != 0))
            {
                return false;
            }

            tags[index] = new byte[16];
            if (!category.Available)
            {
                continue;
            }

            availability |= (byte)(1 << index);
            byte[] material = new byte[2 + 8 + 16 + 1 + 32];
            BinaryPrimitives.WriteUInt16LittleEndian(material, Schema);
            BinaryPrimitives.WriteUInt64LittleEndian(
                material.AsSpan(2),
                checkpointId);
            contextTag.CopyTo(material, 10);
            material[26] = (byte)category.Category;
            category.Digest.CopyTo(material, 27);
            using HMACSHA256 hmac = new(sessionId.ToByteArray());
            tags[index] = hmac.ComputeHash(material)[..16];
        }

        checkpoint = new(
            Schema,
            checkpointId,
            contextTag,
            availability,
            tags);
        return true;
    }

    private static F1CategoryDigest Encode(
        F1Category category,
        int count,
        Func<BinaryWriter, bool> records)
    {
        try
        {
            using MemoryStream stream = new(MaxBlobBytes);
            using BinaryWriter writer =
                new(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write("CGF1"u8);
            writer.Write(Schema);
            writer.Write((byte)category);
            writer.Write((byte)1);
            writer.Write((ushort)count);
            if (!records(writer) || stream.Length > MaxBlobBytes)
            {
                return Unsupported(category);
            }

            return new(category, true, SHA256.HashData(stream.ToArray()));
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or EncoderFallbackException
                or IOException
                or OverflowException)
        {
            return Unsupported(category);
        }
    }

    private static F1CategoryDigest EncodeList<T, TKey>(
        F1Category category,
        IReadOnlyList<T>? values,
        int max,
        Func<T, TKey> key,
        Func<BinaryWriter, T, bool> write)
    {
        if (values == null)
        {
            return Unsupported(category);
        }

        if (values.Count > max)
        {
            return Unsupported(category);
        }

        T[] sorted;
        try
        {
            sorted = values.OrderBy(key).ToArray();
            for (int index = 1; index < sorted.Length; index++)
            {
                if (EqualityComparer<TKey>.Default.Equals(
                        key(sorted[index - 1]),
                        key(sorted[index])))
                {
                    return Unsupported(category);
                }
            }
        }
        catch
        {
            return Unsupported(category);
        }

        return Encode(category, sorted.Length, writer =>
        {
            foreach (T item in sorted)
            {
                if (!write(writer, item)
                    || writer.BaseStream.Length > MaxBlobBytes)
                {
                    return false;
                }
            }

            return true;
        });
    }

    private static bool WriteMaskAndString(
        BinaryWriter writer,
        ushort mask,
        string value)
    {
        writer.Write(mask);
        return WriteString(writer, value);
    }

    private static bool WriteString(BinaryWriter writer, string value)
    {
        if (!TryStrictUtf8(value, 64, out byte[] bytes))
        {
            return false;
        }

        writer.Write((byte)bytes.Length);
        writer.Write(bytes);
        return true;
    }

    private static bool TryStrictUtf8(
        string value,
        int maxBytes,
        out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(value)
            || value.Any(char.IsControl))
        {
            return false;
        }

        try
        {
            bytes = StrictUtf8.GetBytes(value);
            return bytes.Length <= maxBytes;
        }
        catch (EncoderFallbackException)
        {
            bytes = [];
            return false;
        }
    }

    private static string Utf8SortKey(string value) =>
        TryStrictUtf8(value, 64, out byte[] bytes)
            ? Convert.ToHexString(bytes)
            : "\uFFFF";

    private static bool Has(ushort mask, int bit) =>
        (mask & (1 << bit)) != 0;

    private static ushort RoomKind(ushort value) =>
        value <= 8 ? value : ushort.MaxValue;

    private static ushort CombatPhase(ushort value) =>
        value <= 5 ? value : ushort.MaxValue;

    private static void WriteIf(
        BinaryWriter writer,
        ushort mask,
        int bit,
        int value)
    {
        if (Has(mask, bit))
        {
            writer.Write(value);
        }
    }

    private static void WriteIf(
        BinaryWriter writer,
        ushort mask,
        int bit,
        ushort value)
    {
        if (Has(mask, bit))
        {
            writer.Write(value);
        }
    }

    private static void WriteIf(
        BinaryWriter writer,
        ushort mask,
        int bit,
        ulong value)
    {
        if (Has(mask, bit))
        {
            writer.Write(value);
        }
    }
}

public static class F1CheckpointCodec
{
    public const int PayloadBytes = 155;

    public static bool TryEncode(F1Checkpoint value, out byte[] payload)
    {
        payload = [];
        if (value.Schema != F1SchemaV1.Schema
            || value.ContextTag.Length != 16
            || value.Tags.Length != 8
            || value.Tags.Any(tag => tag.Length != 16))
        {
            return false;
        }

        payload = new byte[PayloadBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, value.Schema);
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload.AsSpan(2),
            value.CheckpointId);
        value.ContextTag.CopyTo(payload, 10);
        payload[26] = value.AvailabilityMask;
        for (int index = 0; index < 8; index++)
        {
            value.Tags[index].CopyTo(payload, 27 + index * 16);
        }

        return true;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> payload,
        out F1Checkpoint checkpoint)
    {
        checkpoint = null!;
        if (payload.Length != PayloadBytes
            || BinaryPrimitives.ReadUInt16LittleEndian(payload)
                != F1SchemaV1.Schema)
        {
            return false;
        }

        ulong id = BinaryPrimitives.ReadUInt64LittleEndian(payload[2..]);
        byte mask = payload[26];
        byte[][] tags = new byte[8][];
        for (int index = 0; index < 8; index++)
        {
            tags[index] =
                payload.Slice(27 + index * 16, 16).ToArray();
            bool available = (mask & (1 << index)) != 0;
            if (!available && tags[index].Any(value => value != 0))
            {
                return false;
            }
        }

        checkpoint = new(
            F1SchemaV1.Schema,
            id,
            payload.Slice(10, 16).ToArray(),
            mask,
            tags);
        return true;
    }
}

public readonly record struct F1CheckpointResult(
    byte SubjectOrdinal,
    ulong CheckpointId,
    ulong? LastCommonCheckpointId,
    byte ComparableMask,
    byte MismatchMask);

public static class F1CheckpointResultCodec
{
    public const int PayloadBytes = 20;

    public static byte[] Encode(F1CheckpointResult value)
    {
        if (value.SubjectOrdinal is < 1 or > 16
            || (value.MismatchMask & ~value.ComparableMask) != 0)
        {
            return [];
        }

        byte[] payload = new byte[PayloadBytes];
        payload[0] = value.SubjectOrdinal;
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload.AsSpan(1),
            value.CheckpointId);
        payload[9] = (byte)(value.LastCommonCheckpointId.HasValue ? 1 : 0);
        if (value.LastCommonCheckpointId.HasValue)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                payload.AsSpan(10),
                value.LastCommonCheckpointId.Value);
        }

        payload[18] = value.ComparableMask;
        payload[19] = value.MismatchMask;
        return payload;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> payload,
        out F1CheckpointResult value)
    {
        value = default;
        if (payload.Length != PayloadBytes
            || payload[0] is < 1 or > 16)
        {
            return false;
        }

        if (payload[9] > 1
            || (payload[9] == 0
                && HasNonZero(payload.Slice(10, 8))))
        {
            return false;
        }

        value = new(
            payload[0],
            BinaryPrimitives.ReadUInt64LittleEndian(payload[1..]),
            payload[9] == 1
                ? BinaryPrimitives.ReadUInt64LittleEndian(payload[10..])
                : null,
            payload[18],
            payload[19]);
        return (value.MismatchMask & ~value.ComparableMask) == 0;
    }

    private static bool HasNonZero(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value != 0)
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class F2History
{
    public const int MaxCheckpoints = 64;
    private readonly Queue<(byte Peer, F1Checkpoint Value)> _items = [];

    public void Add(byte peer, F1Checkpoint checkpoint)
    {
        if (peer is < 1 or > 16)
        {
            return;
        }

        while (_items.Count >= MaxCheckpoints * 16)
        {
            _items.Dequeue();
        }

        _items.Enqueue((peer, checkpoint));
    }

    public F2Result Compare(byte left, byte right)
    {
        Dictionary<ulong, F1Checkpoint> a = For(left);
        Dictionary<ulong, F1Checkpoint> b = For(right);
        ulong? lastCommon = null;
        foreach (ulong id in a.Keys.Intersect(b.Keys).Order())
        {
            F1Checkpoint x = a[id];
            F1Checkpoint y = b[id];
            if (x.Schema != y.Schema
                || !x.ContextTag.AsSpan().SequenceEqual(y.ContextTag))
            {
                continue;
            }

            byte commonMask =
                (byte)(x.AvailabilityMask & y.AvailabilityMask);
            byte mismatch = 0;
            for (int index = 0; index < 8; index++)
            {
                if ((commonMask & (1 << index)) != 0
                    && !x.Tags[index].AsSpan().SequenceEqual(y.Tags[index]))
                {
                    mismatch |= (byte)(1 << index);
                }
            }

            if (mismatch == 0)
            {
                lastCommon = id;
                continue;
            }

            int lowest = Enumerable.Range(0, 8)
                .First(index => (mismatch & (1 << index)) != 0);
            return new(
                lastCommon,
                id,
                (F1Category)(lowest + 1),
                "First observed divergence; cause and responsible Mod/player are not established.");
        }

        return new(
            lastCommon,
            null,
            null,
            a.Count == 0 || b.Count == 0
                ? "Hierarchical localization unavailable."
                : "No observed comparable divergence.");
    }

    public void Clear() => _items.Clear();

    private Dictionary<ulong, F1Checkpoint> For(byte peer) =>
        _items.Where(item => item.Peer == peer)
            .GroupBy(item => item.Value.CheckpointId)
            .ToDictionary(group => group.Key, group => group.Last().Value);
}

public static class ToolkitRngCounter
{
    public static readonly string[] AllowedStreams =
    [
        "Rng.NextBool",
        "Rng.NextInt(max)",
        "Rng.NextInt(range)",
        "Rng.NextUInt(range)",
        "Rng.NextULong",
        "Rng.NextULong(range)",
        "Rng.NextFloat(range)",
        "Rng.NextDouble",
        "Rng.NextDouble(range)"
    ];

    private static readonly long[] Counters =
        new long[AllowedStreams.Length];
    private static int _available;

    public static bool IsAvailable =>
        Volatile.Read(ref _available) != 0;

    public static void Enable() =>
        Volatile.Write(ref _available, 1);

    public static void Increment(int stream)
    {
        if (IsAvailable && (uint)stream < (uint)Counters.Length)
        {
            Interlocked.Increment(ref Counters[stream]);
        }
    }

    public static F1RngCount[] Snapshot() =>
        AllowedStreams.Select((name, index) =>
            new F1RngCount(
                0x3,
                name,
                unchecked((ulong)Math.Max(
                    0,
                    Interlocked.Read(ref Counters[index])))))
            .ToArray();

    public static void Clear()
    {
        for (int index = 0; index < Counters.Length; index++)
        {
            Interlocked.Exchange(ref Counters[index], 0);
        }
    }
}
