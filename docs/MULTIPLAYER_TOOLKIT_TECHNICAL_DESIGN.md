# STS2 Co-op Guard 联机终极工具技术方案

- 文档状态：Proposed
- 目标版本：v0.4.x–v1.x，分阶段交付
- 基线版本：CoopGuard v0.3.3 / Protocol 4 / STS2 v0.109.1
- 范围：H1–H12、C1–C10、G1–G12、D1–D10、F1–F8，共 52 项
- 本文只批准设计与验证门槛，不批准直接实现、安装或发布

## 1. 目标

把 CoopGuard 从“包一致性开局闸门”扩展为一个联机驾驶舱：在不改变
战斗、运行、玩家、RNG 或存档权威状态的前提下，提供实时状态显示、
低风险协作、环境修复指导、事故诊断和确定性取证。

成功标准不是“功能最多”，而是：

1. 现有 Guard 保护不被任何新功能削弱。
2. 可选功能失效时，STS2 原生联机仍能继续。
3. 所有结论都说明数据来源、时间、新鲜度和置信度。
4. 所有远端输入均按不可信输入处理。
5. 不自动修复状态，不自动修改其他 Mod，不自动重连循环，不上传日志。
6. 每个需求 ID 都能追踪到验收条款和自动/人工测试。

## 2. 规范性语言与工程原则

本文的“必须”“禁止”“应当”“可以”分别对应 MUST、MUST NOT、
SHOULD、MAY。无法满足任一 MUST 的功能不得进入 Release 包。

工程基线采用以下公开原则：

- Google SRE：以用户可感知结果定义 SLI/SLO，并用错误预算管理可靠性，
  不用无法实现的“所有东西 100% 可用”承诺。
- Google Engineering Practices：每个变更保持小而完整，测试与实现放在
  同一变更中，任何中间变更都不得破坏构建。
- Google Test Sizes：优先大量小型测试，少量中型契约测试，再用必要的
  多客户端大型测试覆盖真实边界。
- Apple HIG：常态状态使用被动、就地反馈；只有阻塞且需要用户立即决策的
  问题才能弹模态警报。
- Apple Accessibility：文字放大、键盘/控制器导航、足够对比度，任何状态
  都不能只靠颜色或声音表达。
- Apple Privacy：只收集实现功能所需的最少数据，默认本地保存，明确同意后
  才能共享敏感的游戏内信息。

这里的“工程级”表示可追踪、可测试、可回滚且满足上述公开原则，不表示项目得到
Google 或 Apple 的审核、认证或背书。

参考：

- <https://sre.google/sre-book/service-level-objectives/>
- <https://sre.google/sre-book/service-best-practices/>
- <https://testing.googleblog.com/2010/12/test-sizes.html>
- <https://google.github.io/eng-practices/review/developer/small-cls.html>
- <https://google.github.io/eng-practices/review/reviewer/standard.html>
- <https://developer.apple.com/design/human-interface-guidelines/feedback>
- <https://developer.apple.com/design/human-interface-guidelines/alerts>
- <https://developer.apple.com/design/human-interface-guidelines/accessibility/>
- <https://developer.apple.com/design/human-interface-guidelines/privacy>

## 3. 强制安全不变量

以下规则高于所有功能需求：

| ID | 不变量 |
|---|---|
| INV-01 | 只有现有 Guard 可阻止准备、加入或开局；HUD、协作、诊断和取证模块永远不得成为开局依赖。 |
| INV-02 | 禁止写入或修复战斗、运行、玩家、动作队列、RNG 和 STS2 存档状态。 |
| INV-03 | C6 只能由用户明确点击，并且只能调用经当前构建实测可用的 STS2 原生加入流程；状态分歧、Mod 不匹配、主机离开时禁止显示。v0.109.1 不支持恢复运行中 run，因此该场景必须显示 Unsupported。 |
| INV-04 | 禁止自动下载、更新、启用、停用、删除、替换或重排其他 Mod。 |
| INV-05 | 禁止生成额外的 STS2 checksum；只能观察本体已经产生的校验点。 |
| INV-06 | 禁止读取或展示未来 RNG 值、种子、抽牌顺序、未公开选择或其他隐藏信息。 |
| INV-07 | 禁止向同伴发送原始日志、文件内容、绝对路径、设置值、存档、IP、Steam ID、账号数据或凭据。 |
| INV-08 | C9 精确手牌共享默认关闭，必须得到当前房间全员逐次明确同意；成员变化后立即撤销。 |
| INV-09 | 所有跨端字段必须限长、限频、验证枚举和值域，并验证发送者仍在当前原生 lobby。 |
| INV-10 | 所有 Harmony patch 和 Godot callback 必须捕获自身异常；异常不得逃逸到游戏线程。 |
| INV-11 | 未支持的游戏构建、API 形状或数据源必须显示 Unknown/Unsupported，禁止伪造正常结果。 |
| INV-12 | Guard 的本地哈希、原生 Mod 列表哨兵和 fail-closed 路径不得依赖可选协议或 UI 初始化。 |

## 4. 功能目录

### 4.1 H：显示与态势感知

| ID | 功能 | 权威数据源 | 默认模式 |
|---|---|---|---|
| H1 | 网络 HUD | `INetGameService.GetStatsForPeer()` / `ConnectionStats` | 开启，只标注“本机视角” |
| H2 | “正在等什么” | 原生 lobby、动作队列、战斗阶段和选择状态 | 开启，推断项带置信度 |
| H3 | 大厅健康矩阵 | 原生连接/准备状态、Guard、本地/可选 peer 摘要 | 开启；缺失远端能力时降级 |
| H4 | 加入阶段进度 | 原生 `JoinFlow` 阶段观察 | 开启；Hook 不兼容时隐藏 |
| H5 | 联机事件时间线 | 白名单事件环形缓冲 | 开启，本地 |
| H6 | 选择进度 | 只显示已完成数量/等待者，不显示选择内容 | 开启 |
| H7 | 紧凑公开队友状态 | 本体已公开的 HP、格挡、能量、手牌数等 | 开启 |
| H8 | 近 60 秒网络图 | H1 的有界采样 | 开启 |
| H9 | 共享环境短码 | 主机会话 nonce、游戏构建、协议和有效包指纹的单向派生码 | 开启，仅显示当前会话短码 |
| H10 | 加载速度/进度 | 原生 loading 状态；无可靠百分比时仅显示阶段和耗时 | 开启 |
| H11 | 玩家身份颜色 | 当前 session 序号的稳定映射 | 开启，颜色之外同时显示符号/名称 |
| H12 | 警报中心 | 所有模块的去重、分级、可操作事件 | 开启；非致命事件不弹模态窗 |

