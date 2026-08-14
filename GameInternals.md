# 游戏逆向发现记录 (Game Internals Notes)

> 铁巢: 重炮塔模拟器 (正式版) IL2CPP 逆向笔记. 记录通过探针发现的接口 / 组件 / 特性, 供后续开发查阅.
> 所有探针方法都保留在 GunSystem.cs 里 (注释状态), 需要时取消注释调用即可.

---

## 1. 可绑定的游戏组件

### 1.1 炮塔 (TurretSystem)

- 物体: `TurretSystem` (真实炮塔, 房间坐标)
- 组件: `TurretController`
  - `CurrentAngle` (原始值, 负号) / `CurrentAngleCompass` (罗盘值, 与 task.angel 同号)
  - `DesiredRotation` = -task.angel (设置目标方向用)
  - `rotationVelocity`, `CurrentElevation`, `minBarrelElevation`(0) / `maxBarrelElevation`(60)
  - `turretBase` (RectTransform, 挂在 **MapRoot** 网格空间, localPosition = **铁巢当前网格坐标**, 归位/紧急转移由游戏更新 - 铁巢位置的权威源)
  - `turret3DMimic`, `guns` (GunController 列表), `compassBearingOffset`, `invertCompassBearing`

### 1.2 炮管 (GunLeft / GunRight)

- 物体: `GunLeft` / `GunRight` -> 组件 `GunController`
  - `CurrentElevation` (实际仰角, 面板行 1 E:)
  - `PowderCharges` (实装药包数, P4 推入确认后才有值, P3 阶段恒 0)
  - `PredictedImpactTime` (**活变量**: 弹道解算输出, 抬炮实时更新; 击发后由计时表倒数)
  - `CanFire`, `pendingReload`, `ChamberedShellBlueprint` (膛内弹, `shellDefinition.ShellId` - 游戏侧 PLCM 需归一为 PCLM)
  - `ExternalReloadLoweringLocked`, `elevationChangeVelocity`

### 1.3 装填控制台 (`Gun System L/R` -> `--Reloading Console`)

- 组件: `ArtilleryReloadController`
  - `CurrentStateIndex` / `CurrentState.stateKey` (数据驱动状态机)
  - `reloadStates` (状态定义表, 10 个状态)
  - `working`
- 状态机 stateKey 表 (索引数据驱动, 以名字为准):
  | stateKey | 含义 |
  | --- | --- |
  | BreachLocked | 锁闩闭合 (循环回位态) |
  | BreachUnlocking | 解锁闩 |
  | GuideDeploy | 导槽展开 |
  | BreechOpen | P1 炮弹就绪 (架上有弹) |
  | ShellRamming | P2 推弹中 (检查点: 此时膛内读数不可信) |
  | SelectPowderCharge | P3 药包就绪 (拉杆窗口) |
  | RamCharges | P4 药包推入 |
  | CloseShellGuide | 收导槽 |
  | FinalSequence | 收尾 |
  | Done | 完成 |
- 子物体:
  - `PowderChargeController` -> `Button Dispencer (0..5)` (药包拉杆, LookAtTarget)
  - `Universal Button Move Cylinder` (转弹仓), `Universal Button Load shell Rammer` (推弹按钮), `Universal Button Charge Rammer (1)` (推药按钮)
  - 4 个相位灯: `Progress Light` 系列 (Animator 驱动)

### 1.4 里程表 (OdometerDisplay)

| 名称 | 位置 | 含义 |
| --- | --- | --- |
| Odomiter Counter Charge Invenotry | --Reloading Console 直接子级 | 药包库存 |
| Odomiter Counter Calculated Charges | Calculated Charge Display (计算装药量) 下 | 解算输出装药数 |
| Odomiter Counter Selected Charges | **Calculated Charge Display (1) (实际装药量)** 下 | **实拉杆数 (P3 就可见)** |

- 注意: Selected Charges 不在装填控制台里, 必须从炮系统根递归查找 (`FindChildDeep`)
- `Odomiter Output Elivation` (弹道计算器输出仰角) 挂在 Gun Watch 上
- 读法: `(int)odometer.CurrentNumber`

### 1.5 炮兵计时表 (Gun Watch / MissionWatch)

