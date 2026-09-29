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
    throw "CMake 3.24 or newer is required to build DFLegacy.RandomNative."
}

$projectDirectory = $PSScriptRoot
$repositoryRoot = Split-Path (Split-Path $projectDirectory -Parent) -Parent
$buildDirectory = Join-Path $repositoryRoot "artifacts\native\random"

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
    $cachedGenerator = Get-Content -LiteralPath $cachePath |
        Where-Object { $_ -like "CMAKE_GENERATOR:INTERNAL=*" } |
        Select-Object -First 1
    $cachedPlatform = Get-Content -LiteralPath $cachePath |
        Where-Object { $_ -like "CMAKE_GENERATOR_PLATFORM:INTERNAL=*" } |
        Select-Object -First 1
    if ($cachedGenerator -ne "CMAKE_GENERATOR:INTERNAL=$generator" -or
        $cachedPlatform -ne "CMAKE_GENERATOR_PLATFORM:INTERNAL=x64") {
        $resolvedBuild = [IO.Path]::GetFullPath($buildDirectory)
        $resolvedArtifacts = [IO.Path]::GetFullPath(
            (Join-Path $repositoryRoot "artifacts"))
        if (-not $resolvedBuild.StartsWith(
                $resolvedArtifacts + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected build directory: $resolvedBuild"
        }

        Remove-Item -LiteralPath $resolvedBuild -Recurse -Force
    }
}

& $cmake -S $projectDirectory -B $buildDirectory `
    -G $generator -A x64
if ($LASTEXITCODE -ne 0) {
    throw "CMake configuration failed with exit code $LASTEXITCODE."
}

& $cmake --build $buildDirectory --config $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "Native random bridge build failed with exit code $LASTEXITCODE."
}

if ($RunTests) {
    & $cmake --build $buildDirectory --config $Configuration --target RUN_TESTS
    if ($LASTEXITCODE -ne 0) {
        throw "Native random bridge tests failed with exit code $LASTEXITCODE."
    }
}

