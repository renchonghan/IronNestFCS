# IronNestFCS

[Demo Video](https://www.bilibili.com/video/BV1xc7F6WEET/)

[Iron Nest: Heavy Turret Simulator](https://store.steampowered.com/app/4300500/) 的 [MelonLoader](https://melonwiki.xyz/) Mod，为游戏中的重型炮塔加入一套自动化**火控系统（Fire Control System, FCS）**：在地图上点选目标，Mod 会自动解算弹道、采购/装填炮弹、调整炮塔方向与仰角，并完成确认与击发的全套流程。

> 基于游戏Steam发行版本开发，使用 IL2CPP + MelonLoader。

# 功能手册

完整的功能说明与操作指南（沙盘点选、任务队列、扫荡、齐射、HUD 面板、常见情形）见 **[Function.md](Function.md)**；架构与开发细节见 [Architecture.md](Architecture.md)。

# 安装教程

> 本教程假设你**只有一款原版游戏**，没装过任何 Mod。按顺序一步步做，不用懂任何技术。

## 第 0 步：确认你有一款能正常玩的游戏

1. 打开 Steam，在游戏库里找到 **Iron Nest: Heavy Turret Simulator**
2. 先正常启动一次，确认游戏能打开、能进入关卡——**确保游戏本身没问题，再开始装 Mod**
3. 关掉游戏

## 第 1 步：下载 MelonLoader

MelonLoader 是运行本 Mod 必需的前置工具（所有 MelonLoader Mod 都要它）。

1. 打开浏览器，访问 MelonLoader 官方下载页：

   **https://github.com/LavaGang/MelonLoader/releases/latest**

2. 在页面的下载列表里找到 **`MelonLoader.Installer.exe`**（Windows 安装器），点击下载
   - 认准文件名里有 `Installer` 的那个，别下错
3. 如果浏览器/系统提示"此文件可能有风险"，选择 **保留 / 仍然下载**——游戏 Mod 工具经常被安全软件误报，这是正常现象
4. 下载完成后，文件应该在你电脑的"下载"文件夹里

## 第 2 步：安装 MelonLoader

1. 双击运行 `MelonLoader.Installer.exe`
2. 在安装器界面里选择游戏 **Iron Nest: Heavy Turret Simulator**
   - 如果列表里找不到，点 **Browse / 浏览**，手动选择游戏根目录
   - 游戏根目录怎么找？Steam 库 → 右键游戏 → **管理 → 浏览本地文件**，弹出的窗口就是
3. 点击 **Install / 安装**，等待它跑完
4. 验证是否装好：打开游戏根目录，应该能看到一个 **`MelonLoader`** 文件夹——看到了就是装好了

## 第 3 步：下载本 Mod

1. 打开本仓库的 Releases 发布页：**[Releases](../../releases)**
2. 下载最新的 **`IronNestFCS_v2.0.0.zip`**（火控，必装）
3. （可选）如果你还想用"自定义唱片机"，再下载 **`CustomRecords` 压缩包**
4. 解压你下载的 zip（右键 → 全部解压缩），得到文件夹

## 第 4 步：把 Mod 装进游戏（重点，仔细看）

1. 打开解压出来的文件夹，里面应该有 **`Mods`、`UserData`、`UserLibs`** 三个文件夹（唱片机的压缩包打开后先进入里面的 `CustomRecords` 文件夹，也能看到这三个）
2. 打开游戏根目录（Steam 库 → 右键游戏 → **管理 → 浏览本地文件**）
3. 把解压出来的 **`Mods`、`UserData`、`UserLibs` 三个文件夹，一个一个地、整个拖进游戏根目录**
   - ⚠️ 注意：拖的是**文件夹本身**，不是打开文件夹去拖里面的文件
   - 如果弹出"目标文件夹已存在，是否合并/替换？"→ 选 **是 / 合并**
   - 没弹窗也正常，直接放进去就行
4. 放完后，游戏根目录里应该同时有 `Mods`、`UserData`、`UserLibs` 这三个文件夹（不是三个子文件夹里的东西，是三个文件夹本身）
5. 如果你还下载了唱片机压缩包，用同样的方式把它的三个文件夹也拖进去

## 第 5 步：（可选）放入你自己的音乐

自定义唱片机需要音乐文件才能生效：

1. 打开游戏根目录 → `UserData` → `CustomRecords` 文件夹（第一次装可能还没有，自己新建一个也行）
2. 把你的 `.mp3` / `.wav` / `.flac` 音乐文件放进去
3. （可选）给每首歌配一张封面：把同名图片（`.png` / `.jpg` / `.jpeg`）放在旁边，例如 `song.mp3` 配 `song.png`

## 第 6 步：启动游戏，检查是否成功

1. 从 Steam 正常启动游戏
2. 如果 MelonLoader 装好了，启动时屏幕上会**弹出一个黑色控制台窗口**——别关它，那是正常现象
3. 进入有炮塔和地图桌的关卡 — 左上角会先播放**开机自检画面**（CMD 风逐行自检，约 8 秒，自检完成后画面再停留 2.1 秒），随后切换为火控状态面板
4. 如果最终出现**火控状态面板**，说明安装成功 🎉
   - 如果自检卡在红字 `[FAIL]`，按键盘 **F9** 重试绑定即可
5. 使用方法见 [Function.md](Function.md) 功能手册

## 构建与安装

### 前置条件

- 已安装 **.NET 6 SDK**（见 [global.json](global.json)）。
- 游戏本体，并已为其安装 **MelonLoader**（IL2CPP）。

### 配置游戏路径

各 `.csproj` 通过 `GameDir` 属性定位游戏目录下的 MelonLoader 程序集。请把以下三个文件里的 `GameDir` 改成你本机的游戏安装路径：

- [IronNestFCS/IronNestFCS.csproj](IronNestFCS/IronNestFCS.csproj)
- [IronNestFCS.Logic/IronNestFCS.Logic.csproj](IronNestFCS.Logic/IronNestFCS.Logic.csproj)
- [IronNestFCS.CustomRecords/IronNestFCS.CustomRecords.csproj](IronNestFCS.CustomRecords/IronNestFCS.CustomRecords.csproj)

```xml
<GameDir>你的路径\Iron Nest Heavy Turret Simulator</GameDir>
```

### 构建

```bash
dotnet build IronNestFCS.sln -c Release
```

各程序集的输出位置：

- **宿主 Mod**（`IronNestFCS.dll`）：放入游戏的 `Mods/` 目录，由 MelonLoader 自动加载。
- **火控逻辑**（`IronNestFCS.Logic.dll`）：输出到 `UserData/IronNestFCS/`（不放进 `Mods/`，由宿主在运行时反射加载）。
- **契约**（`IronNestFCS.Abstractions.dll`）：放入 `UserLibs/`，确保宿主与逻辑共用同一份接口。
- **自定义唱片机**（`IronNestFCS.CustomRecords.dll`）：放入 `Mods/`。把音频文件（`.mp3` / `.wav` / `.flac`）放入游戏的 `UserData/CustomRecords/` 目录，封面两种方式任选：① 同名图片文件（`song.mp3` 配 `song.png`/`.jpg`/`.jpeg`，音频与图片分开存放）；② 音频文件内嵌封面（TagLib 标签）。进场景后自动为每个文件克隆一张 RecordDisk；两种封面都没有的文件会被跳过。目录不存在时首次运行会自动创建，无需手工准备素材。构建时依赖（CSCore、TagLibSharp）会自动拷贝到 `UserLibs/`（不能留在 `Mods/`，否则会被 MelonLoader 误当作 Mod 加载）。

> `IronNestFCS.Logic.csproj` 默认已把 `OutputPath` 指向 `$(GameDir)\UserData\IronNestFCS\`，构建即就位，改完代码进游戏按 F9 即可生效。

## 贡献

欢迎提交 Issue 和 Pull Request。

- 发现 Bug、有功能建议或疑问，请[提交 Issue](../../issues)。
- 改进代码请[提交 Pull Request](../../pulls)。改动火控逻辑时请留意 [Architecture.md](Architecture.md) §1.3 的热重载约束（不要在 Logic 中注册新的 IL2CPP 类型、协程必须登记以便卸载时停止、跨 ALC 只能传递 `IFcsModule`）。

## 免责声明

本项目为非官方的第三方 Mod，与游戏开发商无关。仅供学习与单机娱乐使用，使用风险自负。
