#!/usr/bin/env bash
set -euo pipefail

configuration="${1:-Release}"
case "$configuration" in
  Release|Debug) ;;
  *)
    echo "Configuration must be Release or Debug." >&2
    exit 2
    ;;
esac

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
build="$root/work/native-linux-x64-glibc217"
output="$root/artifacts/linux-x64"
install_lib="$output/lib"

cmake -S "$root/native" -B "$build" \
  -DCMAKE_BUILD_TYPE="$configuration" \
  -DCMAKE_INSTALL_PREFIX="$output" \
  -DINSTALL_LIB_DIR="$install_lib" \
  -DINSTALL_INC_DIR="$output/include" \
  -DINSTALL_PKGCONFIG_DIR="$install_lib/pkgconfig" \
  -DINSTALL_CMAKE_DIR="$install_lib/cmake/libsmb2"
cmake --build "$build" --config "$configuration" --parallel
cmake --install "$build" --config "$configuration" --prefix "$output"

# NuGet only needs the SONAME requested by libstoragec_native.so. Materialize
# it as a regular file so a Windows host can pack Docker-produced artifacts.
runtime_smb="$output/libsmb2.so.7"
runtime_smb_copy="$output/.libsmb2.so.7"
cp -L "$runtime_smb" "$runtime_smb_copy"
mv -f "$runtime_smb_copy" "$runtime_smb"

bash "$root/tests/verify-linux-glibc.sh" "$output" 2.17
