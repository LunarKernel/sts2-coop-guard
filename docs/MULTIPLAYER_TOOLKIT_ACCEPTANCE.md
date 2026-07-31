# 联机终极工具验收规范索引

- 状态：Proposed
- 基线：`MULTIPLAYER_TOOLKIT_TECHNICAL_DESIGN.md`

为保持审查变更小而可读，52 项验收标准按领域拆分：

- H1–H12、C1–C10：
  [`MULTIPLAYER_TOOLKIT_ACCEPTANCE_HC.md`](MULTIPLAYER_TOOLKIT_ACCEPTANCE_HC.md)
- G1–G12、D1–D10：
  [`MULTIPLAYER_TOOLKIT_ACCEPTANCE_GD.md`](MULTIPLAYER_TOOLKIT_ACCEPTANCE_GD.md)
- F1–F8 及全项目测试：
  [`MULTIPLAYER_TOOLKIT_TEST_PLAN.md`](MULTIPLAYER_TOOLKIT_TEST_PLAN.md)

## 统一完成定义

单个功能只有同时满足以下条件才算验收：

1. 需求 ID、用户可观察行为、数据来源、权限和非目标已经写清。
2. 正常、边界、错误、降级和恢复路径均映射到稳定测试 ID；一个测试 ID 可以用
   独立断言覆盖多个 path tag，但机器可读追踪记录必须逐个列出
   `normal/boundary/error/degraded/recovery` 覆盖关系和未适用理由。
3. 不违反 `INV-01`–`INV-12`。
4. 支持构建内所有 MUST 条款通过；Unsupported 状态不会伪装成 Healthy。
5. 量化性能、内存、网络、磁盘和新鲜度预算通过。
6. UI 在 200% 文字缩放、键盘/控制器操作和非颜色表达下可用。
7. 远端输入完成限长、限频、身份、枚举、转义和模糊测试。
8. Guard 回归、现有 self-check 和多客户端原生联机不受影响。
9. Release 包不含调试故障入口、测试 DLL 或新增运行时依赖。
10. 验收证据和已知限制写入 `docs/WORKLOG.md`。

## 缺陷等级

| 等级 | 示例 | 发布规则 |
|---|---|---|
| S0 | 修改游戏状态、Guard 绕过、隐私泄漏、远端崩溃/RCE | 立即停止，禁止发布 |
| S1 | 错误阻止联机、错误自动重连、错误显示隐藏信息、无界资源增长 | 禁止发布 |
| S2 | 核心状态错误、误导性高置信诊断、关键无障碍失败 | 默认禁止发布 |
| S3 | 可选图表/文案/非关键视觉问题，有明确降级 | 可评估延期并记录 |

## 证据格式

每个通过项必须记录：

```text
requirement_id
test_id
game_build / guard_build / protocol
topology (local, 2-client, 3-client, 4-client)
transport (ENet or Steam)
result
artifact/log location
known limitations
reviewer and date
```

只有“看到 UI 正常”而没有构建、拓扑、输入和结果的记录，不算工程验收证据。

## 主追踪矩阵

`AT-*` 本身就是可执行的用户验收测试案例，不只是需求文字；工程实现还必须把它
映射到测试计划定义的 UT/CMP/CTR/MP/E2E/SEC/PERF/A11Y 层。下表锁定第一版
映射，后续可以增加测试，不能删除或复用既有 ID。表内
`AT-H01-01/02` 是 `AT-H01-01` 与 `AT-H01-02` 的显示简写，不定义第三个 ID。

