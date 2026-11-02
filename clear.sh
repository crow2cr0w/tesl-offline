#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -P "$(dirname "$0")" && pwd)
for target in "$SCRIPT_DIR/build" "$SCRIPT_DIR/tools/runtime-embed"; do
    if [ -d "$target" ] || [ -L "$target" ]; then
        rm -rf -- "$target"
        printf 'Build artifacts removed: %s\n' "$target"
    else
        printf 'No build artifacts found in: %s\n' "$target"
    fi
done
