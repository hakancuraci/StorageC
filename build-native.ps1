param(
    [ValidateSet('win-x64', 'linux-x64')]
    [string]$RuntimeIdentifier = $(if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'win-x64' } else { 'linux-x64' }),
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$build = Join-Path $root "work/native-$RuntimeIdentifier"
$output = Join-Path $root "artifacts/$RuntimeIdentifier"
$installLib = Join-Path $output 'lib'
$installInclude = Join-Path $output 'include'
$installPkgConfig = Join-Path $installLib 'pkgconfig'
$installCMake = Join-Path $installLib 'cmake/libsmb2'

cmake -S (Join-Path $root 'native') -B $build `
    -DCMAKE_BUILD_TYPE=$Configuration `
    -DCMAKE_INSTALL_PREFIX=$output `
    -DINSTALL_LIB_DIR=$installLib `
    -DINSTALL_INC_DIR=$installInclude `
    -DINSTALL_PKGCONFIG_DIR=$installPkgConfig `
    -DINSTALL_CMAKE_DIR=$installCMake
if ($LASTEXITCODE -ne 0) { throw 'CMake configure failed' }
cmake --build $build --config $Configuration --parallel
if ($LASTEXITCODE -ne 0) { throw 'Native build failed' }
cmake --install $build --config $Configuration --prefix $output
if ($LASTEXITCODE -ne 0) { throw 'Native install failed' }
