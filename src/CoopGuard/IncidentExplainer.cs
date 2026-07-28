using System.Globalization;
using System.Text.RegularExpressions;

namespace CoopGuard;

internal sealed record IncidentText(
    string Code,
    string Title,
    string Body,
    bool ShowReportBugButton);

internal static class IncidentExplainer
{
    private const string FingerprintPrefix = "CoopGuard-package-v3-";

    public static IncidentText? ExplainNetwork(
        string reason,
        IReadOnlyList<string> missingOnHost,
        IReadOnlyList<string> missingOnLocal,
        string nativeDetail,
        IReadOnlyList<string> recentLogs,
        bool chinese)
    {
        bool packageBytesDiffer = missingOnHost.Any(IsFingerprintEntry)
            || missingOnLocal.Any(IsFingerprintEntry);
        string[] hostMods = SafeModNames(missingOnHost);
        string[] localMods = SafeModNames(missingOnLocal);

        return reason switch
        {
            "StateDivergence" => StateDivergence(chinese),
            "ModMismatch" => ModMismatch(
                packageBytesDiffer,
                hostMods,
                localMods,
                chinese),
            "Timeout" => Timeout(nativeDetail, chinese),
            "HandshakeTimeout" => HandshakeTimeout(chinese),
            "VersionMismatch" => VersionMismatch(nativeDetail, chinese),
            "NoInternet" => NoInternet(chinese),
            "SecureConnectionFailed" => SecureConnection(chinese),
            "InternalError" or "UnknownNetworkError" =>
                NetworkInternalError(recentLogs, nativeDetail, chinese),
            "FailedToHost" => FailedToHost(nativeDetail, chinese),
            "RateLimited" => RateLimited(chinese),
            "TryAgainLater" => TryAgainLater(chinese),
            _ => null
        };
    }

