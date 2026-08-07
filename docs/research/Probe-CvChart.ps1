# Follow-up: capture CvChartWindow specifically
$ErrorActionPreference = "Stop"
$outDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class ChartProbe {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWndParent, EnumProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint id);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int x1, int y1, int rop);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT {
        public int Left, Top, Right, Bottom;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
    }
}
"@

$proc = Get-Process Taskmgr -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Start-Process taskmgr; Start-Sleep 2; $proc = Get-Process Taskmgr | Select-Object -First 1 }
$targetPid = $proc.Id

$script:roots = New-Object System.Collections.Generic.List[IntPtr]
$script:charts = New-Object System.Collections.Generic.List[object]

[ChartProbe]::EnumWindows({
    param($h, $lp)
    [uint32]$id = 0
    [void][ChartProbe]::GetWindowThreadProcessId($h, [ref]$id)
    if ([int]$id -eq $targetPid) { [void]$script:roots.Add($h) }
    return $true
}, [IntPtr]::Zero) | Out-Null

function Collect-Charts([IntPtr]$parent) {
    [ChartProbe]::EnumChildWindows($parent, {
        param($ch, $lp)
        $sb = New-Object System.Text.StringBuilder 256
        [void][ChartProbe]::GetClassName($ch, $sb, $sb.Capacity)
        if ($sb.ToString() -eq "CvChartWindow" -and [ChartProbe]::IsWindowVisible($ch)) {
            $r = New-Object ChartProbe+RECT
            [void][ChartProbe]::GetWindowRect($ch, [ref]$r)
            if ($r.Width -gt 0 -and $r.Height -gt 0) {
                [void]$script:charts.Add([PSCustomObject]@{
                    Hwnd = [int64]$ch; W = $r.Width; H = $r.Height; Left = $r.Left; Top = $r.Top
                })
            }
        }
        Collect-Charts $ch
        return $true
    }, [IntPtr]::Zero) | Out-Null
}

foreach ($r in $script:roots) { Collect-Charts $r }
$charts = @($script:charts | Sort-Object { $_.W * $_.H } -Descending | Select-Object -First 5)

$md = New-Object System.Collections.Generic.List[string]
function L([string]$s) { [void]$md.Add($s) }

L "## CvChartWindow follow-up"
L ""
L "- Time: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
L "- Taskmgr PID: $targetPid"
L "- Visible CvChartWindow count (all): $($script:charts.Count)"
L ""
L "| # | HWND | Size | Position |"
L "|---|---|---|---|"
for ($i = 0; $i -lt $charts.Count; $i++) {
    $c = $charts[$i]
    L ("| {0} | `{1}` | {2}x{3} | ({4},{5}) |" -f ($i+1), $c.Hwnd, $c.W, $c.H, $c.Left, $c.Top)
}

function Analyze-Bitmap([System.Drawing.Bitmap]$bmp, [string]$label) {
    $acc = @{}
    $stepX = [Math]::Max(1, [int]($bmp.Width / 80))
    $stepY = [Math]::Max(1, [int]($bmp.Height / 80))
    for ($x = 0; $x -lt $bmp.Width; $x += $stepX) {
        for ($y = 0; $y -lt $bmp.Height; $y += $stepY) {
            $p = $bmp.GetPixel($x, $y)
            if ($p.R -eq $p.G -and $p.R -eq $p.B) { continue }
            $c = [System.Drawing.Color]::FromArgb($p.R, $p.G, $p.B)
            if ($c.GetSaturation() -gt 0.35) {
                $k = $c.ToArgb()
                if (-not $acc.ContainsKey($k)) { $acc[$k] = 0 }
                $acc[$k]++
            }
        }
    }
    $top = $null; $tc = 0
    foreach ($k in $acc.Keys) {
        if ($acc[$k] -gt $tc) { $tc = $acc[$k]; $top = [System.Drawing.Color]::FromArgb([int]$k) }
    }
    if (-not $top) {
        L "- ${label}: no saturated color found"
        return
    }
    L ("- ${label}: dominant RGB({0},{1},{2}) sat={3:N2} samples={4}" -f $top.R, $top.G, $top.B, $top.GetSaturation(), $tc)

    $cols = 40
    $hits = 0
    $vals = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $cols; $i++) {
        $px = [int](($i / ($cols - 1.0)) * ($bmp.Width - 1))
        $found = $false
        for ($y = 0; $y -lt $bmp.Height; $y++) {
            $p = $bmp.GetPixel($px, $y)
            $d = [Math]::Abs([int]$p.R - [int]$top.R) + [Math]::Abs([int]$p.G - [int]$top.G) + [Math]::Abs([int]$p.B - [int]$top.B)
            if ($d -lt 90) {
                [void]$vals.Add(([math]::Round(1.0 - $y / [double]$bmp.Height, 2)).ToString())
                $hits++
                $found = $true
                break
            }
        }
        if (-not $found) { [void]$vals.Add("-") }
    }
    L "- line-scan (top-down) hits=$hits/$cols"
    L "- height sample: ``$($vals -join ', ')``"

    $bh = 0
    for ($i = 0; $i -lt 20; $i++) {
        $px = [int](($i / 19.0) * ($bmp.Width - 1))
        $started = $false
        for ($y = $bmp.Height - 1; $y -ge 0; $y--) {
            $p = $bmp.GetPixel($px, $y)
            $d = [Math]::Abs([int]$p.R - [int]$top.R) + [Math]::Abs([int]$p.G - [int]$top.G) + [Math]::Abs([int]$p.B - [int]$top.B)
            if ($d -lt 90) { $started = $true }
            elseif ($started) { $bh++; break }
        }
    }
    L "- bottom-up fill hits=$bh/20 (reference lunar-lander style)"
}

