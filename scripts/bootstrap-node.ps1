[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskVersion = '26.13.0'
$taskFolder = Join-Path $taskRoot ".tools/node/node-v$taskVersion-win-x64"
$taskExe = Join-Path $taskFolder 'node.exe'
if (Test-Path -LiteralPath $taskExe) {
    if ((& $taskExe --version) -ne "v$taskVersion") { throw 'Unexpected bundled Node version.' }
    Write-Output $taskExe
    exit 0
}
$taskArchive = Join-Path $taskRoot ".tools/node/node-v$taskVersion-win-x64.zip"
New-Item -ItemType Directory -Path (Split-Path -Parent $taskArchive) -Force | Out-Null
Invoke-WebRequest "https://nodejs.org/dist/v$taskVersion/node-v$taskVersion-win-x64.zip" -OutFile $taskArchive
# Official nodejs.org SHASUMS256.txt, verified 2026-11-06.
if ((Get-FileHash $taskArchive).Hash -ne 'dcc60d59a0746999f99064a01c020445d54d84228c691cdbf25b49ac3b59150a') { throw 'Bundled Node archive SHA256 mismatch.' }
Expand-Archive -LiteralPath $taskArchive -DestinationPath (Split-Path -Parent $taskArchive) -Force
Write-Output $taskExe
