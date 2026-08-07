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

需要：Windows 10/11、[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```powershell
cd src
dotnet build CPURacer.sln
dotnet run --project CPURacer.App
```

运行后托盘出现 **CPURacer** 图标（默认系统图标）：

- **显示空 Overlay**：打开红色描边占位透明窗（M0）
- **开始跟踪 Taskmgr（M1）**：占位，M1 再实现定位
- **调试描边**：开关 Overlay 调试框
- **退出**：干净退出

不要求管理员权限（`asInvoker` + PerMonitorV2 DPI）。

## 参考

`reference/copy-dialog-lunar-lander` — 参考项目源码（用于学习窗口叠加与交互实现）。

## 调研与计划

- 可行性调研：[docs/调研报告.md](docs/调研报告.md)
- 实施计划（M0–M3）：[docs/实施计划.md](docs/实施计划.md)

## 状态

**M0 完成**：`src/CPURacer.sln` 可编译；托盘 + 空 Overlay 骨架已就绪。下一步 **M1**（钉住 `CvChartWindow`）。
