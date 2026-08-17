# IronNestFCS 功能详解 (Function Review)

> 基于 svr2kos2/IronNestFCS v1.0.7 + LnsiAxe enhanced fork 合并版编写.
> 目标游戏: Iron Nest: Heavy Turret Simulator (正式版), IL2CPP + MelonLoader, .NET 6.

---

## 1. 项目定位

IronNestFCS 是一个 MelonLoader Mod, 为一战风格重型炮塔游戏加入自动化火控系统 (Fire Control System, FCS). 玩家只需在地图上点选目标 (或让雷达自动标点), Mod 自动完成: 弹道解算 -> 采购/装填炮弹 -> 调整炮塔方向角与仰角 -> 确认击发的全套流程.

## 2. 系统架构

工程拆分为 4 个程序集, 核心是"宿主/逻辑分离"的热重载设计:

| 程序集 | 角色 | 部署位置 | 是否重载 |
| --- | --- | --- | --- |
| IronNestFCS | 宿主 Mod | Mods/ | 永不 |
| IronNestFCS.Abstractions | 契约接口 (IFcsModule) | UserLibs/ | 永不 |
| IronNestFCS.Logic | 火控逻辑 (全部功能代码) | UserData/IronNestFCS/ | F9 热重载 |
| IronNestFCS.CustomRecords | 自定义唱片机 Mod | Mods/ | 永不 |

**热重载机制**: Logic 程序集从内存字节加载进 isCollectible 的 AssemblyLoadContext (不锁磁盘 dll). 按 F9 时: Shutdown (撤销 Harmony 补丁, 停协程, 清空 IL2CPP 引用) -> 卸载旧 ALC -> GC 回收 -> 从磁盘重新加载. 改完代码 build 后进游戏按 F9 即生效, 无需重启游戏.

**热重载约束** (写 Logic 代码必须遵守):
- 不要在 Logic 中注册新的 IL2CPP 类型 (同一类型进程内只能注册一次)
- 所有 IL2CPP 对象引用在 Shutdown 时清空
- 每次实例用独立的 Harmony 实例, Shutdown 时 UnpatchSelf
- 协程必须登记进 _runningCoroutines, 卸载时全部 MelonCoroutines.Stop
- 跨 ALC 边界只能传递 IFcsModule 类型

## 3. 功能总览

| 功能 | 入口 | 说明 |
| --- | --- | --- |
| 一键打击 | 地图右侧 T1~T4 按钮 / Numpad 1-4 | 为目标入队一次完整打击任务 |
| 战术雷达 | 自动 (3s 扫描) | 识别敌我, 标点, 存活检测 |
| 持续扫荡 | Numpad 0 | 新敌人自动入队, 优先级排序 |
| 自动/手动标点 | Numpad 5 | 雷达自动放 T1-T4 标记 / 玩家自拖 |
| 双炮管调度 | 自动 | 任务队列自动派给空闲炮管, 并行作业 |
| 自动弹道解算 | 自动 | 方向角+距离 -> 装药+弹种+仰角 |
| 智能跳装填 | 自动 | 弹种匹配且装药充足时跳过装填 |
| 炮管强制重置 | Numpad 7/8/9 | 停协程, 放锁, 任务回队首重试 |
| 超时自愈 | 自动 | 卡状态 20s 自动重置; 仰角无进展检测 |
| 药包自动补充 | 自动 | 装药余量 < 6 时自动采购 |
| 20 种弹种 | 控制台按钮 | AP/APHE/ATMC/.../WP 全枚举 |
| Auto Fire | 场景 3D 按钮 | 自动完成最后击发 |
| Max Charge | 场景 3D 按钮 | 强制 6 号装药追极限射程 |
| 蒸汽阀门 | Numpad + / - | 一键拧开/拧紧全部蒸汽阀门 |
| 自定义唱片机 | CustomRecords Mod | 扫描音频文件替换游戏唱片 |

## 4. 火控核心 (FSC.cs)

FSC 是纯领域逻辑类: 查找游戏对象, 读取游戏数据, 操控游戏内交互. 不含 UI/生命周期代码 (在 FcsModule/FcsWindow 里).

### 4.1 任务调度
- 用户不指定炮管: 任务入队后由调度器 (TryDispatch) 自动派给空闲炮管
- 任务队列 Queue<ArtilleryTask>, 炮管打完一发自动拉取下一个, 两管炮并行
- EnqueueTask 尾部入队; EnqueueTaskFront 插队到队首 (高优先级目标)
- 所有读写都在 Unity 主线程, 无真正并发, 无需锁