    public static IncidentText ExplainException(
        string exceptionType,
        string? suspectMod,
        string? dependency,
        bool chinese)
    {
        string safeMod = SafeLabel(suspectMod);
        string safeDependency = SafeLabel(dependency);
        string evidence = ExceptionEvidence(exceptionType, safeMod, chinese);

        if (exceptionType.Contains("StateDivergence", StringComparison.Ordinal))
        {
            return StateDivergence(chinese);
        }

        if (exceptionType.Contains("Softlock", StringComparison.Ordinal))
        {
            return Build(
                "CG-SOFTLOCK",
                chinese,
                "检测到游戏软锁",
                "Game soft lock detected",
                "游戏明确报告逻辑流程无法继续。这不是普通网络掉线。",
                "The game explicitly reported that its logic could not continue. This is not a normal network disconnect.",
                evidence,
                evidence,
                "保存本机和队友同一时间段的日志；重启游戏，并从最近一次稳定状态重新开始。",
                "Keep logs from every peer for the same time window, restart the game, and resume from the latest known-good state.",
                "已确认",
                "Confirmed",
                true);
        }

        if (IsApiCompatibilityException(exceptionType))
        {
            return Build(
                "CG-MOD-API-INCOMPATIBLE",
                chinese,
                "Mod 与当前游戏 API 不兼容",
                "Mod is incompatible with the current game API",
                "某段 Mod 代码引用了当前版本中不存在或签名已经变化的类型、方法或成员。",
                "Mod code referenced a type, method, or member that is missing or has a different signature in this game version.",
                evidence,
                evidence,
                "更新相关 Mod；若没有更新，停用它并重启游戏。不要继续原联机局。",
                "Update the related Mod. If no update exists, disable it and restart the game. Do not continue the affected multiplayer run.",
                "已确认",
                "Confirmed",
                true);
        }

        if (IsDependencyException(exceptionType))
        {
            string causeZh = string.IsNullOrEmpty(safeDependency)
                ? "Mod 所需的程序集无法加载；文件可能缺失、损坏、版本错误或架构不兼容。"
                : $"Mod 所需的程序集“{safeDependency}”无法加载；文件可能缺失、损坏、版本错误或架构不兼容。";
            string causeEn = string.IsNullOrEmpty(safeDependency)
                ? "A required Mod assembly could not be loaded. It may be missing, damaged, the wrong version, or built for an incompatible architecture."
                : $"Required Mod assembly \"{safeDependency}\" could not be loaded. It may be missing, damaged, the wrong version, or built for an incompatible architecture.";
            return Build(
                "CG-MOD-DEPENDENCY",
                chinese,
                "Mod 依赖缺失或无法加载",
                "Mod dependency is missing or cannot be loaded",
                causeZh,
                causeEn,
                evidence,
                evidence,
                "重新安装或更新相关 Mod 及其依赖，确认所有玩家使用相同版本后重启游戏。",
                "Reinstall or update the related Mod and its dependencies, make every peer use the same versions, then restart the game.",
                "已确认",
                "Confirmed",
                true);
        }

        if (exceptionType.Contains("Harmony", StringComparison.OrdinalIgnoreCase))
        {
            return Build(
                "CG-HARMONY-PATCH",
                chinese,
                "Mod 补丁无法应用",
                "Mod patch could not be applied",
                "Harmony 无法把 Mod 补丁应用到当前游戏方法，通常表示游戏更新改变了目标方法，或多个 Mod 的补丁互相冲突。",
                "Harmony could not apply a Mod patch to the current game method. The game may have changed that method, or multiple Mod patches may conflict.",
                evidence,
                evidence,
                "更新相关 Mod；若仍出现，逐个停用最近更新或修改同一功能的 Mod，并在每次调整后重启。",
                "Update the related Mod. If the error remains, disable recently updated Mods or Mods changing the same feature one at a time, restarting after each change.",
                "已确认",
                "Confirmed",
                true);
        }

        return Build(
            "CG-INTERNAL-ERROR",
            chinese,
            "游戏内部错误导致联机无法继续",
            "Internal error stopped the multiplayer run",
            string.IsNullOrEmpty(safeMod)
                ? "游戏或某个 Mod 抛出了未处理异常；现有证据不能唯一确定责任组件。"
                : $"未处理异常发生在 Mod“{safeMod}”的代码路径中，但这只能定位故障位置，不能单独证明它是最初根因。",
            string.IsNullOrEmpty(safeMod)
                ? "The game or a Mod threw an unhandled exception. Current evidence cannot identify one responsible component."
                : $"The unhandled exception occurred in Mod \"{safeMod}\" code. This identifies the failure location but does not by itself prove the original cause.",
            evidence,
            evidence,
            "保存双方日志并重启游戏；若问题可复现，先更新该 Mod，再通过逐个停用 Mod 隔离。",
            "Keep logs from both peers and restart the game. If reproducible, update the named Mod first, then isolate the issue by disabling Mods one at a time.",
            string.IsNullOrEmpty(safeMod) ? "证据不足" : "高度疑似",
            string.IsNullOrEmpty(safeMod) ? "Inconclusive" : "Strongly supported",
            true);
    }

