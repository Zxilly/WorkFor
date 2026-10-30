[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.1.0',
    [string]$NativeDirectory,
    [switch]$PrototypeToolchain,
    [switch]$Setup,
    [switch]$SkipSmoke
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) { & (Join-Path $PSScriptRoot 'bootstrap-dotnet.ps1') | Out-Null }
if (-not $NativeDirectory) { $NativeDirectory = Join-Path $taskRoot "out/cmake-native/bin/$Configuration" }
$NativeDirectory = [IO.Path]::GetFullPath($NativeDirectory)
$taskNativeFiles = @('WorkFor.exe', 'WorkFor.NativeBridge.exe', 'WorkFor.Taskbar.dll', 'WorkFor.Taskbar.Live.dll')
foreach ($taskName in $taskNativeFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $NativeDirectory $taskName))) { throw "Native build is incomplete: $taskName. Build the current CMake targets first." }
}
$taskObservedPath = Join-Path $NativeDirectory 'toolchain-observed.json'
$taskObserved = if (Test-Path -LiteralPath $taskObservedPath) { Get-Content -Raw -LiteralPath $taskObservedPath | ConvertFrom-Json } else { $null }
$taskLatest = $taskObserved -and $taskObserved.latestToolchainBaselineMet
$taskNsis = if ($Setup) { & (Join-Path $taskRoot 'installer/bootstrap-nsis.ps1') | Select-Object -Last 1 } else { $null }
if (-not $taskLatest -and -not $PrototypeToolchain) {
    throw 'Current native toolchain has not met the latest stable baseline. Use -PrototypeToolchain only for clearly marked research packages.'
}
$taskId = [Guid]::NewGuid().ToString('N')
$taskStage = Join-Path $taskRoot "out/package-$taskId"
$taskPayload = Join-Path $taskStage 'portable'
$taskAppRelative = "app/$Version"
$taskApp = Join-Path $taskPayload $taskAppRelative
$taskLocks = Join-Path $taskStage 'restore-locks'
New-Item -ItemType Directory -Path $taskPayload,$taskApp,$taskLocks -Force | Out-Null
$taskPackageProps = Join-Path $taskStage 'packaging.props'
$taskImportProps = [Security.SecurityElement]::Escape((Join-Path $taskRoot 'Directory.Build.props'))
$taskLockPrefix = [Security.SecurityElement]::Escape($taskLocks)
$taskPropsXml = '<Project><Import Project="' + $taskImportProps + '" /><PropertyGroup><NuGetLockFilePath>' + $taskLockPrefix + '/$(MSBuildProjectName).lock.json</NuGetLockFilePath><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile></PropertyGroup></Project>'
[IO.File]::WriteAllText($taskPackageProps,$taskPropsXml,[Text.UTF8Encoding]::new($false))

function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $Executable $($Arguments -join ' ')" }
}

function Run-Captured([string]$Executable, [string[]]$Arguments, [int]$TimeoutSeconds = 30) {
    $taskInfo = [Diagnostics.ProcessStartInfo]::new($Executable)
    $taskInfo.UseShellExecute = $false
    $taskInfo.CreateNoWindow = $true
    $taskInfo.RedirectStandardOutput = $true
    $taskInfo.RedirectStandardError = $true
    foreach ($taskArg in $Arguments) { $taskInfo.ArgumentList.Add($taskArg) }
    # Smoke tests must prove package self-containment, not inherit the project-local runtime.
    $taskInfo.Environment.Remove('DOTNET_ROOT') | Out-Null
    $taskInfo.Environment.Remove('DOTNET_ROOT_X64') | Out-Null
    $taskProcess = [Diagnostics.Process]::Start($taskInfo)
    $taskOutput = $taskProcess.StandardOutput.ReadToEndAsync()
    $taskError = $taskProcess.StandardError.ReadToEndAsync()
    try {
        if (-not $taskProcess.WaitForExit($TimeoutSeconds * 1000)) { $taskProcess.Kill($true); throw "Smoke test timed out: $Executable" }
        $taskText = $taskOutput.GetAwaiter().GetResult()
        $taskErrorText = $taskError.GetAwaiter().GetResult()
        if ($taskProcess.ExitCode -ne 0) { throw "Smoke test failed ($($taskProcess.ExitCode)): $taskText $taskErrorText" }
        return $taskText
    }
    finally { $taskProcess.Dispose() }
}

