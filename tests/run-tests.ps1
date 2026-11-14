$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot 'StorageC.Tests/StorageC.Tests.csproj'
$output = Join-Path $PSScriptRoot 'StorageC.Tests/bin/Release/net8.0'

dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }

if ($IsWindows -or $env:OS -eq 'Windows_NT') {
    gcc -shared -O2 -Wall -Wextra -Werror -o (Join-Path $output 'storagec_native.dll') (Join-Path $PSScriptRoot 'nativefixture/storagec_native_fixture.c')
} else {
    gcc -shared -fPIC -O2 -Wall -Wextra -Werror -o (Join-Path $output 'libstoragec_native.so') (Join-Path $PSScriptRoot 'nativefixture/storagec_native_fixture.c')
}
if ($LASTEXITCODE -ne 0) { throw 'native fixture build failed' }

dotnet run --project $project -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'tests failed' }
