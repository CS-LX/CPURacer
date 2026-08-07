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

需要：Windows 10/11、[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。（M1.5 起若含 C++ 项目，需 VS 2022 生成工具；仍保持**一行构建**，见实施计划 §0.4。）

**硬规则：** 开发期只需 IDE「生成」或下面这一行构建；**禁止**构建后再手动复制 DLL/配置。

```powershell
cd src
dotnet build CPURacer.sln
dotnet run --project CPURacer.App
```

> M1.5 引入 `TrackNative` 后，若 `dotnet build` 无法编 C++，README 会把上面唯一构建命令改为等价的一行 `msbuild ...` 或根目录 `.\build.cmd`（内部自动处理，仍无手工拷贝）。

运行后托盘出现 **CPURacer** 图标（默认系统图标）：

- **开始跟踪 Taskmgr**：定位图表 ROI，红框 Overlay 跟随（需「性能 → CPU」且 Taskmgr 前台；M1 已知：拖动/失焦偏慢、内存页可能误钉 → 由 M1.5 修）
- **手动显示 Overlay**：强制显示占位窗（不依赖前台）
- **调试描边**：开关红色调试框
- **退出**：干净退出

不要求管理员权限（`asInvoker` + PerMonitorV2 DPI）。

### M1 验收步骤

1. 打开任务管理器 → **性能** → **CPU**  
2. `dotnet run --project CPURacer.App`  
3. 托盘 → **开始跟踪 Taskmgr**  
4. 点击任务管理器使其前台：红框应盖住大图；拖动/缩放应跟随  
5. 切到「进程」：红框应消失（找不到足够大的图）  
6. 点其它窗口：红框移出屏幕；再点回任务管理器应回来  

## 参考

- `reference/copy-dialog-lunar-lander` — 复制对话框 Lunar Lander（上游叙述链接见调研报告）
- `reference/TaskmgrPlayer` — Taskmgr 图表 `SetParent` 播视频（跟踪参考）

## 调研与计划

- 可行性调研：[docs/调研报告.md](docs/调研报告.md)
- 实施计划（含 **§0.4 一键构建硬规则**、M1.5）：[docs/实施计划.md](docs/实施计划.md)
- TaskmgrPlayer 调研：[docs/research/TaskmgrPlayer-调研.md](docs/research/TaskmgrPlayer-调研.md)

## 状态

**M1 完成**（有体验债务）。**M1.5 已确认**（C++ 跟踪加固 + 一键构建约束），下一步实现 M1.5；暂不进入 M2。
