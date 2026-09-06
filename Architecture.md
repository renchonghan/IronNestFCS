# ArchitectureNote — IronNestFCS 架构与开发手册

> 2.0 架构权威文档。章法: 先谈架构 → 后说实现 → 前后向接口 → 坑与注意事项 → 开发流程。
> 新人上手路径: 本文件通读 → [GameInternals.md](GameInternals.md)(游戏逆向笔记, 组件/数值/踩坑) → 代码。
> 本文已合并 2026-09-05 全量 review 的结论(一致性清单与修复记录见 §5)。

---

## 1. 总体架构

### 1.1 一句话

**五模块数据链 RD→DC_U→FC→GC→DC_D: 雷达找目标, 显控台建表跟踪, 火控解算决策, 炮执行装填追踪, 渲染线程画沙盘。**

### 1.2 数据链与节拍

| 模块 | 循环 | 节拍 | 干什么 |
| --- | --- | --- | --- |
| [RD] Radar | 协程 | 扫描 1~3s + 粗跟 25fps | 纯传感器: 找实体/敌我分类/存活检测 → SRC 列表 |
| [DC_U] DisplayControl | 协程 | 25fps (0.04s) | SRC → 目标参数表 (位置+TWS 轨迹参数); 交互请求; 令牌管理 |
| [FC] FireControl | 协程 | 25fps | 队列/派发/诸元解算/统一火控/HUD 数据 |
| [GC] GunControl ×2 + SalvoDirector | 协程 | 每炮 25fps | 装填链/追踪稳定/击发自检/Flight 更新 |
| [DC_D] SandboxRenderer | 协程 | 每帧 (yield null) | 3D 沙盘渲染 (恒定实体, 只动端点) |
| [DC] FcsHud | IMGUI | 每帧 | 64 字符面板直出 (不经 DisplayControl) |
| [DC] ScenePanel + ClickRaycaster | 帧驱动 | 每帧 | 3D 按钮/右键点击盒 |

- 全部跑在 Unity 主线程协程上 (IL2CPP 只能主线程碰对象), 没有真线程。
- **线程常驻原则**: Stop/Pause/Manual 只改指令内容 (清队列/断输出/不碰硬件), 从不杀线程——飞行计时和落点指示靠 GC 常驻循环持续更新, 线程死了天上炮弹的显示就断。

### 1.3 程序集分工与热重载

