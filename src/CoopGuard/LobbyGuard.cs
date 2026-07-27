using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;

namespace CoopGuard;

public struct FingerprintMessage : INetMessage
{
    public const int CurrentProtocol = 1;

    public int protocol;
    public string digest;
    public string details;
    public string error;

    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(protocol);
        writer.WriteString(digest ?? string.Empty);
        writer.WriteString(details ?? string.Empty);
        writer.WriteString(error ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        protocol = reader.ReadInt();
        digest = reader.ReadString();
        details = reader.ReadString();
        error = reader.ReadString();
    }
}

internal static class LobbyGuards
{
    private static readonly ConditionalWeakTable<StartRunLobby, LobbyGuardSession> Sessions = new();

    public static void Attach(StartRunLobby lobby)
    {
        if (lobby.NetService.Type is NetGameType.Host or NetGameType.Client)
        {
            Sessions.GetValue(lobby, current => new LobbyGuardSession(current, ModFingerprint.GetOrCapture()));
        }
    }

    public static void Detach(StartRunLobby lobby)
    {
        if (Sessions.TryGetValue(lobby, out LobbyGuardSession? session))
        {
            session.Dispose();
            Sessions.Remove(lobby);
        }
    }

    public static bool CanStart(StartRunLobby lobby, out string reason)
    {
        reason = string.Empty;
        if (lobby.NetService.Type is not (NetGameType.Host or NetGameType.Client))
        {
            return true;
        }

        if (!Sessions.TryGetValue(lobby, out LobbyGuardSession? session))
        {
            reason = "CoopGuard did not initialize for this multiplayer lobby.";
            return false;
        }

        return session.CanStart(out reason);
    }
}

internal sealed class LobbyGuardSession : IDisposable
{
    private const int MaxDetailsLength = 512 * 1024;
    private const int MaxErrorLength = 4096;

    private readonly StartRunLobby _lobby;
    private readonly FingerprintSnapshot _local;
    private readonly Dictionary<ulong, FingerprintMessage> _peers = [];
    private readonly MessageHandlerDelegate<FingerprintMessage> _handler;

    public LobbyGuardSession(StartRunLobby lobby, FingerprintSnapshot local)
    {
        _lobby = lobby;
        _local = local;
        _handler = OnFingerprint;
        _peers[lobby.NetService.NetId] = CreateLocalMessage();

        lobby.NetService.RegisterMessageHandler(_handler);
        lobby.PlayerConnected += OnPlayerConnected;
        lobby.PlayerDisconnected += OnPlayerDisconnected;
    }

