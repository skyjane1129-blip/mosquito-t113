#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd -- "$SCRIPT_DIR/.." && pwd)
SDK_INPUT=${1:-${MOSQUITO_SDK:-$REPO_ROOT/tina-t113}}
JOBS=${MOSQUITO_JOBS:-$(nproc)}

if [ ! -f "$SDK_INPUT/build/envsetup.sh" ]; then
    printf '[FAIL] this is not a complete Tina SDK: %s\n' "$SDK_INPUT" >&2
    exit 1
fi
SDK_DIR=$(cd -- "$SDK_INPUT" && pwd)

"$SCRIPT_DIR/verify-sdk.sh" "$SDK_DIR"
cd -- "$SDK_DIR"
# shellcheck disable=SC1091
source build/envsetup.sh
lunch t113_mosquito-tina
make -j"$JOBS"
pack

printf '[PASS] Tina build and PhoenixCard packaging completed\n'
find "$SDK_DIR/out/t113-mosquito" -maxdepth 1 -type f -name '*.img' -print
