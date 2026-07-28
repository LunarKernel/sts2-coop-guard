using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;

namespace CoopGuard;

internal static class FatalIncidentReporter
{
    private const int RecentLogLimit = 80;
    private static readonly object Sync = new();
    private static readonly Queue<string> RecentLogs = new(RecentLogLimit);
    private static readonly ConditionalWeakTable<NErrorPopup, PopupReport>
        PopupReports = new();
    private static Exception? _pendingInternalError;

    private sealed record PopupReport(string Text, bool Chinese);

    public static void Initialize()
    {
        Log.LogCallback += CaptureLog;
    }

    public static bool TryCreateNetworkPopup(
        NetErrorInfo info,
        out NErrorPopup? popup)
    {
        try
        {
            ConnectionFailureExtraInfo? extra = info.ConnectionExtraInfo;
            IncidentText? incident = IncidentExplainer.ExplainNetwork(
                info.GetReason().ToString(),
                extra?.missingModsOnHost ?? [],
                extra?.missingModsOnLocal ?? [],
                info.GetErrorString(),
                SnapshotLogs(),
                IsChinese());
            if (incident == null)
            {
                popup = null;
                return false;
            }

            popup = CreateDiagnosticPopup(incident);
            return true;
        }
        catch (Exception ex)
        {
            Main.Log.Error(
                $"Could not build enhanced network error explanation: {ex}");
            popup = null;
            return false;
        }
    }

    public static void RememberInternalError(Exception exception)
    {
        lock (Sync)
        {
            _pendingInternalError = exception;
        }
    }

    public static bool TryCreateInternalPopup(out NErrorPopup? popup)
    {
        Exception? exception;
        lock (Sync)
        {
            exception = _pendingInternalError;
            _pendingInternalError = null;
        }

        if (exception == null)
        {
            popup = null;
            return false;
        }

        try
        {
            Exception root = exception.GetBaseException();
            IncidentText incident = IncidentExplainer.ExplainException(
                root.GetType().Name,
                FindSuspectMod(root),
                DependencyName(root),
                IsChinese());
            popup = CreateDiagnosticPopup(incident);
            return true;
        }
        catch (Exception ex)
        {
            Main.Log.Error(
                $"Could not build enhanced internal error explanation: {ex}");
            popup = null;
            return false;
        }
    }

    public static void ShowLocalVerification(string reason)
    {
        IncidentText incident =
            IncidentExplainer.ExplainLocalVerification(reason, IsChinese());
        try
        {
            NErrorPopup? popup = CreateDiagnosticPopup(incident);
            NModalContainer? container = NModalContainer.Instance;
            if (popup != null
                && container != null
                && container.OpenModal == null)
            {
                container.Add(popup);
            }
            else
            {
                popup?.QueueFree();
                Main.Log.Warn(
                    "Could not show CoopGuard diagnosis because another modal is open.");
            }
        }
        catch (Exception ex)
        {
            // Diagnostic UI is fail-open; the compatibility gate remains fail-closed.
            Main.Log.Error($"Could not show CoopGuard diagnosis: {ex}");
        }
    }

    public static void ShowVerified()
    {
        try
        {
            NFullscreenTextVfx? notice = NFullscreenTextVfx.Create(
                IsChinese()
                    ? "CoopGuard：联机校验通过"
                    : "CoopGuard: multiplayer verification passed");
            if (notice != null)
            {
                NGame.Instance?.AddChild(notice);
            }
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Could not show CoopGuard health status: {ex}");
        }
    }

    public static void ShowManualSnapshot()
    {
        try
        {
            FingerprintSnapshot snapshot = ModFingerprint.ValidateQuick();
            IncidentText incident = snapshot.Errors.Count == 0
                ? IncidentExplainer.ExplainHealthy(
                    snapshot.ModCount,
                    snapshot.FileCount,
                    snapshot.TotalBytes,
                    IsChinese())
                : IncidentExplainer.ExplainLocalVerification(
                    "CoopGuard could not verify the local Mod packages:\n"
                        + string.Join('\n', snapshot.Errors.Take(6)),
                    IsChinese());
            NErrorPopup? popup = CreateDiagnosticPopup(incident, snapshot);
            NModalContainer? container = NModalContainer.Instance;
            if (popup != null
                && container != null
                && container.OpenModal == null)
            {
                container.Add(popup);
            }
            else
            {
                popup?.QueueFree();
                Main.Log.Warn(
                    "Could not show the manual snapshot because another modal is open.");
            }
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Could not create the manual diagnostic snapshot: {ex}");
        }
    }

    public static void LabelCopyButton(NErrorPopup popup)
    {
        if (!PopupReports.TryGetValue(popup, out PopupReport? report))
        {
            return;
        }

        try
        {
            popup.GetNode<NVerticalPopup>("VerticalPopup")
                .YesButton
                .SetText(report.Chinese ? "复制诊断" : "Copy diagnosis");
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Could not label the diagnosis copy button: {ex}");
        }
    }

    public static bool TryCopyReport(NErrorPopup popup)
    {
        if (!PopupReports.TryGetValue(popup, out PopupReport? report))
        {
            return false;
        }

        try
        {
            DisplayServer.ClipboardSet(report.Text);
            Main.Log.Info("Copied a redacted CoopGuard diagnostic report.");
        }
        catch (Exception ex)
        {
            Main.Log.Error($"Could not copy the diagnostic report: {ex}");
        }

        return true;
    }

