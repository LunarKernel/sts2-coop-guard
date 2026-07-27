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
        // The fingerprint is captured later, on first lobby entry. During a Mod
        // initializer, later Mods and even this assembly are not marked loaded yet.
        new Harmony(ModId).PatchAll();
        Log.Info("Initialized. Package fingerprints will be captured on first multiplayer lobby entry.");
    }
}
