using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BetterCoop;

public enum ToolkitConsentFeature : byte
{
  HandSharing = 1,
  Forensics = 2
}

public sealed record ToolkitTextMessage(
    byte OriginOrdinal,
    string Text);

public static class ToolkitTextCodec
{
  public const int MaxUtf8Bytes = 384;
  public const int MaxScalars = 256;
  public const int MaxLines = 4;
  public const int MaxCombiningRun = 8;
  private const int HeaderBytes = 3;
  private static readonly UTF8Encoding StrictUtf8 = new(false, true);

  public static bool TryEncode(
      byte originOrdinal,
      string text,
      out byte[] payload)
  {
    payload = [];
    if (!TryNormalize(text, out string normalized))
    {
      return false;
    }

    byte[] bytes;
    try
    {
      bytes = StrictUtf8.GetBytes(normalized);
    }
    catch (EncoderFallbackException)
    {
      return false;
    }

    if (originOrdinal > ToolkitLimits.MaxPlayers
        || bytes.Length is < 1 or > MaxUtf8Bytes)
    {
      return false;
    }

    payload = new byte[HeaderBytes + bytes.Length];
    payload[0] = originOrdinal;
    BinaryPrimitives.WriteUInt16LittleEndian(
        payload.AsSpan(1),
        (ushort)bytes.Length);
    bytes.CopyTo(payload, HeaderBytes);
    return true;
  }

  public static bool TryDecode(
      ReadOnlySpan<byte> payload,
      out ToolkitTextMessage message)
  {
    message = null!;
    if (payload.Length is < HeaderBytes + 1
        or > HeaderBytes + MaxUtf8Bytes)
    {
      return false;
    }

    int length = BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]);
    if (payload[0] > ToolkitLimits.MaxPlayers
        || length is < 1 or > MaxUtf8Bytes
        || payload.Length != HeaderBytes + length)
    {
      return false;
    }

    string text;
    try
    {
      text = StrictUtf8.GetString(payload[HeaderBytes..]);
    }
    catch (DecoderFallbackException)
    {
      return false;
    }

    if (!TryNormalize(text, out string normalized)
        || !string.Equals(text, normalized, StringComparison.Ordinal))
    {
      return false;
    }

    message = new(payload[0], text);
    return true;
  }

  private static bool TryNormalize(string? text, out string normalized)
  {
    normalized = string.Empty;
    if (string.IsNullOrEmpty(text))
    {
      return false;
    }

    if (HasExcessiveCombiningRun(text))
    {
      return false;
    }

    try
    {
      normalized = text
          .Replace("\r\n", "\n", StringComparison.Ordinal)
          .Replace('\r', '\n')
          .Normalize(NormalizationForm.FormC);
    }
    catch (ArgumentException)
    {
      return false;
    }

    int scalars = 0;
    int lines = 1;
    int combiningRun = 0;
    foreach (Rune rune in normalized.EnumerateRunes())
    {
      scalars++;
      if (scalars > MaxScalars)
      {
        return false;
      }

      if (rune.Value == '\n')
      {
        lines++;
        combiningRun = 0;
        if (lines > MaxLines)
        {
          return false;
        }

        continue;
      }

      UnicodeCategory category = Rune.GetUnicodeCategory(rune);
      bool combining = category is UnicodeCategory.NonSpacingMark
          or UnicodeCategory.SpacingCombiningMark
          or UnicodeCategory.EnclosingMark;
      combiningRun = combining ? combiningRun + 1 : 0;
      if (combiningRun > MaxCombiningRun
          || category == UnicodeCategory.Control
          || (category == UnicodeCategory.Format && rune.Value != 0x200D)
          || rune.Value is >= 0x202A and <= 0x202E
          || rune.Value is >= 0x2066 and <= 0x2069)
      {
        return false;
      }
    }

    return scalars > 0;
  }

  private static bool HasExcessiveCombiningRun(string text)
  {
    int run = 0;
    foreach (Rune rune in text.EnumerateRunes())
    {
      UnicodeCategory category = Rune.GetUnicodeCategory(rune);
      run = category is UnicodeCategory.NonSpacingMark
          or UnicodeCategory.SpacingCombiningMark
          or UnicodeCategory.EnclosingMark
          ? run + 1
          : 0;
      if (run > MaxCombiningRun)
      {
        return true;
      }
    }

    return false;
  }
}

