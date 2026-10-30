[CmdletBinding()]
param([string]$NativeDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $NativeDirectory) { $NativeDirectory = Join-Path $taskRoot 'out/cmake-native/bin/Debug' }
$taskBinaries = @('WorkFor.NativeBridge.exe', 'WorkFor.Taskbar.dll', 'WorkFor.Taskbar.Live.dll')
$taskHashes = foreach ($taskName in $taskBinaries) { (Get-FileHash -LiteralPath (Join-Path $NativeDirectory $taskName) -Algorithm SHA256).Hash }
$taskHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($taskHashes -join ':')))).ToLowerInvariant()
$taskNative = Join-Path $taskRoot ".local/native-acceptance/$taskHash"
New-Item -ItemType Directory -Path $taskNative -Force | Out-Null
for ($taskIndex = 0; $taskIndex -lt $taskBinaries.Count; $taskIndex++) {
    $taskSource = Join-Path $NativeDirectory $taskBinaries[$taskIndex]
    $taskTarget = Join-Path $taskNative $taskBinaries[$taskIndex]
    if (Test-Path -LiteralPath $taskTarget) {
        if ((Get-FileHash -LiteralPath $taskTarget -Algorithm SHA256).Hash -ne $taskHashes[$taskIndex]) { throw 'Immutable native directory mismatch.' }
    } else { Copy-Item -LiteralPath $taskSource -Destination $taskTarget }
}
if (-not ('WorkFor.Acceptance.PipeIdentity' -as [type])) {
    Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
namespace WorkFor.Acceptance {
 public static class PipeIdentity {
  [DllImport("kernel32.dll", SetLastError=true)]
  [return:MarshalAs(UnmanagedType.Bool)]
  public static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
 }
}
'@
}
$taskData = Join-Path $taskRoot ".local/live-acceptance/$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $taskData -Force | Out-Null
$taskHostPath = Join-Path $taskRoot 'src/WorkFor.Host/bin/Debug/net10.0-windows/WorkFor.Host.exe'
$taskUiPath = Join-Path $taskRoot 'src/WorkFor.UI/bin/Debug/net10.0-windows10.0.26100.0/win-x64/WorkFor.UI.exe'
$taskInfo = [Diagnostics.ProcessStartInfo]::new($taskHostPath)
$taskInfo.UseShellExecute = $false; $taskInfo.CreateNoWindow = $true
$taskInfo.Environment['DOTNET_ROOT'] = Join-Path $taskRoot '.tools/dotnet'
foreach ($taskArgument in @('--data-dir',$taskData,'--script-host',(Join-Path $taskRoot 'src/WorkFor.ScriptHost/bin/Debug/net10.0/WorkFor.ScriptHost.exe'),
    '--ui',$taskUiPath,'--native-bridge',(Join-Path $taskNative 'WorkFor.NativeBridge.exe'),'--no-startup-sync')) { $taskInfo.ArgumentList.Add($taskArgument) }