Push-Location $taskRoot
try {
    foreach ($taskProject in @(@{ Name='Host'; Folder='host' }, @{ Name='ScriptHost'; Folder='script' }, @{ Name='UI'; Folder='ui' })) {
        $taskProjectPath = "src/WorkFor.$($taskProject.Name)/WorkFor.$($taskProject.Name).csproj"
        $taskDestination = Join-Path $taskApp $taskProject.Folder
        $taskRuntimeArgument = if ($taskProject.Name -eq 'UI') { '-p:TargetLatestRuntimePatch=true' } else { '-p:RuntimeFrameworkVersion=10.0.14' }
        Invoke-Checked $taskDotnet @('publish', $taskProjectPath, '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true',
            $taskRuntimeArgument, '-p:PublishSingleFile=false', '-p:PublishTrimmed=false',
            "-p:Version=$Version", "-p:DirectoryBuildPropsPath=$taskPackageProps",
            '-o', $taskDestination, '--nologo')
        $taskRuntimeConfig = Get-Content -Raw -LiteralPath (Join-Path $taskDestination "WorkFor.$($taskProject.Name).runtimeconfig.json") | ConvertFrom-Json
        $taskCoreRuntime = @($taskRuntimeConfig.runtimeOptions.includedFrameworks) | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }
        if ($taskCoreRuntime.Count -ne 1 -or $taskCoreRuntime[0].version -ne '10.0.14') {
            throw "Published $($taskProject.Name) is not self-contained on the pinned .NET 10.0.14 runtime."
        }
    }
    $taskNativeOutput = Join-Path $taskApp 'native'
    New-Item -ItemType Directory -Path $taskNativeOutput -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $NativeDirectory 'WorkFor.exe') -Destination (Join-Path $taskPayload 'WorkFor.exe')
    foreach ($taskName in $taskNativeFiles | Where-Object { $_ -ne 'WorkFor.exe' }) {
        Copy-Item -LiteralPath (Join-Path $NativeDirectory $taskName) -Destination (Join-Path $taskNativeOutput $taskName)
    }
    foreach ($taskName in @('compatibility.json', 'native-assets.json')) {
        $taskCandidate = Join-Path $NativeDirectory $taskName
        if (Test-Path -LiteralPath $taskCandidate) { Copy-Item -LiteralPath $taskCandidate -Destination $taskNativeOutput }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $taskRoot 'LICENSE'))) { throw 'Repository LICENSE is required before distribution.' }
    Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE') -Destination $taskPayload
    $taskNotice = Join-Path $taskPayload 'third-party'
    New-Item -ItemType Directory -Path $taskNotice -Force | Out-Null
    $taskPackageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
    $taskDependencies = @{}
    foreach ($taskLock in Get-ChildItem -LiteralPath $taskLocks -Filter '*.json') {
        $taskGraph = Get-Content -Raw -LiteralPath $taskLock.FullName | ConvertFrom-Json -AsHashtable
        foreach ($taskFramework in $taskGraph.dependencies.Values) {
            foreach ($taskIdVersion in $taskFramework.GetEnumerator()) {
                if ($taskIdVersion.Value.resolved) { $taskDependencies["$($taskIdVersion.Key)/$($taskIdVersion.Value.resolved)"] = $taskIdVersion.Value.resolved }
            }
        }
    }
    $taskNotices = foreach ($taskDependency in $taskDependencies.GetEnumerator() | Sort-Object Key) {
        $taskParts = $taskDependency.Key.Split('/')
        $taskPackageDirectory = Join-Path $taskPackageRoot $taskDependency.Key.ToLowerInvariant()
        $taskNuspecPath = Join-Path $taskPackageDirectory "$($taskParts[0].ToLowerInvariant()).nuspec"
        $taskLicense = $null
        $taskExpression = $null
        if (Test-Path -LiteralPath $taskNuspecPath) {
            [xml]$taskNuspec = Get-Content -Raw -LiteralPath $taskNuspecPath
            $taskExpression = [string]$taskNuspec.package.metadata.license.'#text'
            if ($taskNuspec.package.metadata.license.type -eq 'file') { $taskLicense = Join-Path $taskPackageDirectory $taskExpression }
            Copy-Item -LiteralPath $taskNuspecPath -Destination (Join-Path $taskNotice "$($taskParts[0])-$($taskParts[1]).nuspec")
        }
        $taskLicenses = @()
        if ($taskLicense -and (Test-Path -LiteralPath $taskLicense)) { $taskLicenses += Get-Item -LiteralPath $taskLicense }
        $taskLicenses += Get-ChildItem -LiteralPath $taskPackageDirectory -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(LICENSE|LICENCE|NOTICE|THIRD.PARTY)' }
        foreach ($taskLicenseFile in $taskLicenses | Sort-Object FullName -Unique) {
            $taskLicenseDestination = Join-Path $taskNotice "$($taskParts[0])-$($taskParts[1])-$($taskLicenseFile.Name)"
            Copy-Item -LiteralPath $taskLicenseFile.FullName -Destination $taskLicenseDestination
        }
        @{ id=$taskParts[0]; version=$taskParts[1]; source="https://www.nuget.org/packages/$($taskParts[0])/$($taskParts[1])"; license=$taskExpression }
    }
    $taskNotices | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskNotice 'nuget-dependencies.json') -Encoding utf8
    $taskSourceLicenses = @(
        @{Name='Jint-LICENSE.txt'; Url='https://raw.githubusercontent.com/sebastienros/jint/f0e4cbb55467b6e168c9da6392e402e8b3e65c2f/LICENSE.txt'; Hash='7fd0f56c1bc3ac00b50e44fc9b93eeeaeb6905b62a272eb310590b68d4a5c3ff'},
        @{Name='Acornima-LICENSE.txt'; Url='https://raw.githubusercontent.com/adams85/acornima/401bd62d8aeb9f7cbe7b6147937a87e0c71747a8/LICENSE'; Hash='4182d3e7ce42f69b01f35c6ed27d075d26794a3c2f0b0182aa475be6ca565bab'},
        @{Name='Corvus-LICENSE.txt'; Url='https://raw.githubusercontent.com/corvus-dotnet/Corvus.JsonSchema/862901afeb36bfe4d7aace96fa0fb05c55bbe1cc/LICENSE'; Hash='c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4'},
        @{Name='NodaTime-LICENSE.txt'; Url='https://raw.githubusercontent.com/nodatime/nodatime/832acdf7e040478d0a2105f577884c97cb245d10/LICENSE.txt'; Hash='cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30'},
        @{Name='NodaTime-NOTICE.txt'; Url='https://raw.githubusercontent.com/nodatime/nodatime/832acdf7e040478d0a2105f577884c97cb245d10/NOTICE.txt'; Hash='b20873f8469e39d5d09bfbafa9ceb101d555cf8de61f8c2a31394161d3b4c6dd'},
        @{Name='nlohmann-json-LICENSE.MIT'; Url='https://raw.githubusercontent.com/nlohmann/json/v3.12.0/LICENSE.MIT'; Hash='46a65cffd1ea955132d95a8dd921640714a8d6b537d2e4e482d31145ae95b603'}
    )
    foreach ($taskSourceLicense in $taskSourceLicenses) {
        $taskLicensePath = Join-Path $taskNotice $taskSourceLicense.Name
        Invoke-WebRequest -Uri $taskSourceLicense.Url -OutFile $taskLicensePath
        if ((Get-FileHash -LiteralPath $taskLicensePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskSourceLicense.Hash) {
            throw "Pinned source license content changed: $($taskSourceLicense.Name)"
        }
    }
    if ($taskSetupCompiler = $taskNsis) { Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $taskSetupCompiler) 'COPYING') -Destination (Join-Path $taskNotice 'NSIS-COPYING.txt') }
    $taskNoticeText = @'