### 4.2 双锁并发设计
- **_deskLock 控制台锁**: 保护弹道计算器, 确认开关台, 采购台三组全局唯一硬件. 临界区短 (解算/确认弹/击发), 用完即放
- **_turretLock 炮塔锁**: 方向角是全炮塔共享的, 一旦为某任务转到位必须独占到击发完成, 中途被转走会打偏. 与 deskLock 分开, 让任务能在后台早早抢炮塔与装填/升仰角重叠
- **防死锁**: 凡同时需要两把锁处一律"先 turret 后 desk", 无环故不死锁

### 4.3 炮塔预约 (TurretReservation)
- 任务开始即在后台 fire-and-forget 协程 ReserveTurretAndRotate: 阻塞式抢炮塔锁 -> 转向目标方向 -> 置 Ready
- 四个标志 (Acquired/Ready/Canceled/Released) 保证锁恰好归还一次
- 解算失败时 Canceled, 后台抢到锁后自行归还, 不空转

### 4.4 炮管强制重置 (AbortGun)
Numpad 7/8/9 触发: 停该炮管全部协程 -> 强制释放两把锁 -> 清空槽位 -> 被中止任务放回队首重试. abortCount >= 1 的任务不再重试, 直接标记 Failed (防止反复采购/解算的无限循环).

### 4.5 进度超时监控 (ProgressTimeoutMonitor)
- 每 2s 检查一次, 卡在同一状态超 20s 自动 AbortGun
- **豁免阶段** (不做固定超时): Aiming/BackToIdle (由 SetElevation 无进展检测兜底, 慢速瞄准可等任意久), WaitingForFire 且未开 AutoFire (等玩家手动击发)

### 4.6 药包自动补充 (ReplenishPowderLoop)
- 常驻后台协程, 每 5s 检查装药余量 (两炮共用池, 取较小值)
- 余量 < 6 时自动采购一次装药卡 (持 _deskLock, 与任务流程采购互斥)
- 只做检测与补充, 不干预任务流程

## 5. 单次打击任务流程 (RunTaskRoutine)

### 5.1 相位表 (面板 PHASE 代号)

| 代号 | 名称 | 含义 |
| --- | --- | --- |
| 1-0 | PEND | 任务等待调度 (在队列里) |
| 1-1 | CALL | 状态检查枢纽 + 补购药包/弹种 (不解算, 整个任务只解算一次) |
| 1-2 | SELC | 选弹: 转弹仓到目标弹种 |
| 1-3 | DUMP | 退弹平射 (弹种错 / 实装不足) |
| 2-1 | BLRD | 按推弹按钮, 确认架上有弹 (一闪而过) |
| 2-2 | BLLD | 推弹中 (等装填状态机离开 ShellRamming) |
| 2-3 | PWDR | 解算 + 按差补拉药包杆 + 推药 (锁内, 唯一解算点之一) |
| 2-4 | LOAD | 等装填完成 (CanFire) |
| 2-5 | COFM | 击发前装药确认 (实装 vs 快照, 差异回 CALL) |
| 3-1 | TRAK | 双轴并行持续追踪 (25fps, 同一循环, 不切相位): 天顶星伺服设 1 帧预测值 + I 修正 (16 帧误差窗口和 × ki=0.05, 消除持续滞后) + D 阻尼 (误差差分 × kd=6.0 + 0.01° 差分死区); **套上并跟踪稳定** = E 误差 0.01° / H 0.1° 且速度收住连续 5 帧 (0.2s); 套上后不停, 目标动了继续追; 套上后同一循环内五步确认 + Arm 解除保险 (一次性); 10s 无进展兜底 (放弃轴锁套上) |
| 3-2 | FIRE | 击发瞬间 (TRAK 段内套上后解除保险: 五步确认 + Arm 一次性 → AutoFire 立即击发 / 手动等玩家击发) → 击发快照 + 完成入列 |
| 3-3 | RSET | 回位 (13s 最小恢复 + 机构空闲) |
| 0-0 | IDLE / FAIL | 空闲 / 失败 |

### 5.2 弹种保护状态机 (最多两轮)

现场三要素: **膛内弹种** (ChamberedShellBlueprint) / **实装药包数** (PowderCharges) / **需求量** (Max Charge 开启为 6, 否则 MinimumCharge 按距离查表).