### 4.2 C：协作与易用性

| ID | 功能 | 数据边界 | 默认模式 |
|---|---|---|---|
| C1 | 快捷状态消息 | 固定本地化枚举，无自由文本 | 开启 |
| C2 | 地图选择进度 | 只显示完成状态，不显示路线/节点 | 开启 |
| C3 | 公开行动流 | 只记录已经对所有玩家公开并完成的动作 | 开启 |
| C4 | 等待计时器 | 单调时钟，阶段改变即重置 | 开启 |
| C5 | 重连状态卡 | 断线原因、最后心跳、原生重连资格 | 开启 |
| C6 | 手动一键重连 | v0.109.1 仅限审计过的 pre-run lobby 重进；运行中恢复等待未来原生支持 | 条件启用，运行中为 Unsupported |
| C7 | 声音提示 | 关键且可操作事件；有视觉等价物并可静音 | 默认开启，尊重游戏音频设置 |
| C8 | 无障碍 | 200% 文字、键盘/控制器、对比度、非颜色编码 | 强制 |
| C9 | 精确手牌共享 | 全员逐次同意、默认关闭、成员变化即撤销 | 关闭 |
| C10 | 可选贡献统计 | 仅事实计数，不形成评分或自动归责 | 关闭 |

### 4.3 G：兼容性与环境治理

| ID | 功能 | 行为边界 |
|---|---|---|
| G1 | 不匹配修复表 | 说明哪一端缺失/多余/同 ID 内容不同/仅顺序不同，不改文件 |
| G2 | 复制修复清单 | 复制红acted、无摘要值和绝对路径的操作清单 |
| G3 | 本地 Mod Doctor | 检查重复 ID、残留、依赖、覆盖和危险文件形态，只读 |
| G4 | Workshop 更新观察器 | 只读本地 Steam/原生 UGC 状态，不触发更新 |
| G5 | 环境 lockfile/导出 | 显式导出版本化、可验证、无本机路径的环境描述；可包含包摘要 |
| G6 | 存档环境 sidecar 封印 | 原子写入独立 sidecar，不修改 STS2 存档 |
| G7 | 旧存档兼容警告 | 缺少历史证据时显示“无法证明”，不谎报不兼容 |
| G8 | Harmony 冲突图 | 展示 owner、目标、patch 类型和优先级；不解除 patch |
| G9 | 分类指纹 | 将差异定位到程序集/PCK/确定性设置/包结构等类别 |
| G10 | 确定性设置声明 | 合作 Mod 只发布设置摘要；失败时对声明项 fail-closed |
| G11 | 能力/协议协商 | 可选诊断通道协商；不影响 Guard Protocol 4 |
| G12 | 已知问题规则目录 | 随发布包审查和版本化；无远端动态规则执行 |

### 4.4 D：诊断与事故响应

| ID | 功能 | 行为边界 |
|---|---|---|
| D1 | 软锁观察器 | 只报警、记录证据；不取消动作、不强制结束回合 |
| D2 | 等待原因置信度 | 每条原因显示 High/Medium/Low、证据和数据年龄 |
| D3 | 本地飞行记录器 | 有界内存环形缓冲，默认不落盘 |
| D4 | 断线前网络快照 | 保存断线前 60 秒统计摘要，不含端点 |
| D5 | 报告历史 | 有界、原子、可清除，默认最多 20 份/25 MiB |
| D6 | 离线对比两份报告 | 版本化、限长解析，纯文本差异，不执行内容 |
| D7 | 共享会话 ID | 随机 128 位 ID，显示短前缀；不用 lobby/账号 ID 派生 |
| D8 | 下次启动原生崩溃复盘 | 根据未清理会话标记和本地日志推断，明确可能是断电/强退 |
| D9 | 依赖感知 Mod 二分计划 | 输出测试计划，不修改启用列表 |
| D10 | 主机诊断视图 | 汇总主机可见统计，不自动踢人或归责 |

### 4.5 F：确定性取证与开发者能力

| ID | 功能 | 发布约束 |
|---|---|---|
| F1 | 分层状态摘要（Hierarchical State Digests） | 只在本体原生 checkpoint 上计算白名单类别摘要 |
| F2 | 首个分歧定位器 | 只报告最早观测到的 checkpoint/类别，不宣布责任 Mod |
| F3 | 动作/检查点日志 | 记录白名单公开动作类型、actor、checkpoint，不记录参数或隐藏状态 |
| F4 | RNG 计数哨兵 | 只计数已发生调用；不得读取值、种子或额外调用 RNG |
| F5 | 有界 peer heartbeat | 固定字段、限频、过期、无日志/路径/自由文本 |
| F6 | Mod 作者诊断/确定性 API | Guard 设置摘要入口与可选诊断入口物理分离；均为 push-only |
| F7 | 自动 2/3/4 客户端 CI | 隔离 profile、ENet 主矩阵；Steam 传输保留人工/受控机验收 |
| F8 | 仅开发者故障注入 | 仅 Debug/测试构建；Release 包不包含可触发入口 |

## 5. 总体架构

```text
STS2 native state/events
          |
          v
  [Read-only adapters] ----> [Guard adapter: existing, fail-closed]
          |                              |
          v                              v
 [Observation store]              native Mod-list gate
          |
    +-----+---------+--------------+
    |               |              |
    v               v              v
 [HUD]       [Diagnostics]     [Local Doctor]
    |               |              |
    +---------+-----+--------------+
              |
              v
      [Optional diagnostics plane]
        fixed/bounded/negotiated
              |
              v
       peer observations only
```

### 5.1 模块边界