    public bool CanStart(out string reason)
    {
        if (_local.Errors.Count > 0)
        {
            reason = "Local package hashing failed:\n" + string.Join('\n', _local.Errors.Take(6));
            return false;
        }

        List<ulong> missing = _lobby.Players
            .Select(player => player.id)
            .Where(id => !_peers.ContainsKey(id))
            .ToList();
        if (missing.Count > 0)
        {
            reason = "Waiting for package fingerprints from: " + string.Join(", ", missing);
            return false;
        }

        foreach (LobbyPlayer player in _lobby.Players)
        {
            FingerprintMessage peer = _peers[player.id];
            if (peer.protocol != FingerprintMessage.CurrentProtocol)
            {
                reason = $"Player {player.id} uses CoopGuard protocol {peer.protocol}; expected {FingerprintMessage.CurrentProtocol}.";
                return false;
            }

            if (!string.IsNullOrEmpty(peer.error))
            {
                reason = $"Player {player.id} could not hash their Mod packages:\n{peer.error}";
                return false;
            }

            if (!string.Equals(peer.digest, _local.Digest, StringComparison.Ordinal))
            {
                IReadOnlyList<string> diff = FingerprintCodec.Diff(_local.Details, peer.details);
                reason = $"Player {player.id} has different Mod package files:\n"
                    + string.Join('\n', diff);
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    public void Dispose()
    {
        _lobby.NetService.UnregisterMessageHandler(_handler);
        _lobby.PlayerConnected -= OnPlayerConnected;
        _lobby.PlayerDisconnected -= OnPlayerDisconnected;
    }

    private void OnPlayerConnected(LobbyPlayer _)
    {
        // Every existing peer re-announces itself when someone joins. This
        // handles clients that were not ready to receive the host's first send.
        try
        {
            _lobby.NetService.SendMessage(CreateLocalMessage());
        }
        catch (Exception ex)
        {
            // A failed send leaves the peer missing and therefore blocks ready.
            Main.Log.Error($"Could not send package fingerprint: {ex}");
        }
    }

    private void OnPlayerDisconnected(LobbyPlayer player) =>
        _peers.Remove(player.id);

    private void OnFingerprint(FingerprintMessage message, ulong senderId)
    {
        if (message.details.Length > MaxDetailsLength || message.error.Length > MaxErrorLength)
        {
            message = new FingerprintMessage
            {
                protocol = message.protocol,
                error = "Rejected an oversized CoopGuard fingerprint message.",
                digest = string.Empty,
                details = string.Empty
            };
        }

        _peers[senderId] = message;
        Main.Log.Info($"Received package fingerprint {message.digest} from {senderId}.");

        if (_lobby.NetService.Type == NetGameType.Host && senderId != _lobby.NetService.NetId)
        {
            // Direct reply avoids a join-time race where the host's first
            // broadcast arrives before the new client's lobby handler exists.
            try
            {
                _lobby.NetService.SendMessage(CreateLocalMessage(), senderId);
            }
            catch (Exception ex)
            {
                Main.Log.Error($"Could not reply with package fingerprint to {senderId}: {ex}");
            }
        }
    }

    private FingerprintMessage CreateLocalMessage() => new()
    {
        protocol = FingerprintMessage.CurrentProtocol,
        digest = _local.Digest,
        details = _local.Details,
        error = string.Join('\n', _local.Errors)
    };
}

[HarmonyPatch]
internal static class StartRunLobbyConstructorPatch
{
    private static System.Reflection.MethodBase TargetMethod() =>
        AccessTools.Constructor(
            typeof(StartRunLobby),
            [typeof(GameMode), typeof(INetGameService), typeof(IStartRunLobbyListener), typeof(int)]);

    private static void Postfix(StartRunLobby __instance) =>
        LobbyGuards.Attach(__instance);
}

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.CleanUp))]
internal static class StartRunLobbyCleanupPatch
{
    private static void Prefix(StartRunLobby __instance) =>
        LobbyGuards.Detach(__instance);
}

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.SetReady))]
internal static class StartRunLobbyReadyPatch
{
    private static bool Prefix(StartRunLobby __instance, bool ready)
    {
        if (!ready || LobbyGuards.CanStart(__instance, out string reason))
        {
            return true;
        }

        Main.Log.Warn("Blocked ready: " + reason.Replace('\n', ' '));
        try
        {
            NErrorPopup? popup = NErrorPopup.Create("STS2 Co-op Guard", reason, showReportBugButton: false);
            if (popup != null && NModalContainer.Instance != null)
            {
                NModalContainer.Instance.Add(popup);
            }
        }
        catch (Exception ex)
        {
            // Popup failure must not bypass the compatibility gate.
            Main.Log.Error($"Could not show blocked-ready popup: {ex}");
        }

        return false;
    }
}

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.IsAboutToBeginGame))]
internal static class StartRunLobbyBeginPatch
{
    private static void Postfix(StartRunLobby __instance, ref bool __result)
    {
        if (__result && !LobbyGuards.CanStart(__instance, out string reason))
        {
            // This is the host-side final gate; never rely only on client UI.
            Main.Log.Warn("Blocked begin-run: " + reason.Replace('\n', ' '));
            __result = false;
        }
    }
}