for ($i = 0; $i -lt [Math]::Min(2, $charts.Count); $i++) {
    $c = $charts[$i]
    $hwnd = [IntPtr]$c.Hwnd
    L ""
    L "### Chart #$($i+1) HWND=$($c.Hwnd) $($c.W)x$($c.H)"
    L ""
    L "| Method | OK | Non-black | File |"
    L "|---|---|---|---|"

    foreach ($method in @("BitBlt", "PrintWindow", "Screen")) {
        try {
            $path = Join-Path $outDir ("capture-cvchart-{0}-{1}.png" -f ($i+1), $method.ToLower())
            $r = New-Object ChartProbe+RECT
            [void][ChartProbe]::GetWindowRect($hwnd, [ref]$r)
            if ($method -eq "Screen") {
                $bmp = New-Object System.Drawing.Bitmap $r.Width, $r.Height
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
                $g.Dispose()
            } else {
                $hdc = [ChartProbe]::GetDC($hwnd)
                $mem = [ChartProbe]::CreateCompatibleDC($hdc)
                $hb = [ChartProbe]::CreateCompatibleBitmap($hdc, $r.Width, $r.Height)
                [void][ChartProbe]::SelectObject($mem, $hb)
                $okDraw = if ($method -eq "BitBlt") {
                    [ChartProbe]::BitBlt($mem, 0, 0, $r.Width, $r.Height, $hdc, 0, 0, 0x00CC0020)
                } else {
                    [ChartProbe]::PrintWindow($hwnd, $mem, 2)
                }
                $bmp = [System.Drawing.Image]::FromHbitmap($hb)
                [void][ChartProbe]::DeleteObject($hb)
                [void][ChartProbe]::DeleteDC($mem)
                [void][ChartProbe]::ReleaseDC($hwnd, $hdc)
                if (-not $okDraw) {
                    $bmp.Dispose()
                    L "| $method | False |  | draw failed |"
                    continue
                }
            }

            $non = 0; $samp = 0
            $sx = [Math]::Max(1, [int]($bmp.Width / 30))
            $sy = [Math]::Max(1, [int]($bmp.Height / 30))
            for ($x = 0; $x -lt $bmp.Width; $x += $sx) {
                for ($y = 0; $y -lt $bmp.Height; $y += $sy) {
                    $samp++
                    $p = $bmp.GetPixel($x, $y)
                    if ($p.R -gt 8 -or $p.G -gt 8 -or $p.B -gt 8) { $non++ }
                }
            }
            $ok = $non -gt ($samp * 0.05)
            $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
            L ("| {0} | {1} | {2}/{3} | `{4}` |" -f $method, $ok, $non, $samp, (Split-Path $path -Leaf))
            if ($ok -and $i -eq 0 -and $method -eq "Screen") {
                Analyze-Bitmap $bmp "Screen capture analysis"
            } elseif ($ok -and $i -eq 0 -and $method -eq "BitBlt") {
                Analyze-Bitmap $bmp "BitBlt analysis"
            } elseif ($ok -and $i -eq 0 -and $method -eq "PrintWindow") {
                Analyze-Bitmap $bmp "PrintWindow analysis"
            }
            $bmp.Dispose()
        } catch {
            L "| $method | False |  | $($_.Exception.Message) |"
        }
    }
}

L ""
L "### Conclusions"
L ""
L "1. Win11 Taskmgr exposes real chart HWNDs named ``CvChartWindow`` (not ChartView)."
L "2. Largest ``CvChartWindow`` is the primary performance graph candidate."
L "3. Capture method success is recorded in tables above."

$path = Join-Path $outDir "probe-cvchart.md"
$md | Set-Content -Path $path -Encoding UTF8
Write-Host "Wrote $path"
