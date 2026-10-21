# Builds MdViewer as a single exe.
#   .\publish.ps1                 -> app\MdViewer.exe   (small; needs the .NET 10 Desktop Runtime)
#   .\publish.ps1 -SelfContained  -> dist\MdViewer.exe  (about 70 MB; runs on any 64-bit Windows 10/11)
# A running MdViewer is asked to quit first (a running exe cannot be overwritten) and started again after.
param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'

$running = Get-Process MdViewer -ErrorAction SilentlyContinue | Select-Object -First 1
if ($running) {
    $exe = $running.Path
    & $exe --exit
    if (-not $running.WaitForExit(10000)) { throw 'MdViewer did not quit; exit it from its tray icon menu and run this again.' }
}

$out = Join-Path $PSScriptRoot $(if ($SelfContained) { 'dist' } else { 'app' })
$publishArgs = @(
    'publish', (Join-Path $PSScriptRoot 'MdViewer\MdViewer.csproj'),
    '-c', 'Release', '-r', 'win-x64', '-o', $out,
    '--self-contained', $(if ($SelfContained) { 'true' } else { 'false' }),
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true'
)
if ($SelfContained) { $publishArgs += '-p:EnableCompressionInSingleFile=true' }

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
Write-Host "Built $(Join-Path $out 'MdViewer.exe')"
if ($running -and -not $SelfContained) { Start-Process $exe }
