# BetterCoop v0.6.0 验收标准

- 文档状态：Active RC gate
- 适用设计：[BetterCoop v0.6.0 技术方案](BETTERCOOP_V0_6_TECHNICAL_DESIGN.md)
- 适用版本：BetterCoop v0.6.0
- 原则：任一 MUST 未通过时，不得把对应功能标记为 Release

## 1. 结果定义

每条验收项只能记录以下结果之一：

| 结果 | 定义 |
|---|---|
| Pass | 在指定构建、拓扑和证据要求下全部满足 |
| Fail | 已执行且至少一个 oracle 不满足 |
| Unsupported | 已证明当前构建/API 不具备安全实现条件 |
| Not Run | 未执行或证据不完整 |

`Unsupported` 不是 `Pass`。它允许其他相互隔离的功能发布，但对应功能必须在 UI
中明确不可用。以下结果不能算通过：

- “没有看到报错”；
- 只检查房主；
- 只进入 lobby 或只进入 run；
- `Requesting 0 actions` 单行日志；
- 没有 commit、二进制 hash、构建、拓扑和每 peer sentinel 的手工回忆。

## 2. 全局发布门禁

| ID | 验收标准 |
|---|---|
| V6-AC-CORE-001 | BetterCoop v0.5.0 的 Guard、诊断、自检、Harmony smoke 和 2/3/4 人矩阵全部无回归。 |
| V6-AC-CORE-002 | Guard Protocol 6 在包、协议、R1 设置或 G13 有效环境不一致时，于准备/开局前给出具体 peer 和字段。 |
| V6-AC-CORE-003 | C11、H13、F9 任一 handler、renderer 或 hook 抛错时，仅对应模块熔断；原生联机和 Guard 继续。 |
| V6-AC-CORE-004 | Run Control 事务不存在部分 peer 仍活动于旧节点、部分 peer 活动于目标节点的可继续 split-brain。 |
| V6-AC-CORE-005 | 所有远端长度/count 在分配前验证；协议 fuzz 1,000,000 次零崩溃、零逃逸异常、缓存保持有界。 |
| V6-AC-CORE-006 | 16 人满载时所有 peer ordinal、颜色、缓存键和 UI 身份稳定唯一；P17 在分配前拒绝。 |
| V6-AC-CORE-007 | Release 包无 test-driver、fault injection、调试命令、原始聊天/seed/save 输出路径。 |
| V6-AC-CORE-008 | 全部验收结果能从 requirement ID 追踪到测试 ID 和证据路径。 |

## 3. C11 有界自由文本

### 3.1 功能

| ID | 验收标准 |
|---|---|
| V6-AC-C11-001 | 中文、英文、数字、标点、Emoji 和 ZWJ 文本经 NFC 规范化后稳定 round-trip。 |
| V6-AC-C11-002 | 空文本、384-byte、256-scalar、4-line 边界行为确定；任一上限 +1 在发送前拒绝并给出本地原因。 |
| V6-AC-C11-003 | 客户端不能声明发送者；所有接收端显示的 ordinal 来自房主的原生 peer 映射。 |
| V6-AC-C11-004 | 同一 session 内房主 sequence 去重且有界重排；重放、旧 session、旧 epoch 和未知 peer 全部丢弃。 |
| V6-AC-C11-005 | 正常 ENet 负载下，从发送确认到其他 peer 可见的延迟 p95 不超过 500 ms。 |
| V6-AC-C11-006 | 本地按玩家静音/全部静音立即生效，且不改变远端、Guard、ready 或原生动作。 |
| V6-AC-C11-007 | IME、Enter、Shift+Enter、Esc、键盘和手柄焦点流程无丢字、误发送或焦点陷阱。 |

### 3.2 安全、隐私和资源

| ID | 验收标准 |
|---|---|
| V6-AC-C11-008 | NUL、非法 UTF-8、C0/C1、双向覆盖和组合字符洪泛在创建大对象前被拒绝。 |
| V6-AC-C11-009 | BBCode、Markdown、HTML、URL、路径和 `/rollback` 等命令样式文本只能惰性显示，零执行、零自动打开。 |
| V6-AC-C11-010 | 每发送者 1 条/秒 burst 2、全房间 2 条/秒 burst 6；洪泛不能饿死 Guard、revoke 或 Run Control。 |
| V6-AC-C11-011 | 历史不超过 100 条或 64 KiB；退出 lobby/session 变化后同步清空。 |
| V6-AC-C11-012 | 普通日志、事故报告、磁盘、剪贴板和自动测试证据不包含聊天正文。 |
| V6-AC-C11-013 | 渲染或提示音故障只使 C11 显示 Degraded/Disabled，不影响 run。 |

