# Quick probe: CvChartWindow count + UIA names for CPU page detection
$ErrorActionPreference = 'Stop'

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class ProbeWin {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lp, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc lp, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT {
    public int L,T,R,B;
    public int W { get { return R-L; } }
    public int H { get { return B-T; } }
  }
}
"@

$script:main = [IntPtr]::Zero
$script:charts = New-Object System.Collections.Generic.List[object]

[ProbeWin]::EnumWindows({
  param($h, $l)
  if (-not [ProbeWin]::IsWindowVisible($h)) { return $true }
  $sb = New-Object System.Text.StringBuilder 256
  [void][ProbeWin]::GetClassName($h, $sb, 256)
  if ($sb.ToString() -eq 'TaskManagerWindow') {
    $script:main = $h
    return $false
  }
  return $true
}, [IntPtr]::Zero)

if ($script:main -eq [IntPtr]::Zero) {
  Write-Output 'NO TaskManagerWindow'
  exit 1
}

function Walk([IntPtr]$p) {
  [ProbeWin]::EnumChildWindows($p, {
    param($h, $l)
    $sb = New-Object System.Text.StringBuilder 256
    [void][ProbeWin]::GetClassName($h, $sb, 256)
    if ($sb.ToString() -eq 'CvChartWindow' -and [ProbeWin]::IsWindowVisible($h)) {
      $r = New-Object ProbeWin+RECT
      [void][ProbeWin]::GetWindowRect($h, [ref]$r)
      $script:charts.Add([pscustomobject]@{ Hwnd = [int64]$h; W = $r.W; H = $r.H; Area = ($r.W * $r.H) })
    }
    Walk $h
    return $true
  }, [IntPtr]::Zero) | Out-Null
}

Walk $script:main
Write-Output "MainHwnd=$([int64]$script:main)"
Write-Output "ChartCount=$($script:charts.Count)"
$script:charts | Sort-Object Area -Descending | Select-Object -First 10 | Format-Table -AutoSize | Out-String | Write-Output

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::FromHandle($script:main)
$cond = New-Object System.Windows.Automation.PropertyCondition(
  [System.Windows.Automation.AutomationElement]::NameProperty, 'CPU')
$exact = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
Write-Output "ExactName='CPU' count=$($exact.Count)"
for ($i = 0; $i -lt [Math]::Min(8, $exact.Count); $i++) {
  $el = $exact.Item($i)
  Write-Output ("  exact[{0}] name='{1}' ctl={2}" -f $i, $el.Current.Name, $el.Current.ControlType.ProgrammaticName)
}

$selId = [System.Windows.Automation.SelectionItemPattern]::Pattern
$walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
function DumpSelected([System.Windows.Automation.AutomationElement]$el, [int]$depth) {
  if ($depth -gt 40 -or $null -eq $el) { return }
  try {
    $p = $el.GetCurrentPattern($selId)
    if ($null -ne $p -and $p.Current.IsSelected) {
      Write-Output ("SELECTED name='{0}' ctl={1}" -f $el.Current.Name, $el.Current.ControlType.ProgrammaticName)
    }
  } catch {}
  $c = $walker.GetFirstChild($el)
  while ($null -ne $c) {
    DumpSelected $c ($depth + 1)
    $c = $walker.GetNextSibling($c)
  }
}
Write-Output '--- Selected SelectionItems ---'
DumpSelected $root 0

$trueCond = [System.Windows.Automation.Condition]::TrueCondition
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $trueCond)
$uniq = New-Object 'System.Collections.Generic.HashSet[string]'
for ($i = 0; $i -lt $all.Count; $i++) {
  $n = $all.Item($i).Current.Name
  if ([string]::IsNullOrWhiteSpace($n)) { continue }
  if ($n -match 'CPU|Cpu|内存|Memory|磁盘|Disk|GPU|利用率|Performance|性能') {
    [void]$uniq.Add($n)
  }
}
Write-Output '--- Name hits ---'
$uniq | Sort-Object | ForEach-Object { Write-Output "  $_" }