| 模块 | 职责 | 故障策略 |
|---|---|---|
| Guard | v0.3.3 包/构建/协议完整性、G10 已声明设置摘要与开局闸门 | fail-closed |
| Lifecycle | 绑定/解绑原生事件、session 生命周期、主线程调度 | 自身失败时关闭所有可选模块，不影响 Guard |
| Observations | 规范化只读观测、时间、来源、置信度、过期 | 丢弃无效/过期输入 |
| HUD | H/C 的展示与交互 | fail-open，隐藏受影响控件 |
| Diagnostics | D、警报、飞行记录和报告 | fail-open，有界 |
| Doctor | G 的本地扫描、规则和导出 | fail-open，不改 Mod |
| Forensics | F1–F4 与 F6 的 checkpoint/event 诊断入口 | 默认实验性、按 lobby 同意 |
| Optional plane | C/H/D/F 的有限跨端数据 | 不兼容或异常即按 peer 禁用 |
| Persistence | 设置、sidecar、报告历史 | 原子写；失败仅提示 |

不为单一功能预建接口或工厂。只有原生版本差异已经出现两个真实实现时，
才为对应 adapter 引入第二实现。

时间线只有一个存储所有者：D3 的有界飞行记录环。F3 只把规范化的公开动作/
自然 checkpoint 事件写入 D3；D3 自己再加入 peer/等待/网络事件；H5 只渲染
D3 过滤后的最近 100 项。F3、H5 不得再创建第二/第三个环形缓冲或落盘格式。

### 5.2 观测模型

所有显示使用同一个不可变观测记录：

```text
value
source = NativeAuthoritative | NativeObserved | NativeStatistic | PeerDeclared | LocalInference
confidence = High | Medium | Low | Unknown
captured_at = monotonic timestamp
max_age
sequence
```

规则：

- `NativeAuthoritative` 可以显示为确定状态。
- `NativeObserved` 表示从受支持构建的只读 prefix/finalizer/event 观察到流程，
  可给 High 置信但不能写成游戏权威根因；Hook 缺失立即变 Unknown。
- `NativeStatistic` 必须标注“本机视角”或采样窗口。
- `PeerDeclared` 必须显示来源玩家，过期后变为 Unknown。
- `LocalInference` 禁止使用“确定”“根因”等字样；必须给出置信度。
- UI 不得延长过期数据的生命期；最后值可灰显但必须显示数据年龄。
- 时长统一使用 `Stopwatch`/单调时钟；墙钟只用于报告时间戳。

### 5.3 生命周期与线程

1. Guard 哨兵最先安装，保持现有顺序。
2. Guard 核心使用独立 Harmony owner（如 `coopguard.guard`）和安装事务；禁止把
   可选 `[HarmonyPatch]` 与核心放进同一次 `PatchAll`。
3. 每个可选模块使用独立 owner/目标清单，在 Guard 初始化完成后逐模块安装。
   目标缺失或安装回滚只禁用该模块；卸载只能移除自己的 owner。
4. Harmony 回调只采集最小不可变数据，不直接操作 Godot UI。
5. 所有 UI 创建、更新和销毁只在 Godot 主线程执行。
6. 离开 lobby、回主菜单、载入新 run 或成员变化时，取消旧 session，
   丢弃排队更新并清空同意状态。
7. 每个可选模块的首次未处理内部异常都被捕获、结构化记录并关闭该模块至
   本次 session 结束；禁止自动反复重启形成错误风暴。
8. 数据源暂时不可用不是异常，显示 Unknown；连续恢复三个有效样本后再恢复
   Healthy，避免 UI 抖动。

## 6. 原生能力复用

必须先复用以下本体能力，再考虑自定义实现：

- H1/H8：`INetGameService.GetStatsForPeer()` 和
  `ConnectionStats.PingMsec`、`PacketLoss`、`LastReceivedTime`、
  `RemoteIsLoading`。
- H2/D1：`CombatManager.IsPlayerReadyToEndTurn()`、
  `RunLobby.ConnectedPlayerIds`、`ActionQueueSynchronizer.CombatState`、
  `ActionExecutor.CurrentlyRunningAction`，以及公开的动作/队列/checksum
  事件。
- 精确“正在等玩家 B 完成选择”没有稳定的公开当前集合。只有独立可选模块成功
  安装受支持构建的 `PlayerChoiceSynchronizer.WaitForRemoteChoice(...)`
  prefix/finalizer 并观察到未完成调用时，才能以 `NativeObserved/High` 显示；
  禁止主动调用该方法或读取/取出动作队列。签名漂移时显示 Unknown。
- C5/C6：原生 disconnect reason、仍连接时复制的最小 raw lobby identifier，
  以及 `SteamClientConnectionInitializer.FromLobby()` / 菜单 `JoinGame()`。
- H4：只读观察 `JoinFlow` 的已审计阶段；私有方法 Hook 不匹配时隐藏功能。
- F1/F2/F3：只监听 `ChecksumTracker.ChecksumGenerated` 等本体已经生成的
  checkpoint。
- v0.109.1 的生产 `JoinFlow` 遇到 `RunSessionState.Running` 会返回
  `RunInProgress`，其调试路径也明确表示 running rejoin 尚未实现。
  `SerializableRun + NetFullCombatState` 数据结构本身不能证明存在生产恢复消费
  路径。当前 C6 只能对 `InLobby`/`InLoadedLobby` 做一次原生加入；运行中断线
  只由 C5 解释，不显示恢复按钮。未来构建只有通过签名审计和真实双客户端恢复
  测试后才可启用，仍禁止自建第二套状态同步。

可恢复的 lobby ticket 必须在原生 disconnect 清空 `_lobbyId` 之前、仍连接时复制，
只存内存，TTL 最长 10 分钟且只能消费一次；成功、过期、房主/会话变化或任一
不安全断线原因立即清除。会话其他缓存和所有同意状态仍在断线 callback 立即清空，
不得因为保留 ticket 而延长 C9/诊断数据生命期。

本体已有 3 秒普通断线遮罩、远端加载时 8 秒遮罩、网络异常图标、队友公开
状态和 Ping UI。CoopGuard 只补充可解释数字、历史和跨功能汇总，不复制本体
已有交互。

## 7. Guard 与可选诊断通道

### 7.1 两个协议域

| 协议域 | 用途 | 失败结果 |
|---|---|---|
| Guard Protocol 4+ | 游戏构建和有效 Mod 包兼容性 | 本地错误或不匹配时 fail-closed |
| Diagnostics Protocol 1 | UI、协作、心跳和摘要 | 仅禁用对应远端功能，原生联机继续 |

Diagnostics Protocol 禁止承载准备、开局、战斗动作、存档、修复命令或任何
Guard 真值。其代码不得被 Guard 程序集初始化路径调用。