- shellWrong = 膛内有弹且弹种 != 任务弹种
- 有弹正确 + CANFIRE 且实装 < 需求 → 退弹 (平射打掉错误状态)
- 本轮装药量: 退弹轮 = 已装 (没装补 1); 实射轮 = 实装 >= 需求按实装, 否则按需求
- 第一轮退弹, 等 2s 机构自动循环, 第二轮重装正确弹实射; 两轮未完 → Failed
- **CALL 计数器**: 每个任务最多 2 次完整检查, 第 3 次跳过检查直接按实际装药实射 (保证终止)
- **检查点鲁棒性**: 推弹机动作中 (ShellRamming) 膛内读数不可信, 等推完再决策 (不消耗 CALL 预算)

### 5.3 每轮详细流程

```
PEND 安排任务> CALL

CALL > 检查炮的状态
  - 有弹 正确
    - CANFIRE > 检查装药
      - 不够 > DUMP
      - 超出 > CALL(带条件)
      - 符合 > EAIM
    - NOTCANFIRE > PWDR
  - 有弹 错误 > DUMP
  - 无弹 > SELC

SELC > BLRD

DUMP > 检查炮的状态状态
  - CANFIRE > FIRE
  - NOTCANFIRE
    有弹 > 架上保证至少有一药 > 装填 > FIRE
    无弹 > CALL
*** 推弹机动作过程的状态要考虑

BLRD > BLLD

BLLD > PWDR

PWDR > 检查装药
  - 不够 > 补全
  - 超出 > CALL(带条件)
  - 符合/超时 > LOAD

LOAD > COFM
  - 确认 > AIMING
  - 差异 > CALL
```

1. **炮塔预约** (任务开始即启动): 后台协程抢炮塔锁并转向目标方向, 与整个装填段重叠; 方向角独占到实射完成
2. **CALL 枢纽**: 读现场 → 判定分支 (dump/selc/pwdr/aim) → 定本轮装药量 powderCount; 第 3 次 CALL 跳过检查强制实射
3. **临界区 1 补购** (持 deskLock, 1-1 CALL): 药包不足循环补购 (上限 10 次). 解算不在这里 — **整个任务只解算一次**
4. **弹仓缺目标弹种则采购** (1-2 SELC 分支内, 持 deskLock): 弹仓全满无空位 → Failed; 采购后等 3s 入仓
5. **分支执行**:
   - 退弹轮 (1-3 DUMP): CANFIRE 直接平射; 无弹回 CALL; 有弹保证至少一药 → 装填 → 平射
   - 空膛 (1-2 SELC): 先等残留实装计数清零 (10s) → 转弹仓 → 2-1 BLRD 按推弹按钮 + 等推弹机启动 → 2-2 BLLD 等推到位 (离开 ShellRamming 或膛内出现炮弹, 30s 兜底) → 2-3 PWDR
   - 弹已在膛且无实装: 直接 2-3 PWDR
   - 实装 >= 需求: 跳过装填, 解算放 3-1 EAIM 前 (锁内)
   - **2-3 PWDR 唯一解算点**: 游戏只在按 Calculate 那一刻存储"已计算装药数", 药包杆最多允许拉到这个数; 弹道计算器全局唯一, 另一炮的解算会改写存储值 → 锁内完整重算 (距离/方向/装药/弹种 + Calculate) 后按差补拉 (实拉不足补拉差 / 超出回 CALL 带条件 / 符合或超时直接推药) → 2-4 LOAD 等 CanFire
6. **2-5 COFM 击发前装药确认**: 对比实际实装 vs task.charge 快照; 一致直接过; 差异回 CALL (不本地重算)
7. **3-1 TRAK 持续追踪** (退弹轮平射跳过): 双轴并行 25fps 循环追 1 帧预测点, 套上并稳定后同一循环内五步确认 + Arm 解除保险; 击发快照 + 完成入列 (LatchFireTime + AddFinished); 退弹轮仍走旧 FireSequence
8. **3-2 FIRE**: AutoFire 解除保险即击发 (TriggerConsole.Fire + WaitFire 等 pendingReload) / 手动持续追踪等玩家击发. 游戏击发校验的是**实时状态** (实际仰角/膛内弹种/飞行时间/装填完成), 不读计算台; **确认台串行过台** (deskLock, 防两炮抢台)
9. **3-3 RSET 回位** (13s + 机构空闲) → Finished → 释放槽位拉下一单; finally 归还炮塔锁. **退弹轮**: 不归还炮塔, 等 2s 进入下一轮
10. **两轮未完 (兜底)**: 归还炮塔 → Failed → 释放槽位

