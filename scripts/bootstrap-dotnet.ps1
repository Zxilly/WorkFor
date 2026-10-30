[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskVersion = '10.0.403'
$taskDestination = Join-Path $taskRoot '.tools/dotnet'
$taskExe = Join-Path $taskDestination 'dotnet.exe'
if ((Test-Path -LiteralPath $taskExe) -and (& $taskExe --version) -eq $taskVersion) {
    Write-Output $taskExe
    exit 0
}
$taskMetadataUrl = 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$taskMetadata = Invoke-RestMethod $taskMetadataUrl
$taskRelease = $taskMetadata.releases | Where-Object { $_.sdk.version -eq $taskVersion } | Select-Object -First 1
if (-not $taskRelease) { throw "SDK $taskVersion is absent from official release metadata." }
$taskAsset = $taskRelease.sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -eq 'dotnet-sdk-win-x64.zip' } | Select-Object -First 1
if (-not $taskAsset) { throw 'Windows x64 SDK archive is absent.' }
$taskArchive = Join-Path $taskRoot ".tools/dotnet-sdk-$taskVersion.zip"
New-Item -ItemType Directory -Path (Split-Path -Parent $taskArchive) -Force | Out-Null
if (-not (Test-Path -LiteralPath $taskArchive)) {
    Invoke-WebRequest -Uri $taskAsset.url -OutFile $taskArchive
}
$taskActualHash = (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA512).Hash
if ($taskActualHash -ne $taskAsset.hash) { throw 'SDK archive SHA512 does not match official metadata.' }
New-Item -ItemType Directory -Path $taskDestination -Force | Out-Null
Expand-Archive -LiteralPath $taskArchive -DestinationPath $taskDestination -Force
if ((& $taskExe --version) -ne $taskVersion) { throw 'Unexpected SDK version after extraction.' }
Write-Output $taskExe