G9/G10 中会影响兼容结论的分类包摘要和已声明设置摘要属于 Guard 数据，必须像
现有逐 Mod 摘要一样编码进 STS2 原生 Mod-list 条目，并提升 Guard Protocol。
实现应优先把一个 Mod 的固定分类向量压入同一条有界条目，避免每个类别产生一条
消息。发布前必须审计并固定 `InitialGameInfoMessage` 接收路径对列表 count、
单字符串长度和累计 UTF-8 字节的“分配前”硬上限；用恶意 count/length 和接近
总上限的真实原生包捕获验证实际分配。无法证明上限在远端数据分配前生效时，不得
扩展现有 Guard 条目。即使证明成立，也必须通过 256-Mod 大小和原生十秒握手
压力测试。可选诊断通道可以改善表格显示，但不得提供或覆盖这两个功能的兼容真值。

### 7.2 启用前置门槛

自定义诊断通道只有同时满足以下条件才能进入 Release：

1. 已证明 STS2 的反序列化层可在分配可变长度 payload 前执行硬上限；
2. sender identity 来自原生连接，并能验证其当前 lobby 成员身份；
3. 模糊测试覆盖截断、超长、未知枚举、重复、乱序和洪泛；
4. 2/3/4 客户端验证迟到加入、离开、重连和 host/client 非对称视角；
5. 任何解析器异常都只禁用该 peer 的诊断能力；
6. 关闭通道后 Guard 和原生联机测试仍全部通过。

任一条件未满足时，H3/H6/H9/H10/C1/C2/C9/D7/D10/F1–F5 的跨端增强显示
Unknown 或 Local only，不得用不安全实现补齐。

### 7.3 固定 envelope

首个实现只允许固定版本 envelope：

```text
magic        4 bytes
major        uint8
minor        uint8
message_type uint8
flags        uint8
session_id   16 bytes
sequence     uint32
payload_len  uint16
payload      0..4096 bytes
```

强制规则：

- 单消息 payload 最大 4096 字节，超限在分配前拒绝；
- payload schema 必须扁平；最多允许一层固定上限的重复记录，禁止记录内再含
  容器。解析器先在 `ReadOnlySpan<byte>` 上验证 count、每项 length、
  remaining bytes、UTF-8 上限和累计预算，再创建字符串/数组；
- 伪造的 `uint32.MaxValue` count、截断项或累计长度溢出必须在任何按该值分配
  前拒绝；单次解析额外分配 ≤16 KiB，每 peer session 缓存 ≤64 KiB；
- F1/F2 每条消息只携带一个 checkpoint 的固定类别摘要，不发送历史数组；
- 每 peer 只有一个共享出站调度器；F5 liveness header、最新状态和可选摘要尽量
  合并进同一 envelope，不允许各功能建立自己的心跳/定时发送器；
- 调度优先级依次为：session/consent 撤销与成员 epoch、用户主动状态、最新手牌
  快照、liveness、可替换状态/图表。背压时同键状态只保留最新值，先丢图表/
  低优先级重复项，绝不延迟本地撤回生效；
- 总体稳态每 peer 不超过每秒 1 条，令牌桶 burst 4 条；空闲时每 2 秒一个合并
  keepalive。C9 的“最多每秒 2 次变化”只能消费 burst，并仍受总体调度器限制；
- H3/F5 只有连续错过两个 2 秒 keepalive 再加 1 秒容差后（共 5 秒）才标 stale；
- 每 peer 每分钟解析错误达到 3 次时，本 session 禁用其可选能力；
- 序列号只用于去重/新鲜度，不用于 Guard 或游戏动作排序；
- 不接受远端 URL、路径、RichText/BBCode、自由文本或可执行规则；
- unknown major：禁用通道；unknown minor/type/capability：忽略；
- lobby 成员变化后产生新 session ID，旧消息全部丢弃；
- 所有字段先验证再进入 observation store。

### 7.4 能力协商

Hello 只包含协议 major/minor、固定 feature bitset、locale 类别和隐私能力
状态。不存在“最低共同能力就可以降低 Guard”的逻辑。功能仅在双方均声明且
本地允许时启用。

当前单 DLL 且严格包哈希的产品形态意味着诚实玩家必须先使用完全相同的
CoopGuard 发布包；不同 Release/Diagnostics 实现会在进入 lobby 前由 Guard
拒绝，不能承诺“混合版本进入房间后降级”。协商只覆盖同一发布包内的本地开关、
平台/API 可用性、模块熔断和防御性畸形/未知消息测试。unknown major 的运行时
分支属于安全防御，不是版本偏斜兼容承诺；若未来拆包支持混合版本，必须另立 ADR。

首次广播可能丢失，因此采用有界的定向 Hello/ACK：

- handler 就绪后发送；
- 新成员加入时重发；
- 未收到 ACK 时最多重试 3 次，间隔 1/2/4 秒并加入小抖动；
- 超时后该 peer 显示“诊断通道不可用”，不继续重试。

## 8. 隐私、信任与内容安全

### 8.1 威胁模型

CoopGuard 仍面向可信合作玩家，不是反作弊或远程证明系统；但必须把远端
payload 当成可能恶意的数据，以防内存放大、异常、UI 注入和日志污染。

| 数据级别 | 示例 | 处理 |
|---|---|---|
| Public | 玩家显示名、角色、公开行动、连接/准备状态 | 可展示，仍需限长和转义 |
| Derived | ping、等待推断、分类摘要、短码 | 显示来源/置信度，不显示原摘要 |
| Consent | C9 精确手牌 | 默认关闭，全员逐 session 同意 |
| Local sensitive | 日志、报告历史、安装路径、Harmony 详情 | 只留本机，复制前红action |
| Forbidden | 原始存档、完整状态、未来 RNG、端点、Steam ID、凭据 | 不采集、不发送、不展示 |

### 8.2 C9 同意状态机

```text
Off -> LocalOptIn -> Collect(epoch, roster) -> Commit(epoch) -> Active(epoch)
 ^                         |                       |               |
 +--- decline/timeout/member-change/rejoin/revoke ----------------+
```

- 每个 lobby session 都从 Off 开始，不持久化同意。
- UI 必须先解释共享字段、接收者和撤销方法。
- 同意键绑定 `{session_id, membership_epoch, exact_roster_digest}`。主机只在
  收齐当前 roster 的 OptIn 后广播 Commit；所有成员确认同一 epoch/roster 后，
  主机才广播 Active。未收到 Active 的端禁止发送或显示手牌。