### 5.4 相关机制

- **WaitAndClick**: 所有按钮点击带 9s 超时, 超时打日志跳过 (按钮不激活时不再无限挂)
- **进度超时监视器**: 卡状态 20s 自动重置; EAIM / HAIM / RSET / 手动击发等待豁免
- **面板两行**: 行 1 = 炮实际状态 (膛内弹种 / 实际仰角 / 实际方位角 / 实装药包 / FT 飞行时间); 行 2 = 火控解快照 (RQTA 占位 / 目标方位距离 / 弹种 / 解算仰角 / 装药 / T:- 目标倒计时)
- **飞行时间两个变量**: 活变量 `GunController.PredictedImpactTime` 抬炮实时更新 (行 1 瞄准期 FT 数据源, 仰角就位时锁存进 task.impactTime); 击发后游戏炮兵计时表 (GunStopwatch) 倒数, `previousCountingDownRemainingSeconds` 是剩余秒数 (行 2 T:- 数据源)
- **铁巢棋子自动吸附** (SyncIronNestLoop, 10fps): 真源 = `turretController.turretBase.localPosition` (MapRoot 网格空间, 归位/紧急转移由游戏更新); 经沙盘校准常数映射到棋子局部系 (格长 = 1/3.8164, 左下角经平射真值+目测校准), 棋子摆错不再导致火控打飞

### 5.5 单独开火 (击发段) 步骤清单

> 供齐射设计参考: 单任务从仰角就位到击发完成的完整步骤, 按代码顺序, 含硬件操作与锁.

| # | 步骤 | 位置 | 操作与硬件 | 锁 |
| --- | --- | --- | --- | --- |
| 1 | 3-1 EAIM | RunTaskRoutine | SetElevation(计算仰角): 循环设仰角杆到到位; 10s 无进展放弃 (退弹轮平射跳过) | - |
| 2 | 锁存飞时 | EAIM 后 | task.impactTime = 距离 x 10/7 / 速度倍率(药包) (射表公式) | - |
| 3 | 3-2 HAIM | RunTaskRoutine | 等 turret.Ready — 后台炮塔预约早已转向目标方位角 | turretLock (后台预约持有) |
| 4 | 3-3 WAIT | RunTaskRoutine | 标记自己 WaitingForFire; **齐射任务在此等搭档也到 WAIT** (搭档失败/被取消则不等) | - |
| 5 | 抢控制台锁 | FireSequence | 五步确认台全局唯一, 两炮串行过台 | deskLock |
| 6 | 五步确认 | TriggerConsole | ConfirmTask → ConfirmBullet → ConfirmRotation → ConfirmElevation → ReadyToFire: 依次拨 .Review Console Parent 的 5 个 .Check Switch | deskLock |
| 7 | Arm 开保险 | TriggerConsole.Arm(炮) | 本炮 ArmingLever 按下 0.2s 松开 — **保险按炮分开** | deskLock |
| 8 | 飞时兜底 | FireSequence | impactTime 未锁存时用公式补 | deskLock |
| 9 | 3-4 FIRE | TriggerConsole.Fire | AutoFire: fire spinner AddEnergy(255) — **击发钮全局一个** | deskLock |
| 10 | WaitFire | GunSystem | 等游戏 pendingReload 置位 (发射完成) | deskLock |
| 11 | 放控制台锁 | FireSequence finally | | - |
| 12 | 击发快照 | FireSequence | fireTime = 炮兵计时表 countdownStartTime; impactTime = latchedTravelTime (游戏真值, 消除固定时间差) | - |
| 13 | 完成入列 | FireSequence | _finished 追加 (最多 8 条 + 溢出计数); 退弹轮不入列 | - |
| 14 | 还炮塔锁 | FireSequence finally | 非退弹轮 ReleaseTurretOnce | turretLock |
| 15 | 3-5 RSET | RunTaskRoutine | WaitBackToIdle (13s 最小恢复 + 机构空闲) | - |
| 16 | Finished | RunTaskRoutine | ReleaseSlot → TryDispatch 拉下一单 | - |

