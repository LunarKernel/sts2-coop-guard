# BetterCoop v0.6.0 联机增强技术方案

- 文档状态：Implemented RC（发布级多人/Steam 门禁仍按验收表记录）
- 目标版本：BetterCoop v0.6.0
- 基线：BetterCoop v0.5.0 / Guard Protocol 5 / Diagnostics Protocol 1
- 游戏基线：Slay the Spire 2 v0.109.1
- 第三方兼容基线：`STS2-MultiplayerLimitBreak` v0.1.3，
  DLL SHA-256
  `b2785afd3dc31fd6b32cb073af495ab343fcc31fa9e479049959ff43eb09356f`，
  `STS2-RitsuLib >= 0.4.13`（本机实测 v0.4.66）
- 范围：C11、H13、R1、G13、F9
- 实现状态：C11、H13、R1、G13、F9 已进入 v0.6.0 源码；安装、上传和发布
  仍需通过配套验收与测试门禁
- 配套文档：[验收标准](BETTERCOOP_V0_6_ACCEPTANCE.md) /
  [测试计划](BETTERCOOP_V0_6_TEST_PLAN.md)

## 1. 目标与非目标

本版本把 BetterCoop 从联机 Guard 和诊断驾驶舱扩展为可交流、可观察、
可恢复的联机工具，同时保持 STS2 原生房主权威和现有 Guard 的隔离性。

| ID | 用户能力 | 最小实现原则 |
|---|---|---|
| C11 | 对端发送任意用户文本 | 复用现有有界消息信封，只增加纯文本消息 |
| H13 | 快速查看队友实时手牌 | 复用 C9 的同意、快照、缓存和 4 Hz 采样 |
| R1 | 房主回溯到已访问房间节点 | 使用原生存档和 loaded-lobby，不热改对象图 |
| G13 | 适配 Multiplayer Limit Break | 可选反射适配，不增加第三方编译依赖 |
| F9 | 种子与随机数分析 | 复用 F4 计数和自然校验点，绝不额外调用 RNG |

本版本不实现：

- 富文本、脚本、控制台命令、文件传输、私聊、云端聊天记录；
- 跳转到未访问节点、战斗动作中间帧或任意对象状态；
- 预测未来抽牌、事件、奖励或随机数；
- 修改 Multiplayer Limit Break 的人数、缩放、布局或 Harmony patch；
- 为单一实现创建新的网络框架、序列化框架或外部运行时依赖。

## 2. 现有安全合同的冲突与迁移

现有设计明确禁止自由文本、运行/存档写入和种子展示。因此，实现前必须先合入
一份架构决策记录，并同步修改 `AGENTS.md` 和总技术方案中的安全不变量。
不能一边保留旧禁止条款，一边在代码里暗中例外。

新的安全合同如下：

| ID | v0.6.0 强制不变量 |
|---|---|
| V6-INV-01 | Guard 的包校验、原生 Mod 列表哨兵和 fail-closed 路径不得依赖 C11、H13、R1 或 F9。 |
| V6-INV-02 | 远端文本始终是不可信纯文本；不得解释为 BBCode、Markdown、URL、路径、命令或代码。 |
| V6-INV-03 | R1 只能使用当前支持构建的原生保存、继续和 loaded-lobby 流程；禁止反射写入运行对象图、动作队列或 RNG。 |
| V6-INV-04 | 只有原生 lobby 确认的房主可以发起 R1；所有当前 peer 必须确认就绪，否则事务中止。 |
| V6-INV-05 | F9 只能旁路观察已经发生的 RNG 调用；不得多调用、克隆、推进或预测 RNG。 |
| V6-INV-06 | 所有远端字段在分配前限长、限数、限频，并绑定当前 session、roster epoch 和原生 peer。 |
| V6-INV-07 | 5 人及以上只有在 G13 完整确认兼容环境后才能准备/开局；Unknown 等同于不兼容。 |
| V6-INV-08 | C11、H13、F9 失败时只熔断自身；R1 一旦进入事务则必须完成、干净中止或关闭 lobby，禁止分裂状态。 |
| V6-INV-09 | 原始聊天、手牌列表、Steam ID、绝对路径、原始 RNG state 和存档内容不得进入常规日志或事故报告。 |
| V6-INV-10 | 未支持的 STS2 或第三方 Mod 形状必须显示 Unsupported，不得猜测或绕过。 |

