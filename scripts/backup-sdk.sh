#!/usr/bin/env bash
set -euo pipefail

usage() {
    printf 'Usage: %s SDK_DIR DESTINATION_DIRECTORY\n' "$0" >&2
    printf 'Creates a portable .tar.zst SDK backup. Build output under out/ is excluded.\n' >&2
}

if [ "$#" -ne 2 ]; then
    usage
    exit 2
fi

SDK_INPUT=$1
DEST_INPUT=$2
if [ ! -f "$SDK_INPUT/build/envsetup.sh" ]; then
    printf '[FAIL] this is not a complete Tina SDK: %s\n' "$SDK_INPUT" >&2
    exit 1
fi
if [ ! -d "$DEST_INPUT" ]; then
    printf '[FAIL] destination directory does not exist: %s\n' "$DEST_INPUT" >&2
    exit 1
fi

SDK_DIR=$(cd -- "$SDK_INPUT" && pwd)
DEST_DIR=$(cd -- "$DEST_INPUT" && pwd)
SDK_PARENT=$(dirname -- "$SDK_DIR")
SDK_NAME=$(basename -- "$SDK_DIR")
STAMP=$(date +%Y%m%d)
ARCHIVE="$DEST_DIR/mosquito-t113-sdk-$STAMP.tar.zst"
PARTIAL="$ARCHIVE.partial"

if [ -e "$ARCHIVE" ] || [ -e "$PARTIAL" ]; then
    printf '[FAIL] output already exists: %s\n' "$ARCHIVE" >&2
    exit 1
fi

printf '[INFO] source:      %s\n' "$SDK_DIR"
printf '[INFO] destination: %s\n' "$ARCHIVE"
printf '[INFO] preserving source, .repo metadata, downloads and prebuilt toolchains\n'
printf '[INFO] excluding rebuildable directory: %s/out\n' "$SDK_NAME"

cleanup_partial() {
    if [ -f "$PARTIAL" ]; then
        rm -f -- "$PARTIAL"
    fi
}
trap cleanup_partial EXIT INT TERM

tar --exclude="$SDK_NAME/out" \
    --use-compress-program='zstd -T0 -10' \
    -cf "$PARTIAL" -C "$SDK_PARENT" "$SDK_NAME"
mv -- "$PARTIAL" "$ARCHIVE"
trap - EXIT INT TERM
sha256sum "$ARCHIVE" > "$ARCHIVE.sha256"

printf '[PASS] SDK backup created\n'
ls -lh -- "$ARCHIVE" "$ARCHIVE.sha256"
