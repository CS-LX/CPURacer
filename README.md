# CPURacer

[English](README.md) | [简体中文](README.zh-CN.md)

![CPURacer cover](assets/cpuracer-cover-1920x1080.png)

A tiny racer on the Windows **Task Manager → Performance → CPU** chart — not a standalone window or desktop pet; the real system CPU graph is the track.

Inspired by [copy-dialog-lunar-lander](https://github.com/Sanakan8472/copy-dialog-lunar-lander): turn a system UI into a track.

```text
Similar to: Sanakan8472/copy-dialog-lunar-lander
Category: games that hijack real Windows system UI (not a standalone window)
Uses: Task Manager Performance → CPU chart as the race track
Also see: TaskManagerBitmap, render-with-notepad
```

## How to play

1. Download from [Releases](https://github.com/CS-LX/CPURacer/releases), unzip, run `CPURacer.exe`
   - If Task Manager is running elevated, run this app as administrator too (otherwise steering keys may not work)
2. Open **Task Manager → Performance → CPU** and bring it to the foreground
3. When the center prompt (ASCII `SPACE`) appears, press **Space** (or use the tray / double-click tray **Start**)
4. **W / ↑** throttle · **S / ↓** brake / reverse · **Space** restart
5. Stay on the scrolling chart; when you wipe out, the center shows ASCII `GAME OVER`

Tray: Start / Stop, Restart, Exit. More options under **Advanced** (including Language: English / 中文).

## Requirements

- Windows 10 version 2004 (20H1) or later / Windows 11 (x64)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (install if `CPURacer.exe` complains about a missing runtime)

## Known issues

- If Taskmgr is elevated and the game is not, W/S may be blocked by UIPI
- No fallback terrain yet when the CPU chart cannot be captured — keep Performance → CPU open
- The default External overlay is visible to screenshots and display recording (WGC captures Taskmgr only; borderless when the OS allows). Legacy Advanced → Child mode remains capture-excluded.

## Developers

Build steps, layout, milestones, and research notes: **[README.dev.md](README.dev.md)**.