    public static IncidentText ExplainLocalVerification(
        string detail,
        bool chinese)
    {
        string safeDetail = Redact(detail);
        string code;
        string titleZh;
        string titleEn;
        string causeZh;
        string causeEn;
        string actionZh;
        string actionEn;

        if (ContainsAny(
                detail,
                "changed after startup",
                "changed while",
                "changed before",
                "restart"))
        {
            code = "CG-LOCAL-FILES-CHANGED";
            titleZh = "Mod 文件在启动后发生变化";
            titleEn = "Mod files changed after startup";
            causeZh = "CoopGuard 检测到已经校验过的 Mod、程序集或 PCK 在本次游戏运行期间发生变化，旧指纹已经失效。";
            causeEn = "CoopGuard detected that a verified Mod, assembly, or PCK changed during this game session, invalidating the previous fingerprint.";
            actionZh = "退出游戏，确认创意工坊更新和本地文件操作已经完成，然后重新启动。";
            actionEn = "Exit the game, let Workshop updates and local file operations finish, then restart.";
        }
        else if (ContainsAny(detail, "reparse point", "outside its package root"))
        {
            code = "CG-LOCAL-UNSAFE-PATH";
            titleZh = "Mod 包含无法安全校验的路径";
            titleEn = "Mod contains a path that cannot be verified safely";
            causeZh = "Mod 包含符号链接、联接点或指向包目录之外的程序集；CoopGuard 无法保证双方实际读取相同文件。";
            causeEn = "A Mod contains a symbolic link, junction, or assembly outside its package directory, so CoopGuard cannot prove both peers read the same files.";
            actionZh = "删除该 Mod 的残留目录并从可信来源重新安装，避免使用链接目录。";
            actionEn = "Remove the leftover Mod directory and reinstall it from a trusted source without linked directories.";
        }
        else if (ContainsAny(detail, "safety limit", "limit was exceeded"))
        {
            code = "CG-LOCAL-SAFETY-LIMIT";
            titleZh = "Mod 包超过安全校验上限";
            titleEn = "Mod package exceeded a verification safety limit";
            causeZh = "已加载 Mod 的文件数量、总大小或校验描述超过 CoopGuard 的安全上限，因此校验被拒绝。";
            causeEn = "The loaded Mods exceeded CoopGuard's safe file-count, byte, or fingerprint-description limit, so verification was rejected.";
            actionZh = "检查异常大的 Mod 包、缓存或生成文件；清理后重启游戏。";
            actionEn = "Check for unusually large Mod packages, caches, or generated files, clean them, then restart the game.";
        }
        else if (ContainsAny(detail, "manifest", "declared PCK", "declared DLL"))
        {
            code = "CG-LOCAL-BROKEN-PACKAGE";
            titleZh = "Mod 包不完整或清单错误";
            titleEn = "Mod package is incomplete or has an invalid manifest";
            causeZh = "某个 Mod 的清单、声明的 DLL 或 PCK 与磁盘上的实际文件不一致。";
            causeEn = "A Mod manifest, declared DLL, or PCK does not match the files present on disk.";
            actionZh = "重新安装弹窗证据中指出的 Mod，并确认旧版本文件已经清除。";
            actionEn = "Reinstall the Mod named in the evidence and make sure files from older versions are removed.";
        }
        else if (ContainsAny(detail, "Mod state Failed", "Mod load error"))
        {
            code = "CG-LOCAL-MOD-LOAD-FAILED";
            titleZh = "游戏报告 Mod 加载失败";
            titleEn = "The game reported a Mod load failure";
            causeZh = "至少一个启用的 Mod 没有成功完成加载，因此玩家实际运行的代码集合不可靠。";
            causeEn = "At least one enabled Mod did not finish loading, so the effective code set cannot be verified reliably.";
            actionZh = "查看证据中的 Mod，补齐依赖或更新版本；确认它能正常加载后重启游戏。";
            actionEn = "Check the Mod named in the evidence, install its dependencies or update it, then restart after it loads successfully.";
        }
        else
        {
            code = "CG-LOCAL-VERIFY-FAILED";
            titleZh = "本机 Mod 校验失败";
            titleEn = "Local Mod verification failed";
            causeZh = "CoopGuard 无法生成可信的本机 Mod 指纹，因此按安全策略阻止联机继续。";
            causeEn = "CoopGuard could not produce a trustworthy local Mod fingerprint, so multiplayer was blocked safely.";
            actionZh = "按证据修复对应 Mod，确认文件稳定后重启游戏。";
            actionEn = "Fix the Mod identified by the evidence, make sure its files are stable, then restart the game.";
        }

        return Build(
            code,
            chinese,
            titleZh,
            titleEn,
            causeZh,
            causeEn,
            safeDetail,
            safeDetail,
            actionZh,
            actionEn,
            "已确认",
            "Confirmed",
            false);
    }

    public static IncidentText ExplainHealthy(
        int modCount,
        int fileCount,
        long totalBytes,
        bool chinese) =>
        Build(
            "CG-HEALTHY",
            chinese,
            "联机校验已通过",
            "Multiplayer verification passed",
            "CoopGuard 已确认本机当前加载的 Mod 包与启动时校验结果一致。",
            "CoopGuard confirmed that the currently loaded local Mod packages still match the startup verification.",
            $"Mods: {modCount}; files: {fileCount}; bytes: {totalBytes.ToString(CultureInfo.InvariantCulture)}",
            $"Mods: {modCount}; files: {fileCount}; bytes: {totalBytes.ToString(CultureInfo.InvariantCulture)}",
            "可继续联机。若疑似卡死，可按 Ctrl+F8 生成一份当前诊断快照。",
            "Multiplayer may continue. If the run appears stuck, press Ctrl+F8 to create a current diagnostic snapshot.",
            "已确认",
            "Confirmed",
            true);