- 路径: `Trigger Console/.Trigger Console Floor/.Review Console Parent/Gun Watch (1)` (每个炮一个)
- 组件: `GunStopwatch`
  - `watchedGun` (GunController, 用它匹配左右)
  - `state` / `currentState` (`"CountingDown"` = 倒计时中)
  - `lastPredictedTravelTime` (表针读数)
  - `previousCountingDownRemainingSeconds` (**倒计时剩余秒数**)
  - `countdownStartTime` (击发时刻 Time.time) / `latchedTravelTime` (闩锁总飞行时间)
  - `secondsPerFullRotation` = 60 (表针 60 秒一圈)
- 数字表: `Odomiter Output Hours/Minutes/Seconds` (OdometerDisplay + GenericTimerSceneSync, 无尽模式恒 0)

### 1.6 弹道计算器 (Balistic Calculator Controls)

- 拨盘: `.Range Dial Parent` / `.Charge Dial Parent` / `.Gross Range Dial` (方向) / `.Shell Dial` (弹种)
- 按钮: `Calculate Universal Button` (按下才存储"已计算装药数", 药包杆最多允许拉到该数 - **拉杆前必须锁内重算**)
- 输出: `Odomiter Output Elivation` (真实解算仰角)

### 1.7 击发确认台 (TriggerConsole)

- 五步确认按钮: 任务 / 弹种 / 旋转 / 仰角 / 准备击发; 校验的是**实时状态** (实际仰角/膛内弹种/飞行时间/装填完成), 不读计算台

### 1.8 沙盘地图 (Tactical Map)

