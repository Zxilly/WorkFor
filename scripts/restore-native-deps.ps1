[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDirectory = Join-Path $taskRoot 'out/native-deps/nlohmann'
$taskFile = Join-Path $taskDirectory 'json.hpp'
$taskHash = 'AAF127C04CB31C406E5B04A63F1AE89369FCCDE6D8FA7CDDA1ED4F32DFc5DE63'
New-Item -ItemType Directory -Path $taskDirectory -Force | Out-Null
if (-not (Test-Path -LiteralPath $taskFile)) {
    Invoke-WebRequest 'https://raw.githubusercontent.com/nlohmann/json/v3.12.0/single_include/nlohmann/json.hpp' -OutFile $taskFile
}
if ((Get-FileHash -LiteralPath $taskFile -Algorithm SHA256).Hash -ne $taskHash) {
    throw 'nlohmann/json 3.12.0 header hash mismatch.'
}
Write-Output $taskFile