- 本地撤回或原生成员变化 callback 必须先同步关闭发送，再发撤回/新 epoch；
  任一玩家拒绝、超时、掉线、重连或新玩家加入都立即清空本地远端手牌缓存并
  回到 Off。传播中的旧 epoch 消息在进入 C9 字段解码或缓存前丢弃。
- 每次发送前重新比较当前 session、epoch 和 roster；不允许用“最多一秒后关闭”
  作为发送端安全边界。
- 只发送卡牌稳定 ID、升级次数和手中顺序；最多 64 张、单 ID 最多 48 UTF-8
  字节；可以发送当前已生效费用这一项有界标量。超过边界时仅该玩家显示
  “手牌过大，无法共享”。
- 禁止发送卡牌动态内部状态、抽牌堆顺序、随机种子或任意 Mod 序列化对象。

### 8.3 文本与链接

- peer 文本按纯文本渲染，删除控制字符并执行 Unicode 安全截断；
- 不解析 BBCode、Markdown、HTML 或 shell-like 内容；
- Workshop 页面只能由本地验证过的纯数字 PublishedFileId 构造；
- 报告复制继续复用现有红action器，不包含完整摘要、包 hash 或环境短码；
- G12 规则建议是本地常量，不接受远端提供的命令、路径或链接。

## 9. 本地持久化

### 9.1 环境 lockfile

使用版本化 JSON，字段固定、排序稳定、UTF-8、最大 512 KiB：

```json
{
  "schema": 1,
  "game": {"version": "0.109.1", "commit": "c8c577f6"},
  "guard": {"version": "0.3.3", "protocol": 4},
  "mods": [
    {
      "id": "Example",
      "version": "1.0.0",
      "source": "workshop-or-local",
      "package_digest": "sha256:..."
    }
  ],
  "environment_code": "ABCD-EFGH"
}
```

包摘要使 lockfile 能验证“同版本不同字节”；它只出现在玩家主动导出的环境
文件中，不进入普通诊断报告或快捷复制文本。导出文件不包含文件级 hash、路径、
Steam ID、设置值或账号信息。导入只用于比较和生成修复建议，不自动应用。

### 9.2 存档 sidecar

- sidecar 与 STS2 存档分离，以稳定 save 标识的 hash 命名；
- 单个 sidecar 最大 64 KiB；
- 只在原生保存成功后，以流式 SHA-256 只读绑定精确存档字节和长度；单个存档
  最大读取 64 MiB，超限或读取失败时标为未封印，不影响原生保存；
- 包含 schema、存档内容摘要/长度、游戏构建、Guard 协议、本地环境摘要、
  创建/最近确认时间；
- 先写同目录临时文件、flush、再原子替换；
- 不包含存档内容、路径、玩家账号、房间标识；
- 写入失败只生成警报，不阻止原生保存；
- sidecar 缺失意味着“来源未知”，不能解释为“不兼容”；
- 加载前必须重新验证当前存档字节摘要/长度；复制、陈旧、篡改或错配 sidecar
  都不得显示成功；
- sidecar 与当前环境相同时只能表述“与本机当时记录的环境一致”，不能声称
  已证明存档兼容；环境不同时给出明确警告和新开局建议，不修改存档；
- 存档内容摘要只留本地 sidecar，不经 peer、普通报告或剪贴板输出。

### 9.3 报告和飞行记录

- D3 持有唯一内存飞行记录环：最多 1024 项、单项规范化后最多 512 字节，
  包含对象/索引在内 retained memory ≤2 MiB；
- 持久报告历史默认 Off。只有玩家逐份点击“保存到历史”，或在看见保留数量/
  容量/清除方法后明确开启“自动保留脱敏报告”，才允许写入用户专属目录；
- 关闭自动保留必须先同步停止新的磁盘写入；是否清除既有历史由用户另行确认，
  不能把“撤销同意”实现成隐式删除；
- 报告最多保留 20 份且总量不超过 25 MiB，先按数量再按总量淘汰最旧项；
- 单报告最大 1 MiB，超限截断并写入 `truncated=true`；
- 报告写入使用临时文件和原子替换；崩溃遗留临时文件下次启动删除；
- 用户可一键清除；失败必须明确提示；
- 历史关闭时，无论触发多少事故都不得创建报告文件或排队延迟写入；
- D8 使用 session marker 判断“上次未正常关闭”，但必须提示断电、任务管理器
  强退也会产生相同现象。

## 10. 诊断与推断规则

### 10.1 等待原因

候选原因按证据排序：

1. High：本体明确报告 peer disconnected、remote loading、等待原生选择或
   某玩家未 ready。
2. Medium：动作执行中且队列/阶段在正常阈值内；或远端声明其公开阶段。
3. Low：网络正常但一段时间没有可观察进度，只能怀疑动画、Mod 回调或软锁。
4. Unknown：数据源缺失、过期或当前构建未审计。

同一时间可以保留多个候选，不强行归一成一个根因。

### 10.2 软锁观察

软锁观察器使用“进度租约”而不是单一超时：

- 进度事件：阶段改变、公开动作完成、队列改变、原生 checkpoint、连接状态
  改变、选择完成；
- 远端 loading 时使用本体 8 秒语义并继续显示耗时，不立即判卡死；
- 30 秒无进度：低级提示；
- 60 秒无进度且心跳正常：中级“疑似停滞”；
- 90 秒无进度且动作/阶段不变：高可见警报，但仍不能称为确定软锁；
- 网络已中断时分类为连接问题，不同时报告软锁。

阈值必须可在开发测试中注入，但首个 Release 不提供用户配置，避免形成无法
验证的组合。

### 10.3 分歧取证

- 只在相同原生 checkpoint ID/context 比较分层摘要；
- F1 摘要类别首版固定为 `RunPublic`、`CombatPublic`、`PlayersPublic`、
  `MonstersPublic`、`PublicCardCounts`、`PublicEffects`、
  `RngConsumptionCounts`、`ModContributions`。每个受支持游戏构建必须固定到
  字段/访问器白名单；
