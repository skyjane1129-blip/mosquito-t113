#!/usr/bin/env bash
set -u

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd -- "$SCRIPT_DIR/.." && pwd)
SDK_INPUT=${1:-${MOSQUITO_SDK:-$REPO_ROOT/tina-t113}}
FAILURES=0
WARNINGS=0

pass() { printf '[PASS] %s\n' "$*"; }
warn() { printf '[WARN] %s\n' "$*"; WARNINGS=$((WARNINGS + 1)); }
fail() { printf '[FAIL] %s\n' "$*"; FAILURES=$((FAILURES + 1)); }

if [ ! -f "$SDK_INPUT/build/envsetup.sh" ]; then
    fail "this is not a complete Tina SDK: $SDK_INPUT"
    exit 1
fi
SDK_DIR=$(cd -- "$SDK_INPUT" && pwd)
# shellcheck disable=SC1091
. "$REPO_ROOT/sdk/BASELINE.env"

printf 'Mosquito T113 SDK verification\n'
printf 'SDK: %s\n' "$SDK_DIR"

for REQUIRED in \
    build/envsetup.sh \
    device/config/chips/t113/configs \
    lichee/linux-5.4 \
    lichee/brandy-2.0/u-boot-2018 \
    package \
    target/allwinner; do
    if [ -e "$SDK_DIR/$REQUIRED" ]; then
        pass "SDK component present: $REQUIRED"
    else
        fail "SDK component missing: $REQUIRED"
    fi
done

while IFS= read -r -d '' SOURCE; do
    RELATIVE=${SOURCE#"$REPO_ROOT/tina-overlay/"}
    if [ "$RELATIVE" = MOSQUITO_BUILD.md ]; then
        continue
    fi
    TARGET="$SDK_DIR/$RELATIVE"
    if [ -L "$SOURCE" ]; then
        if [ -L "$TARGET" ] && [ "$(readlink "$SOURCE")" = "$(readlink "$TARGET")" ]; then
            :
        else
            fail "overlay link differs or is missing: $RELATIVE"
        fi
    elif [ -f "$TARGET" ] && cmp -s "$SOURCE" "$TARGET"; then
        :
    else
        fail "overlay file differs or is missing: $RELATIVE"
    fi
done < <(find "$REPO_ROOT/tina-overlay" \( -type f -o -type l \) -print0)
if [ "$FAILURES" -eq 0 ]; then
    pass 'all Mosquito overlay files match the SDK'
fi

while IFS='|' read -r PROJECT PATCH_FILE PURPOSE; do
    case "$PROJECT" in
        ''|'#'*) continue ;;
    esac
    if git -C "$SDK_DIR/$PROJECT" apply --reverse --check "$REPO_ROOT/$PATCH_FILE" >/dev/null 2>&1; then
        pass "SDK patch present: $PURPOSE"
    else
        fail "SDK patch missing or different: $PATCH_FILE"
    fi
done < "$REPO_ROOT/sdk-patches/series"

COMPILER_BIN="$SDK_DIR/prebuilt/gcc/linux-x86/arm/toolchain-sunxi-musl/toolchain/bin/arm-openwrt-linux-muslgnueabi-gcc-6.4.1.bin"
if [ -x "$COMPILER_BIN" ]; then
    ACTUAL_COMPILER_SHA=$(sha256sum "$COMPILER_BIN" | awk '{print $1}')
    if [ "$ACTUAL_COMPILER_SHA" = "$MOSQUITO_TOOLCHAIN_SHA256" ]; then
        pass "toolchain checksum matches GCC $MOSQUITO_TOOLCHAIN_VERSION baseline"
    else
        warn 'toolchain exists but checksum differs from the validated baseline'
    fi
else
    fail "validated compiler binary missing: $COMPILER_BIN"
fi

check_optional_hash() {
    FILE=$1
    EXPECTED=$2
    LABEL=$3
    if [ ! -f "$FILE" ]; then
        warn "$LABEL is unavailable; exact SDK identity cannot be checked"
        return
    fi
    ACTUAL=$(sha256sum "$FILE" | awk '{print $1}')
    if [ "$ACTUAL" = "$EXPECTED" ]; then
        pass "$LABEL checksum matches baseline"
    else
        warn "$LABEL checksum differs from baseline"
    fi
}
check_optional_hash "$SDK_DIR/.repo/manifest.xml" "$MOSQUITO_MANIFEST_XML_SHA256" 'repo manifest selector'
check_optional_hash "$SDK_DIR/.repo/manifests/tina-d1-h.xml" "$MOSQUITO_TINA_MANIFEST_SHA256" 'Tina manifest'

TARGET_DEFCONFIG="$SDK_DIR/target/allwinner/t113-mosquito/defconfig"
if grep -q '^CONFIG_PACKAGE_lrzsz=y$' "$TARGET_DEFCONFIG" && \
   grep -q '^CONFIG_PACKAGE_mosquito-board-test=y$' "$TARGET_DEFCONFIG"; then
    pass 'target enables lrzsz and mosquito-board-test'
else
    fail 'target defconfig does not enable the required development packages'
fi

if [ "$FAILURES" -eq 0 ]; then
    printf '[READY] SDK verification passed with %d warning(s)\n' "$WARNINGS"
    exit 0
fi
printf '[NOT READY] %d failure(s), %d warning(s)\n' "$FAILURES" "$WARNINGS"
exit 1
