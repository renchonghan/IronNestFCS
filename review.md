# IronNestFCS 全量 Review

日期: 2026-09-05。范围: 2.0 活跃链 (RD→DC→FC→GC + 渲染/HUD + Shared 工具); 旧层 (FSC/TacticalRadar/GunSystem/FcsSceneInteractor/FcsWindow/MapTable) 只标注停用边界, 不深审。

触发原因: 倒计时 bug 暴露"本应同一方法实现的功能, 各自用了不同方法"。

---

## 1. 核心问题: 同功能不同实现

### C1. 落地判定 / 飞行剩余时间 — 4 套互不引用的实现(最高优先级)

同一个物理事实"这发弹什么时候落地", 代码里有 4 套独立口径:

| # | 位置 | 实现 | 特性 |
|---|---|---|---|
| a | [SandboxRenderer.cs FixRemain](IronNestFCS.Logic/DC/SandboxRenderer.cs) | gc 传导值 + 本地基准差锁定 (GcLag), 冻结检测 | **唯一有 gc 冻结修正的地方** (游戏倒计时最后 ~2s 卡住) |
| b | [SandboxRenderer.cs UpdateImpacts](IronNestFCS.Logic/DC/SandboxRenderer.cs) | 本地计时 + FixRemain, 红线/弹头进度 | 落地隐藏 |
| c | [FcsHud.cs FinishRow](IronNestFCS.Logic/FC/FcsHud.cs) | 活读炮表 + 归零/NaN 封存 + 超本地 1.5s 钳制 | **没有 gc 冻结修正** — 游戏倒计时卡 2s 时 HUD 也卡 |
| d | [GunControl.cs Loop](IronNestFCS.Logic/GC/GunControl.cs) | _sawCountdown 见过真值后 NaN = 落地; 3s 超时兜底 | GC 传导的源头 |

本次会话的倒计时 bug(条目互相跟随/归零后再跳一轮)就是这套分裂的直接产物 — 每处补丁都只修了自己的口径。**建议: 抽一个共享 `FlightCountdown` 结构** (gc 活读 + 本地基准 + 冻结修正 + 封存标志), 四处全部消费它。

### C2. 击发检测双判据