- `PublicCardCounts` 只允许本体已向该观察者公开的手牌/抽牌/弃牌/消耗数量，
  禁止卡牌 ID、手牌身份、牌堆顺序、选择候选和动态实例字段；其他类别同样禁止
  种子、RNG 状态、未公开路线/奖励和隐藏意图；
- 契约测试必须用 access spy 证明被禁止 getter/field 从未读取。无法在不访问
  隐藏状态的前提下构造的类别直接标为 Unsupported，不借用 C9 同意来扩大 F1；
- 分类序列化必须稳定排序、显式数值格式、无 locale 依赖；
- F2 保存“双方最后一致 checkpoint”和“最早观测到不同的 checkpoint”；
- 没有连续证据时必须使用“first observed”，不能声称“first caused”；
- F3 只记录已公开动作类型、actor 公共标识和完成结果类别；
- F4 只通过已审计 RNG 调用的 postfix 递增计数；任何 patch 不匹配时整个
  `RngConsumptionCounts` 类别变为 Unsupported；
- 不展示摘要值，不把类别差异自动映射到责任 Mod。

### 10.4 F1SchemaV1

F1 不能只写“按类别 hash”；首版规范化 schema 固定如下：

| 类别 | 允许字段 | 上限 |
|---|---|---|
| `RunPublic` | act、floor、当前公开 room kind | 1 组 |
| `CombatPublic` | round、公开 phase、action-running 布尔 | 1 组 |
| `PlayersPublic` | session ordinal、HP/max HP、block、energy、ready、已公开手牌数量 | 16 players |
| `MonstersPublic` | 原生稳定 entity ID、公开 model ID、HP/max HP、block | 32 monsters |
| `PublicCardCounts` | 每玩家已公开 hand/draw/discard/exhaust 数量 | 16 players |
| `PublicEffects` | 已公开 owner ordinal、power/relic kind、model ID、可见 stack | 256 entries |
| `RngConsumptionCounts` | 审计过的固定 stream ID、已发生调用计数 | 64 streams |
| `ModContributions` | manifest ID、F6 最新 state digest | 256 Mods |

V1 byte grammar：

- 所有整数均 little-endian；`bool` 恰好一个 byte 且只允许 `0/1`；V1 不含浮点；
- `str` 编码为 `byte_length:u8 || strict_utf8_bytes`，最大 64 bytes，不做 Unicode
  normalization；无效 UTF-8 或超限使类别 Unsupported；
- 每个类别 blob 以
  `"CGF1":4 bytes || schema:u16(1) || category_code:u8 ||
  category_available:u8 || record_count:u16` 开始；available 只允许 `0/1`，
  为 0 时 record count 必须为 0；
- category code 与 availability bit 顺序固定：
  `1/bit0 RunPublic`、`2/bit1 CombatPublic`、`3/bit2 PlayersPublic`、
  `4/bit3 MonstersPublic`、`5/bit4 PublicCardCounts`、
  `6/bit5 PublicEffects`、`7/bit6 RngConsumptionCounts`、
  `8/bit7 ModContributions`；
- 每条 record 都以 `field_mask:u16` 开始；bit 0 对应随后列出的第一个字段，
  bit 1 对应第二个字段，依此类推。bit 为 1 才按声明顺序编码该字段；missing
  字段绝不编码占位数值，数值 0 与 missing 不同。排序 key 字段必须 present，
  否则类别 Unsupported。未知 enum 一律 `u16 0xFFFF`，且不能自行扩展 native
  enum 数值；
- `RunPublic` record：
  `field_mask:u16, act:i32, floor:i32, room_kind:u16`。room kind 固定为
  `0 unknown, 1 combat, 2 elite, 3 boss, 4 event, 5 shop, 6 rest,
  7 treasure, 8 map, 0xFFFF unsupported`；
- `CombatPublic` record：
  `field_mask:u16, round:i32, phase:u16, action_running:bool`。phase 固定为
  `0 unknown, 1 setup, 2 player, 3 enemy, 4 resolving, 5 ended,
  0xFFFF unsupported`；
- `PlayersPublic` record：
  `field_mask:u16, session_ordinal:u8, hp:i32, max_hp:i32, block:i32,
  energy:i32, ready:bool, public_hand_count:u16`。session ordinal 是主机在
  当前 `{session_id, membership_epoch, roster}` commit 中分配并由所有 peer
  验证的 `0..15`；按 ordinal 排序；
- `MonstersPublic` record：
  `field_mask:u16, native_entity_id:u64, model_id:str, hp:i32, max_hp:i32,
  block:i32`，按 `native_entity_id` 再按 model raw bytes 排序；支持构建若没有稳定
  原生 entity ID，整个类别 Unsupported，禁止用迭代顺序代替；
- `PublicCardCounts` record：
  `field_mask:u16, session_ordinal:u8, hand:u16, draw:u16, discard:u16,
  exhaust:u16`，
  按 session ordinal 排序；
- `PublicEffects` record：
  `field_mask:u16, owner_kind:u8, owner_id:u64, effect_kind:u8, model_id:str, stack:i32,
  multiplicity:u16`。owner kind 固定为
  `1 player, 2 monster, 3 run, 0xFF unsupported`；effect kind 固定为
  `1 power, 2 relic, 0xFF unsupported`。player owner ID 是 session ordinal
  零扩展到 u64，monster 使用稳定 native entity ID，run 使用 0。按前五个字段
  的编码字节排序；完全
  相同项合并 multiplicity，因而不需要不稳定 instance ordinal；
- `RngConsumptionCounts` record：
  `field_mask:u16, stream_id:str, consumed:u64`，stream ID 是受支持构建审计中固定的 ASCII
  API 名称，不含 seed/value，按 raw bytes 排序；未知 stream 不进入 V1；
- `ModContributions` record：
  `field_mask:u16, mod_id:str, provider_schema:u16, source_revision:u64,
  digest:32 bytes`，
  按 mod ID raw bytes 排序；
- 任何排序 key 重复而又不允许 multiplicity、数值越界、集合超限或 blob 累计
  超过 64 KiB，都使该类别 Unsupported；
- available 类别的 `local_digest = SHA-256(exact_category_blob)`；Unsupported
  类别的 overall availability bit 为 0，对应 wire tag 固定为 16 个零 byte 且
  不参与 mismatch；
- SHA-256 完整类别摘要只留内存，禁止进入 UI、日志、报告或 wire；
- 每个支持构建维护 byte-for-byte 黄金向量，覆盖八类别、每个 mask、空集合、
  最大集合、unknown enum、重复 key、不同 locale 和边界整数。