## 4. H13 快捷队友手牌

### 4.1 易用性

| ID | 验收标准 |
|---|---|
| V6-AC-H13-001 | C9 Active 后，从战斗 HUD 到看到最近/固定队友手牌最多一次点击或按键。 |
| V6-AC-H13-002 | 切换前一/后一名队友最多一次操作；当前选择在布局刷新后保持。 |
| V6-AC-H13-003 | 抽牌、弃牌、升级和当前费用变化在正常链路下 p95 1 秒内可见。 |
| V6-AC-H13-004 | 本地已知卡显示名称、图像、升级、费用和原生 tooltip；未知卡使用 model ID 占位且不抛错。 |
| V6-AC-H13-005 | 2/4/5/8 人完整可达；16 人滚动/虚拟化时不创建无界节点。 |
| V6-AC-H13-006 | 1280×720、200% UI 缩放、键盘和手柄下关键信息无裁切且无焦点陷阱。 |

### 4.2 同意、时效和隐私

| ID | 验收标准 |
|---|---|
| V6-AC-H13-007 | 未完成 C9 全员同意时，手牌网络发送、peer 缓存和 UI 明细均为零。 |
| V6-AC-H13-008 | 本地撤回立即停止发送；其他 peer 在 1 秒内隐藏并清零该玩家明细。 |
| V6-AC-H13-009 | 加入、离开、rejoin、host/session 或 roster epoch 变化使旧同意和快照立即失效。 |
| V6-AC-H13-010 | 1 秒无更新显示 Stale；3 秒无更新隐藏明细，不把旧牌伪装为实时状态。 |
| V6-AC-H13-011 | 日志、事故报告、聊天和自动截图证据不包含具体牌列表。 |
| V6-AC-H13-012 | H13 不新增卡牌数据 schema；现有 C9 `HandSnapshot` golden vector 保持兼容，只新增有界 `HandWatch` 路由控制。 |
| V6-AC-H13-013 | 每个 viewer 最多订阅一个详细 owner；latest-only 公平队列在 16 人聊天洪泛时仍满足 H13-003，且不饿死 Run Control。 |

## 5. R1 房主节点回溯

### 5.1 能力和授权

| ID | 验收标准 |
|---|---|
| V6-AC-R1-001 | R1 默认关闭；启用状态参与 Guard Protocol 6，一致性在开局前验证。 |
| V6-AC-R1-002 | 只有原生 lobby 认定的房主可经“选择目标+破坏性模态确认”发起；客户端 payload、聊天、路径或重放消息不能触发。 |
| V6-AC-R1-003 | 目标只包含当前 run 已访问且原生保存成功的稳定房间节点；未访问、中间动作和无效 checkpoint 不可选。 |
| V6-AC-R1-004 | 请求发生在不安全阶段时只进入 Pending；动作、选择和自然 checkpoint 完成前不写磁盘。 |
| V6-AC-R1-005 | build、包环境、seed tag、roster、hash、run 和 branch 不匹配时，在修改 active save 前拒绝。 |
| V6-AC-R1-006 | 5 人及以上只有在 G13 Compatible 且 loaded-run 能力通过相同人数验证时可用。 |

### 5.2 Checkpoint 完整性

| ID | 验收标准 |
|---|---|
| V6-AC-R1-007 | 每个成功完成 STS2 原生保存的稳定房间入口都归档完整 save checkpoint，且能通过 schema、长度和 SHA-256 校验。 |
| V6-AC-R1-008 | journal 索引丢失后可仅从有效条目重建；损坏条目被隔离且不影响 active save。 |
| V6-AC-R1-009 | 临时写、flush、hash、replace 任一点失败时，始终保留至少一个有效 active save 和 emergency backup。 |
| V6-AC-R1-010 | 每 run 最多 128 条、单条 32 MiB、总计 256 MiB；达到边界明确停止新记录，不静默覆盖恢复点。 |
| V6-AC-R1-011 | 网络消息只含随机 checkpoint ID 和有界元数据，不含本地路径或 save payload。 |

