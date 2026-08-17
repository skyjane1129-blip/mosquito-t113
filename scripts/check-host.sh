#!/usr/bin/env bash
set -u

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd -- "$SCRIPT_DIR/.." && pwd)
SDK_DIR=${1:-${MOSQUITO_SDK:-$REPO_ROOT/tina-t113}}
FAILURES=0
WARNINGS=0

pass() { printf '[PASS] %s\n' "$*"; }
warn() { printf '[WARN] %s\n' "$*"; WARNINGS=$((WARNINGS + 1)); }
fail() { printf '[FAIL] %s\n' "$*"; FAILURES=$((FAILURES + 1)); }

printf 'Mosquito T113 development-host check\n'
printf 'Repository: %s\n' "$REPO_ROOT"
printf 'SDK:        %s\n' "$SDK_DIR"

if [ "$(uname -s)" = Linux ]; then
    pass 'Linux host detected'
else
    fail 'Run the Tina build inside Ubuntu/Linux, not directly in Windows'
fi

if [ "$(uname -m)" = x86_64 ]; then
    pass 'x86_64 host architecture detected'
else
    warn "host architecture is $(uname -m); the supplied Tina toolchain was validated on x86_64"
fi

for COMMAND in bash git make gcc g++ gawk flex bison perl python3 unzip file \
    rsync patch sha256sum tar zstd; do
    if command -v "$COMMAND" >/dev/null 2>&1; then
        pass "host command available: $COMMAND"
    else
        fail "missing host command: $COMMAND"
    fi
done

if [ -r /etc/os-release ]; then
    # shellcheck disable=SC1091
    . /etc/os-release
    printf '[INFO] host OS: %s\n' "${PRETTY_NAME:-unknown}"
fi

TOOLCHAIN_BIN="$SDK_DIR/prebuilt/gcc/linux-x86/arm/toolchain-sunxi-musl/toolchain/bin"
COMPILER="$TOOLCHAIN_BIN/arm-openwrt-linux-muslgnueabi-gcc"
if [ -x "$COMPILER" ]; then
    VERSION=$($COMPILER -dumpversion 2>/dev/null || true)
    if [ "$VERSION" = 6.4.1 ]; then
        pass 'Tina ARM/musl GCC 6.4.1 is executable'
    else
        warn "unexpected Tina compiler version: ${VERSION:-unknown}"
    fi
    if command -v ldd >/dev/null 2>&1 && ldd "$COMPILER" 2>/dev/null | grep -q 'not found'; then
        fail 'the Tina compiler has missing host shared libraries'
    fi
else
    fail "compiler not found: $COMPILER"
fi

if [ "$FAILURES" -eq 0 ]; then
    printf '[READY] host check passed with %d warning(s)\n' "$WARNINGS"
    exit 0
fi

printf '[NOT READY] %d failure(s), %d warning(s)\n' "$FAILURES" "$WARNINGS"
exit 1
