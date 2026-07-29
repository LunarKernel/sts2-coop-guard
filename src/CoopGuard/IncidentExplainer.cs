using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace CoopGuard;

internal sealed record IncidentText(
    string Code,
    string Title,
    string Body,
    bool ShowReportBugButton);

internal static class IncidentExplainer
{
    public static IncidentText? ExplainNetwork(
        string reason,
        IReadOnlyList<string> missingOnHost,
        IReadOnlyList<string> missingOnLocal,
        string nativeDetail,
        IReadOnlyList<string> recentLogs,
        bool chinese)
    {
        bool fingerprintMissingOnHost = missingOnHost.Any(IsAggregateEntry);
        bool fingerprintMissingOnLocal = missingOnLocal.Any(IsAggregateEntry);
        bool failureMissingOnHost = missingOnHost.Any(IsFailureEntry);
        bool failureMissingOnLocal = missingOnLocal.Any(IsFailureEntry);
        bool incompatibleProtocol = missingOnHost
                .Concat(missingOnLocal)
                .Any(IsOtherProtocolEntry);
        string[] hostMods = SafeModNames(missingOnHost);
        string[] localMods = SafeModNames(missingOnLocal);
        string[] componentsMissingOnHost = ComponentNames(missingOnHost);
        string[] componentsMissingOnLocal = ComponentNames(missingOnLocal);

        return reason switch
        {
            "StateDivergence" => StateDivergence(chinese),
            "ModMismatch" => ModMismatch(
                fingerprintMissingOnHost,
                fingerprintMissingOnLocal,
                failureMissingOnHost,
                failureMissingOnLocal,
                incompatibleProtocol,
                hostMods,
                localMods,
                componentsMissingOnHost,
                componentsMissingOnLocal,
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
        string? detail,
        bool chinese)
    {
        string safeMod = SafeLabel(suspectMod);
        string safeDependency = SafeLabel(dependency);
        string evidence = ExceptionEvidence(
            exceptionType,
            safeMod,
            SafeLabel(detail),
            chinese);

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
            bool attributed = !string.IsNullOrEmpty(safeMod);
            return Build(
                "CG-MOD-API-INCOMPATIBLE",
                chinese,
                attributed
                    ? "Mod 与当前游戏 API 不兼容"
                    : "程序集或 API 版本不兼容",
                attributed
                    ? "Mod is incompatible with the current game API"
                    : "Assembly or API version mismatch",
                attributed
                    ? $"Mod“{safeMod}”的代码引用了当前版本中不存在或签名已经变化的类型、方法或成员。"
                    : "某个程序集引用了当前版本中不存在或签名已经变化的类型、方法或成员；现有证据无法确定它属于游戏本体还是第三方 Mod。",
                attributed
                    ? $"Mod \"{safeMod}\" referenced a type, method, or member that is missing or has a different signature in this game version."
                    : "An assembly referenced a type, method, or member that is missing or has a different signature. Current evidence cannot attribute it to the base game or a third-party Mod.",
                evidence,
                evidence,
                attributed
                    ? "更新该 Mod；若没有更新，双方停用它并重启游戏。不要继续原联机局。"
                    : "确认游戏与所有 Mod 均为同一最新版本；保存报告后通过无 Mod 新局隔离来源。",
                attributed
                    ? "Update that Mod. If no update exists, disable it on every peer and restart. Do not continue the affected run."
                    : "Make the game and every Mod the same current version, keep this report, then use a fresh no-Mod run to isolate the source.",
                attributed ? "已确认" : "根因已确认，来源未确定",
                attributed ? "Confirmed" : "Cause confirmed; source unknown",
                true);
        }

        if (IsDependencyException(exceptionType))
        {
            bool attributed = !string.IsNullOrEmpty(safeMod);
            string causeZh = string.IsNullOrEmpty(safeDependency)
                ? "所需程序集无法加载；文件可能缺失、损坏、版本错误或架构不兼容。"
                : $"所需程序集“{safeDependency}”无法加载；文件可能缺失、损坏、版本错误或架构不兼容。";
            string causeEn = string.IsNullOrEmpty(safeDependency)
                ? "A required assembly could not be loaded. It may be missing, damaged, the wrong version, or built for an incompatible architecture."
                : $"Required assembly \"{safeDependency}\" could not be loaded. It may be missing, damaged, the wrong version, or built for an incompatible architecture.";
            return Build(
                "CG-MOD-DEPENDENCY",
                chinese,
                attributed
                    ? "Mod 依赖缺失或无法加载"
                    : "程序集依赖缺失或无法加载",
                attributed
                    ? "Mod dependency is missing or cannot be loaded"
                    : "Assembly dependency is missing or cannot be loaded",
                causeZh,
                causeEn,
                evidence,
                evidence,
                attributed
                    ? $"重新安装或更新 Mod“{safeMod}”及其依赖，确认所有玩家使用相同版本后重启游戏。"
                    : "验证游戏文件，更新所有 Mod 与依赖；若仍出现，通过无 Mod 新局隔离来源。",
                attributed
                    ? $"Reinstall or update Mod \"{safeMod}\" and its dependencies, make every peer use the same versions, then restart."
                    : "Verify the game files and update every Mod and dependency. If it remains, use a fresh no-Mod run to isolate the source.",
                attributed ? "已确认" : "根因已确认，来源未确定",
                attributed ? "Confirmed" : "Cause confirmed; source unknown",
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
                "required multiplayer patches",
                "patch installation failed"))
        {
            code = "CG-GUARD-INITIALIZATION-FAILED";
            titleZh = "CoopGuard 核心补丁安装失败";
            titleEn = "CoopGuard core patch installation failed";
            causeZh = "CoopGuard 无法安全安装当前游戏版本所需的联机保护补丁，因此已经禁用本次联机校验结果。";
            causeEn = "CoopGuard could not safely install the multiplayer protection patches required by this game build, so this session's verification result was disabled.";
            actionZh = "退出游戏，更新 CoopGuard；若尚无兼容版本，请等待更新后再进行 Mod 联机。";
            actionEn = "Exit the game and update CoopGuard. If no compatible build exists yet, wait for an update before playing modded multiplayer.";
        }
        else if (ContainsAny(
                     detail,
                     "STS2 build is not supported",
                     "release metadata could not be verified"))
        {
            code = "CG-UNSUPPORTED-GAME-BUILD";
            titleZh = "当前游戏版本尚未通过 CoopGuard 验证";
            titleEn = "This game build is not yet verified by CoopGuard";
            causeZh = "当前 STS2 版本、commit 或主程序集哈希不在 CoopGuard 已测试的构建列表中。";
            causeEn = "The current STS2 version, commit, or main assembly hash is not in CoopGuard's tested build list.";
            actionZh = "不要继续 Mod 联机；更新 CoopGuard，或等待作者完成当前游戏版本的兼容性测试。";
            actionEn = "Do not continue modded multiplayer. Update CoopGuard or wait until this game build has been compatibility-tested.";
        }
        else if (ContainsAny(
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
            "快速文件新鲜度检查通过",
            "Quick file freshness check passed",
            "CoopGuard 已确认 Mod 路径、文件列表、大小和修改时间仍与完整启动指纹一致；本次快照没有重新读取全部文件内容。",
            "CoopGuard confirmed that Mod paths, file lists, sizes, and modification times still match the full startup fingerprint. This snapshot did not reread every file byte.",
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
        Version? assemblyVersion =
            typeof(IncidentExplainer).Assembly.GetName().Version;
        string coopGuardVersion = assemblyVersion == null
            ? "unavailable"
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
        string[] evidence = recentLogs
            .Reverse()
            .Take(8)
            .Reverse()
            .Select(Redact)
            .ToArray();
        return string.Join(
            '\n',
            "CoopGuard diagnostic report",
            $"CoopGuard version: {coopGuardVersion}",
            "Report format: 1",
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
            @"CoopGuard-(?:package|component)-v\d+-[A-Za-z0-9_-]+",
            "<package-fingerprint>",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        value = Regex.Replace(
            value,
            @"\b[A-Fa-f0-9]{64}\b",
            "<sha256>",
            RegexOptions.CultureInvariant);
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
        value = RedactIpv6(value);
        value = Regex.Replace(
            value,
            @"(?i)\b(?:authorization\s*[:=]\s*)?bearer\s+[A-Za-z0-9._~+/=-]+",
            "<credential>",
            RegexOptions.CultureInvariant);
        value = Regex.Replace(
            value,
            @"(?i)(?:[""']?(?:token|ticket|auth|authorization|api[_-]?key|secret|password)[""']?\s*[:=]\s*)[""']?[^,\s}""']+",
            "<credential>",
            RegexOptions.CultureInvariant);
        value = Regex.Replace(
            value,
            @"\\\\[^\s\\/:""<>|]+\\[^\s""<>|]+",
            "<path>",
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
        bool fingerprintMissingOnHost,
        bool fingerprintMissingOnLocal,
        bool failureMissingOnHost,
        bool failureMissingOnLocal,
        bool incompatibleProtocol,
        IReadOnlyList<string> missingOnHost,
        IReadOnlyList<string> missingOnLocal,
        IReadOnlyList<string> componentsMissingOnHost,
        IReadOnlyList<string> componentsMissingOnLocal,
        bool chinese)
    {
        if (fingerprintMissingOnHost != fingerprintMissingOnLocal
            || incompatibleProtocol)
        {
            return Build(
                "CG-COOPGUARD-MISSING",
                chinese,
                "有玩家未安装同版 CoopGuard",
                "A peer is missing the same CoopGuard version",
                "只有一侧提供了 CoopGuard 兼容性条目，说明某位玩家没有安装 CoopGuard，或使用了不兼容的协议版本。",
                "Only one side supplied a CoopGuard compatibility entry. A peer is missing CoopGuard or uses an incompatible protocol version.",
                incompatibleProtocol
                    ? "双方提供了不同协议版本的 CoopGuard 条目。"
                    : fingerprintMissingOnHost
                        ? "主机缺少本机提供的 CoopGuard 条目。"
                        : "本机缺少主机提供的 CoopGuard 条目。",
                incompatibleProtocol
                    ? "The peers supplied different CoopGuard protocol versions."
                    : fingerprintMissingOnHost
                        ? "The host is missing the CoopGuard entry supplied locally."
                        : "The local client is missing the CoopGuard entry supplied by the host.",
                "所有玩家安装同一个 CoopGuard 版本，完全退出并重启游戏后重新创建房间。",
                "Install the same CoopGuard version on every peer, fully exit and restart the game, then create a new lobby.",
                "已确认",
                "Confirmed",
                false);
        }

        if (failureMissingOnHost || failureMissingOnLocal)
        {
            string sideZh = (failureMissingOnHost, failureMissingOnLocal) switch
            {
                (true, true) => "主机和本机都返回了本地校验失败令牌。",
                (true, false) => "本机返回了本地校验失败令牌。",
                _ => "主机返回了本地校验失败令牌。"
            };
            string sideEn = (failureMissingOnHost, failureMissingOnLocal) switch
            {
                (true, true) => "Both the host and local client returned local verification failure tokens.",
                (true, false) => "The local client returned a local verification failure token.",
                _ => "The host returned a local verification failure token."
            };
            return Build(
                "CG-PEER-VERIFY-FAILED",
                chinese,
                "至少一名玩家的 CoopGuard 本地校验失败",
                "A peer failed CoopGuard local verification",
                "这不是已经确认的 Mod 包字节差异；至少一侧无法生成可信指纹，例如游戏版本未验证、补丁安装失败、Mod 加载失败或文件正在变化。",
                "This is not a confirmed package-byte difference. At least one side could not create a trustworthy fingerprint because of an unverified game build, patch failure, Mod load failure, or changing files.",
                sideZh,
                sideEn,
                "校验失败的一方查看自己的 CoopGuard 本地弹窗并按其提示修复；所有玩家重启后再创建房间。",
                "The failing peer should follow its local CoopGuard popup. Restart every peer before creating another lobby.",
                "已确认",
                "Confirmed",
                false);
        }

        bool packageBytesDiffer =
            fingerprintMissingOnHost && fingerprintMissingOnLocal;
        string[] differingComponents = componentsMissingOnHost
            .Intersect(componentsMissingOnLocal, StringComparer.Ordinal)
            .Take(5)
            .ToArray();
        string[] localOnlyComponents = componentsMissingOnHost
            .Except(componentsMissingOnLocal, StringComparer.Ordinal)
            .Take(5)
            .ToArray();
        string[] hostOnlyComponents = componentsMissingOnLocal
            .Except(componentsMissingOnHost, StringComparer.Ordinal)
            .Take(5)
            .ToArray();
        bool locatedComponents = differingComponents.Length > 0
            || localOnlyComponents.Length > 0
            || hostOnlyComponents.Length > 0;
        string evidenceZh;
        string evidenceEn;
        if (locatedComponents)
        {
            List<string> zh = [];
            List<string> en = [];
            if (differingComponents.Length > 0)
            {
                zh.Add("内容或版本不同：" + string.Join(", ", differingComponents));
                en.Add("Different content or version: " + string.Join(", ", differingComponents));
            }

            if (localOnlyComponents.Length > 0)
            {
                zh.Add("仅本机存在：" + string.Join(", ", localOnlyComponents));
                en.Add("Present only locally: " + string.Join(", ", localOnlyComponents));
            }

            if (hostOnlyComponents.Length > 0)
            {
                zh.Add("仅主机存在：" + string.Join(", ", hostOnlyComponents));
                en.Add("Present only on host: " + string.Join(", ", hostOnlyComponents));
            }

            evidenceZh = string.Join('\n', zh);
            evidenceEn = string.Join('\n', en);
        }
        else if (missingOnHost.Count > 0 || missingOnLocal.Count > 0)
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
            evidenceZh = "单个 Mod 包指纹一致，但整体指纹不同；差异可能来自 Mod 加载顺序或其他全局组成。";
            evidenceEn = "Individual Mod package fingerprints match, but the aggregate differs; Mod load order or another global component may differ.";
        }
        else
        {
            evidenceZh = "STS2 报告 NetError.ModMismatch，但没有提供可安全显示的差异列表。";
            evidenceEn = "STS2 reported NetError.ModMismatch without a safely displayable difference list.";
        }

        string causeZh = locatedComponents
            ? "CoopGuard 已通过双方的逐 Mod 包指纹定位到上述差异；这不是根据日志猜测的责任 Mod。"
            : packageBytesDiffer
            ? "双方整体 Mod 组成不同，但现有逐 Mod 指纹没有定位到单个包，不能可靠点名某个 Mod。"
            : "双方启用的 Mod 集合或声明版本不同。";
        string causeEn = locatedComponents
            ? "CoopGuard located the listed differences from per-Mod package fingerprints exchanged by both peers; this is not a guess from log proximity."
            : packageBytesDiffer
            ? "The aggregate Mod composition differs, but the per-Mod fingerprints do not identify one package, so no single Mod can be named reliably."
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
            IncidentText explained = ExplainException(
                exception,
                null,
                null,
                null,
                chinese);
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
        string safeDetail,
        bool chinese)
    {
        string type = SafeLabel(exceptionType);
        string detail = string.IsNullOrEmpty(safeDetail)
            ? string.Empty
            : chinese
                ? $"\n异常详情：{safeDetail}"
                : $"\nException detail: {safeDetail}";
        if (string.IsNullOrEmpty(safeMod))
        {
            return chinese
                ? $"异常类型：{type}{detail}\n没有找到可可靠归属的第三方 Mod 程序集。"
                : $"Exception type: {type}{detail}\nNo third-party Mod assembly could be attributed reliably.";
        }

        return chinese
            ? $"异常类型：{type}{detail}\n首个明确的第三方程序集属于：{safeMod}"
            : $"Exception type: {type}{detail}\nFirst clearly identified third-party assembly: {safeMod}";
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

    private static bool IsAggregateEntry(string value) =>
        value.StartsWith(
            FingerprintCodec.CompatibilityFamilyPrefix,
            StringComparison.Ordinal);

    private static bool IsGuardEntry(string value) =>
        IsAggregateEntry(value)
        || value.StartsWith(
            FingerprintCodec.ComponentFamilyPrefix,
            StringComparison.Ordinal);

    private static bool IsFailureEntry(string value) =>
        value.StartsWith(
            FingerprintCodec.CompatibilityPrefix + "error-",
            StringComparison.Ordinal);

    private static bool IsOtherProtocolEntry(string value) =>
        IsAggregateEntry(value)
        && !value.StartsWith(
            FingerprintCodec.CompatibilityPrefix,
            StringComparison.Ordinal);

    private static string[] ComponentNames(IEnumerable<string> values) =>
        values
            .Select(value =>
                FingerprintCodec.TryParseComponentEntry(
                    value,
                    out string modId)
                    ? SafeLabel(modId)
                    : string.Empty)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string[] SafeModNames(IEnumerable<string> values) =>
        values
            .Where(value => !IsGuardEntry(value))
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

        string safe = new(
            Redact(value)
                .Select(character =>
                    char.IsControl(character) ? ' ' : character)
                .ToArray());
        return safe.Length <= 100 ? safe : safe[..97] + "...";
    }

    private static string RedactIpv6(string value) =>
        Regex.Replace(
            value,
            @"(?<![A-Za-z0-9])(?:\[[0-9A-Fa-f:.%]+\](?::\d+)?|[0-9A-Fa-f:%]{2,})(?![A-Za-z0-9])",
            match =>
            {
                string candidate = match.Value;
                if (candidate.StartsWith("[", StringComparison.Ordinal))
                {
                    int close = candidate.IndexOf(']');
                    candidate = close > 0
                        ? candidate[1..close]
                        : candidate;
                }

                int zone = candidate.IndexOf('%');
                if (zone > 0)
                {
                    candidate = candidate[..zone];
                }

                return candidate.Contains(':')
                    && IPAddress.TryParse(candidate, out IPAddress? address)
                    && address.AddressFamily
                        == System.Net.Sockets.AddressFamily.InterNetworkV6
                    ? "<ip>"
                    : match.Value;
            },
            RegexOptions.CultureInvariant);
}
