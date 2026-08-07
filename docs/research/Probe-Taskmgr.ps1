# Temporary probe for CPURacer research report. Not a product deliverable.
$ErrorActionPreference = "Stop"
$outDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$reportPath = Join-Path $outDir "probe-results.md"
$lines = New-Object System.Collections.Generic.List[string]

function Add-Line([string]$s) { [void]$lines.Add($s) }

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class NativeProbe {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWndParent, EnumProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int x1, int y1, int rop);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; public int Width { get { return Right-Left; } } public int Height { get { return Bottom-Top; } } }

    public static string ClassName(IntPtr hwnd) {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
    public static string Title(IntPtr hwnd) {
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
"@

Add-Line "# Taskmgr Probe Results"
Add-Line ""
Add-Line "- Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$os = Get-CimInstance Win32_OperatingSystem
Add-Line "- OS: $($os.Caption) Build $($os.BuildNumber)"
Add-Line "- Culture: $([System.Globalization.CultureInfo]::CurrentUICulture.Name)"
Add-Line ""

# --- Process / top-level windows ---
$procs = @(Get-Process -Name Taskmgr -ErrorAction SilentlyContinue)
Add-Line "## 1. Process / top-level windows"
Add-Line ""
Add-Line "- Taskmgr process count: $($procs.Count)"
if ($procs.Count -eq 0) {
    Add-Line "- Starting Taskmgr..."
    Start-Process taskmgr
    Start-Sleep -Seconds 2
    $procs = @(Get-Process -Name Taskmgr -ErrorAction SilentlyContinue)
    Add-Line "- After start: $($procs.Count)"
}
$pids = @($procs | ForEach-Object { $_.Id })
Add-Line "- PIDs: $($pids -join ', ')"
Add-Line ""
Add-Line "| HWND | Visible | ClassName | Title | Rect WxH | DPI |"
Add-Line "|---|---|---|---|---|---|"

$topWindows = New-Object System.Collections.Generic.List[object]
[NativeProbe]::EnumWindows({
    param($hwnd, $lp)
    [uint32]$procId = 0
    [void][NativeProbe]::GetWindowThreadProcessId($hwnd, [ref]$procId)
    if ($pids -contains [int]$procId) {
        $cls = [NativeProbe]::ClassName($hwnd)
        $title = [NativeProbe]::Title($hwnd)
        $vis = [NativeProbe]::IsWindowVisible($hwnd)
        $r = New-Object NativeProbe+RECT
        [void][NativeProbe]::GetWindowRect($hwnd, [ref]$r)
        $dpi = 0
        try { $dpi = [NativeProbe]::GetDpiForWindow($hwnd) } catch { $dpi = 0 }
        $obj = [PSCustomObject]@{ Hwnd=[int64]$hwnd; Visible=$vis; Class=$cls; Title=$title; W=$r.Width; H=$r.Height; Dpi=$dpi }
        [void]$script:topWindows.Add($obj)
    }
    return $true
}, [IntPtr]::Zero)

foreach ($w in ($topWindows | Sort-Object -Property Visible -Descending)) {
    $t = ($w.Title -replace '\|','/').Trim()
    if ([string]::IsNullOrWhiteSpace($t)) { $t = "(empty)" }
    Add-Line ("| `{0}` | {1} | `{2}` | {3} | {4}x{5} | {6} |" -f $w.Hwnd, $w.Visible, $w.Class, $t, $w.W, $w.H, $w.Dpi)
}

$main = $topWindows | Where-Object { $_.Visible -and $_.W -gt 100 -and $_.H -gt 100 } | Sort-Object -Property W -Descending | Select-Object -First 1
if (-not $main) { $main = $topWindows | Where-Object { $_.Visible } | Select-Object -First 1 }
Add-Line ""
if ($main) {
    Add-Line "- Selected main window HWND: ``$($main.Hwnd)`` Class=``$($main.Class)``"
} else {
    Add-Line "- ERROR: no visible Taskmgr top window found"
}

# --- Child HWND enum (depth-limited breadth) ---
Add-Line ""
Add-Line "## 2. Child HWND sample (visible, depth-first up to 80)"
Add-Line ""
$childRows = New-Object System.Collections.Generic.List[string]
if ($main) {
    $root = [IntPtr]$main.Hwnd
    $count = 0
    $queue = New-Object System.Collections.Generic.Queue[IntPtr]
    $queue.Enqueue($root)
    $seen = New-Object 'System.Collections.Generic.HashSet[int64]'
    while ($queue.Count -gt 0 -and $count -lt 80) {
        $cur = $queue.Dequeue()
        [NativeProbe]::EnumChildWindows($cur, {
            param($ch, $lp)
            $key = [int64]$ch
            if (-not $script:seen.Add($key)) { return $true }
            $cls = [NativeProbe]::ClassName($ch)
            $title = [NativeProbe]::Title($ch)
            $vis = [NativeProbe]::IsWindowVisible($ch)
            $rr = New-Object NativeProbe+RECT
            [void][NativeProbe]::GetWindowRect($ch, [ref]$rr)
            if ($vis -and $rr.Width -gt 0 -and $rr.Height -gt 0) {
                $script:count++
                $tt = if ([string]::IsNullOrWhiteSpace($title)) { "" } else { ($title.Substring(0, [Math]::Min(40, $title.Length)) -replace '\|','/') }
                [void]$script:childRows.Add(("| `{0}` | `{1}` | {2}x{3} | {4} |" -f $key, $cls, $rr.Width, $rr.Height, $tt))
            }
            $script:queue.Enqueue($ch)
            return $true
        }, [IntPtr]::Zero)
    }
}
Add-Line "| HWND | ClassName | WxH | Title |"
Add-Line "|---|---|---|---|"
if ($childRows.Count -eq 0) {
    Add-Line "| (none) |  |  |  |"
} else {
    foreach ($row in $childRows) { Add-Line $row }
}
Add-Line ""
Add-Line "- Visible child HWND count (capped): $($childRows.Count)"

# --- UIA dump ---
Add-Line ""
Add-Line "## 3. UI Automation tree (trimmed)"
Add-Line ""

function Get-UiaProps($el) {
    try {
        $c = $el.Current
        return [PSCustomObject]@{
            Name = $c.Name
            ClassName = $c.ClassName
            AutomationId = $c.AutomationId
            ControlType = $c.ControlType.ProgrammaticName
            FrameworkId = $c.FrameworkId
            NativeHwnd = [int64]$c.NativeWindowHandle
            Rect = "{0:N0}x{1:N0} @({2:N0},{3:N0})" -f $c.BoundingRectangle.Width, $c.BoundingRectangle.Height, $c.BoundingRectangle.X, $c.BoundingRectangle.Y
        }
    } catch {
        return $null
    }
}

$uiaHits = New-Object System.Collections.Generic.List[string]
$chartCandidates = New-Object System.Collections.Generic.List[object]

try {
    $rootEl = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        [int]$pids[0])
    $taskRoots = $rootEl.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    Add-Line "- UIA top-level elements for Taskmgr PID: $($taskRoots.Count)"
    Add-Line ""

    function Walk-Uia($el, $depth, $maxDepth, $maxNodes, [ref]$nodeCount) {
        if ($nodeCount.Value -ge $maxNodes) { return }
        if ($depth -gt $maxDepth) { return }
        $p = Get-UiaProps $el
        if ($null -eq $p) { return }
        $nodeCount.Value++
        $indent = ("  " * $depth)
        $name = if ($p.Name) { $p.Name.Substring(0, [Math]::Min(60, $p.Name.Length)) } else { "" }
        $aid = $p.AutomationId
        $line = ("{0}- [{1}] Name=`"{2}`" Class=`"{3}`" Id=`"{4}`" Fw=`"{5}`" HWND={6} Rect={7}" -f `
            $indent, ($p.ControlType -replace 'ControlType.',''), $name, $p.ClassName, $aid, $p.FrameworkId, $p.NativeHwnd, $p.Rect)
        [void]$script:uiaHits.Add($line)

        $interesting = $false
        $n = $p.Name
        if ($n -match 'CPU|性能|逻辑处理器|利用率|Performance') { $interesting = $true }
        if ($p.ControlType -match 'Image|Custom|Document|Pane|Group') {
            $w = 0; $h = 0
            if ($p.Rect -match '([\d\.]+)x([\d\.]+)') { $w = [double]$Matches[1]; $h = [double]$Matches[2] }
            if ($w -gt 200 -and $h -gt 120) { $interesting = $true }
        }
        if ($interesting -and $depth -ge 1) {
            [void]$script:chartCandidates.Add($p)
        }

        try {
            $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
            $child = $walker.GetFirstChild($el)
            while ($null -ne $child -and $nodeCount.Value -lt $maxNodes) {
                Walk-Uia $child ($depth + 1) $maxDepth $maxNodes $nodeCount
                $child = $walker.GetNextSibling($child)
            }
        } catch {}
    }

    $nc = 0
    foreach ($tr in $taskRoots) {
        Walk-Uia $tr 0 8 250 ([ref]$nc)
    }
    Add-Line '```'
    foreach ($l in $uiaHits) { Add-Line $l }
    Add-Line '```'
    Add-Line ""
    Add-Line "### Chart-area candidates (heuristic)"
    Add-Line ""
    Add-Line "| Name | Class | AutomationId | ControlType | Framework | HWND | Rect |"
    Add-Line "|---|---|---|---|---|---|---|"
    $uniq = $chartCandidates | Sort-Object -Property @{Expression={$_.Rect}} -Unique | Select-Object -First 25
    foreach ($c in $uniq) {
        $nm = if ($c.Name) { ($c.Name.Substring(0,[Math]::Min(40,$c.Name.Length)) -replace '\|','/') } else { "" }
        Add-Line ("| {0} | `{1}` | `{2}` | {3} | {4} | `{5}` | {6} |" -f $nm, $c.ClassName, $c.AutomationId, ($c.ControlType -replace 'ControlType.',''), $c.FrameworkId, $c.NativeHwnd, $c.Rect)
    }
} catch {
    Add-Line "- UIA dump FAILED: $($_.Exception.Message)"
}

# --- Capture experiments ---
Add-Line ""
Add-Line "## 4. Capture experiments"
Add-Line ""

function Save-Capture([IntPtr]$hwnd, [string]$tag) {
    $result = [PSCustomObject]@{ Tag=$tag; Ok=$false; Method=""; Path=""; W=0; H=0; NonBlack=0; Note="" }
    $r = New-Object NativeProbe+RECT
    if (-not [NativeProbe]::GetWindowRect($hwnd, [ref]$r) -or $r.Width -le 0 -or $r.Height -le 0) {
        $result.Note = "GetWindowRect failed"
        return $result
    }
    $w = $r.Width; $h = $r.Height
    $result.W = $w; $result.H = $h

    foreach ($method in @("BitBlt","PrintWindow")) {
        try {
            $hdc = [NativeProbe]::GetDC($hwnd)
            $mem = [NativeProbe]::CreateCompatibleDC($hdc)
            $bmp = [NativeProbe]::CreateCompatibleBitmap($hdc, $w, $h)
            [void][NativeProbe]::SelectObject($mem, $bmp)
            $ok = $false
            if ($method -eq "BitBlt") {
                $ok = [NativeProbe]::BitBlt($mem, 0, 0, $w, $h, $hdc, 0, 0, 0x00CC0020)
            } else {
                # PW_RENDERFULLCONTENT = 0x2
                $ok = [NativeProbe]::PrintWindow($hwnd, $mem, 2)
            }
            if ($ok) {
                $managed = [System.Drawing.Image]::FromHbitmap($bmp)
                $path = Join-Path $outDir ("capture-{0}-{1}.png" -f $tag, $method.ToLower())
                $managed.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
                # sample non-black pixels
                $nonBlack = 0
                $sample = 0
                for ($x = 0; $x -lt $managed.Width; $x += [Math]::Max(1, [int]($managed.Width/40))) {
                    for ($y = 0; $y -lt $managed.Height; $y += [Math]::Max(1, [int]($managed.Height/40))) {
                        $sample++
                        $px = $managed.GetPixel($x, $y)
                        if ($px.R -gt 8 -or $px.G -gt 8 -or $px.B -gt 8) { $nonBlack++ }
                    }
                }
                $managed.Dispose()
                $result.Ok = ($nonBlack -gt ($sample * 0.05))
                $result.Method = $method
                $result.Path = $path
                $result.NonBlack = $nonBlack
                $result.Note = "samples non-black=$nonBlack/$sample"
                [void][NativeProbe]::DeleteObject($bmp)
                [void][NativeProbe]::DeleteDC($mem)
                [void][NativeProbe]::ReleaseDC($hwnd, $hdc)
                if ($result.Ok) { return $result }
            }
            [void][NativeProbe]::DeleteObject($bmp)
            [void][NativeProbe]::DeleteDC($mem)
            [void][NativeProbe]::ReleaseDC($hwnd, $hdc)
            $result.Note = "$method returned empty/black"
        } catch {
            $result.Note = "$method exception: $($_.Exception.Message)"
        }
    }
    return $result
}

Add-Line "| Target | Method | OK | Size | Notes | File |"
Add-Line "|---|---|---|---|---|---|"
if ($main) {
    $capMain = Save-Capture ([IntPtr]$main.Hwnd) "main"
    Add-Line ("| main HWND | {0} | {1} | {2}x{3} | {4} | `{5}` |" -f $capMain.Method, $capMain.Ok, $capMain.W, $capMain.H, $capMain.Note, (Split-Path $capMain.Path -Leaf))
}

# Capture largest child if any
$largestChildHwnd = $null
$largestArea = 0
foreach ($row in $childRows) {
    if ($row -match '`(\d+)` \| `([^`]+)` \| (\d+)x(\d+)') {
        $area = [int]$Matches[3] * [int]$Matches[4]
        if ($area -gt $largestArea) {
            $largestArea = $area
            $largestChildHwnd = [int64]$Matches[1]
        }
    }
}
if ($largestChildHwnd) {
    $capChild = Save-Capture ([IntPtr]$largestChildHwnd) "child"
    Add-Line ("| largest child `{0}` | {1} | {2} | {3}x{4} | {5} | `{6}` |" -f $largestChildHwnd, $capChild.Method, $capChild.Ok, $capChild.W, $capChild.H, $capChild.Note, (Split-Path $capChild.Path -Leaf))
}

# Screen-region capture of main window rect (Desktop DC) as Graphics Capture fallback analogue
if ($main) {
    try {
        $r = New-Object NativeProbe+RECT
        [void][NativeProbe]::GetWindowRect([IntPtr]$main.Hwnd, [ref]$r)
        $screenBmp = New-Object System.Drawing.Bitmap $r.Width, $r.Height
        $g = [System.Drawing.Graphics]::FromImage($screenBmp)
        $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $screenBmp.Size)
        $g.Dispose()
        $path = Join-Path $outDir "capture-main-screenblit.png"
        $screenBmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $nonBlack = 0; $sample = 0
        for ($x = 0; $x -lt $screenBmp.Width; $x += [Math]::Max(1,[int]($screenBmp.Width/40))) {
            for ($y = 0; $y -lt $screenBmp.Height; $y += [Math]::Max(1,[int]($screenBmp.Height/40))) {
                $sample++; $px = $screenBmp.GetPixel($x,$y)
                if ($px.R -gt 8 -or $px.G -gt 8 -or $px.B -gt 8) { $nonBlack++ }
            }
        }
        $ok = $nonBlack -gt ($sample * 0.05)
        Add-Line ("| main screen CopyFromScreen | ScreenBlit | {0} | {1}x{2} | non-black={3}/{4} | `{5}` |" -f $ok, $screenBmp.Width, $screenBmp.Height, $nonBlack, $sample, (Split-Path $path -Leaf))
        $screenBmp.Dispose()
    } catch {
        Add-Line ("| main screen CopyFromScreen | ScreenBlit | False |  | {0} |  |" -f $_.Exception.Message)
    }
}