R1 是唯一获准改变当前 run 持久状态的模块。其权限不能被抽成通用“状态编辑”
接口，其他模块仍受原禁止条款约束。

## 3. 总体架构

### 3.1 协议分层

| 层 | 目标版本 | 职责 | 失败策略 |
|---|---:|---|---|
| Guard Protocol | 6 | 包、协议、G13 有效设置和 R1 启用状态的一致性 | fail-closed |
| Diagnostics Protocol | 2 | C11、C9/H13、F9 摘要和现有诊断 | fail-open、按模块熔断 |
| Run Control Protocol | 1 | R1 的准备、确认、提交、取消和恢复 | 事务内 fail-closed |

三层可复用同一底层发送入口和固定信封，但使用不同消息类型、限流桶和状态机。
Run Control 不得注册为可丢弃的 Diagnostics 消息，也不得被聊天洪泛饿死。

协议升级规则：

1. Guard Protocol 6 是破坏性升级；混合 v0.5/v0.6 的 lobby 在准备前被拒绝。
2. Diagnostics Protocol 2 不协商未知消息的“最佳猜测”；未知类型直接丢弃并计数。
3. Run Control Protocol 1 必须全员相同且 R1 设置一致才可启用。
4. 每个消息都携带当前 session nonce、roster epoch、单调 sequence 和发送者映射。
5. 客户端发送时 origin 固定为 0；房主从原生连接重写真实 ordinal 后再转发。

### 3.2 发送优先级和预算

现有 4096-byte 信封、每 peer 64 KiB 缓存和 16-peer 上限继续生效。新增统一公平
调度顺序：

1. 原生 Guard：独立通道，不进入可选消息队列；
2. Run Control、同意撤销和 session/roster 变更；
3. 状态、手牌和 RNG checkpoint 摘要；
4. 聊天和非关键诊断。

每类有独立 token bucket，另有全局上限。低优先级过载只丢弃自身最旧消息，
不得阻塞主线程或挤掉控制消息。

### 3.3 代码边界

沿用现有模块，不创建平行框架：

| 现有模块 | v0.6.0 增量 |
|---|---|
| `ToolkitProtocolCore` | Diagnostics v2 消息、优先级和共享限值 |
| `ToolkitCollaborationCore` | C11 codec；H13 不新增手牌 codec |
| `ToolkitDiagnosticsRuntime` | 聊天、手牌和 F9 peer 状态 |
| `ToolkitRuntime` | 聊天输入、Hand Shelf、种子/RNG 面板 |
| `ToolkitForensicsCore/Runtime` | RNG 事件旁路观察和 checkpoint 摘要 |
| `ToolkitPersistence` | 复用原子写入模式，增加独立回溯 journal |
| 新增 `ToolkitRecoveryCore/Runtime` | R1 独立状态机；不放入 fail-open Diagnostics |
| 新增 `ToolkitCheckpointStore` | R1 journal、原子替换和崩溃恢复 |
| 新增 `LimitBreakInterop` | G13 的清单检查和只读能力探测 |

将所有 `4`、`16`、peer 数组和 ordinal 检查集中为一个
`ToolkitLimits.MaxSupportedPlayers = 16`。禁止在功能模块复制人数常量。

## 4. C11：有界自由文本

### 4.1 “任意文本”的定义

“任意”指玩家可输入任意语义的可打印 Unicode 纯文本，不代表任意字节、无限长度
或可执行内容。允许中文、英文、数字、标点、Emoji 和 ZWJ 序列；不允许 NUL、
非法 UTF-8、C0/C1 控制字符和双向覆盖控制字符。CRLF 规范化为 LF，文本规范化为
NFC。

固定边界：

- 最多 384 UTF-8 bytes；
- 最多 256 Unicode scalar；
- 最多 4 行；
- 连续组合字符数量有界；
- 消息正文不得出现在普通日志、事故报告或磁盘缓存。

超限或非法输入必须在创建大数组、富文本节点或日志字符串之前拒绝。输入
`/rollback 3`、`[url]...`、HTML 或 shell 文本时，只能原样显示，不能触发功能。

### 4.2 消息流

1. 发送端本地做规范化和边界检查。
2. 客户端向房主发送 `ChatSubmit(clientMessageId, text)`，不声明身份。
3. 房主依据原生 peer 映射验证发送者、重新验证文本并执行限流。
4. 房主分配 session 内单调 `hostSequence`，广播
   `ChatDeliver(hostSequence, senderOrdinal, text)`。
