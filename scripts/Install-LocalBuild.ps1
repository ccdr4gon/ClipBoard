param([string]$ImportSet = '', [Alias('OpenManager')][switch]$OpenPanel)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$release = Join-Path $repo 'out\sticker-release'
$install = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\ClipBoard'
$installExe = Join-Path $install 'ClipBoard.exe'
$dataRoot = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'ClipBoard'
foreach ($file in @('ClipBoard.exe','Tools\ffmpeg.exe','Tools\ffprobe.exe')) {
    if (!(Test-Path -LiteralPath (Join-Path $release $file))) { throw "发布包缺少 $file；请先按文档发布到 out/sticker-release。" }
}
$running = @(Get-Process -Name ClipBoard -ErrorAction SilentlyContinue)
if (@($running | Where-Object { $_.Path -ne $installExe }).Count -gt 0) {
    throw '存在其他目录运行的 ClipBoard，请先从它的托盘菜单退出。'
}

if ($running.Count -gt 0) {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    if (-not ('ClipBoardInstallExit' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class ClipBoardInstallExit {
 public delegate bool Callback(IntPtr h,IntPtr p);
 [DllImport("user32.dll")] static extern bool EnumWindows(Callback callback,IntPtr p);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h,StringBuilder b,int n);
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h,uint msg,IntPtr w,IntPtr l);
 public static void Open(uint pid) { bool found=false; EnumWindows((h,p)=>{GetWindowThreadProcessId(h,out uint id);if(id==pid){var b=new StringBuilder(256);GetClassName(h,b,256);if(b.ToString().StartsWith("WPFTaskbarIcon_")){found=PostMessage(h,0x400,IntPtr.Zero,new IntPtr(0x205));}}return true;},IntPtr.Zero);if(!found)throw new Exception("Tray message window not found");}
 public static IntPtr[] Visible(uint pid) {var list=new List<IntPtr>();EnumWindows((h,p)=>{GetWindowThreadProcessId(h,out uint id);if(id==pid&&IsWindowVisible(h))list.Add(h);return true;},IntPtr.Zero);return list.ToArray();}
}
'@
    }
    foreach ($appProcess in $running) {
        [ClipBoardInstallExit]::Open([uint32]$appProcess.Id)
        $nameCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, '退出')
        $typeCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
        $condition = [System.Windows.Automation.AndCondition]::new($nameCondition, $typeCondition)
        $exitItem = $null
        $processCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$appProcess.Id)
        for ($attempt = 0; $attempt -lt 20 -and $null -eq $exitItem; $attempt++) {
            Start-Sleep -Milliseconds 150
            $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $processCondition)
            foreach ($window in $windows) {
                $exitItem = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
                if ($null -ne $exitItem) { break }
            }
        }
        if ($null -eq $exitItem) { throw '未找到退出菜单，未强制结束旧程序。' }
        try { $exitItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
        catch [System.Windows.Automation.ElementNotAvailableException] { }
        if (-not $appProcess.WaitForExit(5000)) { throw '旧程序尚未正常退出，未更新安装文件。' }
    }
}

$backup = Join-Path $repo ('out\backups\sticker-install-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Path $backup, $install -Force | Out-Null
if (Test-Path -LiteralPath $installExe) { Copy-Item -LiteralPath $installExe -Destination (Join-Path $backup 'ClipBoard.exe') }
if (Test-Path -LiteralPath (Join-Path $dataRoot 'data.json')) {
    Copy-Item -LiteralPath (Join-Path $dataRoot 'data.json') -Destination (Join-Path $backup 'data.json')
}
$toolDestination = Join-Path $install 'Tools'
New-Item -ItemType Directory -Path $toolDestination -Force | Out-Null
foreach ($tool in Get-ChildItem -LiteralPath (Join-Path $release 'Tools') -File) {
    $target = Join-Path $toolDestination $tool.Name
    if (Test-Path -LiteralPath $target) {
        if ((Get-FileHash -LiteralPath $target).Hash -eq (Get-FileHash -LiteralPath $tool.FullName).Hash) { continue }
        $toolBackup = Join-Path $backup 'Tools'
        New-Item -ItemType Directory -Path $toolBackup -Force | Out-Null
        Copy-Item -LiteralPath $target -Destination (Join-Path $toolBackup $tool.Name)
    }
    Copy-Item -LiteralPath $tool.FullName -Destination $target -Force
}
Copy-Item -LiteralPath (Join-Path $release 'ClipBoard.exe') -Destination $installExe -Force
$expectedHash = (Get-FileHash -LiteralPath (Join-Path $release 'ClipBoard.exe')).Hash
if ((Get-FileHash -LiteralPath $installExe).Hash -ne $expectedHash) { throw '安装文件校验失败。' }
$launchArguments = @()
if ($ImportSet) { $launchArguments += "--import-stickers=$ImportSet" }
elseif ($OpenPanel) { $launchArguments += '--stickers' }
$start = @{FilePath=$installExe; WorkingDirectory=$repo; WindowStyle='Hidden'; PassThru=$true}
if ($ImportSet -or $OpenPanel) { $start.WindowStyle = 'Normal' }
if ($launchArguments.Count -gt 0) { $start.ArgumentList = $launchArguments }
$launched = Start-Process @start
Start-Sleep -Seconds 3
$launched.Refresh()
if ($launched.HasExited) { throw "新程序已退出，代码 $($launched.ExitCode)。备份：$backup" }
$result = @{ProcessId=$launched.Id; InstalledPath=$installExe; BackupPath=$backup; Sha256=$expectedHash}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repo 'out\sticker-install-result.json') -Encoding utf8
$result | ConvertTo-Json -Compress
