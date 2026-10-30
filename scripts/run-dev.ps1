[CmdletBinding()]
param(
    [switch]$WithoutTaskbar,
    [switch]$Settings,
    [string]$DataDirectory
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskRuntime = Join-Path $taskRoot '.tools/dotnet'
$taskHost = Join-Path $taskRoot 'src/WorkFor.Host/bin/Debug/net10.0-windows/WorkFor.Host.exe'
$taskScript = Join-Path $taskRoot 'src/WorkFor.ScriptHost/bin/Debug/net10.0/WorkFor.ScriptHost.exe'
$taskUi = Join-Path $taskRoot 'src/WorkFor.UI/bin/Debug/net10.0-windows10.0.26100.0/win-x64/WorkFor.UI.exe'
$taskNative = Join-Path $taskRoot 'out/cmake-native/bin/Debug/WorkFor.NativeBridge.exe'
foreach ($taskFile in @($taskHost, $taskScript, $taskUi)) {
    if (-not (Test-Path -LiteralPath $taskFile)) { throw "Build first: missing $taskFile" }
}
if (-not $DataDirectory) { $DataDirectory = Join-Path $taskRoot '.local/dev' }
$taskInfo = [System.Diagnostics.ProcessStartInfo]::new($taskHost)
$taskInfo.UseShellExecute = $false
$taskInfo.CreateNoWindow = $true
$taskInfo.Environment['DOTNET_ROOT'] = $taskRuntime
foreach ($taskArg in @('--data-dir', [System.IO.Path]::GetFullPath($DataDirectory), '--script-host', $taskScript, '--ui', $taskUi)) { $taskInfo.ArgumentList.Add($taskArg) }
if (-not $WithoutTaskbar) {
    if (-not (Test-Path -LiteralPath $taskNative)) { throw 'Native build missing. Build native targets or use -WithoutTaskbar.' }
    $taskNativeBuild = Split-Path -Parent $taskNative
    $taskNativeFiles = @('WorkFor.NativeBridge.exe', 'WorkFor.Taskbar.dll', 'WorkFor.Taskbar.Live.dll')
    $taskHashes = foreach ($taskName in $taskNativeFiles) { (Get-FileHash -LiteralPath (Join-Path $taskNativeBuild $taskName) -Algorithm SHA256).Hash }
    $taskRunHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes(($taskHashes -join ':')))).ToLowerInvariant()
    $taskImmutableRun = Join-Path $taskRoot ".local/native-runs/$taskRunHash"
    New-Item -ItemType Directory -Path $taskImmutableRun -Force | Out-Null
    for ($taskIndex = 0; $taskIndex -lt $taskNativeFiles.Count; $taskIndex++) {
        $taskSource = Join-Path $taskNativeBuild $taskNativeFiles[$taskIndex]
        $taskTarget = Join-Path $taskImmutableRun $taskNativeFiles[$taskIndex]
        if (Test-Path -LiteralPath $taskTarget) {
            if ((Get-FileHash -LiteralPath $taskTarget -Algorithm SHA256).Hash -ne $taskHashes[$taskIndex]) { throw 'Immutable native run directory was modified.' }
        } else { Copy-Item -LiteralPath $taskSource -Destination $taskTarget }
    }
    $taskNative = Join-Path $taskImmutableRun 'WorkFor.NativeBridge.exe'
    $taskInfo.ArgumentList.Add('--native-bridge'); $taskInfo.ArgumentList.Add($taskNative)
}
$taskProcess = [System.Diagnostics.Process]::Start($taskInfo)
$taskUiInfo = [System.Diagnostics.ProcessStartInfo]::new($taskUi)
$taskUiInfo.UseShellExecute = $false
$taskUiInfo.CreateNoWindow = $true
$taskUiInfo.Environment['DOTNET_ROOT'] = $taskRuntime
if ($Settings) { $taskUiInfo.ArgumentList.Add('--settings') }
$taskWindow = [System.Diagnostics.Process]::Start($taskUiInfo)
Write-Output "Host PID: $($taskProcess.Id); UI PID: $($taskWindow.Id)"
Write-Output 'Use scripts/stop-dev.ps1 to request a graceful host shutdown and native logical detach.'