5. 客户端按 sequence 去重；短暂乱序进入最多 16 条的重排窗口，超时后显示缺口，
   不无限等待。

房主自己的消息也走同一验证和排序函数。这样只保留一个权威路径。

### 4.3 限流、隐私和 UI

- 每发送者 1 条/秒、burst 2；全房间 2 条/秒、burst 6。
- 会话内历史最多 100 条或 64 KiB，先到者为准；溢出丢最旧消息。
- 支持按玩家静音和全部静音；设置只影响本地显示。
- 退出 lobby、session 变化或 Mod 卸载时同步清空历史。
- Godot 控件禁用 BBCode、Markdown 和 URL 自动打开；链接仅作为字符显示。
- Enter 聚焦/发送，Shift+Enter 换行，Esc 释放焦点；必须支持 IME、键盘和手柄。
- 渲染或声音失败只熔断 C11，不能影响 Guard、ready、原生动作和 R1。

## 5. H13：快捷队友手牌视图

H13 只改变查看方式，不创建第二套手牌协议。数据继续来自 C9：

- 全员逐 session 明确同意；
- roster 变化立即撤销；
- 每名玩家最多 64 张牌；
- 本地 4 Hz 采样；
- revision、owner ordinal、卡牌模型 ID、升级和公开费用沿用现有 codec。

新增 `Hand Shelf`：

1. 战斗 HUD 显示可折叠队友 chip：身份、手牌数、更新时间和 stale 状态。
2. 一次点击/按键打开最近或已固定队友；一次操作切换前/后一名队友。
3. 使用本地 `ModelDb` 渲染卡名、图像、升级、费用和原生 tooltip。
4. 未知 Mod 卡只显示安全占位和规范化 model ID，不因资源缺失抛错。
5. 5–16 人使用滚动和虚拟化；不为不可见玩家或卡牌持续创建节点。
6. 1 秒未更新标记 Stale，3 秒未更新隐藏明细；撤销或 epoch 变化立即清零。
7. 1280×720、200% UI 缩放、键盘和手柄下不裁切关键信息且无焦点陷阱。

为避免 16 人时现有共享发送预算导致轮询延迟，继续复用 C9 的
`HandSnapshot` 数据模型，但调整路由而不是全房间广播：

1. owner 只在内容变化时把最新 revision 发给房主，最高 2 Hz；
2. 每个 viewer 同时只订阅一个详细 hand owner；
3. `HandWatch(owner, knownRevision)` 只控制路由，不携带牌内容；
4. 房主从有界 latest-only 缓存向该 viewer 转发最新完整 snapshot；
5. watcher 切换、revision 缺口或恢复后发送一次完整 snapshot，平时丢弃过时 revision；
6. 手牌使用独立公平预算，低优先级聊天不能占用；Run Control 始终更高优先。

这样不创建第二种卡牌表示，也避免 16×16 的持续全量 fan-out。未选中的队友只显示
本体已有的手牌数量和 stale 状态。

手牌内容不进入聊天、日志、事故报告或自动截图证据。H13 UI 隐藏不改变 C9
同意状态；只有明确的“停止共享”操作才撤销。

## 6. R1：房主节点回溯

### 6.1 精确定义

“任意房间节点”定义为本次 run 已经进入、且 BetterCoop 在原生可保存的稳定房间
边界成功建立完整 checkpoint 的节点。以下目标不属于回溯：

- 未访问节点；
- 战斗动作、选择或奖励处理中间状态；
- checkpoint 生成失败或校验失败的节点；
- 不同游戏构建、Mod 环境、seed、roster 或 run；
- 当前分支时间线之后的废弃“未来”节点。

房主可以在联机过程中提出回溯；若当前不在安全边界，请求显示为 Pending，直到
动作队列为空、没有未完成原生选择且所有 peer 抵达同一自然 checkpoint。房主可在
提交前取消。绝不在动作执行一半时强制冻结并序列化。

### 6.2 Checkpoint journal

BetterCoop 不额外调用 `RunManager.ToSave`、`RunSaveManager.SaveRun`、RNG 或
checksum。它只在 STS2 原生多人保存成功后的 postfix 中，归档刚刚完成且已经过
本体序列化的完整 save payload。没有成功原生保存的节点就没有可回溯 checkpoint。
不手工序列化运行对象图，不做自研增量快照。

每个 journal 条目包含：

