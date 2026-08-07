$ErrorActionPreference = 'Continue'
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class W3 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lp, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc lp, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
"@

$script:main = [IntPtr]::Zero
[W3]::EnumWindows({
  param($h,$l)
  if (-not [W3]::IsWindowVisible($h)) { return $true }
  $sb = New-Object System.Text.StringBuilder 256
  [void][W3]::GetClassName($h,$sb,256)
  if ($sb.ToString() -eq 'TaskManagerWindow') { $script:main=$h; return $false }
  return $true
}, [IntPtr]::Zero)

$wins = New-Object System.Collections.Generic.List[object]
function Walk([IntPtr]$p, [int]$depth) {
  [W3]::EnumChildWindows($p, {
    param($h,$l)
    $sb = New-Object System.Text.StringBuilder 256
    [void][W3]::GetClassName($h,$sb,256)
    $tb = New-Object System.Text.StringBuilder 256
    [void][W3]::GetWindowText($h,$tb,256)
    $r = New-Object W3+RECT
    [void][W3]::GetWindowRect($h,[ref]$r)
    $cn = $sb.ToString()
    if ($cn -match 'NativeHWNDHost|DesktopWindowXamlSource|DirectUI|CvChart|Xaml') {
      $wins.Add([pscustomobject]@{Depth=$depth; Hwnd=[int64]$h; Class=$cn; Title=$tb.ToString(); W=($r.R-$r.L); H=($r.B-$r.T); Vis=[W3]::IsWindowVisible($h)})
    }
    if ($depth -lt 8) { Walk $h ($depth+1) }
    return $true
  }, [IntPtr]::Zero) | Out-Null
}
Walk $script:main 0
$wins | Format-Table -AutoSize | Out-String | Write-Output

Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
foreach ($w in $wins) {
  if ($w.Class -notmatch 'NativeHWNDHost|DesktopWindowXamlSource|DirectUI') { continue }
  try {
    $el = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$w.Hwnd)
    $all = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $named = 0
    $hits = New-Object System.Collections.Generic.List[string]
    for ($i=0; $i -lt $all.Count; $i++) {
      $n = $all.Item($i).Current.Name
      if ([string]::IsNullOrWhiteSpace($n)) { continue }
      $named++
      if ($n -match 'CPU|Memory|GPU|Disk|Wi-Fi|WLAN|Ethernet|Util') {
        [void]$hits.Add($n)
      }
    }
    Write-Output ("UIA from {0} hwnd={1} descendants={2} named={3} hits={4}" -f $w.Class,$w.Hwnd,$all.Count,$named,$hits.Count)
    $hits | Select-Object -Unique | Select-Object -First 20 | ForEach-Object { Write-Output ("  HIT: " + $_) }
  } catch {
    Write-Output ("UIA fail {0}: {1}" -f $w.Class, $_.Exception.Message)
  }
}
