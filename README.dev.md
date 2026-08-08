# CPURacer — 开发者说明

玩家向说明见根目录 [README.md](README.md)。

## 环境

- Windows 10/11 x64  
- [Visual Studio 2022](https://visualstudio.microsoft.com/)：MSBuild + Desktop C++ + .NET 桌面工作负载  
- .NET 8 SDK  

**硬规则：** 开发期用仓库根目录一行构建；原生 DLL 自动进 App 输出目录，禁止构建后再手工拷贝。

```powershell
.\build.cmd              # Debug（含 PDB）
.\build.cmd Release      # Release（不产出 PDB；见 src/Directory.Build.props）
```

运行（构建后）：

```powershell
dotnet run --project src\CPURacer.App --no-build
```

或打开 `src/CPURacer.sln` → 生成 → F5。

> 勿对含 C++ 的解决方案单独 `dotnet build`（dotnet CLI 编不了 vcxproj）。

打包发布 zip（先 `.\build.cmd Release`）：

```powershell
.\pack-release.cmd
```

产物默认在 `dist\CPURacer-<version>-win-x64.zip`。

## 工程结构

```text
src/
  CPURacer.App/          托盘入口、RaceHost
  CPURacer.TrackNative/  C++ WinEvent 跟踪 DLL
  CPURacer.Taskmgr/      ROI / 跟随
  CPURacer.Capture/      捕获 + 高度场
  CPURacer.Game/         RaceSim 物理
  CPURacer.Overlay/      WPF Child + 原生 External Overlay
  CPURacer.Native/       P/Invoke
```

图标：`src/CPURacer.App/CPURacer.ico`（源图 `assets/cpuracer-icon.png`）。

## 里程碑状态（摘要）

| 阶段 | 状态 |
|---|---|
| M1 / M1.5 钉框与原生跟踪 | ✅ |
| M2 / M2.5 / M2.6.2 拟合与 External Overlay | ✅ |
| M3 物理赛车 | ✅ |
| M4 玩家壳 | ✅（PDH / 单文件发布等延后） |

## 文档与参考

- [docs/调研报告.md](docs/调研报告.md)
- [docs/实施计划.md](docs/实施计划.md)
- [docs/research/](docs/research/) — M1.5～M4、TaskmgrPlayer 等笔记
- `reference/copy-dialog-lunar-lander` — Overlay / 像素地形
- `reference/TaskmgrPlayer` — SetParent / 配色（`config.cfg` ColorEdge / ColorDark）

## 调试提示

- **Tab** 或托盘「高级」：调试描边 / 拟合线  
- 工程状态串在「高级 → 状态」  
- 托盘往返后 Overlay 不得被 Taskmgr 盖住（M2.6.2 门禁）