- 使用 BCL `RandomNumberGenerator` 生成的 128-bit checkpoint ID；不调用 STS2
  RNG，网络永不发送本地路径；
- run ID、node/act、单调 visit index、branch ID 和 rollback epoch；
- STS2 build、BetterCoop/协议、有效 Mod 环境和 roster digest；
- session-salted seed tag 与 RNG checkpoint digest；
- 压缩前后长度、schema 版本和 SHA-256；
- 原生完整 save payload，使用 BCL 压缩。

存储边界为每个 run 最多 128 个 checkpoint、单条最多 32 MiB、总计最多
256 MiB。达到任一边界时明确停止记录新 checkpoint 并禁用更早未记录节点，
不得静默覆盖当前 active save 或紧急恢复点。

写入采用同卷临时文件、flush、校验、原子 replace；索引可由已校验条目重建。
任何时刻必须至少保有当前 active save 和一份独立 emergency backup。

### 6.3 房主权威事务

R1 使用独立单事务状态机：

```text
Idle
  -> Prepare
  -> Quiesce
  -> BackupCurrent
  -> ActivateTarget
  -> NativeLoadedLobby
  -> VerifyAllPeers
  -> Commit
```

任一步可以转入 `Abort`；磁盘已经替换或房主崩溃时进入 `RecoveryRequired`。

具体流程：

1. 房主必须先选择目标，再通过显示 node/branch/时间的破坏性模态确认；UI 只向
   本地控制器提交 checkpoint ID，由本地 registry 解析目标。
2. 在任何磁盘写入前验证 build、包环境、seed tag、roster、hash 和目标时间线。
3. 房主广播 `RollbackPrepare(transactionId, targetMetadata)`。
4. 每个当前 peer 在 10 秒内确认同一 session、epoch、空动作队列和可重载状态。
5. 任一拒绝、掉线或超时则广播 `Abort`，所有人留在原节点。
6. 房主建立 emergency backup，原子激活目标原生多人 save。
7. 关闭当前 run，通过 STS2 原生 loaded-run lobby/Continue 流程重建会话。
8. 所有 peer 重新加入并回报 node、roster、环境、epoch 和自然 checksum 摘要。
9. 全员一致才提交新 branch/epoch；旧分支的手牌、聊天顺序和 RNG 摘要全部失效。

禁止多数投票、客户端发起、远端路径、热替换内存状态和“部分成功”。若无法证明
所有 peer 同处目标节点，则关闭活动 lobby，并保留可由房主明确选择的有效恢复
save；不得自动猜测继续哪一份。

### 6.4 能力门禁

R1 默认关闭。启用状态作为影响玩法的设置进入 Guard Protocol 6。只有以下条件
全部成立时 UI 才显示 Available：

- 当前 STS2 构建的保存/加载目标通过 Harmony smoke；
- 原生多人 loaded-run 在当前人数下通过契约测试；
- 当前 roster 与目标 checkpoint 完全一致；
- 所有 peer 的 BetterCoop、协议、有效包和 R1 设置一致；
- journal 健康且存在 emergency backup 空间；
- 若人数大于 4，G13 状态为 Compatible。

如果当前 STS2 无法可靠恢复相应人数，显示 Experimental/Unsupported，而不是
伪装为已完成。

## 7. G13：Multiplayer Limit Break 兼容

### 7.1 已验证基线

Workshop `3747606832` 的身份必须按 manifest 判断：

- Mod ID：`STS2-MultiplayerLimitBreak`
- 名称：Multiplayer Limit Break
- 本机 Workshop 版本：v0.1.3
- 本机 DLL SHA-256：
  `b2785afd3dc31fd6b32cb073af495ab343fcc31fa9e479049959ff43eb09356f`
- 玩家上限：16
- 依赖：`STS2-RitsuLib >= 0.4.13`

其公开源码将原版 4 人上限提升到 16，并把 slot ID 从 2 bit 扩为 4 bit、玩家列表
长度从 3 bit 扩为 5 bit，同时调整 Steam lobby member limit。BetterCoop 不得
重复或覆盖已经生效的这些 patch；fresh-run 路径只做验证。

当前 GitHub `main` 的 manifest 仍为 v0.1.2，而 Workshop 已是 v0.1.3。公开源码
用于理解设计，首版兼容判定必须以 Workshop manifest、DLL hash 和实测 IL/API
合同为准。

参考：

