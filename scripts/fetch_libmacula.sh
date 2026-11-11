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
# written against: abi/macula.h is the release's own header at the release
# abi/MACULA_GO_REF names, the binding reads the ABI and the wire out of it
# when it builds (Macula.csproj, GenerateMaculaAbi), and it binds
# cabi/macula.h's declarations. So the two files are the same file, not merely
# two that agree on two numbers.
if ! cmp -s "$download/macula.h" "$root/abi/macula.h"; then
  echo "fetch_libmacula.sh: $tag's macula.h is not abi/macula.h (abi/MACULA_GO_REF)" >&2
  exit 1
fi
header_abi=$(awk '$1 == "#define" && $2 == "MACULA_ABI_VERSION" { print $3 }' "$download/macula.h")
# The wire the release speaks. The whole string is compared, where the load
# path refuses another major only (AcceptsWire, NativeCall): a pin names one
# macula-go release, and what a build lays out under native/ has to be the
# release the binding was written and verified against, not merely one of the
# same wire major, which is all a library a caller puts beside the package has
# to be.
header_wire=$(awk '$1 == "#define" && $2 == "MACULA_WIRE_VERSION" { gsub(/"/, "", $3); print $3 }' "$download/macula.h")
echo "verified macula.h ($tag): ABI $header_abi, wire $header_wire, the header abi/macula.h holds"

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
