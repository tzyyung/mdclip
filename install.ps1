# Makes MdClip open .md and .markdown files on double-click, offers it for pictures (.png, .jpg ...) and
# adds it to the Start menu. Current user only; no administrator rights needed.
#   .\install.ps1                          use app\MdClip.exe next to this script
#   .\install.ps1 -Exe D:\tools\MdClip.exe
#   .\install.ps1 -Uninstall
# The file types are set by MdClip itself (MdClip.exe --register / --unregister, FileTypes.cs), the same
# code as the boxes in MdClip's 更多 > 設定, where they can also be turned on and off later.
# Types where another app was picked with "Always use this app" (pictures usually): 更多 > 設定 > 改用.
param(
    [string]$Exe = (Join-Path $PSScriptRoot 'app\MdClip.exe'),
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'

$programs = [Environment]::GetFolderPath('Programs')
$shortcut = Join-Path $programs 'MdClip.lnk'
$Exe = (Resolve-Path $Exe).Path

if ($Uninstall) {
    Start-Process $Exe -ArgumentList '--unregister' -Wait
    Remove-Item $shortcut -ErrorAction SilentlyContinue
    Write-Host 'MdClip file types and shortcut removed.'
    return
}

Start-Process $Exe -ArgumentList '--register' -Wait
Remove-Item (Join-Path $programs 'MdViewer.lnk') -ErrorAction SilentlyContinue # MdClip used to be MdViewer

$link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
$link.TargetPath = $Exe
$link.WorkingDirectory = Split-Path $Exe
$link.Description = 'Markdown viewer'
$link.Save()

Write-Host "MdClip now opens .md and .markdown files ($Exe), and can be picked for pictures."
Write-Host 'To make it the default for pictures: MdClip > 更多 > 設定 > 改用.'
Write-Host "Start menu shortcut: $shortcut"