public readonly record struct ToolkitHandWatch(
    uint MembershipEpoch,
    byte OwnerOrdinal);

public static class ToolkitHandWatchCodec
{
  public const int PayloadBytes = 5;

  public static byte[] Encode(ToolkitHandWatch value)
  {
    if (value.MembershipEpoch == 0
        || value.OwnerOrdinal is < 1 or > ToolkitLimits.MaxPlayers)
    {
      return [];
    }

    byte[] payload = new byte[PayloadBytes];
    BinaryPrimitives.WriteUInt32LittleEndian(
        payload,
        value.MembershipEpoch);
    payload[4] = value.OwnerOrdinal;
    return payload;
  }

  public static bool TryDecode(
      ReadOnlySpan<byte> payload,
      out ToolkitHandWatch value)
  {
    value = default;
    if (payload.Length != PayloadBytes)
    {
      return false;
    }

    value = new(
        BinaryPrimitives.ReadUInt32LittleEndian(payload),
        payload[4]);
    return value.MembershipEpoch != 0
        && value.OwnerOrdinal is >= 1 and <= ToolkitLimits.MaxPlayers;
  }
}

[Flags]
public enum ToolkitLimitBreakFlags : ushort
{
  None = 0,
  Active = 1 << 0,
  NetworkPatches = 1 << 1,
  LayoutPatches = 1 << 2,
  ScalingPatches = 1 << 3,
  SteamCapacity = 1 << 4,
  Enabled = 1 << 5,
  SettingsSynchronized = 1 << 6,
  KnownContract = 1 << 7
}

public sealed record ToolkitLimitBreakCapability(
    byte OriginOrdinal,
    uint MembershipEpoch,
    ToolkitLimitBreakFlags Flags,
    byte Capacity,
    byte SlotBits,
    byte ListBits,
    uint HostSettingsEpoch,
    byte[] ContractDigest,
    byte[] SettingsDigest);

public static class ToolkitLimitBreakContract
{
  public const int DigestBytes = 16;
  public const int Capacity = 16;
  public const int SlotBits = 4;
  public const int ListBits = 5;
  public const ToolkitLimitBreakFlags RequiredFlags =
      ToolkitLimitBreakFlags.Active
      | ToolkitLimitBreakFlags.NetworkPatches
      | ToolkitLimitBreakFlags.LayoutPatches
      | ToolkitLimitBreakFlags.ScalingPatches
      | ToolkitLimitBreakFlags.SteamCapacity
      | ToolkitLimitBreakFlags.Enabled
      | ToolkitLimitBreakFlags.SettingsSynchronized
      | ToolkitLimitBreakFlags.KnownContract;

  public static byte[] DigestSettings(
      bool enabled,
      double multiplier)
  {
    if (!double.IsFinite(multiplier))
    {
      return [];
    }

    Span<byte> normalized =
        stackalloc byte[1 + sizeof(long)];
    normalized[0] = enabled ? (byte)1 : (byte)0;
    BinaryPrimitives.WriteInt64BigEndian(
        normalized[1..],
        BitConverter.DoubleToInt64Bits(multiplier));
    return SHA256.HashData(normalized)[..DigestBytes];
  }

  public static byte[] DigestIdentity(
      string gameVersion,
      string modVersion,
      string dllHash,
      string ritsuVersion)
  {
    string identity = string.Join(
        '\n',
        gameVersion,
        modVersion,
        dllHash,
        ritsuVersion);
    return SHA256.HashData(
        Encoding.UTF8.GetBytes(identity))[..DigestBytes];
  }

  public static bool IsCompatible(
      ToolkitLimitBreakCapability value,
      out string reason) =>
      IsCompatible(
          value,
          requireSettingsSynchronized: true,
          out reason);

  public static bool IsReadyCompatible(
      ToolkitLimitBreakCapability value,
      out string reason) =>
      IsCompatible(
          value,
          requireSettingsSynchronized: false,
          out reason);

