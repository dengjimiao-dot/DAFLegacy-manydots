[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [switch] $RunTests
)

$ErrorActionPreference = "Stop"

$cmakeCandidates = @(
    $env:CMAKE_EXE,
    (Join-Path $env:ProgramFiles "CMake\bin\cmake.exe"),
    (Get-Command cmake.exe -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty Source)
)

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path -LiteralPath $vswhere) {
    $vsInstallPath = & $vswhere -latest -products * -property installationPath 2>$null |
        Select-Object -First 1
    if ($vsInstallPath) {
        $cmakeCandidates += Join-Path $vsInstallPath `
            "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
    }
}

$cmake = $cmakeCandidates |
    Where-Object { $_ -and (Test-Path -LiteralPath $_) } |
    Select-Object -First 1
if (-not $cmake) {
    throw "CMake 3.24 or newer is required to build DFLegacy.Ijl15."
}

$projectDirectory = $PSScriptRoot
$repositoryRoot = Split-Path (Split-Path $projectDirectory -Parent) -Parent
$buildDirectory = Join-Path $repositoryRoot "artifacts\native\ijl15"
$outputDirectory = Join-Path (Join-Path $buildDirectory $Configuration) "win-x86"

$cmakeHelp = & $cmake --help
$generator = @(
    "Visual Studio 18 2026",
    "Visual Studio 17 2022",
    "Visual Studio 16 2019"
) | Where-Object { $cmakeHelp -match [regex]::Escape($_) } |
    Select-Object -First 1
if (-not $generator) {
    throw "A Visual Studio C++ CMake generator is required."
}

$cachePath = Join-Path $buildDirectory "CMakeCache.txt"
if (Test-Path -LiteralPath $cachePath) {
    $cacheLines = Get-Content -LiteralPath $cachePath
    $cachedGenerator = $cacheLines |
        Where-Object { $_ -like "CMAKE_GENERATOR:INTERNAL=*" } |
        Select-Object -First 1
    # CMake 缓存内路径是正斜杠形式，比较前统一。
    $cachedHome = ($cacheLines |
        Where-Object { $_ -like "CMAKE_HOME_DIRECTORY:INTERNAL=*" } |
        Select-Object -First 1) -replace '\\', '/'
    $expectedHome = "CMAKE_HOME_DIRECTORY:INTERNAL=" + ($projectDirectory -replace '\\', '/')
    if ($cachedGenerator -ne "CMAKE_GENERATOR:INTERNAL=$generator" -or
        $cachedHome -ne $expectedHome) {
        $resolvedBuild = [IO.Path]::GetFullPath($buildDirectory)
        $resolvedArtifacts = [IO.Path]::GetFullPath(
            (Join-Path $repositoryRoot "artifacts"))
        if (-not $resolvedBuild.StartsWith(
                $resolvedArtifacts + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected build directory: $resolvedBuild"
        }
        Write-Host "[build-native] Stale CMake cache (generator or source moved); reconfiguring."
        Remove-Item -LiteralPath $resolvedBuild -Recurse -Force
    }
}

# 产物带 RID 维度落地：artifacts/native/ijl15/<config>/win-x86/
# （Launcher.csproj 的 Ijl15Binary 与 vcxproj 的 OutDir 都指向这里，两条构建路径收敛到同一位置）。
# 通过命令行 -D 更新缓存变量即可对旧缓存生效，无需清空重建。
& $cmake -S $projectDirectory -B $buildDirectory `
    -G $generator -A Win32 `
    "-DCMAKE_RUNTIME_OUTPUT_DIRECTORY_DEBUG=$outputDirectory" `
    "-DCMAKE_RUNTIME_OUTPUT_DIRECTORY_RELEASE=$outputDirectory"
if ($LASTEXITCODE -ne 0) {
    throw "CMake configuration failed with exit code $LASTEXITCODE."
}

& $cmake --build $buildDirectory --config $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "Native ijl15 build failed with exit code $LASTEXITCODE."
}

if ($RunTests) {
    & $cmake --build $buildDirectory --config $Configuration --target RUN_TESTS
    if ($LASTEXITCODE -ne 0) {
        throw "Native ijl15 tests failed with exit code $LASTEXITCODE."
    }
}