| 功能 | 正常/异常验收案例 | 必需主测试层 | 阶段 |
|---|---|---|---|
| H1 网络 HUD | AT-H01-01/02 | CMP, MP2, PERF | P1 |
| H2 正在等什么 | AT-H02-01/02 | UT, CMP, E2E | P1 |
| H3 大厅健康矩阵 | AT-H03-01/02 | CTR, MP4, SEC | P3 |
| H4 加入阶段进度 | AT-H04-01/02 | CMP, MP2, UPG | P1 |
| H5 联机事件时间线 | AT-H05-01/02 | UT, CMP, SOAK | P1 |
| H6 选择进度 | AT-H06-01/02 | CMP, MP4, SEC | P3 |
| H7 紧凑公开队友状态 | AT-H07-01/02 | CMP, MP4, A11Y | P1 |
| H8 近 60 秒网络图 | AT-H08-01/02 | UT, CMP, PERF | P1 |
| H9 共享环境短码 | AT-H09-01/02 | UT, CTR, MP3, SEC | P3 |
| H10 加载速度/进度 | AT-H10-01/02 | CMP, MP3, E2E | P1 |
| H11 玩家身份颜色 | AT-H11-01/02 | UT, CMP, A11Y | P1 |
| H12 警报中心 | AT-H12-01/02 | UT, CMP, A11Y | P1 |
| C1 快捷状态消息 | AT-C01-01/02 | CTR, MP3, SEC | P3 |
| C2 地图选择进度 | AT-C02-01/02 | CMP, MP4, SEC | P3 |
| C3 公开行动流 | AT-C03-01/02 | CMP, E2E, SEC | P3 |
| C4 等待计时器 | AT-C04-01/02 | UT, CMP | P1 |
| C5 重连状态卡 | AT-C05-01/02 | CMP, MP2, E2E | P1 |
| C6 手动一键重连 | AT-C06-01/02; AT-C06-03 | CMP, MP2, SEC, UPG | P1/P6 |
| C7 声音提示 | AT-C07-01/02 | CMP, A11Y | P1 |
| C8 无障碍 | AT-C08-01/02 | A11Y, E2E | P1 |
| C9 精确手牌共享 | AT-C09-01/02 | CTR, MP4, SEC, E2E | P3 |
| C10 可选贡献统计 | AT-C10-01/02 | UT, CMP, MP4 | P3 |
| G1 不匹配修复表 | AT-G01-01/02 | UT, MP2, SEC | P2 |
| G2 复制修复清单 | AT-G02-01/02 | UT, CMP, SEC | P2 |
| G3 本地 Mod Doctor | AT-G03-01/02 | UT, CMP, PERF | P2 |
| G4 Workshop 更新观察器 | AT-G04-01/02 | CMP, E2E, PERF | P2 |
| G5 环境 lockfile/导出 | AT-G05-01/02 | UT, SEC, UPG | P2 |
| G6 存档环境 sidecar | AT-G06-01/02 | UT, CMP, SEC | P2 |
| G7 旧存档兼容警告 | AT-G07-01/02 | UT, E2E, UPG | P2 |
| G8 Harmony 冲突图 | AT-G08-01/02 | CMP, SEC, PERF | P2 |
| G9 分类指纹 | AT-G09-01/02 | UT, CTR, MP2, PERF | P2 |
| G10 确定性设置声明 | AT-G10-01/02 | CTR, MP2, SEC | P2 |
| G11 能力/协议协商 | AT-G11-01/02 | CTR, MP4, SEC, UPG | P3 |
| G12 已知问题规则目录 | AT-G12-01/02 | UT, SEC, UPG | P2 |
| D1 软锁观察器 | AT-D01-01/02 | UT, CMP, E2E | P1 |
| D2 等待原因置信度 | AT-D02-01/02 | UT, CMP, E2E | P1 |
| D3 本地飞行记录器 | AT-D03-01/02 | UT, CMP, SOAK | P1 |
| D4 断线前网络快照 | AT-D04-01/02 | CMP, MP2, PERF | P1 |
| D5 报告历史 | AT-D05-01/02 | UT, SEC, UPG | P1 |
| D6 离线对比报告 | AT-D06-01/02 | UT, SEC, UPG | P2 |
| D7 共享会话 ID | AT-D07-01/02 | CTR, MP3, SEC | P3 |
| D8 下次启动崩溃复盘 | AT-D08-01/02 | CMP, E2E, SEC | P1 |
| D9 依赖感知二分计划 | AT-D09-01/02 | UT, SEC | P2 |
| D10 主机诊断视图 | AT-D10-01/02 | CMP, MP4, SEC | P3 |
| F1 分层状态摘要（Hierarchical State Digests） | CG-TST-F1-UT-001; CG-TST-F1-CMP-002; CG-TST-F1-UPG-003; CG-TST-F1-MP4-004; CG-TST-F1-CTR-005 | UT, CMP, CTR, UPG, MP4 | P4 |
| F2 首个分歧定位器 | CG-TST-F2-UT-001; CG-TST-F2-SEC-002; CG-TST-F2-E2E-003; CG-TST-F2-MP3-004 | UT, SEC, E2E, MP3 | P4 |
| F3 动作/检查点日志 | CG-TST-F3-CMP-001; CG-TST-F3-SEC-002; CG-TST-F3-E2E-003 | CMP, SEC, E2E | P4 |
| F4 RNG 计数哨兵 | CG-TST-F4-CTR-001; CG-TST-F4-UT-002; CG-TST-F4-UPG-003; CG-TST-F4-MP2-004 | CTR, UT, UPG, MP2 | P4 |
| F5 有界 peer heartbeat | CG-TST-F5-MP4-001; CG-TST-F5-SEC-002; CG-TST-F5-MP2-003 | MP2, MP4, SEC | P3 |
| F6 Mod 作者 API | CG-TST-F6-CMP-001; CG-TST-F6-SEC-002; CG-TST-F6-UPG-003; CG-TST-F6-CMP-004; CG-TST-F6-CMP-005; CG-TST-F6-CMP-006 | CMP, SEC, UPG | P2/P4 |
| F7 2/3/4 客户端 CI | CG-TST-F7-MP4-001; CG-TST-F7-MP3-002; CG-TST-F7-E2E-003; CG-TST-F7-MP2-004; CG-TST-F7-MP3-005 | MP2, MP3, MP4, E2E | P5 |
| F8 开发故障注入 | CG-TST-F8-E2E-001; CG-TST-F8-SEC-002; CG-TST-F8-UPG-003 | E2E, SEC, UPG | P5 |
| CORE 协议/隔离硬门禁 | CG-TST-CORE-CTR-001; CG-TST-CORE-SEC-002; CG-TST-CORE-MP2-003; CG-TST-CORE-MP3-004; CG-TST-CORE-MP4-005; CG-TST-CORE-UPG-006; CG-TST-CORE-CTR-007; CG-TST-CORE-MP4-008; CG-TST-CORE-MP4-009; CG-TST-CORE-CMP-010; CG-TST-CORE-A11Y-011; CG-TST-CORE-MP3-012; CG-TST-CORE-SEC-013 | CTR, SEC, CMP, MP2, MP3, MP4, UPG, A11Y | P0–P5 |
