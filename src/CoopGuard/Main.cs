using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace CoopGuard;

[ModInitializer(nameof(Initialize))]
public static class Main
{
    public const string ModId = "CoopGuard";

    internal static Logger Log { get; } = new(ModId, LogType.Network);

    public static void Initialize()
    {
        // The ExecuteEssential postfix captures after every startup Mod has loaded,
        // before any command-line or menu join can enter the network handshake.
        new Harmony(ModId).PatchAll();
        Log.Info("Initialized. Package fingerprints will be added to STS2's native Mod compatibility check.");
    }
}