要点:
- 游戏击发校验的是**实时状态** (实际仰角/膛内弹种/飞行时间/装填完成), 不读计算台输出
- 五步确认台 (.Check Switch x5) 全局只有一套 — 校验的是**两门炮的实时状态**, 不区分炮; 只有 Arm 保险按炮分开 → 两炮同时走确认会互相踩 (齐射右炮保险没开即此因), 当前用 deskLock 串行
- **齐射当前同步点 = 第 4 步 (WAIT)**: 双方都到才依次过台; 方案重设计方向 = 改到 EAIM 结束同步

### 5.6 齐射流程 (RunSalvoRoutine, 独立实现)

> 齐射 = **独立流程**, 不在单发状态机上堆分支: 一个齐射对 (主任务 + 跟随任务) 驱动两门炮,
> 每个相位双炮并行执行, **双方都到位才推进** 下一相位.

**相位表** (代号与单发一致, 语义双炮化):

| 代号 | 名称 | 齐射语义 |
| --- | --- | --- |
| 1-0 | PEND | 齐射对等待调度: **两炮同时空闲**才一起出队 (主+随), 只空闲一门整对等待 |
| 1-1 | CALL | 检查两炮: 药包库存 >= 2 x 需求 (两炮共用池), 膛内弹种正确才下一步 (误弹走 DUMP); **快速路径**: 两炮同弹同装药 (实装 >= 需求, 均 CanFire) → 锁内解算一次直接进 TRAK, 跳过装填段 |
| 1-2 | SELC | 两炮弹仓都转到目标弹种, 双方对位才下一步 |
| 1-3 | DUMP | 双炮退弹平射 (各自打掉误弹), 两炮都平射完回 CALL |
| 2-1 | BLRD | 两炮一起按推弹按钮 (一闪而过) |
| 2-2 | BLLD | 两炮都推弹到位 (离开 ShellRamming) |
| 2-3 | PWDR | **解算一次** (计算台全局唯一, 同目标同距离同装药, 一次 Calculate 供两炮); 两炮同时拉杆, 都拉到位才推药 |
| 2-4 | LOAD | 两炮都 CanFire |
| 2-5 | COFM | 两炮实装 vs 快照都一致 (任一差异回 CALL) |
| 3-1 | TRAK | 双炮持续追踪同一目标 (25fps, TrackAxis 与单发同构): 两炮各自仰角追踪 + 炮塔方位追踪 (共享), **三轴都套上并稳定** → 五步确认走一遍 + 双炮并行 Arm (一次性) |
| 3-2 | FIRE | AutoFire 解除保险即击发 (击发钮全局一个, **一按两炮齐射**); 手动双炮持续追踪等玩家击发 |
| 3-3 | RSET | 两炮都回位 (13s 最小恢复 + 机构空闲) |
| 0-0 | IDLE / FAIL | 空闲 / 失败 |

**与单发流程的差异**:

1. **派发**: 齐射对要求两炮同时空闲, 主+随两条一起出队; 只空闲一门则整对等待 (不拆开)
2. **相位同步**: 每个相位双炮并行执行 (各自子协程), 双方都完成才推进
3. **解算**: PWDR 只做一次 (共享计算台), 同距离同装药两炮通用, 仰角/飞时按公式直算
4. **击发**: 五步确认走一遍 → 两炮 Arm → 按一次击发钮 → 双炮各自 WaitFire; 飞时快照取左炮计时表真值 (两炮同飞时)
5. **采购**: 药包库存按两炮总量保证 (>= 2 x 需求, 上限 12)
6. **显示**: 面板主炮行正常, 跟随炮行 `>>>[SALVO]`; 地图目标顶部显示 S; 完成队列入两条

## 6. 硬件抽象层 (FCS/ 目录)

### 6.1 BallisticCalculator - 弹道计算器
- 绑定游戏内"Balistic Calculator Controls": 距离拨盘, 装药拨盘, 方向拨盘, 弹种拨盘, 计算按钮, 仰角里程表
- SetDistance/SetDirection/SetCharge/SetShellType 设拨盘值, Calculate 点计算, GetElevation 读结果
- MinimumCharge 装药查表: <5km->1 号, <10km->2, <15km->3, <20km->4, <25km->5, 其余 6 号

