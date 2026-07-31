# BetterCoop v0.6.0 测试计划与测试案例

- 文档状态：Active RC plan
- 技术方案：[BetterCoop v0.6.0 技术方案](BETTERCOOP_V0_6_TECHNICAL_DESIGN.md)
- 验收标准：[BetterCoop v0.6.0 验收标准](BETTERCOOP_V0_6_ACCEPTANCE.md)
- 测试对象：BetterCoop v0.6.0 候选包，不是开发目录中的任意 DLL

## 1. 测试策略

采用“小测试为主、组件契约其次、必要的真实多客户端为发布门禁”的分层：

| 层 | 目的 | 是否自动 |
|---|---|---|
| UT | codec、状态机、边界、golden vector | 必须 |
| SEC | 不可信输入、身份、重放、隐私 | 必须 |
| CMP | STS2/Harmony/Godot/文件系统组件契约 | 必须 |
| MP2/4/5/8/16 | 隔离多客户端与人数边界 | 必须 |
| FAULT | 掉线、异常、崩溃、磁盘故障 | 必须 |
| PERF/SOAK | 热路径开销、内存和长稳 | 必须 |
| STEAM | 真实 Steam lobby/relay/Workshop 组合 | RC 必须 |
| A11Y | 分辨率、缩放、键盘、手柄和 IME | 必须 |

测试不通过时先分类：

- `StateDivergence / reason 1012`；
- `Disconnect / reason 4001 / Timeout`；
- 网络仍正常但进入 `PlayPhase` 后无后续动作的软锁；
- BetterCoop 模块熔断；
- 原生/第三方能力 Unsupported。

`Requesting 0 actions` 本身不是“没有抽牌”或软锁的充分证据，必须结合受影响客户端
后续手牌 revision、PlayPhase 和动作 sentinel。

## 2. 测试环境

### 2.1 固定矩阵

| Profile | STS2 | BetterCoop | Limit Break | RitsuLib | Transport |
|---|---|---|---|---|---|
| Vanilla-2/4 | v0.109.1 | v0.6.0 RC | 无 | 无 | ENet |
| LB-2/4 | v0.109.1 | v0.6.0 RC | v0.1.3 | >=0.4.13 | ENet |
| LB-5/8/16 | v0.109.1 | v0.6.0 RC | v0.1.3 | >=0.4.13 | ENet |
| Steam-LB-5 | v0.109.1 | v0.6.0 RC | Workshop 同一 bytes | 同一 bytes | Steam |

每个 peer 使用隔离的 APPDATA、TEMP、配置、日志和 Mod root。禁止继续使用一个共享
Mod root 来模拟包差异。所有测试必须记录实际 manifest ID/version/hash，不能把
Workshop 文件夹号当身份。

### 2.2 现有测试基础的扩展

复用：

- `tests/FingerprintSelfCheck` 的 `Check`/`Throws`、codec、fuzz、C9 和 RNG fixture；
- `tests/HarmonySmoke` 的真实目标反射、owner 隔离和事务式 patch rollback；
- `tests/Run-MultiplayerMatrix.ps1` 的隔离目录、隐藏进程、PID 清理、超时和证据保留；
- `artifacts/test-driver` 的 ready/start、共享 token 和每 peer sentinel；
- `ToolkitPersistence` 已有原子写入模式。

测试工具必须先补齐：

1. 将矩阵人数验证从 2–4 改为 2–16，P17 明确拒绝。
2. 每个 peer 可指定独立包和设置 fixture。
3. 结果 JSON 增加 commit、DLL/manifest/protocol、STS2 build、Mod hashes、seed tag、
   topology、transport、每 peer sentinel、fault ID 和单调时间。
4. 新增真实战斗驱动：抽牌、进入 PlayPhase、执行合法动作/end-turn、敌方回合、
   第二玩家回合。
5. fault handler 只编入 Debug test-driver，Release 扫描必须证明不存在。

### 2.3 每客户端通过 sentinel

高人数测试中，每个客户端都必须依次产生：

