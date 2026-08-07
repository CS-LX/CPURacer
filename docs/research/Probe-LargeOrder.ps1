$ErrorActionPreference = 'Continue'
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class W7 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lp, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc lp, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
"@
$script:main=[IntPtr]::Zero
[W7]::EnumWindows({ param($h,$l) if(-not [W7]::IsWindowVisible($h)){return $true}; $sb=New-Object Text.StringBuilder 256; [void][W7]::GetClassName($h,$sb,256); if($sb.ToString() -eq 'TaskManagerWindow'){$script:main=$h; return $false}; return $true }, [IntPtr]::Zero)
$i=0
[W7]::EnumChildWindows($script:main, {
  param($h,$l)
  $sb=New-Object Text.StringBuilder 256
  [void][W7]::GetClassName($h,$sb,256)
  if ($sb.ToString() -ne 'CvChartWindow') { return $true }
  $r=New-Object W7+RECT; [void][W7]::GetWindowRect($h,[ref]$r)
  $w=$r.R-$r.L; $hh=$r.B-$r.T
  if ($w -lt 200 -or $hh -lt 150) { return $true }
  Write-Output ("order={0} vis={1} hwnd={2} {3}x{4}" -f $script:i, ([W7]::IsWindowVisible($h)), ([int64]$h), $w, $hh)
  $script:i++
  return $true
}, [IntPtr]::Zero) | Out-Null