- GC: `HasFired()` 上升沿 (pendingReload), 击发瞬间即得 — [GunControl.cs:160-176](IronNestFCS.Logic/GC/GunControl.cs#L160-L176)
- FC: `FinishRoutine` 等 `FlyRemaining` 从 NaN 变有效 (炮表倒计时启动), 滞后 ~1s, 5s 超时哑炮重试 — [FireControl.cs:747-759](IronNestFCS.Logic/FC/FireControl.cs#L747-L759)

两判据时差 ~1s, 是 C1 中 `FireMission` 晚记 ~1.2s 的根源。GC 的 Fired 沿更准, 建议 FC 收尾改吃 GC 发布的击发事件, 或至少用 GC 沿校准 FireMission。

### C3. km ↔ 板面换算 (3.8164) 散落

- [GeoMap.cs](IronNestFCS.Logic/Shared/GeoMap.cs) 集中定义了 MapCellSize = 1/3.8164、LocalToKm = ×3.8164 ✓
- [DisplayControl.cs TrackMotion](IronNestFCS.Logic/DC/DisplayControl.cs): 硬编码 `slope * 25f * 3.8164f` — **该走 GeoMap 常量**。历史上"×/÷ 弄反"导致速度小 14 倍的 bug 就是这处。
- GunControl.PushBallistic / FireControl push / SandboxRenderer 均走 GeoMap 或 InverseTransformPoint ✓

### C4. 解算代码写了两份

[FireControl.cs UpdateQueueSolutions](IronNestFCS.Logic/FC/FireControl.cs)(~195-212) 与 [ComputeGunSolutions](IronNestFCS.Logic/FC/FireControl.cs)(~372-407) 是几乎同款的 "RelToTarget → 直瞄/LeadSolve → AimBoard" 序列, 区别只在后者管装药冻结、前者管队列显示。**建议抽 `SolveOne(DcTarget) → (dist, angle, aimKm, v, a, j, T)`**, 两处调用。

### C5. 同一解算的存储双副本

- `task.Angle/Distance/AimBoard`(HUD 显示缓存, UpdateQueueSolutions/Compute 写)
- `_solDistL/_solElevL/_solAimL/_solVL/...` 快照(Apply/Push 用, Compute 写)

双副本 + "派发当帧快照未刷" 靠 `_solTask` 判等防错 (ApplySolutions 450 行)。**建议合并**: 快照即任务缓存, 带任务归属校验。

### C6. 渲染实体创建两条路径

- `Line()` helper(prio 分层统一)
- 手建 GameObject + AddComponent\<Il2CppShapes.Line\>(A1 预瞄线、C1 红固定虚线)— 8 行同款设置 (Thickness/Start/End/Color/ColorStart/ColorEnd) + renderQueue 单独设。

**建议抽 `CreateDashedLine(parent, thickness, color, prio)`** 消除手建副本。

### C7. 棋盘边界检查两份且口径不同

- [SandboxRenderer.cs ImpactFired](IronNestFCS.Logic/DC/SandboxRenderer.cs): 向外扩 1 小格 (MapCellSize)
- [DisplayControl.cs IsOnMap](IronNestFCS.Logic/DC/DisplayControl.cs): 向外扩 0.2 (板面单位) — 与 MapCellSize (0.262) 不一致

**建议统一到 GeoMap.IsOnBoard(boardPos, margin)**。

### C8. L/R 成对字段手写模式

FC 内 `_solXxxL/_solXxxR`(6 对)、`_armedL/R`、`_confirmedL/R`、`_confirmingL/R`、`_fireL/R`、`_dumpStuckL/R` — 全部 "成对变量 + isL 三元" 手写。建议 `struct GunSide { ... }` 数组化, 消除成对抄写 (也是"同方法不同实现"的温床)。

### C9. 对账探针日志散在生产路径

DC `trak/tgt`、SandboxRenderer `impact src/landing`、FC `FIRE` 探针 — 已降频但仍在生产代码里。建议集中到 `DebugProbe` 静态类 + 开关常量。

### C10. ✅ 已解决(本次会话)

- 绿/红渲染权重分层 (prio 体系 + 画画模型注释)
- 完成队列倒计时分裂 (C1c 与其余口径)
- 预瞄线/红虚线规格分裂 (统一为游戏 Dashed 单线)
- 交汇点/轨迹点渲染 (单圆环 FillDot / 点式 BuildDots)

---

## 2. 其他发现(正确性/脆弱性)

1. **MissionClock 同名歧义**(游戏新版本新增 Il2Cpp.MissionClock): [FcsModule.cs:154](IronNestFCS.Logic/FcsModule.cs#L154) 已全限定修复。FCS 命名空间内文件靠命名空间优先安全, 但这是隐式依赖 — 任何文件移出 FCS 命名空间即炸 (CS0104)。建议其余裸用点也全限定或加 alias。
2. **LeadSolve 的 k 用满装药斜率**: `k = ShellData.FlightTime(1f, 6)` [FireControl.cs:490](IronNestFCS.Logic/FC/FireControl.cs#L490), 注释承认"骨架先按满装药"。N/X 档提前量有系统性微差 — 待办。
3. **ArmRoutine 不校验任务仍在炮上** [FireControl.cs:717-725](IronNestFCS.Logic/FC/FireControl.cs#L717-L725): 与 SalvoArmRoutine 的 `_taskL == task` 检查不一致。撤任务竞态下 armed 可能置位残留 (RequestCancel 已清, 但 Routine 内无二次确认)。
4. **SalvoDirector._children 静态列表**: 跨任务共享, finally 清理。依赖 MelonCoroutines.Stop 触发 Dispose (CoroutineLock 注释确认此行为) — 若某版本不 Dispose, 句柄泄漏跨重载累积。
5. **PurchaseDeck.GetLeftRightDial 每次买弹都 GameObject.Find("Console Box")** [PurchaseDeck.cs:37-40](IronNestFCS.Logic/GC/PurchaseDeck.cs#L37-L40) — 可缓存。
6. **Turret.SetDesiredRotation 负号约定** (`DesiredRotation = -angle`) 无注释说明为什么取负 — 加一行注释防后人"修正"。
7. **循环节拍不统一**: DC/FC/GC 固定 0.04s (25fps), SandboxRenderer 每帧 (yield null) — 渲染层刻意跟帧, 但意味着渲染吃的是数据层更新间隙的值, 可接受; 记档。
8. **TrackMotion 窗口 75 帧硬编码** (hist.Count > 75) 未提 const, 注释联动靠人工 — 建议 `TrackWindowFrames` 常量。

---

## 3. 清理项

1. **FSC.LegacyDisabled = true** 下旧循环不启动 [FSC.cs:148](IronNestFCS.Logic/FSC.cs#L148) — 但 FSC 本体 (1427 行)/FcsWindow/MapTable 仍参与编译, 只被 FSC 内部引用。清退计划: 先删 FcsWindow → MapTable → FSC 壳 (只留 TryBind 硬件绑定)。
2. **TacticalRadar** 被 Radar 复用静态方法 (GetIcon/GetArmour/IsUnitAlive/IsHostile) — 这些方法应收编进 Radar 或 Shared, 类本身退役。
3. **FcsSceneInteractor.WaitAndClick** 被活跃代码用 (TriggerConsole/PurchaseDeck/GunSystem) — 该辅助方法移到 Shared, 类退役。
4. **GunSystem** (503 行) 是活跃硬件驱动 (GunControl 全量依赖), 不是旧层 — 归类: 保留, 名字带 "System" 易被误判旧层, 建议改名 GunDriver 或注释标注。
5. **UserData/IronNestFCS/ 下 4 个 clock_probe.txt + radar_log.txt** (~100MB 探针残留) 可删。

---

## 4. 建议优先级

1. **C1** (落地判定统一) — 本次 bug 之源, 最高
2. **C3 + C7** (GeoMap 收敛换算/边界) — 小改动, 直接消除两类历史 bug 温床
3. **C2** (击发单判据) — 消除 1.2s 时差
4. **C4 + C5** (解算去重) — 纯重构, 无行为变化
5. **C6** (CreateDashedLine) — 小
6. **C8** (PerGun struct) — 中重构
7. 清理项按 1→5 顺序推进
