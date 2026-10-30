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
        & (Join-Path $PSScriptRoot 'restore-native-deps.ps1') | Out-Null
        $taskVswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        $taskInstallations = & $taskVswhere -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json | ConvertFrom-Json
        $taskVs = $taskInstallations | Sort-Object { [version]$_.installationVersion } -Descending | Select-Object -First 1
        if (-not $taskVs) { throw 'MSVC x64 Build Tools are required.' }
        $taskCurrentVs = [version]$taskVs.installationVersion
        $taskCmakeVersion = ((& cmake --version)[0] -replace '^cmake version ', '').Trim()
        $taskVsProductVersion = [version]$taskVs.catalog.productDisplayVersion
        $taskLatest = $taskVsProductVersion -ge [version]'18.11.2' -and [version]$taskCmakeVersion -ge [version]'4.4.5'
        if (-not $taskLatest -and -not $PrototypeToolchain) {
            throw 'Latest baseline requires VS 2026 18.11.2+ and CMake 4.4.5+. Use -PrototypeToolchain only for local research with an explicit non-release stamp.'
        }
        $taskGenerator = if ($taskCurrentVs.Major -ge 18) { 'Visual Studio 18 2026' } else { 'Visual Studio 17 2022' }
        & cmake -S . -B out/cmake-native -G $taskGenerator -A x64
        if ($LASTEXITCODE -ne 0) { throw 'Native configure failed.' }
        & cmake --build out/cmake-native --config $Configuration --parallel 4
        if ($LASTEXITCODE -ne 0) { throw 'Native build failed. Do not overwrite a DLL currently pinned in Explorer; choose a new build directory or sign out first.' }
        @{ sdk = (& $taskDotnet --version); visualStudio = $taskVs.installationVersion; cmake = $taskCmakeVersion; latestToolchainBaselineMet = $taskLatest } |
            ConvertTo-Json | Set-Content -LiteralPath 'out/toolchain-observed.json' -Encoding utf8
        if ($Test) {
            & ctest --test-dir out/cmake-native -C $Configuration --output-on-failure
            if ($LASTEXITCODE -ne 0) { throw 'Native owned-target tests failed.' }
        }
    }
    if ($Test) {
        foreach ($taskProject in @('WorkFor.Core.Tests', 'WorkFor.ScriptHost.Tests', 'WorkFor.Host.Tests')) {
            & $taskDotnet test --project "tests/$taskProject/$taskProject.csproj" -c $Configuration --no-build
            if ($LASTEXITCODE -ne 0) { throw "Tests failed: $taskProject" }
        }
    }
}
finally { Pop-Location }
