$ErrorActionPreference = 'Continue'
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class W5 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lp, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc lp, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr hdc);
  [DllImport("gdi32.dll")] public static extern uint GetPixel(IntPtr hdc, int x, int y);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
"@

$script:main = [IntPtr]::Zero
[W5]::EnumWindows({
  param($h,$l)
  if (-not [W5]::IsWindowVisible($h)) { return $true }
  $sb = New-Object System.Text.StringBuilder 256
  [void][W5]::GetClassName($h,$sb,256)
  if ($sb.ToString() -eq 'TaskManagerWindow') { $script:main=$h; return $false }
  return $true
}, [IntPtr]::Zero)

$charts = @{}
[W5]::EnumChildWindows($script:main, {
  param($h,$l)
  $sb = New-Object System.Text.StringBuilder 256
  [void][W5]::GetClassName($h,$sb,256)
  if ($sb.ToString() -eq 'CvChartWindow' -and [W5]::IsWindowVisible($h)) {
    $r = New-Object W5+RECT
    [void][W5]::GetWindowRect($h,[ref]$r)
    $w = $r.R-$r.L; $hh = $r.B-$r.T
    if ($w -ge 40 -and $w -lt 150 -and $hh -ge 30) {
      $charts[[int64]$h] = [pscustomobject]@{Hwnd=[int64]$h; L=$r.L; T=$r.T; W=$w; H=$hh}
    }
  }
  return $true
}, [IntPtr]::Zero) | Out-Null

$hdc = [W5]::GetDC([IntPtr]::Zero)
$i = 0
foreach ($c in ($charts.Values | Sort-Object T)) {
  $pts = @(
    @{Name='left'; X=($c.L-6); Y=($c.T+[int]($c.H/2))},
    @{Name='right'; X=($c.L+$c.W+24); Y=($c.T+[int]($c.H/2))},
    @{Name='midrow'; X=($c.L+$c.W+80); Y=($c.T+[int]($c.H/2))},
    @{Name='far'; X=($c.L+$c.W+160); Y=($c.T+[int]($c.H/2))}
  )
  $line = "idx=$i hwnd=$($c.Hwnd) y=$($c.T)"
  foreach ($p in $pts) {
    $px = [W5]::GetPixel($hdc, $p.X, $p.Y)
    $r = $px -band 0xFF; $g = ($px -shr 8) -band 0xFF; $b = ($px -shr 16) -band 0xFF
    $line += (" | {0}=({1},{2},{3})@{4},{5}" -f $p.Name,$r,$g,$b,$p.X,$p.Y)
  }
  Write-Output $line
  $i++
}
[void][W5]::ReleaseDC([IntPtr]::Zero, $hdc)
Write-Output 'NOTE: leave Taskmgr on CPU for this capture; switch to Memory and re-run to compare'