### 5.3 事务正确性

| ID | 验收标准 |
|---|---|
| V6-AC-R1-012 | 状态机只接受合法转移；同一时刻最多一个事务，重复 transaction ID 幂等拒绝。 |
| V6-AC-R1-013 | 所有当前 peer 必须在 10 秒内 ACK 同一 session/epoch 和安全状态；任一拒绝、掉线或超时则中止。 |
| V6-AC-R1-014 | 成功路径只通过 STS2 原生 save/Continue/loaded-lobby 恢复，不反射写入运行对象图、动作队列或 RNG。 |
| V6-AC-R1-015 | Commit 前所有 peer 的 node、roster、环境、rollback epoch 和自然 checksum 摘要一致。 |
| V6-AC-R1-016 | 任一失败只允许：全员仍在原节点；或 lobby 关闭且房主保留明确可恢复的有效 save。 |
| V6-AC-R1-017 | 回溯创建新 branch/epoch；旧分支手牌、聊天 sequence、RNG 摘要和诊断状态不能进入新分支。 |
| V6-AC-R1-018 | A→B→C 回溯 B 后，所有 peer 能完成两个完整战斗回合且无 StateDivergence、Timeout 或软锁。 |
| V6-AC-R1-019 | 房主在事务每个持久化阶段被终止后，下次启动能确定恢复旧状态或已提交目标，无损坏、无自动猜测。 |
| V6-AC-R1-020 | ENet 本机成功回溯耗时 p95 不超过 30 秒；真实 Steam 五人目标为 60 秒，超时安全中止。 |

## 6. G13 Multiplayer Limit Break

### 6.1 身份和契约

| ID | 验收标准 |
|---|---|
| V6-AC-G13-001 | 按 manifest ID `STS2-MultiplayerLimitBreak` 识别，不依赖 Workshop 文件夹号、显示名或文件名。 |
| V6-AC-G13-002 | 初始 allowlist 精确覆盖 v0.1.3、DLL SHA-256 `b2785afd3dc31fd6b32cb073af495ab343fcc31fa9e479049959ff43eb09356f`、STS2 v0.109.1、玩家上限 16 和 RitsuLib 最低 v0.4.13；至少实测 v0.4.66。 |
| V6-AC-G13-003 | 所有 peer 的 Limit Break/RitsuLib ID、版本、包 bytes、14/4/2 patch 组和大厅阶段有效设置摘要一致；证明由房主在星型拓扑中转发。 |
| V6-AC-G13-004 | Limit Break disabled、RitsuLib 缺失/过旧、hash 不同、patch Partial、设置不同、Steam capacity 未确认或反射形状漂移时，第 5 人场景在开局前精确拒绝。上游只在 `RunManager` 建立后写入房主 sidecar，因此大厅中的 sidecar Pending 不得形成循环阻塞；进入 run 后若仍未同步则严格 G13 保持 Pending 并阻止 R1。 |
| V6-AC-G13-005 | 适配器不修改第三方设置、难度缩放、房间布局或 Harmony owner。 |
| V6-AC-G13-006 | BetterCoop 和 Limit Break 两种初始化顺序下，受支持 hook 数量和结果相同。 |

### 6.2 人数和流程

| ID | 验收标准 |
|---|---|
| V6-AC-G13-007 | 未安装 Limit Break 的 2/4 人矩阵与 v0.5.0 基线一致。 |
| V6-AC-G13-008 | 安装并启用 Limit Break 的 2/4 人矩阵与原生/第三方基线一致。 |
| V6-AC-G13-009 | 5 人每个客户端完成加入、run、抽牌、PlayPhase、合法动作、敌方回合和下一玩家回合。 |
| V6-AC-G13-010 | 8 人覆盖地图、战斗、奖励、休息点、商店、宝箱、保存和重新载入，所有 peer 继续行动。 |
| V6-AC-G13-011 | 16 人至少完成 lobby、ready、embark、ordinal/颜色/UI/codec 边界 smoke；P17 分配前拒绝。 |
| V6-AC-G13-012 | 8 人 8 小时长稳中缓存、订阅、后台任务和 Godot 节点无单调增长；无 BetterCoop 导致的断线或卡回合。 |
| V6-AC-G13-013 | 真实 Steam 五客户端完成 G13-009；ENet 证据不能替代。 |

