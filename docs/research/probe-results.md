# Taskmgr Probe Results

- Generated: 2026-08-08 02:05:15
- OS: Microsoft Windows 11 家庭版 中文版 Build 26200
- Culture: zh-CN

## 1. Process / top-level windows

- Taskmgr process count: 1
- PIDs: 18740

| HWND | Visible | ClassName | Title | Rect WxH | DPI |
|---|---|---|---|---|---|
| 328800 | True | TaskManagerWindow | 任务管理器 | 1247x763 | 144 |
| 328380 | False | IME | Default IME | 0x0 | 144 |
| 262584 | False | MSCTFIME UI | MSCTFIME UI | 0x0 | 144 |
| 132166 | False | IME | Default IME | 0x0 | 144 |
| 131404 | False | IME | Default IME | 0x0 | 144 |
| 67398 | False | IME | Default IME | 0x0 | 144 |
| 132164 | False | WorkerW | (empty) | 135x37 | 144 |
| 67482 | False | tooltips_class32 | (empty) | 61x19 | 144 |
| 67394 | False | GDI+ Hook Window Class | GDI+ Window (taskmgr.exe) | 1x1 | 144 |
| 394436 | False | TrayiconMessageWindow | (empty) | 135x37 | 144 |
| 197642 | False | tooltips_class32 | (empty) | 0x0 | 144 |

- Selected main window HWND: `328800` Class=`TaskManagerWindow`

## 2. Child HWND sample (visible, depth-first up to 80)

| HWND | ClassName | WxH | Title |
|---|---|---|---|
| 131128 | Windows.UI.Composition.DesktopWindowContentBridge | 1232x704 | DesktopWindowXamlSource |
| 132066 | GlassWindow | 661x37 | Glass Window |
| 132064 | GlassWindow | 341x37 | Glass Window |
| 131140 | Windows.UI.Composition.DesktopWindowContentBridge | 1002x37 | DesktopWindowXamlSource |
| 1179702 | Windows.UI.Composition.DesktopWindowContentBridge | 992x51 | DesktopWindowXamlSource |
| 263366 | NativeHWNDHost | 991x652 | TaskManagerMain |
| 131398 | DirectUIHWND | 991x652 |  |
| 67396 | CtrlNotifySink | 60x40 |  |
| 67400 | CvChartWindow | 60x40 |  |
| 67402 | CtrlNotifySink | 60x40 |  |
| 67404 | CvChartWindow | 60x40 |  |
| 67406 | CtrlNotifySink | 60x47 |  |
| 67408 | CvChartWindow | 60x47 |  |
| 67410 | CtrlNotifySink | 60x47 |  |
| 67412 | CvChartWindow | 60x47 |  |
| 67422 | CtrlNotifySink | 60x47 |  |
| 67424 | CvChartWindow | 60x47 |  |
| 67426 | CtrlNotifySink | 60x47 |  |
| 67428 | CvChartWindow | 60x47 |  |
| 67430 | CtrlNotifySink | 60x47 |  |
| 67432 | CvChartWindow | 60x47 |  |
| 262910 | CtrlNotifySink | 729x381 |  |
| 132668 | CvChartWindow | 729x381 |  |
| 131144 | Windows.UI.Composition.DesktopWindowContentBridge | 992x652 | DesktopWindowXamlSource |

- Visible child HWND count (capped): 24

## 3. UI Automation tree (trimmed)

- UIA top-level elements for Taskmgr PID: 1

