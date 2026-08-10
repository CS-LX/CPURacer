# CPURacer — 开发者说明

玩家向说明：[English](README.md) · [简体中文](README.zh-CN.md)。

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
# 可选：.\pack-release.cmd 1.0.0.0
# CI 可设环境变量 PACK_NAME=CPURacer-ci-<sha> 覆盖 zip 名
```

产物默认在 `dist\CPURacer-<version>-win-x64.zip`。

## GitHub Actions

工作流 [`.github/workflows/build.yml`](.github/workflows/build.yml)：`main` / PR / `v*` tag 在 `windows-latest` 上 Release 构建并上传 Artifact。

| 触发 | zip 名 | Release |
|---|---|---|
| push / PR（无 tag） | `CPURacer-ci-<短sha>.zip` | 否 |
| tag `v*` | `CPURacer-<x.x.x.x>-win-x64.zip`（缺段补 `0`） | 是 |

> Windows 文件名不能含 `:`，故 CI 包用连字符而非 `CPURacer-ci:…`。

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
  CPURacer.Localization/ en / zh-Hans 资源 + Figgle 开局/结束提示
```

图标：`src/CPURacer.App/CPURacer.ico`（源图 `assets/cpuracer-icon.png`）。

**本地化 / Figgle：** `Strings*.resx` 提供托盘与 MessageBox；`<figgle>SPACE</figgle>` 由 `FigglePrompt` 运行时展开为 ASCII，画在 Overlay 正中。默认跟随系统 UI（`zh*` → 中文，否则英文）；托盘「高级 → 语言」可切换（进程内，不落盘）。

## 里程碑状态（摘要）

| 阶段 | 状态 |
|---|---|
| M1 / M1.5 钉框与原生跟踪 | ✅ |
| M2 / M2.5 / M2.6.2 拟合与 External Overlay | ✅ |
| M3 物理赛车 | ✅ |
| M4 玩家壳 | ✅（PDH / 单文件发布等延后） |
| Figgle 开局/结束 + en/zh-Hans | ✅（2026-08-09） |

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

## Figgle 提示手工验收（短）

1. 系统中文或托盘切「中文」：中央「按下 + ASCII SPACE + 开始游戏」；托盘中文  
2. English：`press / SPACE / to play`；托盘英文  
3. Space 开赛后中央提示消失；出界后中央 Figgle `GAME OVER` + 再试提示  

4. External Overlay 仍点击穿透；托盘焦点往返门禁不破