跨端只发送会话盐化 tag：

```text
context_tag = Truncate128(SHA-256("CGF1CTX" || strict_utf8_native_context))
tag = Truncate128(HMAC-SHA256(
  key = session_id,
  schema || checkpoint_id || context_tag || category_code || local_digest))
```

native context 在 hash 前最大 64 UTF-8 bytes，超限/无效时 F1 为 Unsupported。
单条 F1 消息固定为
`schema:u16 || checkpoint_id:u64 || context_tag:16 bytes ||
availability_mask:u8 || tags:8*16 bytes`，不包含历史或原始字段。客户端只发给主机；
主机按相同 checkpoint
比较后向参与者返回类别 mismatch bitmask，不转发其他人的 tag。session rollover
会改变 tag，避免跨会话稳定关联。如果当前网络 API 不能证明消息为定向 host-only，
则跨端 F1 保持 Disabled。该机制只证明同一会话内 tag 相等，不是反作弊。

### 10.5 Forensics 同意与 checkpoint 截止点

- F1–F4 和 F6 Optional Diagnostics 默认关闭。只有当前 roster 的所有玩家在同一
  `{session_id, membership_epoch}` 明确开启，并收到主机 Active commit 后才发送；
- 任一成员变化、撤回或模块失效先同步停止发送并清空 tag/journal peer cache；
  旧 epoch 数据在解析前丢弃。该状态永远不参与 ready/start；
- F6 Optional API 使用 `PublishStateDigest(modId, schemaVersion,
  sourceRevision, sha256Digest)`。Mod 在自己的公开确定性状态改变时主动推送缓存值；
- CoopGuard 在收到自然 `ChecksumGenerated` callback 的入口时立即冻结当时最新值。
  callback 入口之后到达的 publish 只属于下一个自然 checkpoint；
- CoopGuard 不等待 publisher、不改变事件订阅顺序、不请求补发，也不因 missing
  contribution 阻塞游戏；missing 只使对应 `ModContributions` 项 Unknown。

## 11. Mod 作者 API

首版 API 保持 push-only，避免 CoopGuard 在游戏线程同步调用第三方委托。
需要把设置摘要纳入 Guard 的 Mod，必须在自身包内附带一个受现有包指纹保护的
固定声明文件，声明 provider ID 和 schema；声明文件不包含用户设置值。运行时
Mod 再主动发布摘要：

```text
PublishSettingsDigest(modId, schemaVersion, sha256Digest)
PublishStateDigest(modId, schemaVersion, sourceRevision, sha256Digest)
RecordPublicDiagnosticEvent(modId, eventCode)
```

同一个公开 facade 在内部必须路由到两个不互相依赖的实现：

| 入口 | 所属 | 生命周期 | 失败语义 |
|---|---|---|---|
| `PublishSettingsDigest` | Guard Settings Digest API | 随 Guard 哨兵初始化，不需要 lobby 同意 | 对包内已声明 provider fail-closed |
| `PublishStateDigest` / `RecordPublicDiagnosticEvent` | Optional Diagnostics API | 随 Forensics 启停并受 lobby 能力/同意约束 | fail-open，仅该诊断类别不可用 |

Optional Diagnostics API 被关闭、故障注入或模块熔断时，不得卸载、清空或改变已经
冻结的 Guard 设置摘要；反过来，Guard Settings API 错误也不能使可选模块伪造
Guard 已通过。必须有双向隔离测试证明这两条边界。

约束：

- `modId` 必须与已加载 manifest ID 一致；
- schema、eventCode 和长度严格验证；
- 只接收已经计算好的 32 字节摘要，不接收设置值或状态内容；
- 存在设置声明文件时，缺少、重复、过期或 schema 不符的设置摘要在 Guard gate
  fail-closed；没有声明文件的 Mod 只显示“未声明”，不能被误报为设置安全；
- 设置摘要必须在 Mod 初始化结算期限内发布，并与包指纹一起冻结进原生 Mod-list
  条目；冻结后重复发布同一摘要是幂等操作，发布不同摘要会设置 sticky
  restart-required，并在下一个现有 Guard gate 阻断，不能静默替换已暴露条目；
- 同一 Mod/类别只保存最新值，限频每秒 2 次；
- checkpoint contribution 只用于可选 F1，不影响 STS2 原生 checksum；
- API 异常只返回失败，不向调用方抛出跨 Mod 异常。

恶意 Mod 可以伪造数据，因此 API 是合作式诊断，不是远程证明。

## 12. UI 与可访问性

### 12.1 信息架构

沿用一个驾驶舱入口，不为 52 项建立 52 个窗口：

1. Overview：H1/H2/H3/H7/H10/H12。
2. Timeline：H5/C3/C4/D3/D4。
3. Compatibility：H9/G1–G12。
4. Recovery：C5/C6/D5/D6/D8/D9。
5. Forensics：D10/F1–F6，仅在能力存在时显示。

Ctrl+F8 保持为健康入口；现有致命错误弹窗仍只处理阻塞问题。普通状态进入
就地 HUD 或 H12 警报中心。

### 12.2 强制交互规则

- 危险/阻塞状态：短标题、原因、证据、下一步和一个主操作；
- 普通警告不抢焦点、不暂停游戏、不重复弹窗；
- 同类警报按 `(event_code, peer, session)` 去重；
- 身份状态同时使用颜色、编号/符号和玩家名；
- 正文普通文字对比度至少 4.5:1，大字至少 3:1；
- 100%–200% 文本缩放不截断核心状态或操作；
- 全部操作可用键盘/控制器遍历，焦点顺序稳定；
- 声音有视觉等价提示，尊重游戏静音/音量，10 秒内同类只播放一次；
- 图表提供文字摘要，如“过去 60 秒 p95 180 ms、丢包峰值 4%”。

## 13. 资源预算与 SLO

安全不变量不是概率目标：支持矩阵中的已知 Guard 不安全条件必须全部拦截。
可选能力使用以下 SLO：

