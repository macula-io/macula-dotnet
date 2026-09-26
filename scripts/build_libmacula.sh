#!/usr/bin/env bash
# Builds libmacula for this machine from a macula-go checkout, into
# native/runtimes/<rid>/native/ where the Macula project packs and copies it
# from: for working against a macula-go revision that has no release yet.
# Released libraries come from scripts/fetch_libmacula.sh instead.
#
#   scripts/build_libmacula.sh <macula-go checkout>
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/.." && pwd)
macula_go=$(cd "$1" && pwd)

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) rid=linux-x64 file=libmacula.so ;;
  Linux-aarch64) rid=linux-arm64 file=libmacula.so ;;
  Darwin-x86_64) rid=osx-x64 file=libmacula.dylib ;;
  Darwin-arm64) rid=osx-arm64 file=libmacula.dylib ;;
  *) echo "build_libmacula.sh: no runtime for $(uname -s)-$(uname -m)" >&2; exit 2 ;;
esac

out="$root/native/runtimes/$rid/native"
mkdir -p "$out"
(cd "$macula_go" && CGO_ENABLED=1 go build -buildmode=c-shared -trimpath -o "$out/$file" ./cabi)
rm -f "$out/${file%.*}.h"
echo "built $out/$file from $(git -C "$macula_go" rev-parse --short HEAD)"
