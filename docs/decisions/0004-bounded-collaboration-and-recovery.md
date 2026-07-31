# ADR 0004: 有界协作、RNG 观察与房主恢复

- Status: accepted
- Date: 2026-07-31
- Supersedes in part: ADR 0003 items 2, 8 and its no-state-change promotion rule

## Context

BetterCoop v0.6.0 增加三种旧安全合同没有允许的能力：

1. 玩家向当前 lobby 发送自由选择的文本；
2. 本机显示 run seed，并比较已经消费的 RNG 流摘要；
3. 房主回到本次 run 中已经由 STS2 原生保存的房间 checkpoint。

继续保留旧禁止条款会让实现依赖未记录的例外。完全放开远端内容或状态修改又会
破坏 Guard 的安全边界。

## Decision

只批准以下最窄例外：

1. C11 允许有界、严格 UTF-8、NFC 的可打印纯文本。房主从原生连接重写身份；
   文本永不解释为命令、路径、URL、BBCode、Markdown 或代码，也不持久化。
2. H13 继续受 C9 的逐 session 全员同意、roster epoch 和立即撤销约束。
3. F9 可以读取本机公开 run seed，并旁路记录已经发生的 RNG 调用。它不得额外
   调用、克隆、推进或预测 RNG；网络只传 session HMAC、计数和摘要。
4. R1 是唯一可以替换 active multiplayer save 的模块。它只能归档 STS2 已经
   成功写出的原生 save，只能由原生房主经本地双重确认发起，并要求当前全员 ACK。
5. R1 不通过网络发送 save，不热改运行对象图、动作队列或 RNG。替换前必须保留
   emergency backup；验证失败只能整体中止或关闭 lobby，禁止 split-brain。
6. Guard 的原生 gameplay-Mod-list 路径保持独立。可选功能故障不能放宽包校验；
   第 5 名玩家开始需要额外的 fail-closed 高容量能力证明。
7. 所有新增消息在可变分配前限长、限数、限频，并绑定 session、roster epoch、
   sequence 和当前原生 peer。

## Consequences

- ADR 0003 的有界 envelope、身份、重放和 C9 同意规则继续有效。
- ADR 0003 对自由文本的全面禁止改为本 ADR 的有界纯文本规则。
- “不得修改 save”仍适用于 Guard、诊断和取证；仅 R1 的审计事务例外。
- “不得展示 seed”改为本机主动展开；未来 RNG 和原始 peer RNG 数据仍禁止。
- R1 和大于 4 人 loaded-run 没有对应人数实测时必须显示 Unsupported。

## Promotion criteria

- C11 parser fuzz 1,000,000 次且无执行入口、正文持久化或身份伪造；
- F9 spy 证明 BetterCoop 对 STS2 RNG 和 checksum 的额外调用数为 0；
- R1 在每个持久化故障点都保留有效 active save 或 emergency backup；
- 5/8 人 loaded-run 与回溯均完成第二个玩家回合；
- Release 包不含 fault handler、原始聊天、原始 peer seed 或 save 传输入口。