```text
SESSION_NEGOTIATED
RUN_ENTERED
LOCAL_HAND_REVISION
PLAY_PHASE_ENTERED
LOCAL_ACTION_ACCEPTED
ENEMY_TURN_COMPLETED
NEXT_PLAYER_TURN_ENTERED
```

缺任一项即失败或 Unsupported，不能由房主结果代替。

## 3. C11 测试案例

| Test ID | 层/拓扑 | 步骤 | 通过 oracle |
|---|---|---|---|
| CG-TST-C11-UT-001 | UT | 编解码中英、标点、Emoji、ZWJ、组合字符和 CRLF；重复编码 | NFC 后内容稳定，CRLF 变 LF，无 culture 差异 |
| CG-TST-C11-UT-002 | UT | 测试空、1、383/384/385 bytes，255/256/257 scalar，4/5 行 | 合法边界通过，+1 在发送前给出稳定错误码 |
| CG-TST-C11-SEC-003 | SEC | 注入非法 UTF-8、NUL、C0/C1、bidi override 和超长组合序列 | 分配大正文前拒绝，零崩溃、零正文日志 |
| CG-TST-C11-SEC-004 | SEC | 发送 BBCode、Markdown、HTML、URL、路径、shell 和 `/rollback 3` | 仅惰性纯文本显示，不执行、不跳转、不触发 R1 |
| CG-TST-C11-SEC-005 | SEC | 伪造 origin、未知 peer、旧 session/epoch、重复和乱序 sequence | 房主重写身份；非法包丢弃；有界窗口内顺序确定 |
| CG-TST-C11-FUZZ-006 | SEC | 对 header、长度、Unicode 和 sequence 做 1,000,000 次随机/变异输入 | 零未捕获异常；单 peer 和总缓存不增长越界 |
| CG-TST-C11-MP2-007 | MP2 | 双方各发送 20 条合法文本，交错静音/解除静音 | 非静音消息 p95 <=500 ms；显示身份/顺序一致 |
| CG-TST-C11-MP8-008 | MP8 | 8 人同时正常发送，2 人持续洪泛 5 分钟 | 公平限流；正常发送者仍可见；控制和原生动作继续 |
| CG-TST-C11-CMP-009 | CMP | 在 codec、relay、renderer 和提示音分别注入异常 | 只熔断 C11，Guard/ready/run/原生动作通过 |
| CG-TST-C11-A11Y-010 | A11Y | 中文/日文 IME，Enter/Shift+Enter/Esc，键盘与手柄遍历 | 无丢字、误发送、焦点陷阱；文本不被按钮执行 |
| CG-TST-C11-PRI-011 | SEC | 完成聊天后收集普通日志、事故报告、磁盘、剪贴板和退出后内存快照 | 不含消息正文；session 结束历史清零 |

覆盖：V6-AC-C11-001–013。

## 4. H13 测试案例

| Test ID | 层/拓扑 | 步骤 | 通过 oracle |
|---|---|---|---|
| CG-TST-H13-UT-001 | UT | P1–P16 排序、选择保持、stale、unknown card、P17 | 结果稳定；P17 分配前拒绝 |
| CG-TST-H13-UT-002 | UT | 对现有 C9 hand snapshot golden vector 重跑 v0.5 fixture | wire bytes 不变；未新增平行 codec |
| CG-TST-H13-MP4-003 | MP4 | 穷举 OptIn/Commit/Active；真实抽牌、弃牌、升级和费用变化 | Active 前零发送；变化 p95 <=1 秒 |
| CG-TST-H13-SEC-004 | MP4/SEC | Active 后撤回、加入、离开、rejoin、换 host、旧快照乱序 | 本地立即停发；远端 <=1 s 清零；旧 epoch 不复活 |
| CG-TST-H13-STALE-005 | MP4/FAULT | 阻断某 peer 手牌快照 0.9、1.1、3.1 秒后恢复 | 阈值前实时；1 秒 Stale；3 秒隐藏；新 revision 恢复 |
| CG-TST-H13-MP8-006 | MP8 | 8 人各持不同牌，循环选择；同时持续聊天并执行两回合 | 每 viewer 仅一个 detailed watch；切换正确；聊天不挤占手牌/control |
| CG-TST-H13-MP16-007 | MP16 | 16 人、每人 64 张合成快照，持续滚动 10 分钟 | UI 虚拟化；Godot 节点/内存不随滚动单调增长 |
| CG-TST-H13-A11Y-008 | A11Y | 1280×720、200% 缩放、键盘和手柄打开/切换/关闭 | 关键信息可见，无裁切和焦点陷阱 |
| CG-TST-H13-CMP-009 | CMP | ModelDb 缺卡、缺图、tooltip 异常、UI 节点释放 | 安全占位；只降级 H13；游戏线程无逃逸异常 |
| CG-TST-H13-PRI-010 | SEC | 收集日志、报告、聊天和自动截图证据 | 不含具体牌列表 |

