# Makes MdViewer open .md and .markdown files on double-click and adds it to the Start menu.
# Current user only; no administrator rights needed.
#   .\install.ps1                          use app\MdViewer.exe next to this script
#   .\install.ps1 -Exe D:\tools\MdViewer.exe
#   .\install.ps1 -Uninstall
# If Windows still opens .md with another app, that app was picked with "Always use this app";
# right-click a .md > Open with > Choose another app > MdViewer > Always.
param(
    [string]$Exe = (Join-Path $PSScriptRoot 'app\MdViewer.exe'),
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'

$classes = 'HKCU:\Software\Classes'
$progId = 'mdviewer.md'
$extensions = '.md', '.markdown' # the same list as Paths.MarkdownExtensions in MdViewer.Core (this script runs without the app)
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'MdViewer.lnk'

Add-Type -Namespace MdViewer -Name Shell -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("shell32.dll")]
public static extern void SHChangeNotify(int eventId, int flags, System.IntPtr item1, System.IntPtr item2);
'@

if ($Uninstall) {
    foreach ($ext in $extensions) {
        $key = Join-Path $classes $ext
        if ((Get-ItemProperty $key -ErrorAction SilentlyContinue).'(default)' -eq $progId) { Remove-ItemProperty $key '(default)' }
        Remove-ItemProperty (Join-Path $key 'OpenWithProgids') $progId -ErrorAction SilentlyContinue
    }
    Remove-Item (Join-Path $classes $progId) -Recurse -ErrorAction SilentlyContinue
    Remove-Item $shortcut -ErrorAction SilentlyContinue
    [MdViewer.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero) # SHCNE_ASSOCCHANGED
    Write-Host 'MdViewer file association and shortcut removed.'
    return
}

$Exe = (Resolve-Path $Exe).Path

$prog = Join-Path $classes $progId
New-Item "$prog\shell\open\command" -Force | Out-Null
Set-ItemProperty $prog '(default)' 'Markdown document'
New-Item "$prog\DefaultIcon" -Force | Out-Null
Set-ItemProperty "$prog\DefaultIcon" '(default)' "`"$Exe`",0"
Set-ItemProperty "$prog\shell\open\command" '(default)' "`"$Exe`" `"%1`""

foreach ($ext in $extensions) {
    $key = Join-Path $classes $ext
    New-Item "$key\OpenWithProgids" -Force | Out-Null
    Set-ItemProperty $key '(default)' $progId
    New-ItemProperty "$key\OpenWithProgids" $progId -Value '' -PropertyType String -Force | Out-Null
}

$link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
$link.TargetPath = $Exe
$link.WorkingDirectory = Split-Path $Exe
$link.Description = 'Markdown viewer'
$link.Save()

[MdViewer.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero) # SHCNE_ASSOCCHANGED
Write-Host "MdViewer now opens $($extensions -join ', ') files ($Exe)."
Write-Host "Start menu shortcut: $shortcut"