- <https://github.com/BAKAOLC/STS2-MultiplayerLimitBreak>
- <https://github.com/BAKAOLC/STS2-MultiplayerLimitBreak/blob/main/src/Network/MultiplayerLimitPatches.cs>
- <https://github.com/BAKAOLC/STS2-MultiplayerLimitBreak/blob/main/src/Network/SerializationBitWidthPatches.cs>
- <https://github.com/BAKAOLC/STS2-MultiplayerLimitBreak/blob/main/src/Settings/RuntimeMultiplayerSettings.cs>

### 7.2 适配策略

G13 是可选适配器，不增加对 Multiplayer Limit Break 或 RitsuLib DLL 的编译引用。

1. 先从原生已加载 Mod 清单读取 manifest ID、版本、依赖和包 hash。
2. 仅对已列入 allowlist 的版本/构建执行只读反射能力探测。
3. 验证 assembly active、14/4/2 网络/布局/缩放 patch 组状态、有效玩家上限 16、
   slot/list 位宽 4/5、Steam lobby member limit 和 Limit Break enabled。
4. 大厅阶段验证每端本地 `LimitBreakEnabled`、位宽和设置摘要。上游只会在
   `RunManager` 建立后把 RitsuLib 房主设置写入 `_remoteHostSettings`，因此
   sidecar Pending 不能作为 ready/embark 的前置条件。
5. 进入 run 后继续探测房主 sidecar；在它到达前，严格 G13 状态为 Pending，
   R1 等高风险功能不可用，但不终止上游已支持的正常联机。
6. 将 `LimitBreakEnabled` 和 extra-player scaling 的规范化有效值摘要加入
   Guard Protocol 6；不向诊断报告泄露原始用户设置。
7. 每个 peer 交换有界 capability proof；房主在星型拓扑中验证并转发全员证明。
8. 所有 peer 的 Mod bytes、依赖版本和大厅阶段有效设置摘要必须一致。
9. 反射目标缺失、初始化顺序未知或形状漂移时，G13 为 Unsupported。

capability proof 只包含固定字段：`active`、三个 patch-group 状态、
`capacity=16`、`slotBits=4`、`listBits=5`、Steam limit 状态、
`hostSettingsEpoch`、`enabled` 和 multiplier digest。它不接受自由键值或远端类型名。
RitsuLib 原生 trailer 与 BetterCoop 信封分别按各自声明长度读取；BetterCoop
不会吞掉、复制或解析第三方 sidecar 内容。

2–4 人时 G13 缺失不能影响原生基线。第 5 名 peer 出现后，G13 必须
PreRunCompatible；否则 Guard 在准备/开局前给出具体缺失项。进入 run 后，
`SettingsSynchronized` 才能把状态提升为 Compatible。P17 在任何数组或 UI
分配前拒绝。

BetterCoop 不调用第三方设置写接口，不修改难度缩放，不改变房间布局，也不接管
其 Harmony owner。

### 7.3 Loaded-run 风险和窄适配

v0.1.3 已确认覆盖 fresh-run 的 host、slot 和玩家列表路径；当前
`LobbyBeginLoadedRunMessage` 本身没有玩家列表字段，因此“没有该类 patch”不等于
已发现 bug，也不证明大于 4 人 loaded-run 正常。R1 开发前必须先完成
`LoadRunLobby` 全链路 API/IL spike 和 5/8 人实测。

若实测证明 STS2 loaded-run 的某个 count/slot 点确实仍使用原版边界，优先推动
上游修复。只有在目标点和失败机制均已证明时，BetterCoop 才能安装一个窄 shim：

- 只 patch 已证明缺失的 STS2 loaded-run 方法，不复制 fresh-run patch；
- 绑定精确 STS2 build、Limit Break version/hash 和 RitsuLib 合同；
- 使用独立 Harmony owner 并事务式安装；
- 检测到第三方已覆盖或方法形状漂移时拒绝双重 patch；
- slot 0–15、count 0–16 往返和 5/8 人读档全部通过后才标记 Compatible。

### 7.4 高人数完成条件

“能进入 lobby”不等于兼容完成。5、8 人必须让每个客户端分别证明：

1. 加入并协商同一 session；
2. 进入 run；
3. 收到本地抽牌/手牌 revision；
4. 进入 `PlayPhase`；
5. 至少一个合法原生动作或 end-turn 被接受；
6. 敌方回合完成；
7. 再次进入下一玩家回合。