覆盖：V6-AC-H13-001–013。

## 5. R1 测试案例

### 5.1 状态机、文件和安全

| Test ID | 层 | 步骤 | 通过 oracle |
|---|---|---|---|
| CG-TST-R1-UT-001 | UT | 穷举状态机合法/非法转移、重复 ID、并发请求和超时 | 只允许规范转移；一次一个事务；重放幂等拒绝 |
| CG-TST-R1-UT-002 | UT | 建立 A/B/C branch，回溯 B，再注入旧 C 消息 | 新 epoch 生效；旧未来、手牌、聊天和 RNG 摘要拒绝 |
| CG-TST-R1-CMP-003 | CMP | 对 snapshot schema、长度、hash、索引重建和损坏条目测试 | 只列出完整有效 checkpoint；索引可重建 |
| CG-TST-R1-FAULT-004 | FAULT | 在 temp write、flush、hash、backup、replace、commit 各点抛异常 | 始终至少有一个有效 active save 和 emergency backup |
| CG-TST-R1-SEC-005 | SEC | 客户端伪装房主、路径穿越、远端 checkpoint path、篡改 hash | 所有请求在磁盘 IO 前拒绝，无任意文件访问 |
| CG-TST-R1-CTR-006 | UT/CMP | 构造 128/129 条、32 MiB/+1、256 MiB/+1 | 到界提示并停录；不覆盖 active/emergency save |
| CG-TST-R1-CMP-007 | CMP | 在战斗动作、选择、奖励中请求，随后抵达稳定 checkpoint | 请求仅 Pending；安全边界前零持久化修改 |
| CG-TST-R1-CTR-018 | CMP | spy 统计 BetterCoop 触发的 `ToSave`、`SaveRun`、RNG 和 checksum 调用 | checkpoint 归档造成的额外调用严格为 0 |

### 5.2 多客户端成功和失败

| Test ID | 层/拓扑 | 步骤 | 通过 oracle |
|---|---|---|---|
| CG-TST-R1-MP2-008 | MP2 | A→B→C，房主回溯 B，经 native loaded-lobby 重连 | 双方 node/roster/epoch/checksum 一致并完成两回合 |
| CG-TST-R1-MP4-009 | MP4 | 在 Prepare、Quiesce、Load、Verify 各断开一名 peer | 事务中止或关闭 lobby；不存在可继续 split-brain |
| CG-TST-R1-MP5-010 | MP5 | Limit Break 下回溯已访问战斗前节点 | 5 人全部重连、抽牌、行动并完成两回合 |
| CG-TST-R1-MP8-011 | MP8 | 8 人跨地图房间回溯；重新建立 C9 同意 | roster/layout 正确；旧同意清零；8 人完成两回合 |
| CG-TST-R1-STEAM-012 | STEAM-5 | Workshop 候选包，真实 Steam lobby 回溯一次 | 5 人 <=60 秒恢复并满足全部 sentinel |
| CG-TST-R1-HOST-013 | FAULT | 在每个持久化状态强制终止房主，重新启动 | 确定恢复旧状态或已提交目标；损坏项被隔离 |
| CG-TST-R1-DROP-014 | MP4/FAULT | ACK 前、激活后、loaded-lobby 中 peer 掉线/重连 | 严格按阶段中止或进入恢复；无自动降低为多数 |
| CG-TST-R1-DISK-015 | FAULT | 只读目录、磁盘满、权限拒绝、文件被占用 | 用户得到具体错误；active save 可用；无忙等重试 |
| CG-TST-R1-MISMATCH-016 | MP4 | 改变 build、Mod hash、seed tag、roster 各一项 | active save 修改前精确拒绝并指出字段 |
| CG-TST-R1-PERF-017 | PERF | MP2 连续执行 30 次受支持回溯 | 成功耗时 p95 <=30 秒；资源回到稳定基线 |