CoopGuard 不上传生产遥测，因此这里是 Release 验证 SLO，不冒充线上观测值。
一个 eligible client-minute 指支持构建上功能已启用且数据源可用的一分钟；该分钟
只有在无 callback 逃逸异常、UI 没有阻碍原生进度且新鲜度目标满足时才算 good。
每个 Release 候选必须累计至少 2,000 个 eligible client-minutes，目标
`good/eligible ≥99.9%`。主动关闭和明确 Unsupported 不进入分母，但错误降级不能
借此排除。

| 指标 | 目标 |
|---|---|
| 可选 UI 不影响游戏继续的 session-minute | ≥ 99.9% |
| 本地事件状态新鲜度 | p95 ≤ 500 ms |
| 1 Hz 采样指标的数据年龄 | p95 ≤ 1.25 s |
| 跨端声明状态新鲜度 | p95 ≤ 2 s；超时立即标记 stale |
| HUD 主线程开销 | p95 ≤ 0.25 ms/frame，p99 ≤ 0.75 ms/frame |
| 稳态可选模块分配 | ≤ 10 KiB/s，8 小时无单调增长 |
| 可选模块额外内存 | p95 ≤ 32 MiB，4 客户端场景 |
| 诊断网络平均流量 | ≤ 1 KiB/s/peer |
| 诊断网络硬突发 | ≤ 4 KiB/s/peer，单 payload ≤ 4096 B |
| 图表采样 | 1 Hz，60 秒/peer，即最多 60 点 |
| 报告磁盘保留 | ≤ 20 份且总计 ≤ 25 MiB |
| 发布包新增依赖 | 0 |
| callback 逃逸异常 | 0 |
| 隐私模糊测试泄漏 | 0 / 1,000,000 个生成样本 |

错误预算只适用于可选 UI。任何 Guard 回归、状态写入、隐私泄漏、远端输入导致
崩溃或 Release 中存在故障注入入口，错误预算为零并立即停止发布。

性能证据必须记录参考机 CPU/GPU/RAM/OS、游戏/Guard 构建、拓扑、seed 和场景；
先 warm-up 60 秒，再采集至少 10 分钟与 10,000 帧，重复三次并报告各次及中位数，
不能只挑最好的一次。启用/禁用 Toolkit 使用相同 seed、拓扑和场景。

## 14. 实施阶段与变更纪律

全部 52 项进入最终范围，但禁止一次性“大爆炸”实现。每个阶段都必须能独立
发布或安全放弃：

| 阶段 | 范围 | 出口条件 |
|---|---|---|
| P0 基础 | Lifecycle、Observation、UI shell、限界/红action测试 | Guard 全回归；可选模块强制崩溃仍可联机 |
| P1 本地驾驶舱 | H1/H2/H4/H5/H7/H8/H10/H11/H12、C4/C5/C6(lobby-only)/C7/C8、D1–D5/D8 | 无自定义消息；2-client/8h soak 通过；running C6 明确 Unsupported |
| P2 环境治理 | G1–G10/G12、D6/D9、F6 Guard Settings API | 不修改游戏/存档/其他 Mod；只允许用户导出、原生保存后 sidecar 和获同意的报告历史写入 CoopGuard 自有目录；原子性测试通过 |
| P3 可选协作 | H3/H6/H9、C1–C3/C9/C10、G11、D7/D10、F5 | ADR 0003 所有安全门槛通过 |
| P4 高级取证 | F1–F4、F6 Optional Diagnostics API | 不额外生成 checksum/RNG；错误定位无越权数据 |
| P5 工程化 | F7/F8 和完整矩阵 | Release 包检查、2/3/4-client CI、Steam人工验收 |
| P6 本体能力解锁 | C6 running rejoin | 新 STS2 构建存在生产恢复消费者，签名审计和真实双客户端恢复通过 |

每个代码变更：

1. 只实现一个可独立审查的垂直功能或一个共享安全边界；
2. 同一变更包含对应小型测试；
3. 不提交无法运行的中间抽象；
4. 修改 wire format 时单独提升 Diagnostics Protocol；
5. 修改 Guard wire format 时按现有规则提升 Guard Protocol；
6. 分布式 DLL 字节变化时提升 manifest 版本；
7. 更新支持构建前重跑完整兼容矩阵；
8. 在 `docs/WORKLOG.md` 记录设计、测试和已知限制。

## 15. 发布门禁

Release 候选必须全部通过：

- `dotnet build` Release + warnings as errors；
- 现有 FingerprintSelfCheck；
- 新增纯逻辑 self-check；
- Harmony 目标签名 smoke；
- 2/3/4 客户端 matching、mismatch、迟到加入、离开、超时、loaded run、
  pre-run lobby rejoin，以及 v0.109.1 running rejoin 的明确 Unsupported 路径；
- 可选通道未知版本、丢包、乱序、重复、超长、洪泛和成员变化；
- UI 100%/150%/200% 缩放、键盘/控制器和色觉模拟检查；
- 8 小时 4 客户端 soak；
- 报告和 peer payload 的隐私/注入 fuzz；
- Debug 故障注入点在 Release 二进制和两文件包中不可触达；
- 在真实 Steam 传输上完成至少 2 客户端 smoke；
- Workshop 上传前后字节 hash 一致，并再次确认 Public 可见度。

任何一项失败都不得用“功能降级”掩盖 Guard、安全、隐私或状态不变性问题。
只有纯可选显示项可以在明确标记 Unsupported 后推迟。

## 16. 可追踪性

需求、验收与测试使用固定 ID：

```text
Feature: H1
Acceptance: AT-H01-01
Engineering test: CG-TST-CORE-MP2-003
Fault injection: CG-FLT-NET-001
```

`AT-*` 是用户验收契约；可执行工程测试统一使用 `CG-TST-*`，故障目录统一使用
`CG-FLT-*`。禁止再引入 `TC-*`/`FI-*` 第三套命名。唯一 registry 和退休规则见
测试计划。

- H/C 的逐项验收见 `MULTIPLAYER_TOOLKIT_ACCEPTANCE_HC.md`。
- G/D 的逐项验收见 `MULTIPLAYER_TOOLKIT_ACCEPTANCE_GD.md`。
- F 及跨功能测试见 `MULTIPLAYER_TOOLKIT_TEST_PLAN.md`。
- 可选诊断协议决策见 `decisions/0003-optional-diagnostics-plane.md`。

只有需求、验收和至少一个正常/一个异常测试都存在时，功能才允许从 Proposed
进入 Implementing；只有全部 MUST 验收通过并有证据时，才允许进入 Released。
