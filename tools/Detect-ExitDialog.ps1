# 确认框探测脚本：用于查清「点游戏右上角 × 之后弹出的那个确认窗口」到底是什么形态。
#
# 用法：
#   1. 双击运行本脚本（右键「使用 PowerShell 运行」，或在终端里 .\Detect-ExitDialog.ps1）
#   2. 把游戏窗口点出来，点右上角 ×，让确认框留在屏幕上不要点掉
#   3. 回到本窗口按回车，脚本会列出所有窗口及子控件
#   4. 把输出（同目录下的 exit-dialog-report.txt）发给开发者
#
# 只读操作：不点任何按钮、不改任何设置，看完可以直接关掉。

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class W {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc p, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@

function Get-Text([IntPtr]$h) {
    $n = [W]::GetWindowTextLength($h)
    if ($n -le 0) { return "" }
    $sb = New-Object System.Text.StringBuilder ($n + 2)
    [void][W]::GetWindowText($h, $sb, $sb.Capacity)
    return $sb.ToString()
}

function Get-Class([IntPtr]$h) {
    $sb = New-Object System.Text.StringBuilder 256
    [void][W]::GetClassName($h, $sb, 256)
    return $sb.ToString()
}

function Get-Children([IntPtr]$parent) {
    $list = New-Object System.Collections.ArrayList
    $cb = [W+EnumProc]{
        param($h, $l)
        $r = New-Object W+RECT
        [void][W]::GetWindowRect($h, [ref]$r)
        [void]$list.Add([pscustomobject]@{
            Handle = $h
            Class  = (Get-Class $h)
            Text   = (Get-Text $h)
            Visible = [W]::IsWindowVisible($h)
            Rect   = "$($r.L),$($r.T) $($r.R - $r.L)x$($r.B - $r.T)"
        })
        return $true
    }
    [void][W]::EnumChildWindows($parent, $cb, [IntPtr]::Zero)
    return $list
}

Write-Host ""
Write-Host "=== 确认框探测 ===" -ForegroundColor Cyan
Write-Host "请先让游戏的退出确认框显示在屏幕上，然后回到这个窗口按回车。" -ForegroundColor Yellow
Write-Host ""
[void](Read-Host "确认框已经显示出来后，按回车继续")

$windows = New-Object System.Collections.ArrayList
$cbTop = [W+EnumProc]{
    param($h, $l)
    if ([W]::IsWindowVisible($h)) {
        $r = New-Object W+RECT
        [void][W]::GetWindowRect($h, [ref]$r)
        $w = $r.R - $r.L
        $hh = $r.B - $r.T
        if ($w -gt 60 -and $hh -gt 30) {
            # 注意：$pid 是 PowerShell 内置只读变量，这里必须换个名字
            $ownerPid = 0
            [void][W]::GetWindowThreadProcessId($h, [ref]$ownerPid)
            $procName = ""
            try { $procName = (Get-Process -Id $ownerPid -ErrorAction Stop).ProcessName } catch { $procName = "?" }
            [void]$windows.Add([pscustomobject]@{
                Handle   = $h
                ProcId   = $ownerPid
                ProcName = $procName
                Class    = (Get-Class $h)
                Title    = (Get-Text $h)
                Rect     = "$($r.L),$($r.T) $w x $hh"
            })
        }
    }
    return $true
}
[void][W]::EnumWindows($cbTop, [IntPtr]::Zero)

$out = New-Object System.Collections.ArrayList
[void]$out.Add("=== 可见顶层窗口 ===")
foreach ($w in $windows) {
    [void]$out.Add("")
    [void]$out.Add("[$($w.Class)] 「$($w.Title)」  进程=$($w.ProcName)($($w.ProcId))  位置=$($w.Rect)")
    $children = Get-Children $w.Handle
    if ($children.Count -gt 0) {
        foreach ($c in $children) {
            [void]$out.Add("    子控件 [$($c.Class)] 「$($c.Text)」 visible=$($c.Visible)  $($c.Rect)")
        }
    }
}

$report = $out -join "`r`n"
$file = Join-Path $PSScriptRoot "exit-dialog-report.txt"
$report | Out-File -FilePath $file -Encoding UTF8

Write-Host ""
Write-Host $report
Write-Host ""
Write-Host "已保存到：$file" -ForegroundColor Green
[void](Read-Host "按回车关闭")
