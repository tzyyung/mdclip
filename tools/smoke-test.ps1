# Unit tests for the path rules (MdViewer.Tests), then an end-to-end check of the built viewer
# (app\MdViewer.exe): page features through the debug port (tools\smoke.mjs), then the tray: close
# button hides, a clipboard path brings it back, --exit quits.
#   .\tools\smoke-test.ps1
#   .\tools\smoke-test.ps1 -Only "heading link" -Repeat 20   # one page check, many times (intermittent failures)
# A running viewer is closed first and reopened on the same file afterwards. The clipboard is saved
# and restored; fixture entries are removed from the reading history.
param(
    [string]$Only,    # run only the page checks whose name contains this, and skip the tray checks
    [int]$Repeat = 1  # run each page check this many times
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace Smoke -Name Win -MemberDefinition @'
public delegate bool EnumProc(System.IntPtr h, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetWindowText(System.IntPtr h, System.Text.StringBuilder s, int n);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool PostMessage(System.IntPtr h, uint m, System.IntPtr w, System.IntPtr l);
'@

$repo = Split-Path $PSScriptRoot
$exe = Join-Path $repo 'app\MdViewer.exe'
$fixtures = Join-Path $PSScriptRoot 'fixtures'
$settings = Join-Path $env:LOCALAPPDATA 'MdViewer\theme.txt'

dotnet test (Join-Path $repo 'MdViewer.Tests') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'unit tests failed' }

function Get-Viewer { Get-Process MdViewer -ErrorAction SilentlyContinue | Select-Object -First 1 }

function Stop-Viewer {
    $p = Get-Viewer
    if (-not $p) { return }
    & $exe --exit
    if (-not $p.WaitForExit(10000)) { throw 'MdViewer did not quit' }
}

function Get-MainWindow {
    $p = Get-Viewer
    if (-not $p) { return $null }
    $script:found = $null
    [Smoke.Win]::EnumWindows({ param($h, $l)
        $id = 0; [void][Smoke.Win]::GetWindowThreadProcessId($h, [ref]$id)
        $sb = New-Object System.Text.StringBuilder 256; [void][Smoke.Win]::GetWindowText($h, $sb, 256)
        if ($id -eq $p.Id -and $sb.ToString() -like '*MdViewer') { $script:found = $h }
        $true }, [IntPtr]::Zero) | Out-Null
    $script:found
}

function Get-WebViewProcessCount {
    @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -match 'webview-exe-name=MdViewer.exe' }).Count
}

$failed = 0
function Report($name, $ok, $detail = '') {
    if ($ok) { Write-Host "PASS  $name  $detail" } else { Write-Host "FAIL  $name  $detail"; $script:failed++ }
}

# the file the user was reading, to reopen afterwards
$wasRunning = [bool](Get-Viewer)
$lastFile = (Get-Content $settings -ErrorAction SilentlyContinue | Where-Object { $_ -like 'hist=*' } | Select-Object -First 1) -replace '^hist=[^|]*\|(\d+\|)?', ''

Stop-Viewer
$clip = [Windows.Forms.Clipboard]::GetDataObject()
$saved = New-Object Windows.Forms.DataObject
foreach ($format in $clip.GetFormats($false)) { try { $saved.SetData($format, $clip.GetData($format)) } catch { } }

try {
    $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = '--remote-debugging-port=9333'
    Start-Process $exe -ArgumentList "`"$(Join-Path $fixtures 'guide.md')`""
    $env:SMOKE_ONLY = $Only
    $env:SMOKE_REPEAT = $Repeat
    node (Join-Path $PSScriptRoot 'smoke.mjs') 9333 $fixtures
    if ($LASTEXITCODE -ne 0) { $failed++ }
    if ($Only) { return } # straight to the clean-up below

    Write-Host ''
    $window = Get-MainWindow
    if (-not $window) { Report 'viewer window found' $false; return } # straight to the clean-up below
    [void][Smoke.Win]::PostMessage($window, 0x0112, [IntPtr]0xF060, [IntPtr]::Zero) # SC_CLOSE, as the X button sends
    Start-Sleep 4
    Report 'close button hides to the tray' ((Get-Viewer) -and -not [Smoke.Win]::IsWindowVisible($window))
    Report 'hidden window releases the web view' ((Get-WebViewProcessCount) -eq 0) "$(Get-WebViewProcessCount) left"

    [Windows.Forms.Clipboard]::SetText('other.md')
    $shown = $false
    for ($i = 0; $i -lt 30 -and -not $shown; $i++) { Start-Sleep -Milliseconds 300; $shown = [Smoke.Win]::IsWindowVisible($window) }
    Report 'clipboard path brings the window back' $shown

    Stop-Viewer
    Report '--exit quits' (-not (Get-Viewer))
}
finally {
    Remove-Item Env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS, Env:SMOKE_ONLY, Env:SMOKE_REPEAT -ErrorAction SilentlyContinue
    if (Get-Viewer) { Stop-Viewer }
    [Windows.Forms.Clipboard]::SetDataObject($saved, $true)
    # keep the fixtures out of the user's reading history and remembered folders
    if (Test-Path $settings) {
        $kept = Get-Content $settings -Encoding UTF8 | Where-Object { $_ -notmatch '^(hist|dir)=.*\\tools\\fixtures' }
        [IO.File]::WriteAllLines($settings, [string[]]$kept, (New-Object Text.UTF8Encoding $false))
    }
    if ($wasRunning) {
        if ($lastFile -and (Test-Path $lastFile)) { Start-Process $exe -ArgumentList "`"$lastFile`"" } else { Start-Process $exe }
    }
}

Write-Host ''
if ($failed) { Write-Host "$failed check(s) failed"; exit 1 } else { Write-Host 'all checks passed' }
