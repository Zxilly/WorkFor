[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDefinition = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'nsis-toolchain.json') | ConvertFrom-Json
$taskTools = Join-Path $taskRoot '.tools'
$taskArchive = Join-Path $taskTools "nsis-$($taskDefinition.version).zip"
$taskDirectory = Join-Path $taskTools "nsis-$($taskDefinition.version)"
New-Item -ItemType Directory -Path $taskTools -Force | Out-Null
if (-not (Test-Path -LiteralPath $taskArchive) -or
    (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskDefinition.sha256) {
    $taskVerified = $false
    foreach ($taskUrl in @($taskDefinition.archive, $taskDefinition.verifiedMirror)) {
        try {
            Invoke-WebRequest -Uri $taskUrl -OutFile $taskArchive
            if ((Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash.ToLowerInvariant() -eq $taskDefinition.sha256) { $taskVerified = $true; break }
        }
        catch { Write-Verbose "NSIS download endpoint failed: $taskUrl" }
    }
    if (-not $taskVerified) { throw 'No NSIS download matched the official SourceForge archive SHA256.' }
}
if ((Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskDefinition.sha256) {
    throw 'NSIS archive SHA256 does not match the hash published on the official SourceForge download page.'
}
if (-not (Test-Path -LiteralPath (Join-Path $taskDirectory 'makensis.exe'))) {
    Expand-Archive -LiteralPath $taskArchive -DestinationPath $taskTools -Force
}
$taskCompiler = Join-Path $taskDirectory 'makensis.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw 'NSIS archive did not contain the expected compiler path.' }
$taskVersion = (& $taskCompiler /VERSION).Trim()
if ($LASTEXITCODE -ne 0 -or $taskVersion -ne "v$($taskDefinition.version)") { throw "Unexpected NSIS version: $taskVersion" }
Write-Output $taskCompiler