### 6.3 Loaded-run 硬门禁

| ID | 验收标准 |
|---|---|
| V6-AC-G13-014 | 先完成 `LoadRunLobby`、loaded-run message 和玩家 count/slot 编解码的 API/IL spike，形成逐字段证据。 |
| V6-AC-G13-015 | 若 v0.1.3 已完整覆盖 loaded-run，则 BetterCoop 只验证；若确有缺口，只允许对已证明缺失的 STS2 loaded-run 点安装窄 shim。 |
| V6-AC-G13-016 | 窄 shim 必须绑定精确 STS2/Mod 版本和 hash、事务式安装、owner 独立，并在第三方未来已覆盖时拒绝双重 patch。 |
| V6-AC-G13-017 | 5/8 人 native loaded-run 往返后，所有 ordinal、roster、node、手牌和动作继续正确，才能让相应人数的 R1 退出 Unsupported。 |

## 7. F9 种子与 RNG 分析

### 7.1 正确性和零侵入

| ID | 验收标准 |
|---|---|
| V6-AC-F9-001 | seed 的字节顺序、格式和 culture-independent 输出通过固定 golden vector。 |
| V6-AC-F9-002 | 活跃联机默认只显示 equality 和 session-salted tag；原始 seed 仅本机明确展开。 |
| V6-AC-F9-003 | spy 证明所有 F9 路径对 `Rng.Next*`、克隆、内部 state 和 STS2 checksum 入口的额外调用次数为 0。 |
| V6-AC-F9-004 | 已知 stream 的 call index、method、参数摘要和返回摘要与审计 fixture 完全一致。 |
| V6-AC-F9-005 | `Rng.Chaotic` 和未知 stream 明确标记，不参与确定性一致结论。 |
| V6-AC-F9-006 | 相同 seed、build、epoch 和动作序列在自然 checkpoint 产生相同计数和摘要。 |
| V6-AC-F9-007 | 注入“仅报告层”的计数漂移后，在下一自然 checkpoint 定位首个不同 stream/index，不修改真实 RNG。 |
| V6-AC-F9-008 | schema、checkpoint、branch 或 epoch 不同显示 Unknown，不制造假分歧。 |

### 7.2 隐私、性能和降级

| ID | 验收标准 |
|---|---|
| V6-AC-F9-009 | 网络、普通日志、事故报告和剪贴板默认不含 raw seed、RNG state 或返回值。 |
| V6-AC-F9-010 | 只交换 `HMACSHA256(sessionNonce, normalizedSeedBytes)` tag、计数和摘要；退出 lobby 后旧摘要不可跨 session 关联。 |
| V6-AC-F9-011 | RNG hook 稳态开销相对无 BetterCoop 基线 p95 不超过 5%，热路径 warm-up 后无与历史长度相关的分配增长。 |
| V6-AC-F9-012 | Harmony 目标或 stream 映射缺失只使相应 F9 类别 Unsupported，其他诊断和 Guard 不受影响。 |
| V6-AC-F9-013 | 回溯后从目标 checkpoint 建立新 baseline；旧分支事件不参与新分支比较。 |
| V6-AC-F9-014 | Release UI 和二进制不包含未来 RNG 预测、重掷或牌序预览入口。 |

## 8. Google 规范和仓库质量

| ID | 验收标准 |
|---|---|
| V6-AC-ENG-001 | 根 `.editorconfig` 覆盖全部 tracked C#：2 空格、无 tab、100 列、brace、using 顺序和 Google 命名。 |
| V6-AC-ENG-002 | `Directory.Build.props` 对生产、测试和 test-driver 启用 nullable、deterministic、内置 analyzer、code style in build 和 warnings-as-errors。 |
| V6-AC-ENG-003 | `dotnet format --verify-no-changes` 对全部项目返回 0。 |
| V6-AC-ENG-004 | Harmony 特殊参数以最窄规则豁免并写原因；不存在项目级 broad suppression 或扩大的 `NoWarn`。 |
| V6-AC-ENG-005 | PSScriptAnalyzer 对 tracked PowerShell 零 error；warning 必须修复或逐条记录理由。 |
| V6-AC-ENG-006 | 所有 JSON/XML 可解析、JSON 无重复键；Markdown 标题和本地链接检查通过。 |
| V6-AC-ENG-007 | 纯格式迁移与功能行为变更分开，且迁移前后 Release DLL 的行为自检结果一致。 |
| V6-AC-ENG-008 | Release build 零 warning/error；FingerprintSelfCheck、HarmonySmoke 和协议 fuzz 全通过。 |
| V6-AC-ENG-009 | 每个 C11/H13/R1/G13/F9 ID 至少映射一个自动测试；每个人工测试说明无法自动化的原因。 |
| V6-AC-ENG-010 | 所有 material design/action 记录到 WORKLOG；wire、DLL 或 manifest 变化同步升级相应版本。 |

