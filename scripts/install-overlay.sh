#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd -- "$SCRIPT_DIR/.." && pwd)

usage() {
    printf 'Usage: %s SDK_DIR [--apply]\n' "$0" >&2
    printf 'Without --apply, only validates that the SDK can accept this overlay.\n' >&2
}

if [ "$#" -lt 1 ] || [ "$#" -gt 2 ]; then
    usage
    exit 2
fi

SDK_INPUT=$1
MODE=${2:---check}
if [ "$MODE" != '--check' ] && [ "$MODE" != '--apply' ]; then
    usage
    exit 2
fi
if [ ! -f "$SDK_INPUT/build/envsetup.sh" ]; then
    printf '[FAIL] this is not a complete Tina SDK: %s\n' "$SDK_INPUT" >&2
    exit 1
fi

SDK_DIR=$(cd -- "$SDK_INPUT" && pwd)
SERIES="$REPO_ROOT/sdk-patches/series"
PENDING=0

printf '[INFO] validating SDK patches before changing files\n'
while IFS='|' read -r PROJECT PATCH_FILE PURPOSE; do
    case "$PROJECT" in
        ''|'#'*) continue ;;
    esac
    PROJECT_DIR="$SDK_DIR/$PROJECT"
    PATCH_PATH="$REPO_ROOT/$PATCH_FILE"
    if [ ! -d "$PROJECT_DIR/.git" ] && ! git -C "$PROJECT_DIR" rev-parse --git-dir >/dev/null 2>&1; then
        printf '[FAIL] SDK git project missing: %s\n' "$PROJECT_DIR" >&2
        exit 1
    fi
    if git -C "$PROJECT_DIR" apply --reverse --check "$PATCH_PATH" >/dev/null 2>&1; then
        printf '[PASS] already applied: %s\n' "$PURPOSE"
    elif git -C "$PROJECT_DIR" apply --check "$PATCH_PATH" >/dev/null 2>&1; then
        printf '[READY] can apply: %s\n' "$PURPOSE"
        PENDING=$((PENDING + 1))
    else
        printf '[FAIL] patch does not match SDK baseline: %s\n' "$PATCH_FILE" >&2
        exit 1
    fi
done < "$SERIES"

if [ "$MODE" = '--check' ]; then
    printf '[READY] overlay contains %s files/links; %d SDK patch(es) would be applied\n' \
        "$(find "$REPO_ROOT/tina-overlay" \( -type f -o -type l \) ! -name MOSQUITO_BUILD.md | wc -l)" \
        "$PENDING"
    printf '[INFO] run again with --apply to install it\n'
    exit 0
fi

printf '[INFO] copying Mosquito overlay into %s\n' "$SDK_DIR"
rsync -a --exclude='/MOSQUITO_BUILD.md' "$REPO_ROOT/tina-overlay/" "$SDK_DIR/"

while IFS='|' read -r PROJECT PATCH_FILE PURPOSE; do
    case "$PROJECT" in
        ''|'#'*) continue ;;
    esac
    PROJECT_DIR="$SDK_DIR/$PROJECT"
    PATCH_PATH="$REPO_ROOT/$PATCH_FILE"
    if git -C "$PROJECT_DIR" apply --reverse --check "$PATCH_PATH" >/dev/null 2>&1; then
        printf '[PASS] patch already present: %s\n' "$PURPOSE"
    else
        git -C "$PROJECT_DIR" apply "$PATCH_PATH"
        printf '[PASS] patch applied: %s\n' "$PURPOSE"
    fi
done < "$SERIES"

"$SCRIPT_DIR/verify-sdk.sh" "$SDK_DIR"
printf '[PASS] Mosquito overlay installation completed\n'