  private static bool IsCompatible(
      ToolkitLimitBreakCapability value,
      bool requireSettingsSynchronized,
      out string reason)
  {
    ToolkitLimitBreakFlags requiredFlags =
        requireSettingsSynchronized
            ? RequiredFlags
            : RequiredFlags
                & ~ToolkitLimitBreakFlags.SettingsSynchronized;
    if ((value.Flags & requiredFlags) != requiredFlags)
    {
      ToolkitLimitBreakFlags missing =
          requiredFlags & ~value.Flags;
      reason = "missing capability: " + missing;
      return false;
    }

    if (value.Capacity != Capacity)
    {
      reason = $"capacity={value.Capacity}, expected {Capacity}";
      return false;
    }

    if (value.SlotBits != SlotBits)
    {
      reason = $"slot bits={value.SlotBits}, expected {SlotBits}";
      return false;
    }

    if (value.ListBits != ListBits)
    {
      reason = $"list bits={value.ListBits}, expected {ListBits}";
      return false;
    }

    if (requireSettingsSynchronized
        && value.HostSettingsEpoch == 0)
    {
      reason = "host settings have not synchronized";
      return false;
    }

    if (value.ContractDigest.Length != DigestBytes
        || value.SettingsDigest.Length != DigestBytes)
    {
      reason = "contract/settings digest is invalid";
      return false;
    }

    reason = string.Empty;
    return true;
  }
}

public static class ToolkitLimitBreakCapabilityCodec
{
  public const int PayloadBytes =
      1 + sizeof(uint) + sizeof(ushort) + 3 + sizeof(uint)
      + 2 * ToolkitLimitBreakContract.DigestBytes;

  public static bool TryEncode(
      ToolkitLimitBreakCapability value,
      out byte[] payload)
  {
    payload = [];
    if (value.OriginOrdinal > ToolkitLimits.MaxPlayers
        || value.MembershipEpoch == 0
        || (value.Flags & ~ToolkitLimitBreakContract.RequiredFlags) != 0
        || value.ContractDigest.Length
            != ToolkitLimitBreakContract.DigestBytes
        || value.SettingsDigest.Length
            != ToolkitLimitBreakContract.DigestBytes)
    {
      return false;
    }

    payload = new byte[PayloadBytes];
    int offset = 0;
    payload[offset++] = value.OriginOrdinal;
    BinaryPrimitives.WriteUInt32LittleEndian(
        payload.AsSpan(offset),
        value.MembershipEpoch);
    offset += sizeof(uint);
    BinaryPrimitives.WriteUInt16LittleEndian(
        payload.AsSpan(offset),
        (ushort)value.Flags);
    offset += sizeof(ushort);
    payload[offset++] = value.Capacity;
    payload[offset++] = value.SlotBits;
    payload[offset++] = value.ListBits;
    BinaryPrimitives.WriteUInt32LittleEndian(
        payload.AsSpan(offset),
        value.HostSettingsEpoch);
    offset += sizeof(uint);
    value.ContractDigest.CopyTo(payload, offset);
    offset += ToolkitLimitBreakContract.DigestBytes;
    value.SettingsDigest.CopyTo(payload, offset);
    return true;
  }

  public static bool TryDecode(
      ReadOnlySpan<byte> payload,
      out ToolkitLimitBreakCapability value)
  {
    value = null!;
    if (payload.Length != PayloadBytes
        || payload[0] > ToolkitLimits.MaxPlayers)
    {
      return false;
    }

    int offset = 1;
    uint membershipEpoch =
        BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
    offset += sizeof(uint);
    ToolkitLimitBreakFlags flags = (ToolkitLimitBreakFlags)
        BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
    offset += sizeof(ushort);
    byte capacity = payload[offset++];
    byte slotBits = payload[offset++];
    byte listBits = payload[offset++];
    uint settingsEpoch =
        BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
    offset += sizeof(uint);
    if (membershipEpoch == 0
        || (flags & ~ToolkitLimitBreakContract.RequiredFlags) != 0)
    {
      return false;
    }

    byte[] contractDigest = payload.Slice(
        offset,
        ToolkitLimitBreakContract.DigestBytes).ToArray();
    offset += ToolkitLimitBreakContract.DigestBytes;
    byte[] settingsDigest = payload.Slice(
        offset,
        ToolkitLimitBreakContract.DigestBytes).ToArray();
    value = new(
        payload[0],
        membershipEpoch,
        flags,
        capacity,
        slotBits,
        listBits,
        settingsEpoch,
        contractDigest,
        settingsDigest);
    return true;
  }
}

public enum ToolkitRunControlAction : byte
{
  Prepare = 1,
  Ready = 2,
  Abort = 3,
  Activate = 4,
  Verify = 5,
  Commit = 6
}

