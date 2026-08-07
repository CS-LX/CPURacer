# TaskmgrPlayer 调研笔记

> 仓库：https://github.com/svr2kos2/TaskmgrPlayer（子模块 `reference/TaskmgrPlayer`）  
> 语言：C++（Win32 + OpenCV HighGUI）  
> 日期：2026-08-08  

## 1. 它在做什么

在任务管理器（或任意可配置窗口）的目标子窗上**播放视频**（最初为 BadApple）。通过 `config.cfg` 指定主窗类名/标题与子窗类名。

## 2. 与 CPURacer M1 的关键差异

| 点 | TaskmgrPlayer | CPURacer M1（现状） |
|---|---|---|
| 附着方式 | **`SetParent` 把播放器做成图表子窗口** | 外部透明 Topmost Overlay，屏幕坐标对齐 |
| 跟随 | 每视频帧 `GetWindowRect` + 客户端坐标 `MoveWindow(0,0,w,h)` | 150ms 轮询 + WPF `Left/Top` + Dispatcher |
| 找图 | `FindWindow` + `EnumChildWindows`，同名子类取**面积最大** | `Taskmgr` 进程 + `CvChartWindow` 最大 |
| 切 Tab | 父窗隐藏/销毁时子窗一并消失（天然） | Memory 等页也有大 `CvChartWindow` → 误钉 |
| 失焦 | 子窗仍画在父客户区（只要父可见） | 意图：非前台隐藏；实现偏慢且易“露馅” |
| 技术栈 | 纯 C++ 紧循环 | C# / WPF，托管调度延迟明显 |

核心代码路径：`TaskmgrPlayer/TaskmgrPlayer.cpp` 中 `FindWnd` → `SetParent` → 播放循环内 `MoveWindow`。

```cpp
SetParent(playerWnd, EnumHWnd);
MoveWindow(playerWnd, 0, 0, w, h, true);  // 相对父客户区，拖动几乎零漂移
```

## 3. 对 CPURacer 四个问题的启示

1. **拖动露馅**：外部 Overlay + 低频轮询必然滞后；`SetParent` 或 `SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE)` 原生跟随更合适。  
2. **切到内存仍有框**：不能只取“最大 CvChartWindow”；需识别 **CPU 页**（侧栏选中态 / 相对布局 / 标题启发式 / 多图并存模式）。  
3. **失焦响应慢**：应用 `EVENT_SYSTEM_FOREGROUND` 等 WinEvent，立即藏窗；避免仅靠 100ms DispatcherTimer。  
4. **失焦但仍可见还显示**：产品规则应定为 **仅 Taskmgr（或其图表）为前台时显示**；与 TaskmgrPlayer“一直播”相反，需显式隐藏，且钩子要即时。

## 4. 架构建议（待产品确认后再实现）

### 推荐：C++ 跟踪核 + C# 游戏壳

| 放 C++（native DLL） | 放 C# |
|---|---|
| 窗口枚举 / 最大图表 / CPU 页判定 | 托盘、设置、HUD 文案 |
| `SetWinEventHook` 位置/前台/显隐 | 合成帧捕获与高度场（或后期再下沉） |
| 可选：`SetParent` 宿主窗或高速 `SetWindowPos` | Box2D 赛车、玩法、调试绘制 |
| 向托管回调 ROI / 可见性 | Overlay 内容 WPF/D2D 绘制 |

通信：C 导出 API + 回调，或共享内存结构体 `RoiState { hwnd, rect, dpi, flags }`。

### `SetParent` vs 外部 Overlay

| | SetParent 子窗 | 外部 Overlay |
|---|---|---|
| 拖动跟随 | 优 | 需钩子/高频 |
| 切 Tab | 随父消失（优） | 需正确识别页 |
| 输入/焦点 | 更绕 | 更易控 |
| 捕获蓝线 | 子窗可能挡住真图 → **游戏若要采样系统蓝线会冲突** | 不挡采样（半透明） |

**对 CPURacer 玩法**：需要看见并采样系统蓝线 → **不宜**用 TaskmgrPlayer 式不透明子窗盖住整图。更稳妥是：

- **跟踪/显隐/矩形**：C++（可学其找最大子窗 + 原生钩子）  
- **画面**：保持外部（或分层）透明 Overlay，只画车；或 `SetParent` 一个**全透明命中窗**仅作定位锚点  

## 5. 许可与依赖

- 参考用子模块；产品不链接其 OpenCV/ffmpeg 播放管线。  
- 上游含 `LICENSE`；集成时仅作算法/绑定参考。  
- 产品工程必须遵守仓库 [实施计划 §0.4](../实施计划.md)：**一键构建，禁止构建后手工拷贝 DLL**。

## 6. 结论

TaskmgrPlayer 证明：**在 Taskmgr 图表 HWND 上做原生父子绑定，跟随成本极低**。CPURacer 应吸收其“找最大子窗 + 客户区定位”思想，但因要拟合系统折线，不能照搬不透明视频子窗；应用 **C++ 负责跟踪与可见性，C# 负责游戏**，并在实施计划中增加专门阶段（见 `docs/实施计划.md` § M1.5）。
