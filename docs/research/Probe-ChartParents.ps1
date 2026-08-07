$ErrorActionPreference = 'Continue'
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class W4 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lp, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc lp, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
"@

$script:main = [IntPtr]::Zero
[W4]::EnumWindows({
  param($h,$l)
  if (-not [W4]::IsWindowVisible($h)) { return $true }
  $sb = New-Object System.Text.StringBuilder 256
  [void][W4]::GetClassName($h,$sb,256)
  if ($sb.ToString() -eq 'TaskManagerWindow') { $script:main=$h; return $false }
  return $true
}, [IntPtr]::Zero)

$charts = New-Object 'System.Collections.Generic.Dictionary[int64,object]'
function Consider([IntPtr]$h) {
  $sb = New-Object System.Text.StringBuilder 256
  [void][W4]::GetClassName($h,$sb,256)
  if ($sb.ToString() -ne 'CvChartWindow') { return }
  $id = [int64]$h
  if ($charts.ContainsKey($id)) { return }
  $r = New-Object W4+RECT
  [void][W4]::GetWindowRect($h,[ref]$r)
  $p = [W4]::GetParent($h)
  $charts[$id] = [pscustomobject]@{
    Hwnd=$id; Vis=[W4]::IsWindowVisible($h); L=$r.L; T=$r.T; W=($r.R-$r.L); H=($r.B-$r.T); Parent=[int64]$p
  }
}
function Walk([IntPtr]$p) {
  [W4]::EnumChildWindows($p, {
    param($h,$l)
    Consider $h
    Walk $h
    return $true
  }, [IntPtr]::Zero) | Out-Null
}
# EnumChildWindows already walks descendants - still recurse carefully once from main only using single enum
[W4]::EnumChildWindows($script:main, {
  param($h,$l)
  Consider $h
  return $true
}, [IntPtr]::Zero) | Out-Null

Write-Output 'All unique CvChartWindow:'
$charts.Values | Sort-Object T,L | Format-Table Hwnd,Vis,L,T,W,H,Parent -AutoSize | Out-String | Write-Output

$visibleLarge = @($charts.Values | Where-Object { $_.Vis -and $_.W -ge 200 -and $_.H -ge 150 } | Sort-Object T)
$visibleSmall = @($charts.Values | Where-Object { $_.Vis -and $_.W -lt 150 -and $_.W -ge 40 } | Sort-Object T)
Write-Output ('visibleLarge=' + $visibleLarge.Count + ' visibleSmall=' + $visibleSmall.Count)
Write-Output 'Small (sidebar) top-to-bottom:'
$visibleSmall | ForEach-Object { Write-Output ("  {0} y={1} parent={2}" -f $_.Hwnd,$_.T,$_.Parent) }
Write-Output 'Large visible:'
$visibleLarge | ForEach-Object { Write-Output ("  {0} y={1} parent={2}" -f $_.Hwnd,$_.T,$_.Parent) }
Write-Output 'Large hidden:'
$charts.Values | Where-Object { -not $_.Vis -and $_.W -ge 200 -and $_.H -ge 150 } | Sort-Object T | ForEach-Object {
  Write-Output ("  {0} y={1} {2}x{3} parent={4}" -f $_.Hwnd,$_.T,$_.W,$_.H,$_.Parent)
}