16 人用于协议、ordinal、颜色、缓存和 UI 边界 smoke。Release Candidate 还必须
包含真实 Steam 五客户端证据；本地 ENet 不能替代 Steam lobby/relay 验证。

## 8. F9：种子与 RNG 分析

### 8.1 展示层级

F9 分为两个层级：

- 默认：显示 seed equality、session-salted seed tag、已消费调用数、已知 stream、
  checkpoint 增量和首次不一致位置；
- 本机主动展开：显示原始 run seed 和已经返回的本地 RNG 事件明细。

原始 seed、返回值和内部 state 不通过网络发送，也不自动写入报告。seed tag 使用
BCL `HMACSHA256(sessionNonce, normalizedSeedBytes)`；跨 peer 只交换该 tag、计数
和摘要。退出 lobby 或回溯后旧摘要不可复用。

### 8.2 零侵入观察

复用 F4 已审计的 `Rng.Next*` postfix。每次只观察原始调用已经给出的参数和返回值，
禁止再次调用 RNG。事件包含：

- 稳定 stream ID；无法可靠映射时为 `Unknown`；
- stream 内 call index 和方法类别；
- 规范化参数摘要、已返回结果摘要；
- allowlist caller fingerprint；
- session、branch、rollback epoch 和最近自然 checkpoint。

`Rng.Chaotic` 单独标记为非确定性，不参与确定性一致结论。支持构建以外的调用目标
或 stream 映射失败时，整个相应类别显示 Unsupported。

每个 stream 使用固定容量 ring buffer；UI 读取不可阻塞游戏线程。peer 比较在自然
checkpoint 进行，不生成新的 STS2 checksum。摘要链使用 BCL SHA-256，并包含
schema 和 epoch，防止不同口径误比。

### 8.3 分歧定位

当同一 checkpoint 的 peer 摘要不同：

1. 先比较 seed tag、branch/epoch 和 stream 集合；
2. 再比较每个 stream 的累计调用数；
3. 通过有界区间摘要定位首个不同 call index；
4. 只在本地 UI 显示 caller/method/参数类别，不向其他 peer 泄露原始返回值；
5. 证据不足时显示 Unknown，不把“计数不同”自动归因于某个 Mod。

F9 不提供未来 RNG 预测、牌序预览或重掷按钮。R1 回溯后从目标 checkpoint 建立
新 baseline；废弃分支事件保留在本地事故证据中，但不参与新分支比较。

## 9. UI 集成

现有 cockpit 保留，新增三个按需表面。所有身份统一使用排序 roster ordinal，
不再使用各客户端按观察顺序生成的本地 `P1/P2...`；紧凑 HUD 显示前四人时必须
同时显示“另有 N 人”，不得静默隐藏。

| 表面 | 默认状态 | 关键交互 |
|---|---|---|
| Chat Dock | 折叠 | Enter 聚焦；静音入口；未读数 |
| Hand Shelf | 折叠 chip | 一次打开；前后切换；停止共享 |
| Recovery & RNG | 只读摘要 | 房主选择 checkpoint；本机展开 seed |

阻塞问题使用现有警报中心；只有 R1 即将替换 active save 或恢复失败需要模态确认。
聊天、stale 手牌和普通 RNG 不一致不得抢焦点。所有状态同时用文字/图标表达，
不得只靠颜色。

## 10. 线程、异常和资源

- Harmony postfix 只做无锁或短临界区的固定成本记录；文件 IO 和压缩不得在游戏
  线程执行。
- Godot UI 只在主线程创建和修改节点。
- 网络 handler 先验证 header 和长度，再解码正文；异常按消息类型计数并熔断。
- C11/H13/F9 各自有独立 circuit breaker；失败不级联。
- R1 worker 只接受本地 registry 的 checkpoint ID，不接受路径或远端 payload。
- 所有后台任务都绑定 session cancellation token；离开 lobby 后不得继续回调。
- 16 人满载下缓存总量必须有显式上界，不能按消息内容或历史时间无限增长。

## 11. 兼容、升级与降级