# Third-party notices

WorkFor uses .NET and Windows App SDK native/self-contained runtime distributions, Jint, Corvus.Text.Json.Validator and their transitive packages. Package license files, nuspec metadata and the restored dependency inventory are included in this directory and each isolated runtime folder. Upstream sources:

- .NET: https://github.com/dotnet/runtime (MIT; third-party notices bundled by the runtime)
- Windows App SDK: https://github.com/microsoft/WindowsAppSDK (MIT; package-specific notices apply)
- Jint: https://github.com/sebastienros/jint (BSD-2-Clause)
- Corvus.Text.Json.Validator: https://github.com/corvus-dotnet/Corvus.JsonSchema (Apache-2.0; package THIRD-PARTY-NOTICES and source LICENSE are preserved)
- NodaTime: https://github.com/nodatime/nodatime (Apache-2.0; source LICENSE and NOTICE are preserved)
- nlohmann/json: https://github.com/nlohmann/json (MIT)

The Setup executable uses NSIS 3.13 with zlib compression; its license texts are included. Build tools (CMake, MSVC, NSIS) are not required on the end-user machine. This distribution is unsigned; no signing certificate or signed status is claimed.
'@
    Set-Content -LiteralPath (Join-Path $taskPayload 'THIRD-PARTY-NOTICES.md') -Value $taskNoticeText -Encoding utf8
    Copy-Item -LiteralPath $taskLocks -Destination (Join-Path $taskPayload 'build-metadata') -Recurse
    if ($taskObserved) { $taskObserved | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskPayload 'build-metadata/toolchain-observed.json') -Encoding utf8 }
    $taskManifest = [ordered]@{
        schemaVersion = 1; version = $Version; runtime = '10.0.14'; architecture = 'win-x64'; signature = 'unsigned'
        prototypeToolchain = (-not [bool]$taskLatest); nativeUpgradeRequiresExplorerExit = $true
        paths = @{ host="$taskAppRelative/host/WorkFor.Host.exe"; script="$taskAppRelative/script/WorkFor.ScriptHost.exe"; ui="$taskAppRelative/ui/WorkFor.UI.exe"; native="$taskAppRelative/native/WorkFor.NativeBridge.exe" }
        files = @()
    }
    $taskManifest.sourceRevision = (& git -C $taskRoot rev-parse HEAD 2>$null)
    $taskManifest.sourceDirty = [bool](& git -C $taskRoot status --porcelain 2>$null)
    $taskManifest.files = @(Get-ChildItem -LiteralPath $taskPayload -Recurse -File | Sort-Object FullName | ForEach-Object {
        @{ path=[IO.Path]::GetRelativePath($taskPayload, $_.FullName).Replace('\','/'); length=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $taskManifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $taskPayload 'release.json') -Encoding utf8
    if (-not $SkipSmoke) {
        $taskVerify = Run-Captured (Join-Path $taskPayload 'WorkFor.exe') @('--verify') 90 | ConvertFrom-Json
        if (-not $taskVerify.valid) { throw 'Launcher verification did not succeed.' }
        $taskRemovalTest = Run-Captured (Join-Path $taskPayload 'WorkFor.exe') @('--self-test-removal') 30 | ConvertFrom-Json
        if (-not $taskRemovalTest.passed) { throw 'Owned removal inventory experiment did not succeed.' }
        $taskSmokeData = Join-Path $taskRoot ".local/package-smoke/$taskId"
        $taskSnapshot = Run-Captured (Join-Path $taskApp 'host/WorkFor.Host.exe') @('--once','--at','2026-10-30T11:00:00+08:00',
            '--data-dir',$taskSmokeData,'--script-host',(Join-Path $taskApp 'script/WorkFor.ScriptHost.exe')) 30 | ConvertFrom-Json
        if ($taskSnapshot.status -ne 'fresh' -or $taskSnapshot.amountMinor -ne '3200') { throw 'Portable self-contained Host/ScriptHost calculation smoke failed.' }
        Run-Captured (Join-Path $taskNativeOutput 'WorkFor.NativeBridge.exe') @('--self-test') 45 | Set-Content -LiteralPath (Join-Path $taskStage 'native-smoke.json') -Encoding utf8
    }
    $taskArtifacts = Join-Path $taskRoot 'artifacts'
    New-Item -ItemType Directory -Path $taskArtifacts -Force | Out-Null
    $taskSuffix = if ($taskLatest) { '' } else { '-prototype' }
    $taskArtifactName = "WorkFor-$Version-win-x64$taskSuffix-$($taskId.Substring(0,8))"
    $taskZip = Join-Path $taskArtifacts "$taskArtifactName.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($taskPayload, $taskZip, [IO.Compression.CompressionLevel]::Optimal, $false)

    if ($Setup) {
        $taskSetupFile = Join-Path $taskArtifacts "$taskArtifactName-Setup.exe"
        Invoke-Checked $taskNsis @('/V3','/INPUTCHARSET','UTF8',"/DPAYLOAD=$taskPayload","/DOUTFILE=$taskSetupFile","/DVERSION=$Version",(Join-Path $taskRoot 'installer/WorkFor.nsi'))
        if (-not (Test-Path -LiteralPath $taskSetupFile)) { throw 'NSIS did not produce the Setup executable.' }
        Write-Output "Setup: $taskSetupFile"
    }
    Write-Output "Portable: $taskZip"
    Write-Output "Extracted payload for verification: $taskPayload"
    Write-Output 'Unsigned; Setup compilation is not installation/uninstallation validation. No installer has been executed.'
}
finally { Pop-Location }
