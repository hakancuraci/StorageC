#!/usr/bin/env bash
set -euo pipefail

artifact_dir="${1:-artifacts/linux-x64}"
maximum_glibc="${2:-2.17}"

if [[ ! -d "$artifact_dir" ]]; then
  echo "Native artifact directory does not exist: $artifact_dir" >&2
  exit 2
fi

version_is_greater() {
  local candidate="$1"
  local maximum="$2"
  [[ "$candidate" != "$maximum" ]] &&
    [[ "$(printf '%s\n' "$candidate" "$maximum" | sort -V | tail -n 1)" == "$candidate" ]]
}

files=()
while IFS= read -r -d '' file; do
  files+=("$file")
done < <(find "$artifact_dir" -maxdepth 1 -type f \
  \( -name 'libstoragec_native.so' -o -name 'libsmb2.so*' \) -print0)

if (( ${#files[@]} == 0 )); then
  echo "No Linux native libraries found in $artifact_dir." >&2
  exit 2
fi

failed=0
for file in "${files[@]}"; do
  highest="none"
  while IFS= read -r version; do
    [[ -z "$version" ]] && continue
    highest="$version"
    if version_is_greater "$version" "$maximum_glibc"; then
      echo "ERROR: $(basename "$file") requires GLIBC_$version (maximum allowed: GLIBC_$maximum_glibc)." >&2
      failed=1
    fi
  done < <(
    readelf --version-info --wide "$file" |
      grep -oE 'GLIBC_[0-9]+(\.[0-9]+)*' |
      sed 's/^GLIBC_//' |
      sort -Vu || true
  )
  echo "$(basename "$file"): highest required GLIBC version is $highest"
done

exit "$failed"