# --- Curve sampling on screen capture ---
Add-Line ""
Add-Line "## 5. Curve / height-field heuristic on screen capture"
Add-Line ""
$curvePath = Join-Path $outDir "capture-main-screenblit.png"
if (Test-Path $curvePath) {
    $bmp = [System.Drawing.Bitmap]::FromFile($curvePath)
    # Assume chart is roughly center-right content area; sample a band
    $x0 = [int]($bmp.Width * 0.28)
    $x1 = [int]($bmp.Width * 0.96)
    $y0 = [int]($bmp.Height * 0.18)
    $y1 = [int]($bmp.Height * 0.72)
    $accu = @{}
    for ($x = $x0; $x -lt [Math]::Min($x0+8, $x1); $x++) {
        for ($y = $y0; $y -lt $y1; $y++) {
            $px = $bmp.GetPixel($x, $y)
            if ($px.R -eq $px.G -and $px.R -eq $px.B) { continue }
            $col = [System.Drawing.Color]::FromArgb($px.R, $px.G, $px.B)
            if ($col.GetSaturation() -gt 0.45) {
                $key = $col.ToArgb()
                if (-not $accu.ContainsKey($key)) { $accu[$key] = 0 }
                $accu[$key]++
            }
        }
    }
    $topColor = $null
    $topCount = 0
    foreach ($k in $accu.Keys) {
        if ($accu[$k] -gt $topCount) { $topCount = $accu[$k]; $topColor = [System.Drawing.Color]::FromArgb($k) }
    }
    if ($topColor) {
        Add-Line ("- Dominant saturated color in chart band: RGB({0},{1},{2}) count={3} sat={4:N2}" -f $topColor.R, $topColor.G, $topColor.B, $topCount, $topColor.GetSaturation())
        $cols = 48
        $heights = New-Object float[] $cols
        $filled = 0
        for ($i = 0; $i -lt $cols; $i++) {
            $px = $x0 + [int](($i / [double]($cols-1)) * ($x1 - $x0 - 1))
            $found = $false
            # For line charts: scan top-to-bottom for near-color, take first hit as "line y"
            for ($y = $y0; $y -lt $y1; $y++) {
                $p = $bmp.GetPixel($px, $y)
                $dr = [Math]::Abs([int]$p.R - [int]$topColor.R)
                $dg = [Math]::Abs([int]$p.G - [int]$topColor.G)
                $db = [Math]::Abs([int]$p.B - [int]$topColor.B)
                if (($dr + $dg + $db) -lt 80) {
                    $heights[$i] = 1.0 - (($y - $y0) / [double]($y1 - $y0))
                    $found = $true
                    $filled++
                    break
                }
            }
            if (-not $found) { $heights[$i] = -1 }
        }
        $vals = ($heights | ForEach-Object { if ($_ -lt 0) { "-" } else { $_.ToString("0.00") } }) -join ", "
        Add-Line "- Bottom-fill assumption (reference lunar-lander): **likely invalid** for Taskmgr line chart; top-down line search used instead."
        Add-Line "- Sampled heightField ($cols cols, chart-band crop): ``$vals``"
        Add-Line "- Columns with hit: $filled / $cols"
        if ($filled -lt ($cols / 4)) {
            Add-Line "- Conclusion: **weak/unstable** line extraction on this frame (CPU may be flat or color mismatch)."
        } else {
            Add-Line "- Conclusion: **feasible with conditions** — line-edge scan works better than bottom-up fill."
        }
    } else {
        Add-Line "- No saturated dominant color found in heuristic chart band (theme/layout may differ; need CPU page focused)."
    }
    # Also try bottom-up fill for comparison
    if ($topColor) {
        $bottomHits = 0
        for ($i = 0; $i -lt 24; $i++) {
            $px = $x0 + [int](($i / 23.0) * ($x1 - $x0 - 1))
            $terrainStarted = $false
            for ($y = $y1 - 1; $y -ge $y0; $y--) {
                $p = $bmp.GetPixel($px, $y)
                $dr = [Math]::Abs([int]$p.R - [int]$topColor.R)
                $dg = [Math]::Abs([int]$p.G - [int]$topColor.G)
                $db = [Math]::Abs([int]$p.B - [int]$topColor.B)
                $match = (($dr + $dg + $db) -lt 80)
                if ($match) { $terrainStarted = $true }
                elseif ($terrainStarted) { $bottomHits++; break }
            }
        }
        Add-Line "- Bottom-up fill hits (24 cols): $bottomHits — reference algorithm transfer: $(if ($bottomHits -lt 6) { 'poor' } else { 'partial' })"
    }
    $bmp.Dispose()
} else {
    Add-Line "- Skipped: no screen capture file"
}