| 程序集 | 角色 | 部署 | 重载 |
| --- | --- | --- | --- |
| IronNestFCS | 宿主 Mod (FcsHostMod/LogicReloader) | Mods/ | 永不 |
| IronNestFCS.Abstractions | IFcsModule 契约 | UserLibs/ | 永不 |
| IronNestFCS.Logic | 全部火控逻辑 | **UserData/IronNestFCS/** | **F9 热重载** |
| IronNestFCS.CustomRecords | 自定义唱片机 (独立 Mod) | Mods/ | 永不 |

**热重载链**: build 后 Logic.dll 落入 `UserData\IronNestFCS\`(csproj OutputPath 直指), 游戏内 F9 → Shutdown(全模块 Stop+清端口) → 卸载旧 ALC → 重新加载。注意:
- Mods/ 里的两个 dll (宿主壳+CustomRecords) 只在游戏启动头几秒被 MelonLoader 短暂持有锁, **游戏运行中 build 正常**(复制成功); 避开启动窗口即可。
- 运行时创建的 3D 物件不随 F9 销毁, 必须按名字清理旧实例 (SandboxRenderer.ClearAll)。
- Logic 代码禁止注册新 IL2CPP 类型。

### 1.4 旧架构 (历史对照)

每炮一条串行状态机 (FSC.cs 旧调度层, `LegacyDisabled=true` 已停), 毛病: 一条龙串到底 (一步卡住整发卡住)、炮塔锁太粗 (独占一整发)、卡住靠 20s 超时误杀排队。2.0 的核心变化: **执行 (操炮) 与决策 (打谁) 分层**, FC 持续输出两炮完整诸元, 炮自己追稳; 锁只包一次硬件操作。

---

## 2. 模块实现

### 2.1 [RD] Radar (`RD/Radar.cs`)

纯传感器。两速扫描:
- **扫描** (1~3s): Fire Mission Root 子节点 + 根层 Enemy/Tgt_ 名字兜底; EntityLocation 分类 (敌/友/中立三态, 类型 FDC/炮兵/装甲/AA/参考点, 装甲值), 阵亡排除 (EnemyKillTokens 击杀令牌不报)。
- **粗跟** (25fps): 已知目标位置刷新 + 每 10 帧存活快查。

输出 `SrcContact` 列表: Entity 句柄/WorldPos/Side/Kind/Armour。分类静态方法复用旧 TacticalRadar (待收编)。

### 2.2 [DC_U] DisplayControl (`DC/DisplayControl.cs`)

- **目标参数表** `_targetMap`(Dictionary<GameObject, DcTarget> 对象复用, **FC 直读无回调**): 位置 (世界坐标, 每帧刷) + TWS 轨迹参数 + 敌我/类型/装甲。实体与令牌虚拟目标混列; 目标失效 (阵亡/离图) 出表 → FC 读不到 → 撤任务。
- **TWS 运动估计** (`TrackMotion`): 定速模型 — 75 帧环 (3s) 一阶最小二乘线性滤波, 位置先 `InverseTransformPoint` 转板面局部再差分, 斜率 ×25fps×`GeoMap.KmPerLocal` = km/s; a/j 不估 (恒 0)。窗口愈长噪声愈低 (LS 斜率噪声 ∝ 1/√N); 变速目标天然滞后 1.5s — 定速模型既定取舍。不足 5 帧返回零 (无跟踪)。
- **铁巢棋子吸附** (`SyncNestToken`): turretBase 网格坐标 → NestRef 棋子位置 (棋子摆偏不导致打飞)。
- 交互: 右键 toggle (入队→升级齐射→取消)、令牌拖放 (1s 批量发现 T1-10 标记令牌, 上地图注册虚拟目标/拖离撤任务; 击杀/侦察/参考点令牌不注册)、扫荡 (敌对目标按优先级 FDC>炮兵>装甲>其他 持续发请求, `_swept` 去重)。
- 实体图标差集 (`UpdateIcons`): 在列表→画, 不在→删 (阵亡/离图自动清退)。
- 开关: AutoFire/AutoTask/TWS。TWS 关 = 速度矢量全零 → 火控自然无预瞄 (直瞄); 关时清位置历史。
- 对账探针: `trak` (上炮目标速度, 每 8 帧) / `tgt` (目标板面位置, 每秒) 留生产路径降频跑。

### 2.3 [FC] FireControl (`FC/FireControl.cs`)

循环 25fps: 收请求 → 队列 (乱序重排一次) → 解算 → 持续输出 GC 指令 → 统一火控 (确认/保险/击发) → 收尾。

- **诸元解算** (`ComputeGunSolutions`): 相对方位/距离 (`RelToTarget`) → 提前量 (`LeadSolve`, 见 §4.2) → 装药 (`ChargeOf`: T 最少 / N 仰角≤30° 尽量 / X 强制 6) → 仰角 (射表直算 `ShellData.ElevationDeg`)。
- **装药冻结 LockedCharge**: 派发帧锁存一次 (实装匹配 → 锁膛内实际药数; 否则锁解析值), 之后不重解析 — 目标运动不改变装填计划 (装填链中途换药 COFM 报错/打飞); TRAK 后按 GC 活读膛内药数为准。
- **动目标强制 N**: T 模式随距离变, 目标一动锁存装药就失裕量 — 入队时点 TWS 速度矢量非零 → 改 Normal。
- **智能派发** (只队首): 空闲炮三态 — 空膛派队首; 弹对且药够 → 按膛内药解析打; 弹不对/药不够 → **DUMP 占位** (同膛内弹 1 包药 0° 平射)。FALL 炮不派发。
- **乱序重排** (`SortQueueOnce`, Start 时一次): 非空膛炮膛内弹匹配后续任务且方位差 ≤45° → 提前到队首后。
- **统一火控** (`FireArbiter`): 进 TRAK → 五步确认 (前置, 不等稳定; **不再动计算台** — 装填期已 Calculate) → 首次 AllReady → Arm (一次性) → AutoFire/预定时间 → 击发; PreAiming 等玩家击发。
- **收尾** (`FinishRoutine`): 击发确认判据 = **GC `Fired` 沿** (不用 FlyRemaining — 装填期炮表残留值会骗早, 见 §5.1) → Finished 入队 + 槽位释放。哑炮防护: 5s 无沿 → 重按保险+拉绳 ×3。
- **DUMP 占位**: 跳过火控卡/五步确认, AllReady → Arm → 强制击发; 全哑 → `_dumpStuck` 挂起该炮 (Manual/Stop 复位)。
- **预定打击时间**: 任务带时间戳时开火条件 = 当前任务时钟 + FlyTime ≥ 预定 (炮弹按点抵达); -1 = 就绪即打。
- **CBC 反炮兵**: 游戏 CounterBatteryTimer 只读显示 (打 FDC 暂停/延长是游戏自身行为)。
- 齐射: 双炮同任务 + SyncCommand, 独立仲裁 (`SalvoArbiter`) — 双炮 TRAK → 一次确认 → 双炮同时 Arm → 一击发 (击发钮全局一个, 天然齐射)。

### 2.4 [GC] GunControl (`GC/GunControl.cs`) + SalvoDirector

每炮一条 25fps 常驻循环:
- **装填链** (`TaskChain`): DecidePrepStep 逐步决策 (SELC/SHRD/SHLD/PWDR/LOAD) → TRAK (持续追踪到击发) → REST → IDLE。每步 Exec 带看门狗 (超时/异常 → FALL 自报, 等 FC 换策略); 单发步间 0.5s 死区。
- **齐射** (`SalvoDirector`): 一条协程带双炮, 预处理区 (1-x) 独立推进, 同步区 (2-1 起) 每步并行+汇合+0.75s 死区; 任一 FALL/撤任务全停。
- **1-3 DUMP 停手等接管**: 膛内弹不对/齐射多药 → 进 1-3 + DumpWaitActive, FC 撤任务改派 DUMP 占位 (GC 不自行平射)。
- **TRAK 追踪** (`TrackOnce`): E 恒追 (TrackAxis 天顶星伺服 + EWMA 斜率外推 3 帧, α=0.3), H 仅被 AzimuthSelect 选中时追 (外推 4 帧, α=0.5); 齐射右炮直接设左炮设定值。双轴稳定 (死区动态口径: 落点偏移 ≤ DRIL 杀伤半径/5) + 弹药活读确认 → AllReady (不稳回退)。锁定死区 0.05° 起, 按距离动态收紧。
- **击发自检** (常驻循环, 手动/自动通用): `HasFired()` (pendingReload) 上升沿 + 膛空 (CanFire 已掉) → 锁存炮表真值 (StopwatchLatch, 未启动用瞄准期预测兜底) → **新建 Flight** (出膛时刻/FlyTime/任务时钟) → `OnImpactFired` 通知 DC 画线。DUMP 平射不建 Flight。CanFire 上升沿 (装填完成) 复位击发沿与 Fired (手动连打每发都是新事件)。
- **实时弹道指示器** (`PushBallistic`, 每帧): 游戏落点标记 (网格→板面) + 弹种 (CanFire 门控 — 装填完成信号, 不含保险) + AllReady + 飞时。
- **采购** (SelcRoutine/PwdrRoutine): 采购台短锁 (两炮共享) 内查+买。

### 2.5 [DC_D] 渲染与交互

- **SandboxRenderer** (`DC/SandboxRenderer.cs`): 恒定实体原则 — 绿十字/预瞄线/落点指示/轨迹线等固定套件建一次, 不用 SetActive 隐藏, 每帧只动端点; 动态的只有目标标记和杀伤圈 (节流重画)。渲染分层见 §4.6。
- **FcsHud** (`FC/FcsHud.cs`): 64 字符定宽面板直出 (FC 数据直读, 不经 DC): 两炮块 (相位/膛内/仰角/方位/装药/飞时 + 火控解行) + 任务队列 (8 行) + 完成队列 (8 行, **倒序: 最新在最上**) + CBC + 任务时钟。完成行剩余倒计时直读 `Flight.Remain`。
- **ScenePanel** (`DC/ScenePanel.cs`): 3D 按钮列 (AutoFire/AutoTask/TWS/T-N-X 装药模式 + Start/Pause/Stop 三态), 点击盒每帧世界归正 (0.26×0.26×0.1, 挂实体下, RaycastAll 不穿透)。

### 2.6 Shared 工具 (`Shared/`)

| 类 | 职责 |
| --- | --- |
| GeoMap | 坐标换算唯一入口: `KmPerLocal=3.8164` / `MapCellSize` / `MapBottomLeft` / `GridToLocal` / `LocalToKm` / `RelToTarget` / `IsOnBoard` |
| ShellData | 射表: `ElevationDeg`=12d/c, `FlightTime`=d×(10/7)/SpeedMult(c), `KillRadiusKm` (ShellDefinition 扫描) |
| Flight | 飞行状态统一口径 (§4.1) |
| TrackAxis | 单轴追踪稳定器 (变积分+两帧差分+卡死检测) |
| CoroutineLock | 协程级互斥 (主线程协作调度, bool 即可; 配 try/finally) |
| MissionClock | 任务时钟 (MissionWatch GenericTimerSceneSync, 10:00:00 起累计) |
| Glyph16Font | 十六段米字数码 (Il2CppShapes.Line 画字符) |

---

## 3. 前后向接口总表

### 3.1 GC 指令端口 (FC 持续写)

| 端口 | 语义 |
| --- | --- |
| ManualControl | 手动: FC 停机, GC 立即停手不碰硬件 (线程照跑) |
| SyncCommand | 齐射锁定: 右炮按左炮数据走 (DesiredX 输入忽略, E 直接设左炮设定值) |
| AzimuthSelect | 本炮被选中执行水平追踪 (共享炮塔只跟一门) |
| DesiredShell / DesiredCharge | 弹种 / 装药 (-1 = 无任务) |
| DesiredElevation / DesiredAzimuth | 目标俯仰/方位 (FC 持续输出, 提前量已算进) |
| DesiredDistance | 目标距离 km (锁定死区动态口径) |
| DesiredDump | DUMP 占位标志 (击发自检不画落点) |

### 3.2 GC 状态端口 (FC 读)

Chamber/Charges (实装快照, 动作边界刷新) / **ChamberLive/ChargesLive (活读, 玩家介入处用)** / Elevation/Azimuth (每帧传感器) / Action (10 态) / FlyTime (瞄准期活读, 击发锁存) / FlyRemaining (击发后炮表倒计时) / AllReady / DumpWaitActive / Fired (击发沿) / CanFire (装填完成信号) / CurrentFlight (击发时建, 落地后保留引用) / CalcDone (装填期 Calculate 完成信号).

### 3.3 GC → DC push

| push | 时机 | 内容 |
| --- | --- | --- |
| OnBallisticPush | 每帧 | 瞄准点/杀伤圈/弹种 (-1 不渲染)/AllReady/飞时 |
| OnImpactFired | 击发沿 | 落点 (冻结的开火前最后瞄准点) + 弹种 + 飞时 + **Flight 引用** |

DC 侧持 Flight 引用后**直读字段** (Remain/Landed), 不逐帧传导 (统一口径, review C1 落地)。

### 3.4 DC 状态端口 (FC 读)

目标参数表 (`DcTarget`: 句柄/名称/WorldPos/Velocity/Accel/Jerk/敌我/类型/装甲/虚拟标志) + 火控请求列表 (入队/升级齐射/取消) + AutoFire/AutoTask 开关位。TWS 状态不传 — 关 = 速度矢量零。

### 3.5 FC → DC push

`OnQueueChanged` (队列指示器批量同步) / `OnFireSolution` (目标轨迹线+交汇点: 目标引用/交汇点板面/轨迹参数板面/飞时; 无预瞄 → 目标引用 null)。

---

## 4. 关键机制详解

### 4.1 Flight — 飞行状态统一口径 (review C1)

一发炮弹一个 `Flight` 对象 (GC 击发时创建, 每帧由 GC 唯一更新; DC/HUD 只读字段):

- `FlyTime` 锁存总飞时 / `FiredAtLocal` 出膛时刻 (Time.time) / `FiredAtMission` 出膛时刻任务时钟 / `Remain` 剩余秒 / `Landed` 落地封存 / `GcRaw` 原始炮表值 (诊断)。
- **Update 每帧**: 本地 `local = FlyTime − (now − FiredAtLocal)`; 炮表活读正常降速 → 锁基准差 `GcLag = local − gc`; 显示 `Remain = min(gc, local − GcLag)` (与游戏指示器同步, 冻结/清表自动落回本地外推); **落地判据 = local ≤ 0** (唯一口径 — 炮表冻结/清表/未启动都不影响); Remain 钳 ≥0。
- 消费方: DC 红线/红点进度 (`UpdateImpacts` 读 Remain/Landed)、最终线缩短 (`UpdateTracks`)、HUD 完成队列倒计时 (`FinishRow` 读 Remain, Landed → `--.--`)、落地对账日志 (LogLanding)。
- **切任务不碰 Flight** (ResetForTask 不清): 出膛后膛空, 新任务立即起链装填 (20-40s), 上一发飞时 ~13s 还在天上 — 传导不因新任务断 (旧版此处断流, 全靠 FixRemain 外推兜底)。

### 4.2 LeadSolve — 提前量解析解

预测点 = p + v·T (T = k·r, 飞时线性, k = FlightTime(1km, 6 包) — **满装药斜率, 与装药弱相关**)。匀速 (a=j≈0) 闭式一元二次: (1−k²v²)r² − 2k(p·v)r − p² = 0, **b = −2kpv** (符号反了 = 提前量被反向扣除, 炮弹落在目标身后 — 历史事故)。变速走数值不动点 8 次。**无 fireDelay 补偿**: 解算每帧滑动, 出膛瞬间炮指向的就是最新解算; 按钮→出膛延迟 Δ 只是把双方同步平移, 加补偿反而打远。

### 4.3 装填链计算台体系 (Calculate 收口)

**药包杆读数是计算台缓存** — 开火/推药后与物理分配器脱节 (~16s 才同步), **Calculate 后立即是真值** (玩家可不计算直接拉杆, 但 mod 读的缓存要 Calculate 刷新)。每次 Calculate 会往记事本多一张卡 → **每任务至多一次** (`CalcDone` 去重):

- **2-2 时机**: 推弹按钮按下后立即**子协程并行** Calculate (锁内 SetDistance/Direction/Charge/ShellType + Calculate, ~3s 被推弹动画覆盖, 不白等)。
- **2-3 前兜底**: 膛内弹直装路径无 2-2, PWDR 开头串行补 (CalcDone 已置则跳过)。
- **齐射**: 只左炮拉一次 (计算台共享), 右炮 2-3 等 `SyncPeer.CalcDone` (0.1s 轮询, 10s 兜底) — 不能抢先按解算推药。
- **五步确认不再动计算台** (ConfirmRoutine/SalvoConfirmRoutine 只按按钮, 按 mod 缓存值走)。
- **`_forceFullPowder` 兜底**: LOAD 推药 COFM 超时 (药没推进膛) → 置位 → 下轮 PWDR 无视读数按空拉满 (残留读数虚高的自愈; 真药包满时拉不动只是 9s 白等, 不超量)。
- **变量语义**: 2-3 阶段 `SelectedPowderCharges` = 分配器"实际拉了几包"里程表 (有滞后); 2-4 之后 `LoadedPowderCharges` = "膛内实际几包" (推药确认后才有值)。拉几包看 mod 解算 (DesiredCharge), 读数只是参考。
- 分配器 6 包满, 满了拉不动 (重复拉杆无害)。

### 4.4 传导时间问题 (三时刻, 两个 1s)

| 时刻 | 来源 | 与出膛差 |
| --- | --- | --- |
| 按钮按下 | FC FIRE pressed | −1s (fireDelay, 触发核心储能) |
| **出膛** | `HasFired` 沿 (pendingReload) | 0 — Flight 本地基准 |
| 炮表倒计时启动 | CountdownRemainingSeconds 首个有效值 | +1s |

处置: **飞时只有锁存一个真值**; 炮表是显示值 (晚 1s 启动/会冻结/会清表); 本地是判据 (出膛起算)。FC 收尾/完成队列 FireMission 用 GC 记的出膛时刻任务时钟 (`Flight.FiredAtMission`), 不用"倒计时启动时刻"(系统性偏晚 1s 的根源)。

### 4.5 装填链细节 (直装 vs 空膛路径)

- **空膛路径**: SHRD (转弹仓, 含 WaitForReloadReady) → SHLD (推弹) → PWDR (拉药, 2-2 已并行 Calculate) → LOAD (推药 + COFM 实装确认 25s) → TRAK。
- **直装路径** (膛内弹对只缺药): 起链即 PWDR — 没有 SHLD 的机构等待, 所以 PWDR 开头有 `WaitForReloadReady` (机构停稳, 否则 Button Dispencer 不激活 9s 白等) + Calculate 兜底。
- **死区**: 单发步间 0.5s; 齐射同步区每步 0.75s (双炮汇合后才走下一步, 防抢跑/共享分配器让出)。
- **击发沿基线同步** (`ResetForTask` 里 `_lastHasFired = HasFired()`): 不能清 false — 上一发残留沿会被误判成新开火 (入队瞬间红线闪)。

### 4.6 渲染分层 (画画模型)

`renderQueue = 5000 − prio`, queue 大 = 后渲染 = 后画的笔迹盖上面; z = `ZStep × prio` 是同层次级排序:

| prio | queue | z | 元素 |
| --- | --- | --- | --- |
| GreenPrio 0 | 5000 最后一笔 | 0 | GC 弹道 (绿十字/圈/飞时) + 轨迹点/最终线/交汇点 + A1 预瞄线 |
| ImpactPrio 1 | 4999 | 0.001 | C1 红固定虚线 + 落点指示器全套 |
| RedPrio 2 | 4998 | 0.002 | 队列标记 (圈/编号/弹种/菱形/齐射框) + 实体图标 (参考点绿十字留此层 — 地图元素) |

常量在 SandboxRenderer 顶部, 微调只动 prio 数字/ZStep。所有图元 (`Line`/`FillDot`/`RebuildCircle`/`DrawIcon`/`CreateDashedLine`) 带 prio 参数默认值。

### 4.7 线型定稿 (2026-09-05)

- **A1 预瞄线**: 游戏 Dashed 材质单线 (恒定实体), 炮上有任务即显示 (TWS 无关); 终点 = 交汇点 (有预瞄) / 目标本身 (直瞄)。
- **A2 目标轨迹线**: 点式虚线 — 点直径 = 线宽 0.006, 点线段长 0.0015 (胶囊帽相接≈圆), 最小点距 0.0075, 最多 32 点, 点沿切线摆; 轨迹长 ∝ 目标速度, 点疏密直观。
- **A3 最终线**: 细实线 (0.004), 击发冻结缩短, 终点 = 开火瞬间交汇点。
- **C1 红固定虚线**: 游戏 Dashed 单线 (与 A1 同款), 落地保留到下一发。
- 交汇点 = FillDot 单圆环 (环心半径/2, 线宽 = 半径), 半径 0.0067。

### 4.8 其他

- **HUD 完成队列**: 每任务一条, `FireMission` = 出膛时刻任务时钟, 剩余 = `Flight.Remain` 活读, `Landed` → `--.--`; 倒序显示 (最新在上)。
- **排序**: 乱序重排只在 Start 时一次 (45° 一刀切); 派发只队首。
- **Pause 豁免**: 飞行计时/落点指示在 GC/DC 常驻线程, 任何状态不冻结。
- **扫荡**: DC 侧持续生成请求 (去重), FC 只接收; 优先 FDC>炮兵>装甲>其他。

---

## 5. 坑与注意事项 (开发陷阱全集)

### 5.1 注意事项 (机制全景与最终处置)

1. **炮表倒计时不可靠, 落地/剩余判定收口 Flight (见 §4.1)**。游戏炮表 (CountdownRemainingSeconds) 有三种失效形态: (a) 倒计时最后 ~2s 卡住不降; (b) 飞行中给同炮切任务, 炮表被新瞄准流程抢占而冻结 (帧间降速 <0.05); (c) 无下一任务时发射流程收尾 (REST/Idle) 把炮表清成 NaN (这不是落地信号 — 飞行中就会发生)。**最终处置**: Flight 每帧锁 `GcLag = local − gc` 基准差, 显示 `Remain = min(gc, local − GcLag)` (炮表正常时与游戏指示器逐帧同步, 冻结/清表自动落回本地外推); **落地判据唯一 = 本地 `local ≤ 0`** (出膛时刻起算), 炮表只做显示同步、不参与判定。消费方 (红线/最终线/HUD 完成队列) 全部直读 Flight 字段, 不再各自解读炮表。
2. **km↔板面换算**: 一律经 `GeoMap.KmPerLocal` (=3.8164, 乘不是除)。写反 → 速度小 14.5 倍, 轨迹极短; 方向角必须在板面局部系算 (SignedAngle(v, Vector3.up), 0=北顺时针 — 网格画布空间朝向不同, 用错会打飞)。
3. **LeadSolve 二次项系数**: b = −2kpv (提前量正向)。系数写反 = 提前量被反向扣除, 落点偏后。k 用满装药斜率 (与装药弱相关, 待精化)。
4. **分配器读数是计算台缓存, 刷新靠装填期 Calculate (见 §4.3)**。`SelectedPowderCharges` 里程表读的是计算台缓存 — 开火/推药后与物理分配器脱节 (~16s 才同步), 只有 Calculate 后立即是真值 (玩家手动拉杆不需要 Calculate, 但 mod 按读数决策就必须刷新)。**最终处置**: 每任务至多一次 Calculate (`CalcDone` 去重, 记事本卡片副作用): 2-2 推弹按钮按下后**子协程并行** (计算被推弹动画覆盖, 不白等), 膛内弹直装路径在 2-3 前串行兜底; 齐射只左炮拉一次, 右炮等 `CalcDone` 信号 (不能抢先按解算推药); TRAK 后五步确认不再动计算台; LOAD 推药 COFM 超时 → 置 `_forceFullPowder`, 下轮 PWDR 无视读数按空拉满 (分配器 6 包满, 重复拉杆只是 9s 白等不超量)。
5. **FC 收尾判据**: 用 GC `Fired` 沿 (真出膛), 不用 FlyRemaining — 装填期炮表残留值会让收尾早于出膛 1s, 完成队列绑到旧 Flight (HUD 恒 --.--)。完成队列的 `FireMission` 取 `Flight.FiredAtMission` (GC 记的出膛时刻任务时钟), 与红线/HUD 同一基准。
6. **拉杆/推药按钮需机构就绪**: 膛内弹直装路径 (起链 2ms 即进 PWDR) 在 PWDR 前 `WaitForReloadReady` (装填机构停 + 炮闩解锁 + 仰角静止), 否则 Button Dispencer 不激活 (9s 白等); 空膛路径的 SHRD 自带此等待。
7. **击发沿基线**: ResetForTask 里 `_lastHasFired` 同步到当前值, 不能清 false — 上一发残留沿会被误判成新开火 (入队瞬间红线闪)。
8. **游戏 Dashed 按整条线排周期**: 短线显不出虚线 — A1/C1 用游戏 Dashed 单线 (长线无碍), 轨迹线用自建点式, 杀伤圈手排短弧。
9. **Il2Cpp.MissionClock 同名歧义**: 游戏新增同名类, Logic 命名空间外裸用会 CS0104 — 全限定 (FCS 命名空间内靠命名空间优先, 属隐式依赖, 移出即炸)。
10. **build 与游戏进程**: Mods/ 宿主 dll 只在启动头几秒被 MelonLoader 短暂持锁, 避开启动窗口即可正常复制; Logic.dll 走 UserData 不锁, 游戏运行中 build 无碍。
11. **C# 细节**: `virtual` 是保留字 (参数用 isVirtual); `System.Array.Sort` 需显式泛型 (`System.Array.Sort<RaycastHit>(hits, ...)`); 换电脑注意根目录游离副本 (CS0101 重复定义)。

### 5.2 一致性清单 (2026-09-05 全量 review)

**已解决**: C1 落地判定四套 → Flight 统一; C2 击发双判据 → GC Fired 沿; C3 换算散落 → GeoMap.KmPerLocal; C6 手建线 → CreateDashedLine; C7 边界两份 → GeoMap.IsOnBoard; 渲染权重 → prio 分层; 倒计时分裂 → Flight 直读。

**待办 (纯重构, 行为不变)**:
- **C4**: `UpdateQueueSolutions` 与 `ComputeGunSolutions` 解算代码两份 → 抽 `SolveOne`。
- **C5**: 解算存储双副本 (task.Angle/Distance/AimBoard vs `_solXxxL/R` 快照) → 合并。
- **C8**: FC 内 L/R 成对字段手写 (6 对 sol + armed/confirmed/fire/dumpStuck) → PerGun struct。
- **未实现项**: Priority 派发 (字段存在但派发不排序); 旧代码清退 (FSC.cs/MapTable/FcsWindow/TacticalRadar/FcsSceneInteractor — FcsSceneInteractor.WaitAndClick 仍被活跃代码用, TacticalRadar 静态方法被 Radar 复用; 等 2.0 全流程跑稳再清)。
- **探针残留**: UserData/IronNestFCS/ 下 clock_probe*.txt + radar_log.txt (~100MB) 可删。

### 5.3 已知局限 (设计取舍)

- LeadSolve 的 k 用满装药斜率 (与装药弱相关) — 装药档不同提前量有微差, 待精化。
- TWS 定速模型: 变速目标滞后 1.5s (75 帧窗口), 游戏内全匀速直线目标, 无碍。
- 游戏 PredictedImpactTime 是活变量, 手动玩不重算时读数可能来自旧解。
- CoroutineLock 非 FIFO (主线程协作调度, 抢锁顺序不定)。
- 渲染层每帧 vs 数据层 25fps: 渲染吃数据更新间隙的值, 可接受。

---

## 6. 开发流程

### 6.1 日常循环

1. 改 Logic 代码 → `dotnet build`(游戏可开着, 避开启动头几秒)。
2. 游戏内 **F9** → Shutdown → 重载 UserData 里的 Logic.dll。
3. 看 MelonLoader 控制台日志 (Latest.log / 游戏内控制台)。

### 6.2 探针约定

- 调试流程: 现象 → 加日志探针 → 复现 → 看数据 → 定位 → 修 → 清理临时探针。
- 探针降频 (每 8 帧/每秒), 关键沿必打; 对账探针 (trak/tgt/impact src/landing/FIRE) 平时留生产路径降频运行。
- 修完清理临时探针, 保留状态节点日志 (chain start/calculate done 一类)。

### 6.3 Git 纪律

- 分支 v2-refactor; 单人开发, 需要时 push -f 无所谓。
- `.gitignore` 已含 bin/obj/logs.txt/IDE 杂项 — 不要用覆盖方式改它 (曾把 39 行规则覆写丢光, 130 个编译产物暴露)。
- 测试日志 (logs.txt) 不入库。

---

## 7. 迁移清单 (旧层清退)

| 旧件 | 状态 |
| --- | --- |
| FSC.cs (旧调度) | LegacyDisabled=true, 只留硬件绑定 — 待删 |
| MapTable.cs | 仅被旧层引用 — 待删 (Glyph16Font/GeoMap 已抽出) |
| FcsWindow.cs | 旧 IMGUI — 待删 (FcsHud 替代) |
| TacticalRadar.cs | 静态分类方法被 Radar 复用 — 收编后删 |
| FcsSceneInteractor.cs | WaitAndClick 被 TriggerConsole/GunSystem/PurchaseDeck 用 — 移到 Shared 后删 |
| GunSystem.cs | **活跃硬件驱动** (GunControl 全量依赖), 非旧层 — 名字带 System 易误判, 注释已标 |

清退顺序建议: FcsWindow → MapTable → FSC 壳 → FcsSceneInteractor (WaitAndClick 搬 Shared) → TacticalRadar (静态方法收编 Radar)。