### 6.2 GunSystem - 单管炮抽象
- 绑定: 弹仓选择器, 转弹仓按钮, 推弹按钮, 装药按钮组 (PowderChargeController 下 Button Dispencer), 推药杆, GunController, 仰角杆, 装药余量表, 实拉药包数里程表 (递归查找), Shell ID 显示器, GunStopwatch 炮兵计时表 (watchedGun 匹配)
- RotateCylinderTo: 等机构空闲 -> 转弹仓到目标弹 (每步 1.5s); PressRammer: 等机构空闲 -> 按推弹按钮; WaitRammingStart: 等装填状态机进入 ShellRamming; WaitShellRammed: 等离开 ShellRamming 或膛内出现炮弹 (30s 兜底)
- PullPowders: 按次数点装药按钮; RamPowder: 按推药杆; 按钮引用失效时自动重新扫描 (RefreshPowderButtons)
- SelectedPowderCharges: 实拉药包杆数 (Selected Charges 里程表, P3 就可见; 里程表挂在 Calculated Charge Display (1) 下, 不在装填控制台里)
- IsShellRamming: 装填状态机是否在推弹 (检查点用)
- SetElevation: 循环设仰角杆值直到到位, 带无进展检测 (连续 10s 变化 < 0.2° 判定卡死, 放弃本次瞄准)
- WaitForReloadReady: 正式版装填状态索引是数据驱动的, 只依据真实机构状态判断 (reloadController.working / ExternalReloadLoweringLocked / elevationChangeVelocity)
- WaitBackToIdle: 13s 最小恢复窗口 + 机构真正结束工作
- 计时表读数: RemainingFlightSeconds (瞄准期 = 活 PredictedImpactTime, 倒计时 = 表读数), CountdownRemainingSeconds (纯倒计时)
- 弹种名归一: 游戏侧 PLCM 统一 Replace 为 PCLM
- GetState: 炮管状态快照 (膛内弹/CanFire/PendingReload/仰角速度/装药余量/弹仓列表)

### 6.3 MapTable - 地图桌
- 绑定: Player Turret Piece (铁巢棋子), Draggable Surface, MapToken_Artillery 标记 (1~4), Fire Mission Root, ImpactMarkerManager
- GetMarkTarget: 标记位置 - 铁巢棋子位置 (同在地图局部系) -> 距离 (x3.8164f 比例) + 方向角
- SyncIronNestToken: 铁巢棋子吸附到游戏真值 (turretController.turretBase.localPosition 经沙盘校准常数映射); 沙盘校准 = 格长 1/3.8164 + 左下角 (平射真值+目测校准)
- SetMarkerWorldPos/SetMarkerByKmPos/SetMarkerLocalPos/ResetMarker: 四种标记设置方式 (雷达标点用)

### 6.4 PurchaseDeck - 采购台
- 绑定 Requisition Console: 解析全部 PunchcardRuntime 卡牌 (ID 去掉 SMOKE/Shell 前缀映射到 BulletType), PowderCharges 装药卡, 购买按钮
- BuyShell: 拖卡入槽 -> 等 0.5s -> 拨左右炮选择器 -> 点购买
- BuyPowders: 拖装药卡入槽 -> 等 0.5s -> 点购买

### 6.5 TriggerConsole - 击发确认台
- 绑定 .Review Console Parent 的 5 个 .Check Switch, 左右 ArmingLever, .Trigger Core 的 fire spinner
- 五步确认: ConfirmTask -> ConfirmBullet -> ConfirmRotation -> ConfirmElevation -> ReadyToFire
- Arm: 扳机按下 0.2s 松开; Fire: spinner AddEnergy(255)

### 6.6 Turret - 炮塔
- 绑定 TurretSystem 的 TurretController
- SetRotation: 设 DesiredRotation (取负), 等 rotationVelocity 归零

### 6.7 ArtilleryTask / Progress - 任务数据
- 字段: targetId, angel, distance, position, bulletType, progress, abortCount, fireControlId (UID 日志追溯), salvoFollower/salvoLeader (齐射对), impactTime/fireTime (飞时锁存与击发时刻), calculatedElevation/charge (解算快照), trackAngel/trackDistance (TRAK 1 帧预测)
- Progress 状态机: Pending -> Calculating -> SelectingBullet -> (DumpingWrongShell) -> LoadingBullet -> LoadingPowder -> WaitLoading -> Aiming -> WaitingForFire -> BackToIdle -> Finished / Failed

### 6.8 CoroutineLock - 协程互斥锁
- 主线程协作式调度下无真正并发, 一个 bool 即互斥
- Acquire 每帧重试; 配 try/finally, 协程被 Stop 时 finally 照常执行, 锁不泄漏
- Reset 用于热重载时强制复位防死锁

