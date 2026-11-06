[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
$taskProject = Join-Path $taskRoot 'experiments/ui/EditorProbe/WorkFor.UI.EditorProbe.csproj'
& $taskDotnet restore $taskProject --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Editor probe restore failed.' }
& $taskDotnet build $taskProject -c $Configuration --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw 'Editor probe build failed.' }
$taskResult = Join-Path $taskRoot 'out/ui/editor-probe.json'
if (Test-Path -LiteralPath $taskResult) { Remove-Item -LiteralPath $taskResult }
$taskInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $taskRoot "experiments/ui/EditorProbe/bin/$Configuration/net10.0-windows10.0.26100.0/win-x64/WorkFor.UI.EditorProbe.exe"))
$taskInfo.UseShellExecute = $false; $taskInfo.CreateNoWindow = $true
$taskInfo.Environment['DOTNET_ROOT'] = Split-Path -Parent $taskDotnet
$taskInfo.ArgumentList.Add('--result'); $taskInfo.ArgumentList.Add($taskResult)
$taskProcess = [Diagnostics.Process]::Start($taskInfo)
try {
    if (-not $taskProcess.WaitForExit(60000)) { $taskProcess.Kill(); throw 'Owned editor probe timed out.' }
    if (-not (Test-Path -LiteralPath $taskResult)) { throw 'Editor probe exited without a result.' }
    $taskOutcome = Get-Content -Raw -LiteralPath $taskResult | ConvertFrom-Json
    $taskOutcome | ConvertTo-Json -Depth 8
    if ($taskProcess.ExitCode -ne 0 -or -not $taskOutcome.passed) { throw 'Editor/LSP checks failed.' }
} finally { $taskProcess.Dispose() }