$taskProcess = [Diagnostics.Process]::Start($taskInfo)
$taskSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$taskSession = [Diagnostics.Process]::GetCurrentProcess().SessionId
$taskSequence = 0
function Request-Host([string]$Type, $Payload = @{}) {
    $taskPipe = [IO.Pipes.NamedPipeClientStream]::new('.', "workfor-control-v1-$taskSid-$taskSession", [IO.Pipes.PipeDirection]::InOut)
    try {
        $taskPipe.Connect(1000)
        [uint32]$taskServer = 0
        if (-not [WorkFor.Acceptance.PipeIdentity]::GetNamedPipeServerProcessId($taskPipe.SafePipeHandle.DangerousGetHandle(), [ref]$taskServer) -or $taskServer -ne $taskProcess.Id) {
            throw 'Pipe belongs to another Host; test refuses to modify it.'
        }
        $script:taskSequence++
        $taskBody = [Text.Encoding]::UTF8.GetBytes((@{protocolVersion=1;type=$Type;requestId="$taskSequence";payload=$Payload} | ConvertTo-Json -Depth 16 -Compress))
        $taskHeader = [BitConverter]::GetBytes([uint32]$taskBody.Length)
        $taskPipe.Write($taskHeader); $taskPipe.Write($taskBody); $taskPipe.Flush()
        $taskPipe.ReadExactly($taskHeader)
        $taskLength = [BitConverter]::ToUInt32($taskHeader)
        if ($taskLength -gt 262144) { throw 'Invalid reply length.' }
        $taskReply = [byte[]]::new($taskLength); $taskPipe.ReadExactly($taskReply)
        $taskResponse = [Text.Encoding]::UTF8.GetString($taskReply) | ConvertFrom-Json -AsHashtable
        if ($taskResponse.type.EndsWith('.error')) { throw ($taskResponse.payload | ConvertTo-Json -Compress) }
        return $taskResponse.payload
    } finally { $taskPipe.Dispose() }
}
function Wait-Native([int]$Width, [long]$Revision) {
    $taskTimer = [Diagnostics.Stopwatch]::StartNew()
    while ($taskTimer.Elapsed.TotalSeconds -lt 25) {
        if ($taskProcess.HasExited) { throw "Acceptance Host exited $($taskProcess.ExitCode)." }
        try {
            $taskState = (Request-Host 'diagnostics.get').nativeState
            if ($taskState.state -eq 'Attached' -and $taskState.widthDip -eq $Width -and $taskState.configRevision -eq $Revision) { return $taskState }
            if ($taskState.state -in @('Unsupported','Quarantined','DetachPending')) { throw ($taskState | ConvertTo-Json -Compress -Depth 8) }
        } catch [TimeoutException] { }
        Start-Sleep -Milliseconds 100
    }
    throw 'Native did not confirm requested width and revision.'
}
function Inspect-Taskbar {
    $taskJson = & (Join-Path $taskNative 'WorkFor.NativeBridge.exe') --probe-explorer
    if ($LASTEXITCODE -ne 0) { throw 'Readonly taskbar inspection failed.' }
    return (($taskJson -join "`n") | ConvertFrom-Json -AsHashtable).result.inspection.nodes
}
$taskCleanup = $false
try {
    $taskInitial = Wait-Native 260 1
    $taskRows = @()
    foreach ($taskWidth in @(180,360)) {
        $taskConfig = Request-Host 'config.get'
        $taskSaved = Request-Host 'config.activate' @{ expectedRevision=$taskConfig.revision; display=@{taskbarWidthDip=$taskWidth} }
        $taskState = Wait-Native $taskWidth $taskSaved.revision
        $taskNodes = Inspect-Taskbar
        $taskIncome = @($taskNodes | Where-Object name -eq 'WorkForIncomeRegion')
        if ($taskIncome.Count -ne 1 -or [Math]::Abs($taskIncome[0].width-$taskWidth) -gt 1) { throw 'Real income layout width did not match configuration.' }
        $taskApps = $taskNodes | Where-Object name -eq 'TaskbarFrameRepeater' | Select-Object -First 1
        $taskRows += @{requestedDip=$taskWidth;actualDip=$taskIncome[0].width;applicationsDip=$taskApps.width;configRevision=$taskSaved.revision}
    }
    $taskRules = (Request-Host 'rule.catalog').rules
    $taskMonthly = $taskRules | Where-Object id -eq 'builtin.monthly' | Select-Object -First 1
    $taskConfig = Request-Host 'config.get'
    $taskSaved = Request-Host 'config.activate' @{expectedRevision=$taskConfig.revision;source=$taskMonthly.source;manifest=$taskMonthly.manifest;
        parameters=$taskMonthly.defaultParameters;rule=@{id='builtin.monthly';version='1.0.0'};display=@{period='month'}}
    $taskState = Wait-Native 360 $taskSaved.revision
    Request-Host 'host.shutdown' | Out-Null
    if (-not $taskProcess.WaitForExit(15000)) { throw 'Host did not finish graceful detach.' }
    $taskDetached = Inspect-Taskbar
    if (@($taskDetached | Where-Object name -eq 'WorkForIncomeRegion').Count -ne 0) { throw 'Income region was not removed.' }
    $taskCleanup = $true
    @{passed=$true;widthCases=$taskRows;monthlyRuleActivated=$true;configurationSchema=$taskConfig.schemaVersion;
        cleanupConfirmed=$true;noStartupRegistrySync=$true;physicalMouseClickTested=$false} |
        ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $taskData 'summary.json') -Encoding utf8
    Get-Content -Raw -LiteralPath (Join-Path $taskData 'summary.json')
} finally {
    if (-not $taskProcess.HasExited) {
        try { Request-Host 'host.shutdown' | Out-Null } catch { }
        if (-not $taskProcess.WaitForExit(15000)) { $taskProcess.Kill($true); $taskProcess.WaitForExit() }
    }
    $taskProcess.Dispose()
    if (-not $taskCleanup) { Write-Warning 'Acceptance failed; inspect the isolated data directory and native state before another attempt.' }
}
