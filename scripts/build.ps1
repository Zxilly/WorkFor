[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$PrototypeToolchain,
    [switch]$SkipNative,
    [switch]$Test
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) { & (Join-Path $PSScriptRoot 'bootstrap-dotnet.ps1') | Out-Null }
Push-Location $taskRoot
try {
    & $taskDotnet restore WorkFor.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked dependency restore failed.' }
    & $taskDotnet build WorkFor.slnx -c $Configuration --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Managed build failed.' }
    if (-not $SkipNative) {
        $taskCmake = & (Join-Path $PSScriptRoot 'bootstrap-cmake.ps1')
        $taskCtest = Join-Path (Split-Path -Parent $taskCmake) 'ctest.exe'
        & (Join-Path $PSScriptRoot 'restore-native-deps.ps1') | Out-Null
        $taskVswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        $taskInstallations = & $taskVswhere -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json | ConvertFrom-Json
        $taskVs = $taskInstallations | Sort-Object { [version]$_.installationVersion } -Descending | Select-Object -First 1
        if (-not $taskVs) { throw 'MSVC x64 Build Tools are required.' }
        $taskCurrentVs = [version]$taskVs.installationVersion
        $taskCmakeVersion = ((& $taskCmake --version)[0] -replace '^cmake version ', '').Trim()
        $taskVsProductVersion = [version]$taskVs.catalog.productDisplayVersion
        $taskLatest = $taskVsProductVersion -ge [version]'18.11.2' -and [version]$taskCmakeVersion -ge [version]'4.4.5'
        if (-not $taskLatest -and -not $PrototypeToolchain) {
            throw 'Latest baseline requires VS 2026 18.11.2+ and CMake 4.4.5+. Use -PrototypeToolchain only for local research with an explicit non-release stamp.'
        }
        $taskGenerator = if ($taskCurrentVs.Major -ge 18) { 'Visual Studio 18 2026' } else { 'Visual Studio 17 2022' }
        & $taskCmake -S . -B out/cmake-native -G $taskGenerator -A x64
        if ($LASTEXITCODE -ne 0) { throw 'Native configure failed.' }
        [xml]$taskNativeProject = Get-Content -Raw -LiteralPath 'out/cmake-native/WorkFor.NativeBridge.vcxproj'
        $taskSelectedSdk = $taskNativeProject.SelectSingleNode("//*[local-name()='WindowsTargetPlatformVersion']").InnerText
        $taskLatest = $taskLatest -and [version]$taskSelectedSdk -ge [version]'10.0.28000.0'
        if (-not $taskLatest -and -not $PrototypeToolchain) { throw "Native SDK $taskSelectedSdk does not meet the pinned 28000 baseline." }
        & $taskCmake --build out/cmake-native --config $Configuration --parallel 4
        if ($LASTEXITCODE -ne 0) { throw 'Native build failed. Do not overwrite a DLL currently pinned in Explorer; choose a new build directory or sign out first.' }
        $taskToolchain = @{ sdk = (& $taskDotnet --version); visualStudio = $taskVs.installationVersion;
            cmake = $taskCmakeVersion; windowsSdk = $taskSelectedSdk; latestToolchainBaselineMet = $taskLatest }
        $taskToolchain | ConvertTo-Json | Set-Content -LiteralPath 'out/toolchain-observed.json' -Encoding utf8
        $taskToolchain | ConvertTo-Json | Set-Content -LiteralPath "out/cmake-native/bin/$Configuration/toolchain-observed.json" -Encoding utf8
        if ($Test) {
            & $taskCtest --test-dir out/cmake-native -C $Configuration --output-on-failure
            if ($LASTEXITCODE -ne 0) { throw 'Native owned-target tests failed.' }
        }
    }
    if ($Test) {
        foreach ($taskProject in @('WorkFor.Core.Tests', 'WorkFor.ScriptHost.Tests', 'WorkFor.Host.Tests', 'WorkFor.Persistence.Tests', 'WorkFor.Validation.Tests')) {
            & $taskDotnet test --project "tests/$taskProject/$taskProject.csproj" -c $Configuration --no-build
            if ($LASTEXITCODE -ne 0) { throw "Tests failed: $taskProject" }
        }
        & $taskDotnet run --project experiments/ui/WorkFor.UI.ProtocolProbe.csproj -c $Configuration --no-launch-profile
        if ($LASTEXITCODE -ne 0) { throw 'UI protocol and parameter-model checks failed.' }
    }
}
finally { Pop-Location }
