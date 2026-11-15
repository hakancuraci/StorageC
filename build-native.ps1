param(
    [ValidateSet('win-x64', 'linux-x64')]
    [string]$RuntimeIdentifier = $(if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'win-x64' } else { 'linux-x64' }),
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if ($RuntimeIdentifier -eq 'linux-x64') {
    # Build against the oldest maintained manylinux baseline instead of the
    # runner's libc. The digest keeps release ABI compatibility reproducible.
    $image = if ($env:STORAGEC_MANYLINUX_IMAGE) {
        $env:STORAGEC_MANYLINUX_IMAGE
    } else {
        'quay.io/pypa/manylinux2014_x86_64@sha256:e6a785657009c612fe2a85fa933051d03a50ba62b1c5e47d62abcab546a2e423'
    }

    & docker version --format '{{.Server.Version}}' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Docker is required to build the glibc 2.17 compatible Linux native libraries.' }

    $dockerArguments = @('run', '--rm')
    if ($IsLinux) {
        $uid = & id -u
        $gid = & id -g
        if ($LASTEXITCODE -ne 0) { throw 'Could not determine the current Linux user.' }
        $dockerArguments += @('--user', "${uid}:${gid}")
    }
    $dockerArguments += @(
        '--env', 'HOME=/tmp',
        '--mount', "type=bind,source=$root,target=/src",
        '--workdir', '/src',
        $image,
        'bash', './build-native-linux.sh', $Configuration
    )

    & docker @dockerArguments
    if ($LASTEXITCODE -ne 0) { throw 'Containerized Linux native build failed.' }
    exit 0
}

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
