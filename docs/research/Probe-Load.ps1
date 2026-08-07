$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class FG {
  public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
  [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
  [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);
  [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
  [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int x1, int y1, int rop);
  [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
  [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWndParent, EnumProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint id);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [StructLayout(LayoutKind.Sequential)]
  public struct RECT {
    public int Left, Top, Right, Bottom;
    public int Width { get { return Right - Left; } }
    public int Height { get { return Bottom - Top; } }
  }
}
"@

$out = Split-Path -Parent $MyInvocation.MyCommand.Path
$p = Get-Process Taskmgr | Select-Object -First 1
$script:main = [IntPtr]::Zero

[FG]::EnumWindows({
    param($h, $lp)
    [uint32]$id = 0
    [void][FG]::GetWindowThreadProcessId($h, [ref]$id)
    if ([int]$id -ne $p.Id) { return $true }
    $sb = New-Object System.Text.StringBuilder 256
    [void][FG]::GetClassName($h, $sb, $sb.Capacity)
    if ($sb.ToString() -eq "TaskManagerWindow" -and [FG]::IsWindowVisible($h)) {
        $script:main = $h
    }
    return $true
}, [IntPtr]::Zero) | Out-Null

[void][FG]::ShowWindow($script:main, 9)
[void][FG]::SetForegroundWindow($script:main)
Start-Sleep -Milliseconds 800

# CPU burn ~4s
$jobs = 1..([Math]::Max(2, [int]([Environment]::ProcessorCount / 2))) | ForEach-Object {
    Start-Job -ScriptBlock { $end = [datetime]::UtcNow.AddSeconds(5); while ([datetime]::UtcNow -lt $end) { [math]::Sqrt((Get-Random)) | Out-Null } }
}
Start-Sleep -Seconds 4

$script:best = [IntPtr]::Zero
$script:bestA = 0
$script:bestR = New-Object FG+RECT

function Walk-Chart([IntPtr]$parent) {
    [FG]::EnumChildWindows($parent, {
        param($ch, $lp)
        $sb = New-Object System.Text.StringBuilder 256
        [void][FG]::GetClassName($ch, $sb, $sb.Capacity)
        if ($sb.ToString() -eq "CvChartWindow" -and [FG]::IsWindowVisible($ch)) {
            $r = New-Object FG+RECT
            [void][FG]::GetWindowRect($ch, [ref]$r)
            $a = $r.Width * $r.Height
            if ($a -gt $script:bestA) {
                $script:bestA = $a
                $script:best = $ch
                $script:bestR = $r
            }
        }
        Walk-Chart $ch
        return $true
    }, [IntPtr]::Zero) | Out-Null
}
Walk-Chart $script:main

$md = @()
$md += "## Load + foreground recapture"
$md += ""
$md += "- Time: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$md += "- Main HWND: $([int64]$script:main)"
$md += "- Largest CvChartWindow: $([int64]$script:best) $($script:bestR.Width)x$($script:bestR.Height) @($($script:bestR.Left),$($script:bestR.Top))"

# BitBlt
$hdc = [FG]::GetDC($script:best)
$mem = [FG]::CreateCompatibleDC($hdc)
$hb = [FG]::CreateCompatibleBitmap($hdc, $script:bestR.Width, $script:bestR.Height)
[void][FG]::SelectObject($mem, $hb)
[void][FG]::BitBlt($mem, 0, 0, $script:bestR.Width, $script:bestR.Height, $hdc, 0, 0, 0x00CC0020)
$bmp = [System.Drawing.Image]::FromHbitmap($hb)
$path1 = Join-Path $out "capture-cvchart-load-bitblt.png"
$bmp.Save($path1, [System.Drawing.Imaging.ImageFormat]::Png)

function Get-TopColor($bmp) {
    $acc = @{}
    $step = [Math]::Max(1, [int]($bmp.Width / 60))
    for ($x = 0; $x -lt $bmp.Width; $x += $step) {
        for ($y = 0; $y -lt $bmp.Height; $y += $step) {
            $px = $bmp.GetPixel($x, $y)
            if ($px.R -eq $px.G -and $px.R -eq $px.B) { continue }
            $c = [System.Drawing.Color]::FromArgb($px.R, $px.G, $px.B)
            if ($c.GetSaturation() -gt 0.25) {
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
    return @{ Top = $top; Count = $tc; Keys = $acc.Count }
}

$c1 = Get-TopColor $bmp
$md += "- BitBlt file: ``capture-cvchart-load-bitblt.png``"
if ($c1.Top) {
    $md += ("- BitBlt dominant: RGB({0},{1},{2}) sat={3:N2} n={4} keys={5}" -f $c1.Top.R, $c1.Top.G, $c1.Top.B, $c1.Top.GetSaturation(), $c1.Count, $c1.Keys)
} else {
    $md += "- BitBlt dominant: none (grid only / line not in GDI DC)"
}
$bmp.Dispose()
[void][FG]::DeleteObject($hb)
[void][FG]::DeleteDC($mem)
[void][FG]::ReleaseDC($script:best, $hdc)

# Ensure foreground again then screen capture
[void][FG]::SetForegroundWindow($script:main)
Start-Sleep -Milliseconds 400
# refresh rect
[void][FG]::GetWindowRect($script:best, [ref]$script:bestR)
$bmp2 = New-Object System.Drawing.Bitmap $script:bestR.Width, $script:bestR.Height
$g = [System.Drawing.Graphics]::FromImage($bmp2)
$g.CopyFromScreen($script:bestR.Left, $script:bestR.Top, 0, 0, $bmp2.Size)
$g.Dispose()
$path2 = Join-Path $out "capture-cvchart-load-screen.png"
$bmp2.Save($path2, [System.Drawing.Imaging.ImageFormat]::Png)
$c2 = Get-TopColor $bmp2
$md += "- Screen file: ``capture-cvchart-load-screen.png``"
if ($c2.Top) {
    $md += ("- Screen dominant: RGB({0},{1},{2}) sat={3:N2} n={4} keys={5}" -f $c2.Top.R, $c2.Top.G, $c2.Top.B, $c2.Top.GetSaturation(), $c2.Count, $c2.Keys)
    $hits = 0; $cols = 48; $vals = @()
    for ($i = 0; $i -lt $cols; $i++) {
        $px = [int](($i / ($cols - 1.0)) * ($bmp2.Width - 1))
        $found = $false
        for ($y = 0; $y -lt $bmp2.Height; $y++) {
            $pxc = $bmp2.GetPixel($px, $y)
            $d = [Math]::Abs([int]$pxc.R - [int]$c2.Top.R) + [Math]::Abs([int]$pxc.G - [int]$c2.Top.G) + [Math]::Abs([int]$pxc.B - [int]$c2.Top.B)
            if ($d -lt 100) {
                $vals += [math]::Round(1.0 - $y / [double]$bmp2.Height, 2)
                $hits++
                $found = $true
                break
            }
        }
        if (-not $found) { $vals += "-" }
    }
    $md += "- Screen line-scan hits=$hits/$cols"
    $md += "- Sample: ``$($vals -join ', ')``"
} else {
    $md += "- Screen dominant: none"
}
$bmp2.Dispose()

# Main window screen
$mr = New-Object FG+RECT
[void][FG]::GetWindowRect($script:main, [ref]$mr)
$bmp3 = New-Object System.Drawing.Bitmap $mr.Width, $mr.Height
$g = [System.Drawing.Graphics]::FromImage($bmp3)
$g.CopyFromScreen($mr.Left, $mr.Top, 0, 0, $bmp3.Size)
$g.Dispose()
$bmp3.Save((Join-Path $out "capture-main-load-screen.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$bmp3.Dispose()
$md += "- Main screen: ``capture-main-load-screen.png`` $($mr.Width)x$($mr.Height)"

Get-Job | Wait-Job | Remove-Job | Out-Null
$path = Join-Path $out "probe-load.md"
$md | Set-Content $path -Encoding UTF8
Write-Host "Wrote $path"
$md | ForEach-Object { Write-Host $_ }
