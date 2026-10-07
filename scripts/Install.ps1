[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\CodexUsageMonitor'),
    [switch]$SkipLaunch,
    [switch]$SkipShortcuts
)
$ErrorActionPreference = 'Stop'
$sourceExecutable = Join-Path $PSScriptRoot 'CodexUsageMonitor.exe'
if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf)) { throw 'ZIP paketinin tamamını bir klasöre çıkarın; Install.cmd dosyasını o klasörde çalıştırın.' }
$destination = [IO.Path]::GetFullPath($InstallDirectory)
if ($destination.TrimEnd('\') -eq $PSScriptRoot.TrimEnd('\')) { throw 'Kaynak ve kurulum klasörü aynı olamaz. Taşınabilir kullanım için EXE dosyasını açın.' }
if (-not $PSCmdlet.ShouldProcess($destination,'Install Süper Zeka Kullanımı')) { return }
$installedExecutable = Join-Path $destination 'CodexUsageMonitor.exe'
Get-Process -Name CodexUsageMonitor,UsageMonitorSupervisor -ErrorAction SilentlyContinue | ForEach-Object {
    try { if ($_.Path -eq $installedExecutable -or $_.Path -eq (Join-Path $destination 'UsageMonitorSupervisor.exe')) { Stop-Process -Id $_.Id -ErrorAction Stop } }
    catch { throw 'Kurulu uygulamayı tepsi menüsünden kapatıp yeniden deneyin.' }
}
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Get-ChildItem -LiteralPath $PSScriptRoot | Where-Object { $_.Extension -ne '.zip' -and $_.Name -ne 'SHA256SUMS.txt' } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $destination -Recurse -Force
}
if (-not $SkipShortcuts) {
    $menu = [Environment]::GetFolderPath('Programs')
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path $menu 'Süper Zeka Kullanımı.lnk'))
    $shortcut.TargetPath = $installedExecutable
    $shortcut.WorkingDirectory = $destination
    $shortcut.IconLocation = (Join-Path $destination 'Assets\usage-monitor.ico')
    $shortcut.Save()
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
}
Write-Host "Kuruldu: $destination"
if (-not $SkipLaunch) {
    $taskName = 'CodexUsageMonitor-' + [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $startupTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($startupTask -and $startupTask.Settings.Enabled -and $startupTask.Actions.Execute -eq (Join-Path $destination 'UsageMonitorSupervisor.exe')) {
        Start-ScheduledTask -TaskName $taskName
    } else { Start-Process -FilePath $installedExecutable -WindowStyle Hidden }
}
