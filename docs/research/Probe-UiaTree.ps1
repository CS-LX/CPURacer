$ErrorActionPreference = 'Continue'
$me = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$prin = New-Object System.Security.Principal.WindowsPrincipal($me)
Write-Output ('probe elevated=' + $prin.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator))

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class W2 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lp, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
"@

$script:main = [IntPtr]::Zero
[W2]::EnumWindows({
  param($h, $l)
  if (-not [W2]::IsWindowVisible($h)) { return $true }
  $sb = New-Object System.Text.StringBuilder 256
  [void][W2]::GetClassName($h, $sb, 256)
  if ($sb.ToString() -eq 'TaskManagerWindow') { $script:main = $h; return $false }
  return $true
}, [IntPtr]::Zero)

Write-Output ('fg=' + [int64][W2]::GetForegroundWindow() + ' main=' + [int64]$script:main)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::FromHandle($script:main)
Write-Output ('root name=' + $root.Current.Name + ' ctl=' + $root.Current.ControlType.ProgrammaticName)
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
Write-Output ('descendant count=' + $all.Count)
$named = 0
$sample = New-Object System.Collections.Generic.List[string]
for ($i = 0; $i -lt $all.Count; $i++) {
  $n = $all.Item($i).Current.Name
  if (-not [string]::IsNullOrWhiteSpace($n)) {
    $named++
    if ($sample.Count -lt 40) { [void]$sample.Add($n) }
  }
}
Write-Output ('named count=' + $named)
$sample | ForEach-Object { Write-Output ('  N: ' + $_) }
