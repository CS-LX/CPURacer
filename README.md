# CPURacer

在 Windows **任务管理器 → 性能 → CPU** 折线图上开赛车的小游戏。

灵感来自 [copy-dialog-lunar-lander](https://github.com/Sanakan8472/copy-dialog-lunar-lander)：把系统界面当成跑道。

## 怎么玩

1. 从 [Releases](https://github.com/CS-LX/CPURacer/releases) 下载并解压，运行 `CPURacer.exe`  
   - 若任务管理器是管理员启动的，本程序也要用管理员运行（否则方向键可能无效）
2. 打开 **任务管理器 → 性能 → CPU**，点一下让它在前台
3. 图上出现 `Paused — Space 开始` 后按 **Space**（或托盘 / 双击托盘「开始」）
4. **W / ↑** 加油门 · **S / ↓** 减油门（可倒车）· **Space** 重开  
5. 别被滚动的折线图甩出画面

托盘菜单：开始 / 停止、重开、退出。更多选项在「高级」。

## 需要什么

- Windows 10 / 11（x64）
- 已安装 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)（若直接运行 `CPURacer.exe` 提示缺运行时再装）

## 已知问题

- Taskmgr 高权限、游戏非管理员时，W/S 可能被系统挡住（UIPI）
- 捕不到 CPU 大图时暂无备用山路，请确认开着性能/CPU 视图

## 开发者

构建、工程结构、里程碑与调研笔记见 **[README.dev.md](README.dev.md)**。