覆盖：V6-AC-R1-001–020。

## 6. G13 测试案例

### 6.1 Manifest、patch 和 loaded-run 契约

| Test ID | 层 | 步骤 | 通过 oracle |
|---|---|---|---|
| CG-TST-G13-CTR-001 | CMP | 将相同 Mod 放入不同目录并改显示名；保留 manifest ID | 仍按 ID 识别，不依赖目录/显示名 |
| CG-TST-G13-CTR-002 | CMP | 组合 v0.1.3 精确 hash、其他版本、RitsuLib 边界版本和不同 hashes | 仅 allowlist 组合 Compatible，原因精确 |
| CG-TST-G13-CMP-003 | CMP | BetterCoop/Limit Break 两种初始化顺序；检查 Harmony owners | hook 结果一致，无 owner 冲突或重复 patch |
| CG-TST-G13-CMP-004 | CMP | 读取 active、14/4/2 patch、capacity/bit、Steam limit、host-settings received 和 scaling 摘要 | 大厅只要求 PreRunCompatible；`RunManager` 建立后才要求 host-settings received 才为 Compatible；Pending/Partial 指向具体 peer/阶段 |
| CG-TST-G13-SPIKE-005 | CMP | IL/API 检查 `LoadRunLobby`、loaded-run messages、slot/count 字段 | 生成逐字段证据，Unknown 不得进入实现 |
| CG-TST-G13-SHIM-006 | CMP | 在“缺失/已存在/形状漂移”三种 fixture 安装窄 shim | 只在精确缺失时事务式安装；绝不双 patch |
| CG-TST-G13-CODEC-007 | UT/CMP | loaded-run slot 0–15、count 0–16 往返；17 和恶意 count | 合法值稳定，非法值分配前拒绝 |

### 6.2 多人数流程

| Test ID | 层/拓扑 | 步骤 | 通过 oracle |
|---|---|---|---|
| CG-TST-G13-MP2-008 | MP2/MP4 | 未安装 Limit Break 的 2/4 人完整基线 | 与 v0.5.0 行为和 sentinel 一致 |
| CG-TST-G13-MP4-009 | MP2/MP4 | 安装并启用 Limit Break 的 2/4 人完整基线 | BetterCoop 不改变第三方缩放/布局结果 |
| CG-TST-G13-MP5-010 | MP5 | 5 人 join、ready、embark、两完整战斗回合 | 每个 peer 全部 sentinel；无 1012/4001/软锁 |
| CG-TST-G13-MP8-011 | MP8 | 地图、战斗、奖励、休息、商店、宝箱、save/load | 8 人身份/布局/动作一致并继续 |
| CG-TST-G13-MP16-012 | MP16 | 16 人 lobby、ready、embark、UI/codec/cache smoke | P1–P16 唯一稳定；无越界；P17 拒绝 |
| CG-TST-G13-NEG-013 | MP5 | 缺 Mod、RitsuLib 过旧、设置/hash 不同各一轮；另让房主 sidecar 在大厅保持 Pending | 前四种在第 5 人开局前阻止并指出 peer/根因；单独的大厅 sidecar Pending 不循环阻塞，进入 run 后仍未同步则 R1 不可用 |
| CG-TST-G13-LOAD-014 | MP5/MP8 | native multiplayer save→loaded-lobby→继续两回合 | 两种人数全部 sentinel；否则 R1 对该人数 Unsupported |
| CG-TST-G13-STEAM-015 | STEAM-5 | 真实 Steam、Workshop bytes、两完整回合 | 5 个客户端全部 sentinel，证据含各自日志 |
| CG-TST-G13-SOAK-016 | SOAK-8 | 8 人运行 8 小时，定期战斗、切房和 UI 操作 | 无资源单调增长、断线或卡回合 |

