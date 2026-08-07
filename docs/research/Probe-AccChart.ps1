$ErrorActionPreference = 'Continue'
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class W6 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lp, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc lp, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("oleacc.dll", PreserveSig=false)]
  public static extern void AccessibleObjectFromWindow(IntPtr hwnd, uint dwId, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
  public const uint OBJID_CLIENT = 0xFFFFFFFC;
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
"@

$script:main = [IntPtr]::Zero
[W6]::EnumWindows({
  param($h,$l)
  if (-not [W6]::IsWindowVisible($h)) { return $true }
  $sb = New-Object System.Text.StringBuilder 256
  [void][W6]::GetClassName($h,$sb,256)
  if ($sb.ToString() -eq 'TaskManagerWindow') { $script:main=$h; return $false }
  return $true
}, [IntPtr]::Zero)

$iid = [Guid]'618736E0-3C3D-11CF-810C-00AA00389B71' # IAccessible
[W6]::EnumChildWindows($script:main, {
  param($h,$l)
  $sb = New-Object System.Text.StringBuilder 256
  [void][W6]::GetClassName($h,$sb,256)
  if ($sb.ToString() -ne 'CvChartWindow') { return $true }
  $r = New-Object W6+RECT
  [void][W6]::GetWindowRect($h,[ref]$r)
  $w=$r.R-$r.L; $hh=$r.B-$r.T
  if (-not (($w -ge 200 -and $hh -ge 150) -or ($w -ge 40 -and $w -lt 150))) { return $true }
  $name='?'; $val='?'; $desc='?'
  try {
    $obj = $null
    $g = $iid
    [W6]::AccessibleObjectFromWindow($h, [W6]::OBJID_CLIENT, [ref]$g, [ref]$obj)
    $acc = $obj
    $name = $acc.get_accName(0)
    $val = $acc.get_accValue(0)
    $desc = $acc.get_accDescription(0)
  } catch {
    $name = 'ERR:' + $_.Exception.Message
  }
  Write-Output ("vis={0} {1}x{2} hwnd={3} name='{4}' val='{5}' desc='{6}'" -f ([W6]::IsWindowVisible($h)),$w,$hh,([int64]$h),$name,$val,$desc)
  return $true
}, [IntPtr]::Zero) | Out-Null
