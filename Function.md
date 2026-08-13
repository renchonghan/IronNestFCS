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

1. **炮塔预约**: 后台协程抢炮塔锁并转向目标方向
2. **装药计算**: Max Charge 开启则 6 号装药, 否则按距离 MinimumCharge 查表
3. **膛内弹种检查**: 弹种不匹配则进入 DumpingWrongShell 状态, 用 1 号装药平射 dump
4. **临界区 1 解算** (持 deskLock): 设距离 -> 设方向 -> 设装药 -> 设弹种 -> 按计算按钮 -> 读仰角; 装药不足则循环采购 (上限 10 次); 弹仓缺弹则采购并等弹入仓 (最多 3s)
5. **锁外装填**: 智能跳过判定 (CanFire + 膛内弹种匹配 + 非 dump) 通过则跳过; 否则装弹 (先等机构空闲) -> 装药 -> 等 CanFire
6. **锁外升仰角**: SetElevation 循环推进, 带无进展检测 (10s 无变化放弃)
7. **临界区 2 击发**: 等炮塔 Ready -> 五步确认 (任务/弹种/方向/仰角/就绪) -> 扳机 Arm -> Auto Fire 开启则击发 -> WaitFire
8. **锁外回位**: WaitBackToIdle (13s 最小恢复窗口 + 机构真正空闲) -> 标记 Finished -> 释放槽位拉下一单

## 6. 硬件抽象层 (FCS/ 目录)

### 6.1 BallisticCalculator - 弹道计算器
- 绑定游戏内"Balistic Calculator Controls": 距离拨盘, 装药拨盘, 方向拨盘, 弹种拨盘, 计算按钮, 仰角里程表
- SetDistance/SetDirection/SetCharge/SetShellType 设拨盘值, Calculate 点计算, GetElevation 读结果
- MinimumCharge 装药查表: <5km->1 号, <10km->2, <15km->3, <20km->4, <25km->5, 其余 6 号
- FcsCalc (在 TacticalRadar.cs 内) 提供面板预览用: Elevation(distance)/Charge(distance) 分段线性拟合

### 6.2 GunSystem - 单管炮抽象
- 绑定: 弹仓选择器, 转弹仓按钮, 推弹按钮, 装药按钮组 (PowderChargeController 下 Button Dispencer), 推药杆, GunController, 仰角杆, 装药余量表, Shell ID 显示器
- LoadBullet: 等机构空闲 (WaitForReloadReady) -> 转弹仓到目标弹 (每步 1.5s) -> 再等机构空闲 -> 推弹
- LoadPowder: 按次数点装药按钮 -> 推药杆; 按钮引用失效时自动重新扫描 (RefreshPowderButtons)
- SetElevation: 循环设仰角杆值直到到位, 带无进展检测 (连续 10s 变化 < 0.2° 判定卡死, 放弃本次瞄准)
- WaitForReloadReady: 正式版装填状态索引是数据驱动的, 只依据真实机构状态判断 (reloadController.working / ExternalReloadLoweringLocked / elevationChangeVelocity)
- WaitBackToIdle: 13s 最小恢复窗口 + 机构真正结束工作
- 弹种名归一: 游戏侧 PLCM 统一 Replace 为 PCLM
- GetState: 炮管状态快照 (膛内弹/CanFire/PendingReload/仰角速度/装药余量/弹仓列表)

### 6.3 MapTable - 地图桌
- 绑定: Player Turret Piece, Draggable Surface, MapToken_Artillery 标记 (1~4), Fire Mission Root
- GetMarkTarget: 标记位置 - 炮塔位置 (世界坐标统一转地图局部系, 铁巢紧急转移后依然正确) -> 距离 (x3.8164f 比例) + 方向角
- SetMarkerWorldPos/SetMarkerByKmPos/SetMarkerLocalPos/ResetMarker: 四种标记设置方式 (雷达标点用)
- GetAllFireMissionEntities: 枚举 Fire Mission Root 下全部实体

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
- 字段: targetId, angel, distance, position, bulletType, progress, abortCount
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