    public static string BuildReport(
        IncidentText incident,
        string gameVersion,
        string runtimeState,
        string packageHealth,
        IReadOnlyList<string> recentLogs,
        DateTimeOffset capturedAt)
    {
        string[] evidence = recentLogs
            .Reverse()
            .Take(8)
            .Reverse()
            .Select(Redact)
            .ToArray();
        return string.Join(
            '\n',
            "CoopGuard diagnostic report v0.3.1",
            $"Captured UTC: {capturedAt.UtcDateTime:O}",
            $"Game version: {Redact(gameVersion)}",
            $"Runtime state: {Redact(runtimeState)}",
            $"Package health: {Redact(packageHealth)}",
            string.Empty,
            $"[{incident.Code}] {Redact(incident.Title)}",
            Redact(incident.Body),
            string.Empty,
            "Recent warnings/errors (redacted):",
            evidence.Length == 0 ? "<none>" : string.Join('\n', evidence));
    }

    public static string Redact(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "<none>";
        }

        string value = input.Replace('\r', ' ').Replace('\0', ' ');
        value = Regex.Replace(
            value,
            @"\b7656119\d{10}\b",
            "<steam-id>",
            RegexOptions.CultureInvariant);
        value = Regex.Replace(
            value,
            @"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?(?![\d.])",
            "<ip>",
            RegexOptions.CultureInvariant);
        value = Regex.Replace(
            value,
            @"(?i)\b(?:token|ticket|auth|key)=\S+",
            "<credential>",
            RegexOptions.CultureInvariant);
        value = Regex.Replace(
            value,
            @"(?i)(?:[A-Z]:\\|/)(?:[^\s""']+)",
            "<path>",
            RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"[ \t]+", " ", RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"\n{3,}", "\n\n", RegexOptions.CultureInvariant);
        value = value.Trim();
        return value.Length <= 900 ? value : value[..897] + "...";
    }

    private static IncidentText StateDivergence(bool chinese) =>
        Build(
            "CG-STATE-DIVERGENCE",
            chinese,
            "联机状态不同步",
            "Multiplayer state divergence",
            "主机与客户端在同一游戏动作后的状态校验不同，旧局已经产生确定性分叉。这不是普通掉线。",
            "The host and client produced different state checksums after the same game action. The existing run has deterministically diverged; this is not a normal disconnect.",
            "STS2 报告 NetError.StateDivergence。动作名或最后出现的卡牌只能定位触发时刻，不能单独证明责任 Mod。",
            "STS2 reported NetError.StateDivergence. The last action or card only locates when validation failed; it does not by itself identify the responsible Mod.",
            "不要反复重连旧局。保存双方同一时间段日志，重启游戏，确认 Mod 文件一致后新开一局；若复现，再逐个停用影响战斗或状态的 Mod。",
            "Do not repeatedly reconnect to the diverged run. Keep matching log windows, restart, verify identical Mod files, and start a new run. If it repeats, isolate Mods that alter combat or state.",
            "已确认",
            "Confirmed",
            true);

    private static IncidentText ModMismatch(
        bool packageBytesDiffer,
        IReadOnlyList<string> missingOnHost,
        IReadOnlyList<string> missingOnLocal,
        bool chinese)
    {
        string evidenceZh;
        string evidenceEn;
        if (missingOnHost.Count > 0 || missingOnLocal.Count > 0)
        {
            string host = missingOnHost.Count == 0
                ? "无"
                : string.Join(", ", missingOnHost);
            string local = missingOnLocal.Count == 0
                ? "无"
                : string.Join(", ", missingOnLocal);
            evidenceZh = $"主机缺少：{host}\n本机缺少：{local}";
            evidenceEn = $"Missing on host: {(missingOnHost.Count == 0 ? "none" : host)}\nMissing locally: {(missingOnLocal.Count == 0 ? "none" : local)}";
            if (packageBytesDiffer)
            {
                evidenceZh += "\nCoopGuard 同时检测到有效包内容指纹不同。";
                evidenceEn += "\nCoopGuard also detected different effective package fingerprints.";
            }
        }
        else if (packageBytesDiffer)
        {
            evidenceZh = "Mod 名称和声明版本可能相同，但 CoopGuard 的有效包内容指纹不同。";
            evidenceEn = "Mod names and declared versions may match, but CoopGuard found different effective package fingerprints.";
        }
        else
        {
            evidenceZh = "STS2 报告 NetError.ModMismatch，但没有提供可安全显示的差异列表。";
            evidenceEn = "STS2 reported NetError.ModMismatch without a safely displayable difference list.";
        }

        string causeZh = packageBytesDiffer
            ? "双方实际加载的 Mod 程序集、PCK 或其他包文件内容不同。常见原因是创意工坊更新不完整、旧文件残留或本地修改。"
            : "双方启用的 Mod 集合或声明版本不同。";
        string causeEn = packageBytesDiffer
            ? "The peers loaded different Mod assembly, PCK, or package bytes. Common causes are an incomplete Workshop update, leftover old files, or local modifications."
            : "The peers have different enabled Mod sets or declared versions.";

        return Build(
            "CG-MOD-MISMATCH",
            chinese,
            "联机 Mod 不一致",
            "Multiplayer Mods do not match",
            causeZh,
            causeEn,
            evidenceZh,
            evidenceEn,
            "双方退出游戏，重新安装或更新差异 Mod，清除旧版本残留，并在重新启动后新开一局。",
            "Both peers should exit, reinstall or update the differing Mods, remove old leftovers, restart, and begin a new run.",
            "已确认",
            "Confirmed",
            false);
    }

    private static IncidentText Timeout(string detail, bool chinese) =>
        Build(
            "CG-NET-TIMEOUT",
            chinese,
            "Steam 联机连接超时",
            "Steam multiplayer connection timed out",
            "底层联机连接在超时时间内没有收到足够的数据。这说明传输链路中断，但不能仅凭本机日志确定是本机、对方、Steam 中继还是网络运营商。",
            "The transport connection did not receive enough data before its timeout. This proves the link failed, but the local log alone cannot identify whether the cause was this PC, the peer, Steam relay, or an ISP.",
            Redact(detail),
            Redact(detail),
            "双方检查网络稳定性和 Steam 状态后重试；若反复发生，比较双方同一时间段日志，不要先删除 Mod 或重连已分叉的旧局。",
            "Check network stability and Steam status on both peers, then retry. If it repeats, compare the same log window from both peers instead of first removing Mods or reconnecting a diverged run.",
            "已确认",
            "Confirmed",
            false);

    private static IncidentText HandshakeTimeout(bool chinese) =>
        Build(
            "CG-HANDSHAKE-TIMEOUT",
            chinese,
            "联机初始化握手超时",
            "Multiplayer initialization handshake timed out",
            "客户端已连接到底层网络，但没有在游戏规定时间内完成大厅初始化响应；它与互联网连接超时不是同一种错误。",
            "The client reached the transport layer but did not finish the lobby initialization response in time. This is different from an internet transport timeout.",
            "STS2 报告 NetError.HandshakeTimeout。",
            "STS2 reported NetError.HandshakeTimeout.",
            "确认双方游戏版本和 Mod 完全一致，处理 Mod 加载错误并重启；若 Mod 很多，同时检查较慢电脑的加载时间和网络。",
            "Make game and Mod versions identical, fix Mod load errors, and restart. With many Mods, also check initialization time on the slower PC and its network.",
            "已确认",
            "Confirmed",
            false);

    private static IncidentText VersionMismatch(string detail, bool chinese) =>
        Build(
            "CG-GAME-VERSION-MISMATCH",
            chinese,
            "游戏版本或数据模型不一致",
            "Game version or data model mismatch",
            "主机与本机的游戏版本、分支或原生数据模型校验值不同。",
            "The host and local game differ in version, branch, or native data-model checksum.",
            Redact(detail),
            Redact(detail),
            "双方更新到同一 Steam 分支和游戏版本，验证游戏文件后重启。",
            "Use the same Steam branch and game version on every peer, verify game files, then restart.",
            "已确认",
            "Confirmed",
            false);

    private static IncidentText NoInternet(bool chinese) =>
        Build(
            "CG-NET-OFFLINE",
            chinese,
            "本机当前无法使用联机服务",
            "Online service is unavailable on this PC",
            "游戏没有可用的互联网或平台联机连接。",
            "The game has no usable internet or platform connection.",
            "STS2 报告 NetError.NoInternet。",
            "STS2 reported NetError.NoInternet.",
            "检查网络、Steam 在线状态、防火墙和系统时间，然后重新启动 Steam 与游戏。",
            "Check the network, Steam online status, firewall, and system clock, then restart Steam and the game.",
            "已确认",
            "Confirmed",
            false);

    private static IncidentText SecureConnection(bool chinese) =>
        Build(
            "CG-NET-SECURE-CONNECTION",
            chinese,
            "Steam 安全连接建立失败",
            "Steam secure connection failed",
            "Steam 无法建立或验证安全联机通道；这通常属于平台、证书、系统时间或网络过滤问题。",
            "Steam could not establish or validate a secure multiplayer channel. This usually involves the platform, certificates, system time, or network filtering.",
            "STS2 报告 NetError.SecureConnectionFailed。",
            "STS2 reported NetError.SecureConnectionFailed.",
            "确认 Steam 已登录，校准系统时间，检查防火墙/VPN/代理，然后重启 Steam。",
            "Confirm Steam is signed in, correct the system clock, check firewall/VPN/proxy settings, then restart Steam.",
            "已确认",
            "Confirmed",
            false);

    private static IncidentText NetworkInternalError(
        IReadOnlyList<string> logs,
        string detail,
        bool chinese)
    {
        string? exception = FindKnownException(logs);
        if (exception != null)
        {
            IncidentText explained = ExplainException(exception, null, null, chinese);
            string evidence = chinese
                ? $"网络终止前的最近日志包含 {exception}；这是高概率关联证据，不等同于已经证明唯一责任 Mod。\n{Redact(detail)}"
                : $"Recent logs before the network failure contain {exception}. This is strongly related evidence, not proof of one uniquely responsible Mod.\n{Redact(detail)}";
            return explained with
            {
                Code = explained.Code + "-NET",
                Body = ReplaceEvidence(explained.Body, evidence, chinese)
            };
        }

        return Build(
            "CG-NET-INTERNAL",
            chinese,
            "联机内部错误",
            "Internal multiplayer error",
            "游戏或平台联机层报告内部错误，但当前结构化信息不足以确定更具体根因。",
            "The game or platform multiplayer layer reported an internal error, but the structured evidence is insufficient for a more specific cause.",
            Redact(detail),
            Redact(detail),
            "保存双方同一时间段日志并重启；先更新游戏和 Mod，再通过无 Mod 新局判断错误来自游戏、网络还是 Mod。",
            "Keep the same log window from both peers and restart. Update the game and Mods, then use a fresh no-Mod run to separate game/network failures from Mod failures.",
            "证据不足",
            "Inconclusive",
            true);
    }

    private static IncidentText FailedToHost(string detail, bool chinese) =>
        Build(
            "CG-NET-HOST-FAILED",
            chinese,
            "无法创建联机房间",
            "Could not create a multiplayer lobby",
            "Steam 或本地网络后端没有成功创建主机房间。",
            "Steam or the local network backend could not create the host lobby.",
            Redact(detail),
            Redact(detail),
            "确认 Steam 在线、防火墙允许游戏通信且没有失效的旧房间，然后重启 Steam 与游戏后重试。",
            "Confirm Steam is online, allow the game through the firewall, clear any stale lobby by restarting Steam and the game, then retry.",
            "已确认",
            "Confirmed",
            false);

    private static IncidentText RateLimited(bool chinese) =>
        Build(
            "CG-NET-RATE-LIMITED",
            chinese,
            "Steam 请求过于频繁",
            "Steam request rate limit reached",
            "短时间内创建或加入房间的请求过多，平台暂时限制了请求。",
            "Too many lobby create or join requests were made in a short period, so the platform temporarily limited them.",
            "STS2 报告 NetError.RateLimited。",
            "STS2 reported NetError.RateLimited.",
            "停止重复点击或重连，等待几分钟后再试。",
            "Stop repeated join/reconnect attempts and wait a few minutes before trying again.",
            "已确认",
            "Confirmed",
            false);

    private static IncidentText TryAgainLater(bool chinese) =>
        Build(
            "CG-NET-TRY-LATER",
            chinese,
            "Steam 联机服务暂时不可用",
            "Steam multiplayer service is temporarily unavailable",
            "平台要求稍后重试，通常是临时服务或中继状态问题。",
            "The platform asked the game to retry later, usually because of a temporary service or relay issue.",
            "STS2 报告 NetError.TryAgainLater。",
            "STS2 reported NetError.TryAgainLater.",
            "等待几分钟并检查 Steam 服务状态后重试，不需要先修改 Mod。",
            "Wait a few minutes and check Steam service status before retrying. Do not change Mods first.",
            "已确认",
            "Confirmed",
            false);

    private static IncidentText Build(
        string code,
        bool chinese,
        string titleZh,
        string titleEn,
        string causeZh,
        string causeEn,
        string evidenceZh,
        string evidenceEn,
        string actionZh,
        string actionEn,
        string confidenceZh,
        string confidenceEn,
        bool showReportBugButton)
    {
        string title = chinese
            ? $"CoopGuard 联机诊断：{titleZh}"
            : $"CoopGuard Multiplayer Diagnosis: {titleEn}";
        string body = chinese
            ? $"错误编号：{code}\n可信度：{confidenceZh}\n\n根因：\n{causeZh}\n\n证据：\n{evidenceZh}\n\n建议：\n{actionZh}"
            : $"Error code: {code}\nConfidence: {confidenceEn}\n\nRoot cause:\n{causeEn}\n\nEvidence:\n{evidenceEn}\n\nWhat to do:\n{actionEn}";
        return new IncidentText(code, title, body, showReportBugButton);
    }

    private static string ReplaceEvidence(
        string body,
        string evidence,
        bool chinese)
    {
        string evidenceHeader = chinese ? "\n\n证据：\n" : "\n\nEvidence:\n";
        string actionHeader = chinese ? "\n\n建议：\n" : "\n\nWhat to do:\n";
        int start = body.IndexOf(evidenceHeader, StringComparison.Ordinal);
        int end = body.IndexOf(actionHeader, start + evidenceHeader.Length, StringComparison.Ordinal);
        if (start < 0 || end < 0)
        {
            return body;
        }

        return body[..(start + evidenceHeader.Length)]
            + evidence
            + body[end..];
    }

    private static string ExceptionEvidence(
        string exceptionType,
        string safeMod,
        bool chinese)
    {
        string type = SafeLabel(exceptionType);
        if (string.IsNullOrEmpty(safeMod))
        {
            return chinese
                ? $"异常类型：{type}\n没有找到可可靠归属的第三方 Mod 程序集。"
                : $"Exception type: {type}\nNo third-party Mod assembly could be attributed reliably.";
        }

        return chinese
            ? $"异常类型：{type}\n首个明确的第三方程序集属于：{safeMod}"
            : $"Exception type: {type}\nFirst clearly identified third-party assembly: {safeMod}";
    }

    private static string? FindKnownException(IReadOnlyList<string> logs)
    {
        string[] known =
        [
            "MissingMethodException",
            "TypeLoadException",
            "ReflectionTypeLoadException",
            "MissingMemberException",
            "FileNotFoundException",
            "FileLoadException",
            "BadImageFormatException",
            "HarmonyException",
            "NullReferenceException",
            "InvalidOperationException"
        ];

        foreach (string line in logs.Reverse().Take(40))
        {
            string? found = known.FirstOrDefault(name =>
                line.Contains(name, StringComparison.Ordinal));
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static bool IsApiCompatibilityException(string type) =>
        type.Contains("MissingMethod", StringComparison.Ordinal)
        || type.Contains("TypeLoad", StringComparison.Ordinal)
        || type.Contains("ReflectionTypeLoad", StringComparison.Ordinal)
        || type.Contains("MissingMember", StringComparison.Ordinal);

    private static bool IsDependencyException(string type) =>
        type.Contains("FileNotFound", StringComparison.Ordinal)
        || type.Contains("FileLoad", StringComparison.Ordinal)
        || type.Contains("BadImageFormat", StringComparison.Ordinal);

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle =>
            value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static bool IsFingerprintEntry(string value) =>
        value.StartsWith(FingerprintPrefix, StringComparison.Ordinal);

    private static string[] SafeModNames(IEnumerable<string> values) =>
        values
            .Where(value => !IsFingerprintEntry(value))
            .Select(SafeLabel)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToArray();

    private static string SafeLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string safe = Redact(value).Replace('\n', ' ');
        return safe.Length <= 100 ? safe : safe[..97] + "...";
    }
}
