using System.Reflection;
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
        Harmony harmony = new(ModId);
        // Install the native-list sentinel first. If any later patch breaks after
        // a game update, peers still receive process-unique unsafe entries.
        GameplayModListPatch.Apply(harmony);
        FatalIncidentReporter.Initialize();
        try
        {
            // The ExecuteEssential postfix captures after every startup Mod has
            // loaded, before a command-line or menu join enters the handshake.
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            Log.Info(
                "Initialized. Package fingerprints and bilingual fatal-error explanations are active.");
        }
        catch (Exception ex)
        {
            ModFingerprint.MarkInitializationFailure(ex);
            Log.Error(
                "CoopGuard patch installation failed. Multiplayer will fail closed: "
                    + ex);
        }
    }
}
