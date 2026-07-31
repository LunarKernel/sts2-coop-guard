using System.Buffers.Binary;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace BetterCoop;

public sealed class ToolkitEnvelopeMessage : INetMessage
{
  public ToolkitEnvelope Envelope { get; private set; } = null!;

  public bool ShouldBroadcast => false;
  public NetTransferMode Mode => NetTransferMode.Reliable;
  public LogLevel LogLevel => LogLevel.VeryDebug;
  public bool ShouldBuffer => false;

  public ToolkitEnvelopeMessage()
  {
  }

  internal ToolkitEnvelopeMessage(ToolkitEnvelope envelope)
  {
    Envelope = envelope;
  }

  public void Serialize(PacketWriter writer)
  {
    if (!ToolkitEnvelopeCodec.TryEncode(
            Envelope,
            out byte[] encoded))
    {
      throw new InvalidDataException(
          "Invalid optional diagnostics envelope.");
    }

    writer.WriteBytes(encoded, encoded.Length);
  }

  public void Deserialize(PacketReader reader)
  {
    if (reader.BitPosition % 8 != 0)
    {
      throw new InvalidDataException(
          "Diagnostics envelope is not byte-aligned.");
    }

    int remainingBytes =
        (reader.Buffer.Length * 8 - reader.BitPosition) / 8;
    if (remainingBytes < ToolkitEnvelopeCodec.FixedBytes)
    {
      throw new InvalidDataException(
          "Invalid optional diagnostics envelope: packet truncated.");
    }

    byte[] encoded = new byte[ToolkitEnvelopeCodec.FixedBytes];
    reader.ReadBytes(encoded, encoded.Length);
    ushort payloadBytes =
        BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(28));
    if (payloadBytes > ToolkitEnvelopeCodec.MaxPayloadBytes
        || remainingBytes
            < ToolkitEnvelopeCodec.FixedBytes + payloadBytes)
    {
      throw new InvalidDataException(
          "Invalid optional diagnostics envelope: payload length is invalid.");
    }

    if (payloadBytes > 0)
    {
      Array.Resize(
          ref encoded,
          ToolkitEnvelopeCodec.FixedBytes + payloadBytes);
      byte[] payload = new byte[payloadBytes];
      reader.ReadBytes(payload, payload.Length);
      payload.CopyTo(encoded, ToolkitEnvelopeCodec.FixedBytes);
    }

    if (!ToolkitEnvelopeCodec.TryDecode(
            encoded,
            out ToolkitEnvelope envelope,
            out string error))
    {
      throw new InvalidDataException(
          "Invalid optional diagnostics envelope: " + error);
    }

    Envelope = envelope;
  }

  public override string ToString() =>
      $"ToolkitEnvelope({Envelope?.Type.ToString() ?? "invalid"})";
}

[HarmonyPatch]
internal static class ToolkitTransportSenderPatch
{
  private const int NativePrefixBytes = 1 + sizeof(ulong);

  private static IEnumerable<MethodBase> TargetMethods()
  {
    Type[] services =
    [
        typeof(NetHostGameService),
            typeof(NetClientGameService)
    ];
    foreach (Type service in services)
    {
      yield return AccessTools.Method(
              service,
              "OnPacketReceived",
              [
                  typeof(ulong),
                        typeof(byte[]),
                        typeof(NetTransferMode),
                        typeof(int)
              ])
          ?? throw new MissingMethodException(
              service.FullName,
              "OnPacketReceived");
    }
  }

  [HarmonyPrefix]
  [HarmonyPriority(Priority.First)]
  private static bool Prefix(ulong senderId, byte[] packetBytes)
  {
    int messageId;
    try
    {
      messageId = MessageTypes.TypeToId<ToolkitEnvelopeMessage>();
    }
    catch
    {
      return true;
    }

    if (messageId is < 0 or > byte.MaxValue
        || packetBytes.Length == 0
        || packetBytes[0] != (byte)messageId)
    {
      return true;
    }

    int availableBytes = packetBytes.Length - NativePrefixBytes;
    int declaredBytes = 0;
    if (availableBytes >= ToolkitEnvelopeCodec.FixedBytes)
    {
      declaredBytes = ToolkitEnvelopeCodec.FixedBytes
          + BinaryPrimitives.ReadUInt16LittleEndian(
              packetBytes.AsSpan(
                  NativePrefixBytes + 28,
                  sizeof(ushort)));
    }
    string envelopeError = "envelope invalid";
    bool envelopeValid = declaredBytes
            is >= ToolkitEnvelopeCodec.FixedBytes
                and <= ToolkitEnvelopeCodec.MaxEncodedBytes
        && declaredBytes <= availableBytes
        && ToolkitEnvelopeCodec.TryDecode(
            packetBytes.AsSpan(NativePrefixBytes, declaredBytes),
            out _,
            out envelopeError);
    string? rejection = packetBytes.Length < NativePrefixBytes
        + ToolkitEnvelopeCodec.FixedBytes
            ? "packet truncated"
            : declaredBytes > ToolkitEnvelopeCodec.MaxEncodedBytes
                ? "payload oversized"
                : declaredBytes > availableBytes
                    ? "payload truncated"
                : BinaryPrimitives.ReadUInt64LittleEndian(
                    packetBytes.AsSpan(1, sizeof(ulong))) != senderId
                    ? "native sender mismatch"
                    : !envelopeValid
                        ? envelopeError
                        : null;
    if (rejection != null)
    {
      ToolkitDiagnosticsRuntime.RejectTransportPacket(rejection);
      return false;
    }

    return true;
  }
}