覆盖：V6-AC-G13-001–017。

## 7. F9 测试案例

| Test ID | 层/拓扑 | 步骤 | 通过 oracle |
|---|---|---|---|
| CG-TST-F9-UT-001 | UT | seed 字节序、负/正边界、culture 切换和 golden vector | 格式/摘要稳定，无 culture 差异 |
| CG-TST-F9-CTR-002 | CMP | 在所有分析入口安装 spy 统计 `Next*`、clone、state/checksum 调用 | F9 产生的额外调用严格为 0 |
| CG-TST-F9-UT-003 | UT | 已知 stream/method/args/result fixture 和 Unknown/Chaotic | index/摘要匹配；Unknown/Chaotic 不参与结论 |
| CG-TST-F9-MP2-004 | MP2 | 相同 seed 和动作序列执行两个 checkpoint | seed tag、计数和摘要一致 |
| CG-TST-F9-DRIFT-005 | MP2 | 只篡改 test-driver 报告计数，不改真实 RNG | 下一自然 checkpoint 定位首个 stream/index |
| CG-TST-F9-MP8-006 | MP8 | 8 人相同 seed 完成两回合和房间切换 | 无虚假 mismatch；每 peer checkpoint 口径一致 |
| CG-TST-F9-R1-007 | MP4 | 记录 C 后回溯 B，继续产生 RNG 事件 | 新 epoch 从 B baseline 开始，旧 C 不参与 |
| CG-TST-F9-SEC-008 | SEC | 扫描 packet、普通日志、报告、磁盘和剪贴板 | 默认无 raw seed/state/result，只含 salted tag/摘要 |
| CG-TST-F9-UPG-009 | CMP | 移除一个 RNG target、改变签名或让 owner 安装失败 | 对应类别 Unsupported；Guard 和其他诊断通过 |
| CG-TST-F9-PERF-010 | PERF | 固定 RNG microbenchmark，开/关 F9 各 30 次交错运行 | 中位环境稳定；F9 p95 开销 <=5%，内存有界 |
| CG-TST-F9-REL-011 | PACKAGE | 扫描 Release 字符串、入口和 UI action | 无未来预测、重掷、牌序预览和 fault handler |

覆盖：V6-AC-F9-001–014。

## 8. 跨功能和回归案例

| Test ID | 层/拓扑 | 步骤 | 通过 oracle |
|---|---|---|---|
| CG-TST-CORE-MP8-001 | MP8 | C11+H13+F9+G13 全开，聊天洪泛时战斗两回合 | 控制消息/Guard 不饥饿；全部 sentinel |
| CG-TST-CORE-R1-002 | MP5 | C11/H13/F9 活跃时执行 R1 | 新 epoch 后聊天顺序、同意、手牌和 RNG 正确重置 |
| CG-TST-CORE-GUARD-003 | MP4/5 | 依次破坏 BetterCoop、Limit Break、RitsuLib 和设置 | Guard 只按真实不一致阻止并给具体根因 |
| CG-TST-CORE-UPG-004 | MP4 | v0.5/v0.6 混合、Diagnostics v1/v2、Run Control 缺失 | 在准备前确定拒绝；无未知消息误执行 |
| CG-TST-CORE-ISO-005 | CMP | 每个可选 owner 安装/卸载/抛错，Guard owner 保持 | 可选模块互不级联，Guard 始终存在 |
| CG-TST-CORE-PRIV-006 | SEC | 全功能跑完后执行发布证据采集 | 只含允许的 hashes、tags、counts 和 sentinels |
| CG-TST-CORE-STYLE-007 | CI | format、analyzer、Release build、PS/JSON/XML/Markdown 检查 | 全部返回 0，零未记录 suppression |
| CG-TST-CORE-TRACE-008 | CI | 枚举 C11/H13/R1/G13/F9 的验收和测试 ID | 每个 requirement 有验收和测试，无孤立 ID |