public enum ToolkitRunControlResult : byte
{
  None = 0,
  Ready = 1,
  Busy = 2,
  UnsafeState = 3,
  MissingCheckpoint = 4,
  DigestMismatch = 5,
  ActivationFailed = 6,
  Timeout = 7,
  Cancelled = 8
}

public sealed record ToolkitRunControl(
    ToolkitRunControlAction Action,
    byte OriginOrdinal,
    ToolkitRunControlResult Result,
    uint MembershipEpoch,
    uint RollbackEpoch,
    Guid TransactionId,
    Guid CheckpointId,
    ulong VisitIndex,
    byte[] CheckpointDigest);

public static class ToolkitRunControlCodec
{
  public const int DigestBytes = 32;
  public const int PayloadBytes =
      4 + 2 * sizeof(uint) + 2 * 16 + sizeof(ulong) + DigestBytes;

  public static bool TryEncode(
      ToolkitRunControl value,
      out byte[] payload)
  {
    payload = [];
    if (!Valid(value))
    {
      return false;
    }

    payload = new byte[PayloadBytes];
    payload[0] = (byte)value.Action;
    payload[1] = value.OriginOrdinal;
    payload[2] = (byte)value.Result;
    payload[3] = 0;
    BinaryPrimitives.WriteUInt32LittleEndian(
        payload.AsSpan(4),
        value.MembershipEpoch);
    BinaryPrimitives.WriteUInt32LittleEndian(
        payload.AsSpan(8),
        value.RollbackEpoch);
    value.TransactionId.TryWriteBytes(payload.AsSpan(12, 16));
    value.CheckpointId.TryWriteBytes(payload.AsSpan(28, 16));
    BinaryPrimitives.WriteUInt64LittleEndian(
        payload.AsSpan(44),
        value.VisitIndex);
    value.CheckpointDigest.CopyTo(payload, 52);
    return true;
  }

  public static bool TryDecode(
      ReadOnlySpan<byte> payload,
      out ToolkitRunControl value)
  {
    value = null!;
    if (payload.Length != PayloadBytes || payload[3] != 0)
    {
      return false;
    }

    value = new(
        (ToolkitRunControlAction)payload[0],
        payload[1],
        (ToolkitRunControlResult)payload[2],
        BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]),
        BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]),
        new Guid(payload[12..28]),
        new Guid(payload[28..44]),
        BinaryPrimitives.ReadUInt64LittleEndian(payload[44..]),
        payload[52..].ToArray());
    return Valid(value);
  }

  private static bool Valid(ToolkitRunControl value)
  {
    if (!Enum.IsDefined(value.Action)
        || !Enum.IsDefined(value.Result)
        || value.OriginOrdinal > ToolkitLimits.MaxPlayers
        || value.MembershipEpoch == 0
        || value.RollbackEpoch == 0
        || value.TransactionId == Guid.Empty
        || value.CheckpointId == Guid.Empty
        || value.VisitIndex == 0
        || value.CheckpointDigest.Length != DigestBytes)
    {
      return false;
    }

    return value.Action switch
    {
      ToolkitRunControlAction.Prepare
          or ToolkitRunControlAction.Activate
          or ToolkitRunControlAction.Commit =>
          value.Result == ToolkitRunControlResult.None,
      ToolkitRunControlAction.Ready
          or ToolkitRunControlAction.Verify =>
          value.Result is >= ToolkitRunControlResult.Ready
              and <= ToolkitRunControlResult.ActivationFailed,
      ToolkitRunControlAction.Abort =>
          value.Result is >= ToolkitRunControlResult.Busy
              and <= ToolkitRunControlResult.Cancelled,
      _ => false
    };
  }
}

public static class ToolkitRunControlRoster
{
  public static bool IsExact(
      IReadOnlyList<ulong> expected,
      IReadOnlyList<ulong> current) =>
      expected.Count is >= 1 and <= ToolkitLimits.MaxPlayers
      && expected.Count == current.Count
      && expected.SequenceEqual(current);

  public static bool AllResponded(
      IReadOnlyList<ulong> expected,
      IReadOnlyList<ulong> current,
      IReadOnlySet<ulong> responded) =>
      IsExact(expected, current)
      && expected.All(responded.Contains);
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
