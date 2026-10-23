# Shared by publish.ps1 and smoke-test.ps1, which build or test MdViewer while it may be running.
#   . (Join-Path $PSScriptRoot 'viewer.ps1')

function Get-Viewer { Get-Process MdViewer -ErrorAction SilentlyContinue | Select-Object -First 1 }

# Asks a running MdViewer to quit properly and waits for it: it then removes its tray icon, which a
# killed process would leave behind. Returns the path of the exe that was running, or nothing.
function Stop-Viewer {
    $p = Get-Viewer
    if (-not $p) { return }
    $exe = $p.Path
    & $exe --exit
    if (-not $p.WaitForExit(10000)) { throw 'MdViewer did not quit; exit it from its tray icon menu and run this again.' }
    $exe
}