# --- PDH ---
Add-Line ""
Add-Line "## 6. PDH / PerformanceCounter vs wall clock"
Add-Line ""
try {
    $pc = New-Object System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total")
    [void]$pc.NextValue()
    $samples = @()
    for ($i = 0; $i -lt 5; $i++) {
        Start-Sleep -Milliseconds 1000
        $v = $pc.NextValue()
        $samples += [PSCustomObject]@{ t = Get-Date -Format 'HH:mm:ss'; v = [math]::Round($v, 2) }
    }
    Add-Line "| Time | % Processor Time (_Total) |"
    Add-Line "|---|---|"
    foreach ($s in $samples) { Add-Line "| $($s.t) | $($s.v) |" }
    Add-Line ""
    Add-Line "- First NextValue discarded (always ~0). Sampling interval 1s."
    Add-Line "- Qualitative: Taskmgr graph smoothing/period may differ; use PDH for calibration/fallback, not sole visual track."
    $pc.Dispose()
} catch {
    Add-Line "- PerformanceCounter failed: $($_.Exception.Message)"
}

# --- Elevation / integrity note ---
Add-Line ""
Add-Line "## 7. Integrity / elevation snapshot"
Add-Line ""
try {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($id)
    $elev = $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
    Add-Line "- Probe process elevated: $elev"
    $tm = Get-Process -Name Taskmgr | Select-Object -First 1
    Add-Line "- Taskmgr PID: $($tm.Id)"
    # Try open process with limited rights as weak UIPI signal
    Add-Line "- UIA dump above succeeded: $(if ($uiaHits.Count -gt 0) { 'yes' } else { 'no/empty' })"
    Add-Line "- Note: If Taskmgr is Run as administrator and game is not, UIPI may block some cross-process UI ops; treat as conditional risk."
} catch {
    Add-Line "- Integrity probe error: $($_.Exception.Message)"
}

Add-Line ""
Add-Line "## 8. Quick conclusions for report"
Add-Line ""
Add-Line "1. Process name ``Taskmgr.exe`` is a stable language-independent anchor."
Add-Line "2. Child HWND richness and UIA FrameworkId must be read from tables above (Win11 often XAML/WinUI sparse HWND)."
Add-Line "3. Prefer screen/Graphics Capture if window BitBlt/PrintWindow is black."
Add-Line "4. Height field must track a **scrolling polyline**, not bottom-filled terrain."
Add-Line "5. PDH available but not pixel-identical to Taskmgr curve."

$lines | Set-Content -Path $reportPath -Encoding UTF8
Write-Host "Wrote $reportPath"
Write-Host "Lines: $($lines.Count)"
