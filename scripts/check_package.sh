#!/usr/bin/env bash
# Packs Macula and refuses a package that does not carry libmacula for every
# runtime it promises, each file the one scripts/fetch_libmacula.sh verified.
#
#   scripts/check_package.sh [output dir]
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/.." && pwd)
out=${1:-"$root/artifacts"}

rm -rf "$out"
dotnet pack "$root/src/Macula/Macula.csproj" --configuration Release --output "$out"
package=$(find "$out" -name 'Macula.*.nupkg' ! -name '*.snupkg' | head -n 1)
if [ -z "$package" ]; then
  echo "check_package.sh: no Macula package in $out" >&2
  exit 1
fi

listing=$(unzip -Z1 "$package")
status=0
for path in runtimes/linux-x64/native/libmacula.so runtimes/linux-arm64/native/libmacula.so \
  runtimes/osx-x64/native/libmacula.dylib runtimes/osx-arm64/native/libmacula.dylib \
  runtimes/win-x64/native/libmacula.dll; do
  if ! grep -qx "$path" <<<"$listing"; then
    echo "check_package.sh: $package lacks $path" >&2
    status=1
    continue
  fi
  packed=$(unzip -p "$package" "$path" | sha256sum | awk '{print $1}')
  fetched=$(sha256sum "$root/native/$path" | awk '{print $1}')
  if [ "$packed" != "$fetched" ]; then
    echo "check_package.sh: $path in the package is not the verified file" >&2
    status=1
  fi
done
[ "$status" -eq 0 ] && echo "$(basename "$package") carries libmacula for all five runtimes"
exit "$status"