```
- [Window] Name="任务管理器" Class="TaskManagerWindow" Id="" Fw="Win32" HWND=328800 Rect=1,870x1,145 @(217,141)
  - [Pane] Name="DesktopWindowXamlSource" Class="Windows.UI.Composition.DesktopWindowContentBridge" Id="" Fw="Win32" HWND=131128 Rect=1,848x1,056 @(228,219)
    - [Pane] Name="" Class="Windows.UI.Input.InputSite.WindowClass" Id="" Fw="Win32" HWND=262342 Rect=-∞x-∞ @(∞,∞)
  - [Pane] Name="Glass Window" Class="GlassWindow" Id="" Fw="Win32" HWND=132066 Rect=991x56 @(806,152)
  - [Pane] Name="Glass Window" Class="GlassWindow" Id="" Fw="Win32" HWND=132064 Rect=512x56 @(294,152)
  - [Pane] Name="DesktopWindowXamlSource" Class="Windows.UI.Composition.DesktopWindowContentBridge" Id="" Fw="Win32" HWND=131140 Rect=1,503x56 @(294,152)
    - [Pane] Name="" Class="Windows.UI.Input.InputSite.WindowClass" Id="" Fw="Win32" HWND=131522 Rect=-∞x-∞ @(∞,∞)
  - [Pane] Name="DesktopWindowXamlSource" Class="Windows.UI.Composition.DesktopWindowContentBridge" Id="" Fw="Win32" HWND=1179702 Rect=1,488x77 @(588,219)
    - [Pane] Name="" Class="Windows.UI.Input.InputSite.WindowClass" Id="" Fw="Win32" HWND=197672 Rect=-∞x-∞ @(∞,∞)
  - [Pane] Name="TaskManagerMain" Class="NativeHWNDHost" Id="" Fw="Win32" HWND=263366 Rect=1,486x978 @(590,297)
    - [Pane] Name="" Class="DirectUIHWND" Id="" Fw="Win32" HWND=131398 Rect=1,486x978 @(590,297)
      - [Pane] Name="" Class="CtrlNotifySink" Id="" Fw="Win32" HWND=67396 Rect=90x60 @(610,324)
        - [Pane] Name="" Class="CvChartWindow" Id="" Fw="Win32" HWND=67400 Rect=90x60 @(610,324)
      - [Pane] Name="" Class="CtrlNotifySink" Id="" Fw="Win32" HWND=67402 Rect=90x60 @(610,415)
        - [Pane] Name="" Class="CvChartWindow" Id="" Fw="Win32" HWND=67404 Rect=90x60 @(610,415)
      - [Pane] Name="" Class="CtrlNotifySink" Id="" Fw="Win32" HWND=67406 Rect=90x71 @(610,506)
        - [Pane] Name="" Class="CvChartWindow" Id="" Fw="Win32" HWND=67408 Rect=90x71 @(610,506)
      - [Pane] Name="" Class="CtrlNotifySink" Id="" Fw="Win32" HWND=67410 Rect=90x71 @(610,608)
        - [Pane] Name="" Class="CvChartWindow" Id="" Fw="Win32" HWND=67412 Rect=90x71 @(610,608)
      - [Pane] Name="" Class="CtrlNotifySink" Id="" Fw="Win32" HWND=67422 Rect=90x71 @(610,710)
        - [Pane] Name="" Class="CvChartWindow" Id="" Fw="Win32" HWND=67424 Rect=90x71 @(610,710)
      - [Pane] Name="" Class="CtrlNotifySink" Id="" Fw="Win32" HWND=67426 Rect=90x71 @(610,812)
        - [Pane] Name="" Class="CvChartWindow" Id="" Fw="Win32" HWND=67428 Rect=90x71 @(610,812)
      - [Pane] Name="" Class="CtrlNotifySink" Id="" Fw="Win32" HWND=67430 Rect=90x71 @(610,914)
        - [Pane] Name="" Class="CvChartWindow" Id="" Fw="Win32" HWND=67432 Rect=90x71 @(610,914)
      - [Pane] Name="" Class="CtrlNotifySink" Id="" Fw="Win32" HWND=262910 Rect=1,094x571 @(946,403)
        - [Pane] Name="" Class="CvChartWindow" Id="" Fw="Win32" HWND=132668 Rect=1,094x571 @(946,403)
  - [Pane] Name="DesktopWindowXamlSource" Class="Windows.UI.Composition.DesktopWindowContentBridge" Id="" Fw="Win32" HWND=131144 Rect=1,488x978 @(588,297)
    - [Pane] Name="" Class="Windows.UI.Input.InputSite.WindowClass" Id="" Fw="Win32" HWND=131134 Rect=-∞x-∞ @(∞,∞)
```

### Chart-area candidates (heuristic)

| Name | Class | AutomationId | ControlType | Framework | HWND | Rect |
|---|---|---|---|---|---|---|
|  | DirectUIHWND |  | Pane | Win32 | 131398 | 1,486x978 @(590,297) |
| DesktopWindowXamlSource | Windows.UI.Composition.DesktopWindowContentBridge |  | Pane | Win32 | 131144 | 1,488x978 @(588,297) |

## 4. Capture experiments

| Target | Method | OK | Size | Notes | File |
|---|---|---|---|---|---|
| main HWND | BitBlt | False | 1870x1145 | PrintWindow returned empty/black | capture-main-bitblt.png |
| main screen CopyFromScreen | ScreenBlit | True | 1870x1145 | non-black=1596/1600 | *(screenshot removed — captured IDE UI over Taskmgr)* |

## 5. Curve / height-field heuristic on screen capture

- No saturated dominant color found in heuristic chart band (theme/layout may differ; need CPU page focused).

## 6. PDH / PerformanceCounter vs wall clock

| Time | % Processor Time (_Total) |
|---|---|
| 02:05:22 | 3.69 |
| 02:05:23 | 6.17 |
| 02:05:24 | 9.67 |
| 02:05:25 | 6.57 |
| 02:05:26 | 4.26 |

- First NextValue discarded (always ~0). Sampling interval 1s.
- Qualitative: Taskmgr graph smoothing/period may differ; use PDH for calibration/fallback, not sole visual track.

## 7. Integrity / elevation snapshot

- Probe process elevated: False
- Taskmgr PID: 18740
- UIA dump above succeeded: yes
- Note: If Taskmgr is Run as administrator and game is not, UIPI may block some cross-process UI ops; treat as conditional risk.

## 8. Quick conclusions for report

1. Process name `Taskmgr.exe` is a stable language-independent anchor.
2. Child HWND richness and UIA FrameworkId must be read from tables above (Win11 often XAML/WinUI sparse HWND).
3. Prefer screen/Graphics Capture if window BitBlt/PrintWindow is black.
4. Height field must track a **scrolling polyline**, not bottom-filled terrain.
5. PDH available but not pixel-identical to Taskmgr curve.

## 9. Follow-ups merged into report

See also:

- `probe-cvchart.md` / `probe-load.md`
- **Critical:** `BitBlt(CvChartWindow)` yields **grid only**; utilization polyline lives in the composed frame (`capture-cvchart-load-bitblt.png` vs `capture-main-load-screen.png`).
- Non-DPI-aware probe processes can mis-align `GetWindowRect` with `CopyFromScreen` at 150% scaling.
