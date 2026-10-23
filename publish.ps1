# Builds MdClip as a single exe.
#   .\publish.ps1                 -> app\MdClip.exe   (small; needs the .NET 10 Desktop Runtime)
#   .\publish.ps1 -SelfContained  -> dist\MdClip.exe  (about 70 MB; runs on any 64-bit Windows 10/11)
# A running MdClip is asked to quit first (a running exe cannot be overwritten) and started again after.
param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'tools\viewer.ps1')
$exe = Stop-Viewer # the exe that was running, to start again afterwards

$out = Join-Path $PSScriptRoot $(if ($SelfContained) { 'dist' } else { 'app' })
$publishArgs = @(
    'publish', (Join-Path $PSScriptRoot 'MdClip\MdClip.csproj'),
    '-c', 'Release', '-r', 'win-x64', '-o', $out,
    '--self-contained', $(if ($SelfContained) { 'true' } else { 'false' }),
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true'
)
if ($SelfContained) { $publishArgs += '-p:EnableCompressionInSingleFile=true' }

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
Write-Host "Built $(Join-Path $out 'MdClip.exe')"
if ($exe -and -not $SelfContained) { Start-Process $exe }