## 7. 战术雷达 (TacticalRadar.cs)

### 7.1 扫描 (每 3 秒)
两个来源:
1. **Fire Mission Root 子实体**: 逐个取 EntityLocation, IsHostile 判定阵营
2. **全场景名称匹配**: 名字以 Enemy/Tgt_/Target_ 开头的 GameObject (去重)

### 7.2 敌我识别 (IsHostile)
- **第一优先 Icon + Role 位掩码**: Role 含 Reference (33554432) 判中立; 含 Ally(2) 不含 Enemy(1) 判友军; 含 Enemy 判敌对; Icon 字符串含 friendly/frendly 判友军, 含 enemy 判敌对
- **第二优先字段遍历**: 反射遍历 Entity 的 team/side/faction/enemy/hostile 字段
- **第三优先名字匹配** (DB 规则): police/prop/civ/smoke/reference/ref 判平民; hospital 判中立; enemy/hostile/artillery/fdc/target 判敌对; friendly/ally 判友军
- 排除名单 (IsExcludedUnit): Phantom Battery (演示幻影炮组), EnemyKillTokens (击杀令牌 UI)
- 数据来源: ironnestdb.com/enemies

### 7.3 存活检测 (IsUnitAlive)
activeInHierarchy 优先; 再反射查 enabled/alive/dead/destroyed/health/active 字段; 兜底 activeSelf

### 7.4 自动标点
AliveUnits 按序放到 T1~T4 标记 (SetMarkerWorldPos), 不足 4 个时多余标记复位到炮塔位置

### 7.5 雷达面板 (右上角)
- 标题显示存活数与标点模式 [Auto]/[Manual]
- 存活目标红色实心圆 (最多 8 个), 已摧毁灰色空心圆 (最多 3 个), 超出显示 +N
- 扫描日志写入 UserData/IronNestFCS/radar_log.txt

## 8. 场景交互与 UI

### 8.1 FcsSceneInteractor - 场景交互
- 控制台旁 3D 按钮: 20 个弹种按钮 (循环生成 cube + TextMeshPro, ClickRaycaster 注册点击), Auto Fire 开关, Max Charge 开关, T1~T4 目标按钮 (点击后有 1s 冷却灰显)
- 键盘快捷键分发: 见下表
- WaitAndClick: 等按钮 isActive 且冷却结束 -> OnClickDown 0.1s -> OnClickUp, 全部带 10s 超时保护
- SetColor: 换 URP Unlit 材质着色 (CreatePrimitive 默认 Standard 材质在 URP 下紫色)
- AddText: World Space TextMeshPro 文本
- 生命周期: Shutdown 时销毁全部动态创建的 GameObject

### 8.2 FcsWindow - 状态面板 (左上角)
- 标题 + [Sweep ON] 扫荡状态
- 左右炮行: 任务 ID, 弹种, 进度 (颜色区分: Failed 红/Finished 绿/Pending 棕/进行中蓝), 目标参数, 预估仰角/装药
- 队列预览: 每单的目标区号 (ConvertPosition 转 A1 1:2 格式), 角度, 距离, 弹种
- 未绑定场景显示 "Waiting for scene... / Press F9 to reload"
- 绝对 Rect GUI API (不走 GUILayout 布局, 避免 MelonLoader 单 pass 下 controlID 错位)

### 8.3 ClickRaycaster - 点击检测
- 每帧读新 Input System 鼠标左键, 主相机射线, 命中注册 Collider 触发回调
- 不用 OnMouseDown (要注册 IL2CPP 类型破坏热重载), 不用游戏 LookAtTarget (外部硬挂不可靠), 不用 IMGUI (单 pass controlID 错位)

### 8.4 键盘快捷键

| 按键 (Numpad) | 笔记本替代 | 功能 |
| --- | --- | --- |
| F9 | - | 热重载火控逻辑 (开发用) |
| Numpad 0 | Ctrl+0 | 切换扫荡 (强制自动标点) |
| Numpad 1-4 | Ctrl+1-4 | 击发 T1-T4 |
| Numpad 5 | Ctrl+5 | 切换自动/手动标点 |
| Numpad + | - | 一键拧开全部蒸汽阀门 |
| Numpad - | - | 一键拧紧全部蒸汽阀门 |
| Numpad 7 | Ctrl+7 | 重置左炮 |
| Numpad 8 | Ctrl+8 | 重置右炮 |
| Numpad 9 | Ctrl+9 | 重置双炮 |

