#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd -- "$SCRIPT_DIR/.." && pwd)
SDK_INPUT=${1:-${MOSQUITO_SDK:-$REPO_ROOT/tina-t113}}
OUTPUT=${2:-$REPO_ROOT/apps/hello-t113/build/hello-t113}

if [ ! -d "$SDK_INPUT" ]; then
    printf '[FAIL] Tina SDK directory not found: %s\n' "$SDK_INPUT" >&2
    exit 1
fi

SDK_DIR=$(cd -- "$SDK_INPUT" && pwd)
COMPILER="$SDK_DIR/prebuilt/gcc/linux-x86/arm/toolchain-sunxi-musl/toolchain/bin/arm-openwrt-linux-muslgnueabi-gcc"
SOURCE="$REPO_ROOT/apps/hello-t113/hello-t113.c"

if [ ! -x "$COMPILER" ]; then
    printf '[FAIL] compiler not found: %s\n' "$COMPILER" >&2
    exit 1
fi

mkdir -p -- "$(dirname -- "$OUTPUT")"
export STAGING_DIR="$SDK_DIR/out/t113-mosquito/staging_dir/target"
"$COMPILER" -Os -s -o "$OUTPUT" "$SOURCE"

printf '[PASS] built %s\n' "$OUTPUT"
file "$OUTPUT"
md5sum "$OUTPUT"