## 9. 质量门禁层级

| 阶段 | 必须通过 |
|---|---|
| 每次变更 | format、Release build、self-check、相关 unit/component test |
| Pull Request | 上述全部、protocol fuzz、Harmony smoke、ENet MP2 |
| Nightly | 2/4/5/8 ENet、fault injection、隐私扫描、UI/A11Y、短 soak |
| Release Candidate | 全验收、8 人 8 小时、16 人 boundary smoke、真实 Steam 5 人 |
| Workshop/GitHub 发布前 | 候选包 hash 复验、manifest/协议/说明一致、无调试资产 |

任何 R1 或 G13 loaded-run 项为 Fail/Not Run/Unsupported 时，不得在发布说明中宣称
“大于 4 人可回溯”。如果 C11/H13/F9 已独立通过，可以按实际状态发布，但必须
逐项准确标注。

## 10. Requirement-to-test 最低映射

| Requirement | 最低自动层 | 必要真实环境 |
|---|---|---|
| C11 | unit、security fuzz、MP2、MP8 flood | Steam 仅做发布 smoke |
| H13 | unit、component、MP4、MP8 UI | 真实战斗、键盘/手柄 |
| R1 | unit、fault、MP2/4/5/8、crash recovery | 原生 loaded-run、Steam 5 人 |
| G13 | contract、Harmony、MP2/4/5/8/16 | Workshop v0.1.3、Steam 5 人 |
| F9 | golden、zero-call spy、MP2/8、performance | 真实 RNG 调用与自然 checkpoint |
| Engineering | style、build、trace、package scan | CI 和最终候选包 |

完整步骤和 oracle 见
[BetterCoop v0.6.0 测试计划](BETTERCOOP_V0_6_TEST_PLAN.md)。

## 11. 当前自动证据（2026-07-31）

| 范围 | 结果 | 证据与限制 |
|---|---|---|
| Release 编译 | Pass | STS2 v0.109.1 隔离程序集；0 warning / 0 error |
| 核心自检 | Pass | checkpoint archive/activate/recover/commit、Run Control codec/roster、G13 分阶段合同；parser fuzz 1,000,000 |
| ENet 2/4，无 Limit Break | Pass | `artifacts/f7-matrix/summary-20260730-181800.json` |
| ENet 5，Limit Break v0.1.3 + RitsuLib v0.4.66 | Pass | `artifacts/f7-matrix/summary-20260730-184815.json`；join/ready/embark、5/5 protocol 和 collaboration sentinel |
| ENet 8，同上 | Pass | `artifacts/f7-matrix/summary-20260730-184913.json`；join/ready/embark、8/8 protocol 和 collaboration sentinel |
| G13 两完整回合、房间矩阵、save/load | Not Run | 上述 smoke 不等同于 V6-AC-G13-009/010 |
| R1 多客户端真实回溯 | Not Run | 磁盘状态机已自动验证；原生 loaded-run、两回合和 Steam 5 人仍是硬门禁 |
| Steam 5 人、MP8 8 小时、MP16 | Not Run | 不得宣称 release-proven |
| Google/C# 工程门禁 | Partial | 4 个项目 whitespace/style verify、3 个 Release build、tracked JSON/XML 和 `git diff --check` 已通过；PSScriptAnalyzer 未运行 |
| PSScriptAnalyzer | Not Run | 当前机器未安装该模块；未为一次检查引入新依赖 |
| v0.6.0 RC1 | Pass | 两文件候选包；DLL SHA-256 `8080A4FF5CA739AB673FD97A629C5E3C35F9C5C17953052BC2989B18F296D5D2` |
