#!/usr/bin/env bash

# Source this file so the exported variables remain in the current shell:
#   source scripts/setup-env.sh /path/to/tina-t113

if [ -z "${BASH_VERSION:-}" ]; then
    printf '[FAIL] setup-env.sh requires Bash\n' >&2
    return 1 2>/dev/null || exit 1
fi

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd -- "$SCRIPT_DIR/.." && pwd)
SDK_INPUT=${1:-${MOSQUITO_SDK:-$REPO_ROOT/tina-t113}}

if [ ! -d "$SDK_INPUT" ]; then
    printf '[FAIL] Tina SDK directory not found: %s\n' "$SDK_INPUT" >&2
    return 1 2>/dev/null || exit 1
fi

SDK_DIR=$(cd -- "$SDK_INPUT" && pwd)
TOOLCHAIN_BIN="$SDK_DIR/prebuilt/gcc/linux-x86/arm/toolchain-sunxi-musl/toolchain/bin"
COMPILER="$TOOLCHAIN_BIN/arm-openwrt-linux-muslgnueabi-gcc"

if [ ! -x "$COMPILER" ]; then
    printf '[FAIL] ARM/musl compiler not found: %s\n' "$COMPILER" >&2
    return 1 2>/dev/null || exit 1
fi

export MOSQUITO_SDK="$SDK_DIR"
export TINA_BUILD_TOP="$SDK_DIR"
export STAGING_DIR="$SDK_DIR/out/t113-mosquito/staging_dir/target"
export CROSS_COMPILE='arm-openwrt-linux-muslgnueabi-'
case ":$PATH:" in
    *":$TOOLCHAIN_BIN:"*) ;;
    *) export PATH="$TOOLCHAIN_BIN:$PATH" ;;
esac
hash -r

printf '[READY] Mosquito development environment configured\n'
printf 'TINA_BUILD_TOP=%s\n' "$TINA_BUILD_TOP"
printf 'STAGING_DIR=%s\n' "$STAGING_DIR"
printf 'Compiler=%s\n' "$(command -v arm-openwrt-linux-muslgnueabi-gcc)"
