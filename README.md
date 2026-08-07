# CPURacer

在 Windows 任务管理器（taskmgr）的 CPU 性能折线图上玩赛车的 meta 小游戏。

灵感来自 [copy-dialog-lunar-lander](https://github.com/Sanakan8472/copy-dialog-lunar-lander)：把系统原生 UI 当成游戏场地。

## 玩法构想

- **山路**：任务管理器「性能 → CPU」折线图（随时间向后滚动）
- **车辆**：带物理的小车，需沿山路向前开，避免翻车
- **死亡**：唯一条件是被折线卷轴带出视口（翻车后失控，通常也会因此出界）
- **视觉硬约束**：程序内碰撞山路必须拟合屏幕蓝线，否则会出现车在空中开

详见 [docs/调研报告.md](docs/调研报告.md) 第 1.2、5 章。

## 参考

`reference/copy-dialog-lunar-lander` — 参考项目源码（用于学习窗口叠加与交互实现）。

## 调研

实现可行性见 [docs/调研报告.md](docs/调研报告.md)（含本机 Taskmgr 探针记录与 PoC 里程碑）。

## 状态

空仓库骨架；技术调研已完成，游戏工程尚未开始。
