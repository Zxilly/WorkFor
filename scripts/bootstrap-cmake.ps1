[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskVersion = '4.4.5'
$taskAssetName = "cmake-$taskVersion-windows-x86_64.zip"
$taskTools = Join-Path $taskRoot '.tools/cmake'
$taskExecutable = Join-Path $taskTools "cmake-$taskVersion-windows-x86_64/bin/cmake.exe"
if (Test-Path -LiteralPath $taskExecutable) {
    if ((& $taskExecutable --version)[0] -ne "cmake version $taskVersion") { throw 'Unexpected local CMake version.' }
    Write-Output $taskExecutable
    exit 0
}
$taskHeaders = @{ 'User-Agent' = 'WorkFor-build-bootstrap' }
# Verified against the official cmake-4.4.5-SHA-256.txt on 2026-10-30.
# Fixed release URLs avoid the anonymous GitHub API rate limit on shared CI IPs.
$taskArchiveUrl = "https://github.com/Kitware/CMake/releases/download/v$taskVersion/$taskAssetName"
$taskExpectedHash = '326c826486d46a23cf9d5a1c9ee3a567d427bdd02c4a34071937a2a675cff004'
New-Item -ItemType Directory -Path $taskTools -Force | Out-Null
$taskArchive = Join-Path $taskTools $taskAssetName
if (-not (Test-Path -LiteralPath $taskArchive)) { Invoke-WebRequest $taskArchiveUrl -Headers $taskHeaders -OutFile $taskArchive }
if ((Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash -ne $taskExpectedHash) { throw 'CMake archive SHA256 mismatch.' }
Expand-Archive -LiteralPath $taskArchive -DestinationPath $taskTools -Force
if ((& $taskExecutable --version)[0] -ne "cmake version $taskVersion") { throw 'Unexpected CMake version after extraction.' }
Write-Output $taskExecutable