| 场景 | 结果 |
|---|---|
| 2–4 人，没有 Limit Break | C11/H13/F9 正常；G13 不适用 |
| 2–4 人，启用 Limit Break | 维持原生基线；显示兼容状态 |
| 5–16 人，环境完整一致 | 允许准备/开局 |
| 5–16 人，Limit Break/RitsuLib 缺失或设置不一致 | Guard 精确阻止并指出 peer/字段 |
| C11/H13/F9 patch 不支持 | 对应模块 Unsupported；Guard 和原生联机继续 |
| R1 不支持或未启用 | 不记录 checkpoint，不显示可执行回溯 |
| R1 事务中 peer 掉线 | 中止；若已替换 save 则关闭 lobby 并进入确定性恢复 |
| P17 或恶意 count | 分配前拒绝 |

所有参与者都必须安装完全相同的 BetterCoop v0.6.0。5 人及以上还必须安装
完全一致且受支持的 Multiplayer Limit Break 和 RitsuLib。

## 12. Google 工程规范落地

“符合 Google 规范”必须转化为仓库门禁，而不是人工声明。

### 12.1 规范映射

- C#：以 Google C# Style Guide 为规范源：
  <https://google.github.io/styleguide/csharp-style.html>
- C# 构建质量：Nullable、deterministic、内置 .NET analyzers、code style in build
  和 warnings-as-errors。
- PowerShell：Google 没有官方 PowerShell 语言规范；使用 PSScriptAnalyzer、
  仓库一致命名和同等的可读性/测试要求，不声称其为 Google 官方规范。
- JSON/XML：2 空格、UTF-8、稳定排序、无重复键，并由标准解析器验证。
- Markdown：标题层级、链接和 requirement/test ID 由轻量脚本验证。

### 12.2 全仓库迁移

当前仓库没有根 `.editorconfig`，且既有 C# 使用 4 空格和大型多类型文件，不能
声称已经满足 Google C# 风格。实现功能前先做一个独立、纯格式/结构变更：

1. 添加根 `.editorconfig`：2 空格、无 tab、100 列、brace、using 顺序、命名规则。
2. 添加 `Directory.Build.props`：启用内置 analyzer、nullable、
   `TreatWarningsAsErrors` 和 deterministic。
3. 对全部 tracked C# 执行一次格式迁移；不混入行为变化。
4. 生产核心类型原则上一文件一类型；只有紧密耦合的小型 immutable carrier
   可以同文件，并记录原因。
5. Harmony 要求的 `__instance`、`__result`、`____field` 参数只做精确、可审计
   的命名例外，不扩大 suppression。
6. CI 执行 `dotnet format --verify-no-changes`、Release build、测试项目、
   PSScriptAnalyzer 和 JSON/XML/Markdown 轻量检查。

不引入 StyleCop 或新的运行时依赖；优先使用 SDK 内置工具和现有 PowerShell。

### 12.3 可追踪性

每个实现变更必须同时更新：

- requirement ID：C11、H13、R1、G13 或 F9；
- 对应验收条款；
- 至少一个会在回归时失败的测试；
- `docs/WORKLOG.md`；
- 若改变 wire：相应协议版本；
- 若改变分发字节：manifest 版本和发布 hash。

CI 检查所有 requirement 都至少映射一个验收 ID 和测试 ID，且不存在孤立测试。

## 13. 分阶段实现顺序

1. **规范基线**：ADR、安全合同、`.editorconfig`、构建门禁和纯格式迁移。
2. **低风险复用**：C11 codec/relay/UI；H13 只读 UI。
3. **只读诊断**：F9 零侵入观察、摘要和回溯 epoch。
4. **高人数兼容**：G13 allowlist、Guard Protocol 6、5/8/16 人矩阵。
5. **高风险恢复**：R1 可行性 spike、journal、Run Control Protocol 1 和故障注入。
6. **发布候选**：全矩阵、长稳、真实 Steam 五客户端和包复验。

任一阶段都必须保持 Release build 和既有测试可通过。R1 可行性 spike 如果不能
在当前 STS2 loaded-run 流程上证明无 split-brain，则其状态保持
Experimental/Unsupported，不降低其他四项的发布质量。

## 14. 发布证据

每次候选必须保存：

- commit、BetterCoop DLL/manifest/protocol 和 SHA-256；
- STS2 build；
- Multiplayer Limit Break / RitsuLib 的 ID、version、hash 和有效设置摘要；
- topology、transport、session seed tag、开始/结束单调时间；
- 每个 peer 的阶段 sentinel；
- fault injection ID、结果和证据路径；
- 所有验收 ID 的 Pass/Fail/Unsupported 及原因。

没有证据的“没有报错”“进入了 lobby”或只看房主日志，不能作为通过结论。
