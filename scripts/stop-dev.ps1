[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$taskSession = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
$taskPipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', "workfor-control-v1-$taskSid-$taskSession", [System.IO.Pipes.PipeDirection]::InOut)
try {
    $taskPipe.Connect(2000)
    $taskBytes = [System.Text.Encoding]::UTF8.GetBytes('{"protocolVersion":1,"type":"host.shutdown","requestId":"stop-dev","payload":{}}')
    $taskHeader = [System.BitConverter]::GetBytes([uint32]$taskBytes.Length)
    $taskPipe.Write($taskHeader); $taskPipe.Write($taskBytes); $taskPipe.Flush()
    $taskPipe.ReadExactly($taskHeader)
    $taskReply = [byte[]]::new([System.BitConverter]::ToUInt32($taskHeader))
    $taskPipe.ReadExactly($taskReply)
    Write-Output ([System.Text.Encoding]::UTF8.GetString($taskReply))
}
finally { $taskPipe.Dispose() }
