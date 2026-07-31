# ADR 0003: 可选、非权威的诊断协议平面

- Status: proposed
- Date: 2026-07-29
- Relates to: ADR 0002
- Superseded in part by: ADR 0004

## Context

ADR 0002 正确移除了早期用于包一致性验证的自定义 `INetMessage`，因为一次性
广播存在迟到加入竞态，远端可变字符串可能在限长前被分配，且旧协议没有覆盖
全部 join/rejoin 路径。当前 Guard 通过 STS2 原生 gameplay Mod 列表完成
fail-closed 包校验，不应改变。

H3、H6、C1、C2、C9、D7、D10、F1–F5 等新功能需要交换少量、不影响游戏
权威状态的观测。只靠本地数据时，它们仍可降级工作，但无法提供跨玩家完整视图。

## Decision

允许未来引入一个与 Guard 完全分离的 Diagnostics Protocol，但只有满足全部
安全门槛后才可发布：

1. Guard 初始化、Mod-list 条目、ready/start gate 和错误处理不得引用该协议。
2. 协议失败只关闭对应 peer 的可选显示，不能阻止加入、准备、开局或原生重连。
3. 使用 major/minor、feature bitset、session ID、sequence 和固定 4096 字节
   payload 上限。
4. 必须在可变长度 payload 分配前强制上限；若当前 STS2 API 无法证明这一点，
   Release 中不启用协议。
5. sender identity 必须由原生连接提供，并验证为当前 lobby 成员。
6. 使用定向 Hello/ACK 和最多三次有界重试，不依赖一次性广播或消息顺序。
7. 所有消息限频、限长、可丢弃且幂等；未知 major 禁用，未知 minor/type 忽略。
8. 不允许自由文本、远端 URL、路径、日志、设置、存档、账号/端点、完整游戏
   状态或任何状态修改命令。
9. C9 只有全员逐 session 明确同意后才启用；成员变化立即撤销并清空缓存。
10. 包校验继续只走 ADR 0002 的原生通道。Diagnostics Protocol 永远不能替代、
    放宽或解释 Guard 的兼容结果。
11. 当前严格整包哈希保证诚实 peers 使用同一发布包；协议协商只处理同包内的
    本地开关/API 可用性和防御性未知消息。不同 Release 不承诺进入同一 lobby。

## Rejected alternatives

### 复用诊断消息作为包校验

拒绝。它重新引入 ADR 0002 已消除的消息顺序、覆盖范围和远端分配风险。

### 所有状态都广播

拒绝。完整状态包含手牌、牌库、RNG 和其他隐藏信息，扩大隐私与攻击面。

### 协议失败时阻止开局

拒绝。可选 UI 不应成为新的联机单点故障。

### 自动重试直到成功

拒绝。它会形成网络/日志风暴，并可能延迟原生握手。

### 在线下载动态规则或脚本

拒绝。规则随审查过的 Mod 包发布；远端内容不得成为可执行诊断逻辑。

## Consequences

- 本地 HUD 和诊断可以先交付，不等待自定义协议。
- 跨端能力在不兼容或丢包时显示 Partial/Unknown，原生联机继续。
- 需要额外的 parser fuzz、2/3/4-client、迟到加入、重连和洪泛测试。
- C9 等明确同意功能具有可验证的 session 生命周期。
- 该协议不是反作弊；恶意 peer 仍可伪造自己的声明，因此 UI 必须显示来源。

## Promotion criteria

本 ADR 只有在以下证据齐全后才能从 proposed 变为 accepted：

- 反序列化前限长的代码级证明和回归测试；
- 1,000,000 个生成 payload 无崩溃、无越界、无隐私泄漏；
- 2/3/4 客户端完整生命周期矩阵通过；
- 可选协议被强制关闭/破坏时 Guard 与原生联机矩阵仍通过；
- 安全审查明确确认无状态修改入口、无远端自由文本和无 Release 故障注入入口。

对应稳定门禁为 `CG-TST-CORE-CTR-001`、`CG-TST-CORE-SEC-002`、
`CG-TST-CORE-MP2-003`、`CG-TST-CORE-MP3-004`、
`CG-TST-CORE-MP4-005`、`CG-TST-CORE-UPG-006` 和涉及隐私状态的
`CG-TST-CORE-MP4-008/009`。无状态修改入口、无远端自由文本和 Release 无故障
注入入口由 `CG-TST-CORE-SEC-013` 与 `CG-TST-F8-SEC-002` 共同证明。任一没有
当前构建证据时，本 ADR 保持 proposed，Release 编译关闭 Diagnostics Protocol。
