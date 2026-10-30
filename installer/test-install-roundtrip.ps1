[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SetupPath,
    [switch]$Execute
)
$ErrorActionPreference = 'Stop'
$taskSetup = [IO.Path]::GetFullPath($SetupPath)
if (-not (Test-Path -LiteralPath $taskSetup)) { throw 'Setup executable is missing.' }
$taskLocal = [Environment]::GetFolderPath('LocalApplicationData')
$taskRoot = [IO.Path]::GetFullPath((Join-Path $taskLocal 'Programs/WorkFor'))
$taskUserData = Join-Path $taskLocal 'WorkFor'
$taskMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'WorkFor'
$taskUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkFor'
$taskRunKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
function Read-Run {
    $taskKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($taskRunKey)
    try { if ($taskKey) { return $taskKey.GetValue('WorkFor', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } }
    finally { if ($taskKey) { $taskKey.Dispose() } }
    return $null
}
function Has-UninstallKey {
    $taskKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($taskUninstallKey)
    if ($taskKey) { $taskKey.Dispose(); return $true }
    return $false
}
if (Test-Path -LiteralPath $taskRoot) { throw 'Roundtrip requires a completely absent installation directory.' }
if (Test-Path -LiteralPath $taskUserData) { throw 'Roundtrip refuses to use existing salary or rule data.' }
if ((Test-Path -LiteralPath $taskMenu) -or (Has-UninstallKey) -or $null -ne (Read-Run)) {
    throw 'Roundtrip requires no existing WorkFor shortcuts, uninstall registration or startup value.'
}
if (-not $Execute) {
    @{ready=$true;actualInstallExecuted=$false;requiresExplicitExecuteSwitch=$true} | ConvertTo-Json
    exit 0
}
function Run-OwnExecutable([string]$File, [string[]]$Arguments, [int]$DeadlineSeconds = 60) {
    $taskInfo = [Diagnostics.ProcessStartInfo]::new($File)
    $taskInfo.UseShellExecute = $false; $taskInfo.CreateNoWindow = $true
    foreach ($taskArgument in $Arguments) { $taskInfo.ArgumentList.Add($taskArgument) }
    $taskProcess = [Diagnostics.Process]::Start($taskInfo)
    try {
        if (-not $taskProcess.WaitForExit([Math]::Min($DeadlineSeconds,60)*1000)) {
            $taskProcess.Kill($true)
            throw 'Owned installer process exceeded the bounded deadline; inspect before retrying.'
        }
        if ($taskProcess.ExitCode -ne 0) { throw "Owned executable returned $($taskProcess.ExitCode)." }
    } finally { $taskProcess.Dispose() }
}
$taskOwnedStartup = $null
try {
    Run-OwnExecutable $taskSetup @('/S')
    $taskLauncher = Join-Path $taskRoot 'WorkFor.exe'
    if (-not (Test-Path -LiteralPath (Join-Path $taskRoot '.workfor-install')) -or -not (Has-UninstallKey)) { throw 'Installer ownership or registration missing.' }
    Run-OwnExecutable $taskLauncher @('--verify')
    if (-not (Test-Path -LiteralPath (Join-Path $taskMenu 'WorkFor.lnk'))) { throw 'Application shortcut was not created.' }
    # This is exactly StartupRegistration.Command for the installed launcher.
    $taskOwnedStartup = '"' + $taskLauncher + '" "--background"'
    $taskKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($taskRunKey,$true)
    try { $taskKey.SetValue('WorkFor',$taskOwnedStartup,[Microsoft.Win32.RegistryValueKind]::String); $taskKey.Flush() }
    finally { $taskKey.Dispose() }
    Run-OwnExecutable (Join-Path $taskRoot 'Uninstall.exe') @('/S')
    $taskDeadline = [Diagnostics.Stopwatch]::StartNew()
    while ((Test-Path -LiteralPath $taskRoot) -and $taskDeadline.Elapsed.TotalSeconds -lt 15) { Start-Sleep -Milliseconds 100 }
    if ((Test-Path -LiteralPath $taskRoot) -or (Has-UninstallKey) -or (Test-Path -LiteralPath $taskMenu) -or $null -ne (Read-Run)) {
        throw 'Uninstall left owned files, shortcuts, registration or the quoted startup command.'
    }
    if (Test-Path -LiteralPath $taskUserData) { throw 'The installer unexpectedly created user salary data.' }
    @{passed=$true;actualInstallExecuted=$true;actualUninstallExecuted=$true;packageIntegrityVerified=$true;
      quotedStartupEntryRemoved=$true;installationRemoved=$true;shortcutsRemoved=$true;registrationRemoved=$true;
      salaryDataUntouched=$true;applicationStarted=$false;explorerInjectionExecuted=$false} | ConvertTo-Json
} finally {
    if ($taskOwnedStartup -and (Read-Run) -eq $taskOwnedStartup) {
        $taskKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($taskRunKey,$true)
        try { if ($taskKey) { $taskKey.DeleteValue('WorkFor',$false); $taskKey.Flush() } }
        finally { if ($taskKey) { $taskKey.Dispose() } }
    }
}