## 9. Debug-only 故障注入

只允许 test-driver 识别以下固定 ID：

```text
CG-FLT-TXT-MALFORMED
CG-FLT-TXT-FLOOD
CG-FLT-TXT-SPOOF
CG-FLT-RBK-BEFORE-BACKUP
CG-FLT-RBK-AFTER-SWAP
CG-FLT-RBK-BEFORE-LOBBY
CG-FLT-RBK-PEER-DROP
CG-FLT-RBK-HOST-CRASH
CG-FLT-RBK-CORRUPT-SNAPSHOT
CG-FLT-RBK-DISK-FULL
CG-FLT-LB-MISSING-RITSU
CG-FLT-LB-MIXED-SETTING
CG-FLT-RNG-COUNT-DRIFT
CG-FLT-RNG-TARGET-MISSING
```

Release 构建不得仅“关闭开关”，而是不能包含这些 ID 的解析器、handler 或反射入口。

## 10. 自动执行流水线

### 10.1 每次变更

1. `dotnet format --verify-no-changes`
2. Release build with warnings as errors
3. `FingerprintSelfCheck`
4. 受影响模块的 UT/SEC/CMP
5. `git diff --check`

### 10.2 Pull Request

增加：

1. 1,000,000 次协议 fuzz
2. Harmony smoke
3. 隔离 ENet MP2
4. requirement/test trace 检查
5. Release 隐私和 Debug-only 字符串扫描

### 10.3 Nightly

增加：

1. ENet 2/4/5/8
2. 全 fault catalog
3. UI 720p/200% screenshot 和焦点遍历
4. F9 benchmark
5. 60 分钟 MP8 short soak

### 10.4 Release Candidate

增加：

1. ENet 16 人边界 smoke
2. MP8 8 小时 soak
3. 真实 Steam 5 人两回合
4. 真实 Steam 5 人 loaded-run/R1
5. 候选包 SHA-256、manifest、协议和 Workshop staging 复验

## 11. 证据格式

每次多客户端运行输出一个 summary JSON，至少包含：

```json
{
  "commit": "<sha>",
  "betterCoop": {
    "version": "0.6.0",
    "guardProtocol": 6,
    "diagnosticsProtocol": 2,
    "runControlProtocol": 1,
    "dllSha256": "<sha256>"
  },
  "gameBuild": "<build>",
  "mods": [
    {
      "id": "STS2-MultiplayerLimitBreak",
      "version": "0.1.3",
      "sha256": "<sha256>"
    }
  ],
  "transport": "ENet|Steam",
  "players": 5,
  "seedTag": "<session-salted-tag>",
  "faultId": null,
  "startedMonotonicMs": 0,
  "endedMonotonicMs": 0,
  "peers": [
    {
      "ordinal": 1,
      "sentinels": [
        "SESSION_NEGOTIATED",
        "RUN_ENTERED"
      ],
      "result": "Pass",
      "evidence": "<relative-path>"
    }
  ],
  "result": "Pass"
}
```

不得保存 raw seed、聊天正文、手牌列表、Steam ID、绝对路径或 save 内容。

## 12. 停止发布条件

出现以下任一项立即停止相应 Release：

- Guard false-negative；
- 任意远端文本可触发命令、富文本行为或身份冒充；
- 撤回/epoch 变化后手牌仍可见；
- R1 损坏唯一 active save 或产生可继续 split-brain；
- F9 额外调用/推进 RNG；
- 5/8 人第二回合软锁、1012 或 4001 未归因；
- loaded-run >4 未通过而宣称大于 4 人可回溯；
- Release 包含 fault handler、原始聊天/seed/save 泄漏路径；
- 全仓库格式、analyzer 或 warnings-as-errors 未通过。
