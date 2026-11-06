[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskEditor = Join-Path $taskRoot 'src/WorkFor.Editor'
$taskInputs = @((Get-Item -LiteralPath $PSCommandPath), (Get-Item -LiteralPath (Join-Path $taskRoot 'sdk/workfor-api.d.ts')))
$taskInputs += Get-ChildItem -LiteralPath $taskEditor -File | Where-Object { $_.Extension -in @('.json', '.mjs', '.css', '.html') }
$taskInputs += Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src/WorkFor.LanguageServer') -Filter '*.json'
$taskInputs += Get-Item -LiteralPath (Join-Path $PSScriptRoot 'bootstrap-node.ps1')
$taskFingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((($taskInputs | Sort-Object FullName | ForEach-Object { (Get-FileHash $_.FullName).Hash }) -join ':'))))
$taskStamp = Join-Path $taskRoot 'out/editor/.build-stamp'
$taskRequired = @('index.html','main.js','main.css','editor.worker.js','workfor-api.d.ts','lsp/node.exe',
    'lsp/node_modules/typescript/lib/tsserver.js','lsp/node_modules/typescript-language-server/lib/cli.mjs',
    'lsp/node_modules/vscode-json-languageserver/bin/vscode-json-languageserver')
$taskComplete = @($taskRequired | Where-Object { -not (Test-Path -LiteralPath (Join-Path $taskRoot "out/editor/$_") -PathType Leaf) }).Count -eq 0
if ($taskComplete -and (Test-Path -LiteralPath $taskStamp) -and (Get-Content -Raw -LiteralPath $taskStamp).Trim() -eq $taskFingerprint) { return }
if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Node.js 22+ is required to build the offline code editor.' }
& (Join-Path $PSScriptRoot 'bootstrap-node.ps1') | Out-Null
Push-Location (Join-Path $taskRoot 'src/WorkFor.LanguageServer')
try {
    & npm.cmd ci --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Locked language server dependency restore failed.' }
} finally { Pop-Location }
Push-Location $taskEditor
try {
    & npm.cmd ci --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Locked editor dependency restore failed.' }
    & node build.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Offline editor asset build failed.' }
    Set-Content -LiteralPath $taskStamp -Value $taskFingerprint -Encoding ascii
} finally { Pop-Location }
