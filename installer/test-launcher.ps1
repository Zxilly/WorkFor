[CmdletBinding()]
param([Parameter(Mandatory)][string]$Launcher)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDirectory = Join-Path $taskRoot ('.local/launcher-verifier/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskDirectory -Force | Out-Null
Copy-Item -LiteralPath ([IO.Path]::GetFullPath($Launcher)) -Destination (Join-Path $taskDirectory 'WorkFor.exe')
$taskPaths = @{ host='host/WorkFor.Host.exe'; script='script/WorkFor.ScriptHost.exe'; ui='ui/WorkFor.UI.exe'; native='native/WorkFor.NativeBridge.exe' }
foreach ($taskRelative in @($taskPaths.Values) + @('native/WorkFor.Taskbar.dll','native/WorkFor.Taskbar.Live.dll')) {
    $taskFile = Join-Path $taskDirectory $taskRelative
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskFile) -Force | Out-Null
    [IO.File]::WriteAllText($taskFile,'Verifier fixture only; this file is never executed.')
}
$taskManifest = @{ schemaVersion=1; version='0.1.0'; paths=$taskPaths; files=@() }
$taskManifest.files = @(Get-ChildItem -LiteralPath $taskDirectory -Recurse -File | ForEach-Object {
    @{ path=[IO.Path]::GetRelativePath($taskDirectory,$_.FullName).Replace('\','/'); length=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
function Save-Manifest { $taskManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskDirectory 'release.json') -Encoding utf8 }
function Verify-Package([int]$Expected, [string]$Mode = '--verify') {
    $taskInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $taskDirectory 'WorkFor.exe'))
    $taskInfo.ArgumentList.Add($Mode); $taskInfo.UseShellExecute=$false; $taskInfo.CreateNoWindow=$true
    $taskInfo.RedirectStandardOutput=$true; $taskInfo.RedirectStandardError=$true
    $taskProcess = [Diagnostics.Process]::Start($taskInfo)
    $taskOutput = $taskProcess.StandardOutput.ReadToEndAsync()
    try {
        if (-not $taskProcess.WaitForExit(15000)) { $taskProcess.Kill(); throw 'Verifier timed out.' }
        if ($taskProcess.ExitCode -ne $Expected) { throw "Unexpected verifier exit $($taskProcess.ExitCode): $($taskOutput.GetAwaiter().GetResult())" }
        $taskJson = $taskOutput.GetAwaiter().GetResult() | ConvertFrom-Json
        if ([bool]$taskJson.valid -ne ($Expected -eq 0)) { throw 'Verifier result did not match exit status.' }
    }
    finally { $taskProcess.Dispose() }
}
Save-Manifest
Verify-Package 0
Verify-Package 1 '--prepare-uninstall'
Verify-Package 1 '--uninstall-files'
if (-not (Test-Path -LiteralPath (Join-Path $taskDirectory 'script/WorkFor.ScriptHost.exe'))) { throw 'Rejected removal changed a file.' }
$taskChanged = Join-Path $taskDirectory 'script/WorkFor.ScriptHost.exe'
[IO.File]::AppendAllText($taskChanged,'tampered')
Verify-Package 1
[IO.File]::WriteAllText($taskChanged,'Verifier fixture only; this file is never executed.')
$taskSavedPath = $taskManifest.paths.host
$taskManifest.paths.host = '../outside.exe'
Save-Manifest
Verify-Package 1
$taskManifest.paths.host = $taskSavedPath
$taskManifest.files = @($taskManifest.files | Where-Object { $_.path -ne 'native/WorkFor.Taskbar.dll' })
Save-Manifest
Verify-Package 1
Write-Output 'PASS: inventory/tamper/path/omission checks; both removal modes reject a non-installation directory without deleting files.'
Write-Output "Fixture retained for inspection: $taskDirectory"
