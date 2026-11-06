[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release', [switch]$Interactive)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
$taskProject = Join-Path $taskRoot 'experiments/ui/FlyoutProbe/WorkFor.UI.FlyoutProbe.csproj'
& $taskDotnet restore $taskProject --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Flyout probe restore failed.' }
& $taskDotnet build $taskProject -c $Configuration --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw 'Flyout probe build failed.' }
$taskResult = Join-Path $taskRoot 'out/ui/flyout-probe.json'
$taskExecutable = Join-Path $taskRoot "experiments/ui/FlyoutProbe/bin/$Configuration/net10.0-windows10.0.26100.0/win-x64/WorkFor.UI.FlyoutProbe.exe"
$taskInfo = [Diagnostics.ProcessStartInfo]::new($taskExecutable)
$taskInfo.UseShellExecute = $false
$taskInfo.CreateNoWindow = $true
$taskInfo.Environment['DOTNET_ROOT'] = Split-Path -Parent $taskDotnet
$taskInfo.ArgumentList.Add('--result')
$taskInfo.ArgumentList.Add($taskResult)
if ($Interactive) { $taskInfo.ArgumentList.Add('--interactive') }
$taskProcess = [Diagnostics.Process]::Start($taskInfo)
try {
    if (-not $taskProcess.WaitForExit(35000)) { $taskProcess.Kill(); throw 'Owned flyout probe timed out.' }
    if (Test-Path -LiteralPath $taskResult) { Get-Content -LiteralPath $taskResult }
    if ($taskProcess.ExitCode -ne 0) { throw 'Flyout behavior checks failed; this probe requires an interactive Windows desktop.' }
} finally { $taskProcess.Dispose() }
