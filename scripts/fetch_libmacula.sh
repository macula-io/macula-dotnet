#!/usr/bin/env bash
# Fetches libmacula for every runtime from the macula-go release named in
# libmacula.version, checks each file against the release's SHA256SUMS and
# its GitHub build provenance attestation, and lays them out under
# native/runtimes/<rid>/native/ with the names .NET loads. A file that fails
# either check is never used (macula-go's cabi/CONTRACT.md "Release artifacts").
#
#   scripts/fetch_libmacula.sh
#
# Needs gh, authenticated (attestation verification asks GitHub's API).
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/.." && pwd)
repo=macula-io/macula-go
tag=$(tr -d '[:space:]' < "$root/libmacula.version")
download=$(mktemp -d)
trap 'rm -rf "$download"' EXIT

gh release download "$tag" --repo "$repo" --dir "$download" \
  --pattern 'libmacula-*' --pattern 'macula-windows-x64.dll' --pattern 'macula.h' --pattern 'SHA256SUMS'
(cd "$download" && sha256sum --check --strict SHA256SUMS)
gh attestation verify "$download/macula.h" --repo "$repo" > /dev/null
# The header the release was built from must be the one this binding is
# written against: Native/Libmacula.cs binds cabi/macula.h's ABI.
header_abi=$(awk '$1 == "#define" && $2 == "MACULA_ABI_VERSION" { print $3 }' "$download/macula.h")
binding_abi=$(awk -F'= ' '/internal const int AbiVersion/ { gsub(";", "", $2); print $2 }' "$root/src/Macula/Native/Libmacula.cs")
if [ "$header_abi" != "$binding_abi" ]; then
  echo "fetch_libmacula.sh: $tag's macula.h is ABI $header_abi, the binding is ABI $binding_abi" >&2
  exit 1
fi
echo "verified macula.h ($tag): ABI $header_abi"

# release file, runtime id, the name .NET loads
while read -r file rid name; do
  gh attestation verify "$download/$file" --repo "$repo" > /dev/null
  mkdir -p "$root/native/runtimes/$rid/native"
  cp "$download/$file" "$root/native/runtimes/$rid/native/$name"
  echo "verified $file ($tag) -> native/runtimes/$rid/native/$name"
done <<'EOF'
libmacula-linux-x64.so linux-x64 libmacula.so
libmacula-linux-arm64.so linux-arm64 libmacula.so
libmacula-macos-x64.dylib osx-x64 libmacula.dylib
libmacula-macos-arm64.dylib osx-arm64 libmacula.dylib
macula-windows-x64.dll win-x64 libmacula.dll
EOF