## 9. 扫荡模式 (FcsModule)

- Numpad 0 开启: 面板显示 [Sweep ON], 雷达强制自动标点, 现存全部敌立即入队
- 每帧检查雷达 AliveUnits, 新出现的敌对目标自动入队 (HashSet 按 EntityLocation 去重, 已入队不重复)
- **优先级排序** (GetPriority): 3 星 / FDC 指挥官 (Icon 含 fire direction) / Artillery 位 = P4; 1 星以上或装甲 (Fortification/Tank 位) = P3; 其它敌对 = P2
- P3+ 目标用 FireAtWorldPosFront 插队到队列最前面
- F9 热重载后 autoSweep 复位为关, 需重新按 Numpad 0

## 10. 宿主与热重载 (FcsHostMod + LogicReloader)

- FcsHostMod: MelonInfo 声明, OnInitializeMelon 时定位 UserData/IronNestFCS/IronNestFCS.Logic.dll 并首次 Reload
- OnUpdate: F9 按下或 CheckDllUpdated 检测到 dll 时间戳变化 -> Reload; 正常帧转发 Current.Update()
- OnGUI 转发 Current.OnGui(); OnSceneWasLoaded 等 3s 场景稳定后 Reload (重新绑定场景对象)
- OnDeinitializeMelon 卸载
- LogicReloader: 从内存字节 LoadFromStream (不锁磁盘, 带 PDB), isCollectible ALC, Unload 时 Shutdown + alc.Unload + 2 轮 GC.Collect

## 11. 自定义唱片机 (CustomRecords Mod)

- 扫描 UserData/CustomRecords 下全部带内嵌封面的音频 (.mp3/.wav/.flac)
- 为每个文件克隆场景内 RecordDisk: 封面合成唱片贴图, 解码 PCM 作为音轨
- PCMReaderCallback 按需供数, 托管侧样本缓冲保活防 GC 野指针
- 模板定位优先级: 精确名 RecordDisk -> 可见桌面盘 -> 任意可见盘 -> 宽限期 (5s) 后兜底
- 场景切换销毁旧克隆并重置, 支持 Demo 版 RecordDisk 与正式版 RecordDisk 1~13 命名
- 依赖: CSCore (解码) + TagLibSharp (标签/封面), 纯托管库不受 IL2CPP 裁剪影响

## 12. 构建与部署

- `dotnet build IronNestFCS.sln -c Release`: 四个程序集自动归位 (Mods/ UserLibs/ UserData/IronNestFCS/), 无需手动复制
- `build.ps1`: 一键发布脚本, `-p:GameDir=...` 覆盖游戏路径, 产物按 IronNestFCS/CustomRecords 两个安装包整理到 Release 目录 (含空的 UserData/CustomRecords 目录)
- GameDir 在各 csproj 集中配置, 换机器改一处即可

## 13. 弹种列表 (20 种)

AP(1) APHE(2) ATMC(3) CLMN(4) CYAN(5) DRIL(6) EQKE(7) FLCH(8) HCHE(9) HE(10) INCN(11) LE(12) PCLM(13) PHGN(14) PRPG(15) SMK(16) STAR(17) TEAR(18) THRM(19) WP(20)

> 注: 游戏内部 ShellId 为 PLCM, 代码统一归一为 PCLM.

## 14. 鲁棒性设计汇总

| 机制 | 触发条件 | 行为 |
| --- | --- | --- |
| 按钮超时 | WaitAndClick 等超 10s | 打日志放弃, 不永久死锁 |
| 进度超时 | 卡同一状态 20s | 自动 AbortGun, 任务回队首 |
| 仰角无进展 | 连续 10s 变化 < 0.2° | 放弃本次瞄准 |
| 装药采购上限 | 连续采购 10 次仍不足 | 停止补购 |
| 弹种采购确认 | 采购后 3s 内未入仓 | 继续流程 (后续装填会失败并兜底) |
| 炮管重置重试上限 | abortCount >= 1 | 标记 Failed, 不再重试 |
| 机构状态感知 | 连续射击 | WaitForReloadReady 等真实机构空闲 |
| 引用失效重绑定 | 按钮引用被游戏重建 | RefreshPowderButtons/LoadPowder 重新扫描 |
| 铁巢紧急转移 | 炮塔世界坐标变化 | 坐标统一转地图局部系再计算 |
