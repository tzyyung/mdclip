# Makes MdClip open .md and .markdown files on double-click and adds it to the Start menu.
# Current user only; no administrator rights needed.
#   .\install.ps1                          use app\MdClip.exe next to this script
#   .\install.ps1 -Exe D:\tools\MdClip.exe
#   .\install.ps1 -Uninstall
# If Windows still opens .md with another app, that app was picked with "Always use this app";
# right-click a .md > Open with > Choose another app > MdClip > Always.
param(
    [string]$Exe = (Join-Path $PSScriptRoot 'app\MdClip.exe'),
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'

$classes = 'HKCU:\Software\Classes'
$extensions = '.md', '.markdown' # the same list as Paths.MarkdownExtensions in MdClip.Core (this script runs without the app)
$programs = [Environment]::GetFolderPath('Programs')
$progId = 'mdclip.md'
$shortcut = Join-Path $programs 'MdClip.lnk'

Add-Type -Namespace MdClip -Name Shell -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("shell32.dll")]
public static extern void SHChangeNotify(int eventId, int flags, System.IntPtr item1, System.IntPtr item2);
'@

# Takes a file type registration and its Start menu shortcut away (this app's, or the former name's).
function Remove-Association($id, $link) {
    foreach ($ext in $extensions) {
        $key = Join-Path $classes $ext
        if ((Get-ItemProperty $key -ErrorAction SilentlyContinue).'(default)' -eq $id) {
            # Remove-ItemProperty cannot remove a key's default value; the registry API can.
            $open = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("Software\Classes\$ext", $true)
            $open.DeleteValue('', $false)
            $open.Close()
        }
        Remove-ItemProperty (Join-Path $key 'OpenWithProgids') $id -ErrorAction SilentlyContinue
    }
    Remove-Item (Join-Path $classes $id) -Recurse -ErrorAction SilentlyContinue
    Remove-Item $link -ErrorAction SilentlyContinue
}

$notifyShell = { [MdClip.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero) } # SHCNE_ASSOCCHANGED

if ($Uninstall) {
    Remove-Association $progId $shortcut
    & $notifyShell
    Write-Host 'MdClip file association and shortcut removed.'
    return
}

# MdClip used to be MdViewer: its registration and shortcut make way for this one.
Remove-Association 'mdviewer.md' (Join-Path $programs 'MdViewer.lnk')

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

& $notifyShell
Write-Host "MdClip now opens $($extensions -join ', ') files ($Exe)."
Write-Host "Start menu shortcut: $shortcut"
