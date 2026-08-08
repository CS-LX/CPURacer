# CPURacer

在 Windows 任务管理器（taskmgr）的 CPU 性能折线图上玩赛车的 meta 小游戏。

灵感来自 [copy-dialog-lunar-lander](https://github.com/Sanakan8472/copy-dialog-lunar-lander)：把系统原生 UI 当成游戏场地。

## 玩法构想

- **山路**：任务管理器「性能 → CPU」折线图（随时间向后滚动）
- **车辆**：带物理的小车，需沿山路向前开，避免翻车
- **死亡**：唯一条件是被折线卷轴带出视口（翻车后失控，通常也会因此出界）
- **视觉硬约束**：程序内碰撞山路必须拟合屏幕蓝线，否则会出现车在空中开

详见 [docs/调研报告.md](docs/调研报告.md) 第 1.2、5 章。

## 编译与运行

需要：Windows 10/11、[Visual Studio 2022](https://visualstudio.microsoft.com/)（含 **MSBuild + Desktop C++ + .NET 桌面**）。

**硬规则（§0.4）：** 开发期只需下面**一行**构建；DLL 自动进 App 输出目录，禁止构建后再手工拷贝。

```powershell
.\build.cmd
```

然后运行：

```powershell
dotnet run --project src\CPURacer.App --no-build
```

或在 Visual Studio 中打开 `src/CPURacer.sln` → **生成解决方案** → F5（同上，无需拷 DLL）。

> 勿对含 C++ 的解决方案单独使用 `dotnet build`（dotnet CLI 无法编 vcxproj）。统一用 `.\build.cmd` 或 VS 生成。

运行后托盘出现 **CPURacer**（启动即监视 Taskmgr，无需「开始跟踪」）：

- **开始 / 停止**、**重开（Space）**、**退出** — 玩家主菜单；双击托盘 = 开赛  
- **高级**：暂停/恢复监视、跟随方式、手动 Overlay、调试描边 / 拟合线、工程状态  
  - **外部 Overlay（默认）**：原生 Win32 + Direct2D，点击穿透  
  - **子窗 SetParent**：透明 Overlay 进图表（调试用）  

### M1.5 验收

1. `.\build.cmd` 成功，且存在 `src\CPURacer.App\bin\Debug\net8.0-windows\CPURacer.TrackNative.dll`  
2. 打开 Taskmgr → **性能 → CPU**，点 Taskmgr 前台（高级开调试描边时可见红框贴大图）  
3. **快速拖动**窗口：框应紧贴（明显好于 M1）  
4. 切到 **内存 / GPU**：框消失  
5. 切回 **CPU**：框回来  
6. Alt+Tab / 点其它窗：框立刻消失；Taskmgr 仍可见但非前台时也**无**框  
7. 托盘 **高级 → 跟随方式**：可在「外部 Overlay」与「子窗 SetParent」间切换  

**状态：** M1.5 **已验收通过**。完整性级别与跟随方式的交叉限制见 [docs/research/M1.5-验收-跟随与完整性.md](docs/research/M1.5-验收-跟随与完整性.md)。

## 参考

- `reference/copy-dialog-lunar-lander` — Overlay + 像素地形参考  
- `reference/TaskmgrPlayer` — `SetParent` 播视频 / 原生跟随参考  

## 调研与计划

- [docs/调研报告.md](docs/调研报告.md)
- [docs/实施计划.md](docs/实施计划.md)（§0.4 一键构建、M1.5 / M2.5）
- [docs/research/TaskmgrPlayer-调研.md](docs/research/TaskmgrPlayer-调研.md)
- [docs/research/M2.5-Overlay主循环对齐.md](docs/research/M2.5-Overlay主循环对齐.md)
- [docs/research/M3-物理赛车.md](docs/research/M3-物理赛车.md)
- [docs/research/M4-玩家壳与发布.md](docs/research/M4-玩家壳与发布.md)

## 状态

**M2.6.2 已通过**：External 已迁移到原生 Win32 + Direct2D，使用独立 Dispatcher 帧时钟；因普通权限下跨进程相对 Z 序被 Windows 拒绝，采用原生 Topmost + 前台进程显隐。WPF 仅保留 Child。见 [M2.6 迁移笔记](docs/research/M2.6-原生ExternalOverlay迁移.md)。

**M3 可玩验收通过（非侵入）**：旁路 RaceHost + 世界折线续铺/视口投影 + 硬轴无级油门；Overlay 只出高度场、`SetCarPose` 画车，不改 `TickExternalFrame`。见 [M3-物理赛车.md](docs/research/M3-物理赛车.md)。

**M4 进行中**：启动自动监视、托盘玩家化、默认 Play、Space 开赛、Accent 跟蓝线；PDH 降级与发布打包仍待。见 [M4-玩家壳与发布.md](docs/research/M4-玩家壳与发布.md)。

### 玩家快速开赛

1. 运行 `CPURacer.exe`（若 Taskmgr 是管理员，本程序也要管理员）  
2. 打开 **任务管理器 → 性能 → CPU**  
3. 见 `Paused — Space 开始` → **Space**（或托盘/双击「开始」）  
4. **W/S** 油门 · **Space** 重开 · **Tab** 调试  

### 已知问题

- Taskmgr 高特权时，非管理员进程的 W/S 可能被 UIPI 挡住  
- 捕获失败时尚无 PDH 影子山路，仅提示检查 CPU 大图  

### 工程验收（调试）

普通权限 → 性能/CPU → **高级 → 调试拟合线**。静止时橙线应持续可见；状态在高级菜单里看 `cap=`。  
重点回归：右键托盘打开菜单 → 点回 Taskmgr 图表空白；Overlay 不得被盖住。