    private static NErrorPopup? CreateDiagnosticPopup(
        IncidentText incident,
        FingerprintSnapshot? snapshot = null)
    {
        bool chinese = IsChinese();
        snapshot ??= ModFingerprint.ValidateQuick();
        string health = snapshot.Errors.Count == 0
            ? $"verified; mods={snapshot.ModCount}; files={snapshot.FileCount}; bytes={snapshot.TotalBytes}"
            : "blocked; " + string.Join("; ", snapshot.Errors.Take(6));
        NErrorPopup? popup = NErrorPopup.Create(
            incident.Title,
            incident.Body,
            showReportBugButton: true);
        if (popup != null)
        {
            PopupReports.Add(
                popup,
                new PopupReport(
                    IncidentExplainer.BuildReport(
                        incident,
                        GameVersion(),
                        RuntimeState(),
                        health,
                        SnapshotLogs(),
                        DateTimeOffset.UtcNow),
                    chinese));
        }

        return popup;
    }

    private static string GameVersion()
    {
        try
        {
            return NGame.GetGameVersion();
        }
        catch
        {
            return "unavailable";
        }
    }

    private static string RuntimeState()
    {
        try
        {
            RunManager run = RunManager.Instance;
            return $"network={run.NetService.Type}; connected={run.NetService.IsConnected}; runInProgress={run.IsInProgress}";
        }
        catch
        {
            return "unavailable";
        }
    }

    private static void CaptureLog(
        LogLevel level,
        string message,
        int _)
    {
        if (level < LogLevel.Warn || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        string bounded = message.Length <= 1000
            ? message
            : message[..1000];
        lock (Sync)
        {
            if (RecentLogs.Count == RecentLogLimit)
            {
                RecentLogs.Dequeue();
            }

            RecentLogs.Enqueue(bounded);
        }
    }

    private static string[] SnapshotLogs()
    {
        lock (Sync)
        {
            return RecentLogs.ToArray();
        }
    }

    private static bool IsChinese()
    {
        try
        {
            string? language = LocManager.Instance?.Language;
            return language != null
                && (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                    || language.Equals("zhs", StringComparison.OrdinalIgnoreCase)
                    || language.Equals("zht", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static string? FindSuspectMod(Exception exception)
    {
        try
        {
            IEnumerable<Assembly> stackAssemblies = new StackTrace(
                    exception,
                    fNeedFileInfo: false)
                .GetFrames()?
                .Select(frame => frame.GetMethod()?.DeclaringType?.Assembly)
                .Where(assembly => assembly != null)
                .Cast<Assembly>()
                .Distinct()
                ?? [];

            foreach (Assembly assembly in stackAssemblies)
            {
                Mod? owner = ModManager.Mods.FirstOrDefault(mod =>
                    mod.state == ModLoadState.Loaded
                    && mod.assemblies.Contains(assembly));
                if (owner != null)
                {
                    return owner.manifest?.id;
                }
            }
        }
        catch
        {
            // Attribution is optional; never hide the incident if it fails.
        }

        return null;
    }

    private static string? DependencyName(Exception exception)
    {
        string? value = exception switch
        {
            FileNotFoundException fileNotFound => fileNotFound.FileName,
            FileLoadException fileLoad => fileLoad.FileName,
            BadImageFormatException badImage => badImage.FileName,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string fileName = Path.GetFileName(value);
        int comma = fileName.IndexOf(',');
        return comma > 0 ? fileName[..comma] : fileName;
    }
}

[HarmonyPatch(
    typeof(NErrorPopup),
    nameof(NErrorPopup.Create),
    [typeof(NetErrorInfo)])]
internal static class NetworkErrorExplanationPatch
{
    private static bool Prefix(
        NetErrorInfo info,
        ref NErrorPopup? __result)
    {
        if (!FatalIncidentReporter.TryCreateNetworkPopup(info, out __result))
        {
            return true;
        }

        return false;
    }
}

[HarmonyPatch(
    typeof(NGame),
    nameof(NGame.ReturnToMainMenuWithInternalError))]
internal static class InternalErrorCapturePatch
{
    private static void Prefix(Exception e) =>
        FatalIncidentReporter.RememberInternalError(e);
}

[HarmonyPatch(
    typeof(NErrorPopup),
    nameof(NErrorPopup.Create),
    [
        typeof(LocString),
        typeof(LocString),
        typeof(LocString),
        typeof(bool)
    ])]
internal static class InternalErrorExplanationPatch
{
    private static bool Prefix(
        LocString title,
        ref NErrorPopup? __result)
    {
        if (!string.Equals(
                title.LocEntryKey,
                "INTERNAL_ERROR.title",
                StringComparison.Ordinal)
            || !FatalIncidentReporter.TryCreateInternalPopup(out __result))
        {
            return true;
        }

        return false;
    }
}

[HarmonyPatch(typeof(NErrorPopup), nameof(NErrorPopup._Ready))]
internal static class DiagnosticCopyButtonLabelPatch
{
    private static void Postfix(NErrorPopup __instance) =>
        FatalIncidentReporter.LabelCopyButton(__instance);
}

[HarmonyPatch(typeof(NErrorPopup), "OnReportBugButtonPressed")]
internal static class DiagnosticCopyButtonPatch
{
    private static bool Prefix(NErrorPopup __instance) =>
        !FatalIncidentReporter.TryCopyReport(__instance);
}

[HarmonyPatch(typeof(NGame), nameof(NGame._Input))]
internal static class ManualSnapshotHotkeyPatch
{
    private static void Postfix(InputEvent inputEvent)
    {
        if (inputEvent is InputEventKey
            {
                Pressed: true,
                Echo: false,
                CtrlPressed: true,
                Keycode: Key.F8
            })
        {
            FatalIncidentReporter.ShowManualSnapshot();
        }
    }
}