- `Draggable Surface` (BoxCollider 6.29x2.85 = 板子含边框), 令牌的父物体
- `Canvas/MapRoot` (UI 网格空间, 0-20 x 0-10 = A-T x 1-10 大格, 每大格 9x9 小格)
- `Player Turret Piece` (**铁巢棋子**, DraggableItem, 玩家可拖动)
- 令牌: `MapToken_Artillery` (炮击目标 1-4 = T1-T4) / `MapToken_RefrencePoint` (参考点) / `MapToken_Recon` (侦察)
- 实体: `Fire Mission Root` 子物体, 每个带 `EntityLocation` (名字如 hostiletank#1 / allyinfantry#3 / enemytarget#1 / fdc#1)
- `---ImpactMarkerManager` (ImpactMarkerManager + ImpactTracker):
  - `turretController` (真实炮塔引用)
  - `markerDataList` (每炮 MarkerData: gun/container/activeMarkerInstance/lastMarkerName)
- 落点标记: `GunLeft_ImpactMarker` / `GunRight_ImpactMarker` (canvas 坐标 = 网格坐标; **炮放平 = 铁巢, 有解算 = 该炮预测落点**, 所以不是铁巢权威源)

### 1.9 地图箭头工具 (游戏自带)

- `MapMarkers` 下: `MapMarkerWhite/Yellow/RED` (玩家画的为 (Clone)) + `MapMarkerDiscCompass` (罗盘圆盘)
- 组件: `MapMarkerLineUI` (line/disc/pointerTip/pointerRotationOffsetDegrees/angleLabel/distanceLabel/minimumDragDistance=0.075/speedNormalizationRange=13) + `MapMarkerHitTarget` (测速, 对动射击可用)
- 线条核心: **Il2CppShapesRuntime** 程序集的 `Il2CppShapes.Line`
  - `Start` / `End` (Vector3, 线的局部空间) / `Thickness` (0.003-0.01 板面单位可用) / `Color` / `ColorStart` / `ColorEnd` / `Dashed` / `endCaps` / `geometry=Flat2D`
- `Il2CppShapes.Disc`: `Radius` / `Color` / `HasThickness` (只读, 空心需 `type=Ring`, 但 DiscType 枚举 stub 不可见 - 改用四条 Line 画菱形)

---

## 2. 关键数值与校准

- **km 比例**: 3.8164 (1 板面单位 = 3.8164 km; 网格 1 大格 = 1 km)
- **沙盘校准** (MapTable): `MapBottomLeft = (-2.6238, -1.3741)` (网格原点 A1 在棋子空间的位置, 经平射真值+目测校准), `MapCellSize = 1/3.8164`
- **网格**: A-T x 1-10 大格, 每大格 9x9 小格; 1 小格 ≈ 111 m ≈ 0.0291 板面单位
- **世界偏移** (GetMarkTarget 的 position 换算): 板面局部 x 3.8164 + (10.016, 5.235)
- **方向角约定**: `SignedAngle(target, Vector3.up, Vector3.forward)`, 0 = 北(+Y), 顺时针正; **必须在板面局部系算, 网格画布空间朝向不同会打飞**
- **装药查表** MinimumCharge: <5km=1, <10km=2, <15km=3, <20km=4, <25km=5, 其余 6

---

## 3. IL2CPP / MelonLoader 踩坑记录

1. `Transform.Find` / `FindChild` 只查**直接子级**, 不是递归 - 深层的用自己写 `FindChildDeep`
2. `GetComponents<Component>()` 按基类封箱, `is` 判断失效 - 用 `Cast<T>()` try/catch 逐类型试
3. `Font.CreateDynamicFontFromOSFont` 被 IL2CPP 裁剪 (Method unstripping failed) - 用游戏自带 `TMP_FontAsset.sourceFontFile` (Inconsolata-SemiBold 真等宽)
4. 运行时 `AddComponent<TextMeshPro>()` 不渲染 (ForceMeshUpdate 也没救) - 改用 Il2CppShapes.Line 画七段数码
5. 枚举 stub 不可见 (`LineColorMode` / `DiscType` 等, 找不到命名空间) - 反射 `Enum.Parse` 设值或绕开
6. `GameObject.CreatePrimitive` 默认材质着色器被裁剪 (渲染紫块); 克隆场景材质会带原贴图 - 用游戏自己的 `InteractionLight Green` (高发射) 等材质
7. `FindObjectsOfTypeAll` 枚举可能含**销毁中的 null 组件**, 必须判空
8. `Il2CppShapesRuntime.dll` 需要单独加 csproj 引用 (Line/Disc 类型)
9. 热重载约束: 不注册新 IL2CPP 类型, 但实例化现有类型 (new GameObject / AddComponent<现有类型> / Instantiate) 都允许
10. 运行时创建的 3D 物件不会随 F9 重载销毁, 需在生成时按名字清理旧实例

---

## 4. 探针清单 (全部保留在 GunSystem.cs, 注释状态)

| 探针 | 用途 |
| --- | --- |
| DebugDumpReloadState | 炮管+装填控制台全结构 dump, GunController/ArtilleryReloadController 字段 |
| DumpChildrenRecursive | 层级递归 dump (Odometer/TMP/Renderer/Light + 真实类型名) |
| DumpReloadStates | 装填状态定义表 + 当前状态 |
| DumpFields | 托管反射 dump 字段 (强类型引用时值真实) |
| DumpNativeFields | 原生反射 dump (封箱组件用) |
| TrueTypeName | 封箱组件的真实 Il2Cpp 类型名 |
| FindChildDeep | 递归找子物体 |
| ProbeTacticalMap | 地图子树 dump + 坐标/矩形/碰撞体 |
| ProbeNestVariable | ImpactMarkerManager/MarkerData 字段 (确认真源 turretBase) |
| ProbeFlightTimer | Gun Watch / GunStopwatch 读数 |
| ProbeArtilleryTimer | 炮兵计时器 (确认为 PredictedImpactTime) |
| ProbeDrawnArrows | 玩家画的箭头 (MapMarkerLineUI + Line 字段) |
| ProbeNestPrompt | 含"铁巢"的 TMP 文本扫描 |

---

## 5. 已实现的特性速查

- **面板**: 两行火控状态 (行1 炮实际状态 FT:, 行2 火控解 T:-), 两列队列 (任务/完成, 固定 8 行), 荧光绿等宽字体
- **CALL 枢纽状态机**: 检查点鲁棒性 (推弹中读数不可信), 计数器防死循环, 单次解算
- **铁巢棋子吸附**: turretBase 真源, 10fps, 沙盘校准
- **地图元素**: 实体菱形框 (敌对红/友军蓝), 右键入队/取消, 序列标签 (七段数码), 落点计时器 (整秒), 瞄准十字/X (CanFire 显示), 铁巢->目标虚线
- **炮兵计时表**: GunStopwatch 读数, 行 1 瞄准期读活变量 PredictedImpactTime
