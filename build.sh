#!/bin/sh
set -eu
export LC_ALL=C

MCS=${MCS:-mcs}
MONO=${MONO:-mono}
SCRIPT_DIR=$(CDPATH= cd -P "$(dirname "$0")" && pwd)
GAME_DIR=${GAME_DIR:-"$SCRIPT_DIR/../The Elder Scrolls Legends"}
MANAGED="$GAME_DIR/The Elder Scrolls Legends_Data/Managed"
ASSEMBLY="$MANAGED/Assembly-CSharp.dll"
BACKUP="$MANAGED/Assembly-CSharp.dll.nyx-original-ab4b71b62a2dcf268fafc8decc26bd0eaa2e5c09679e566ad7f24f31ffc5fb0a"
CORE="$SCRIPT_DIR/tools/runtime-embed"
BUILD="$SCRIPT_DIR/build"
REFS="$BUILD/refs"
EXPECTED_SHA256='ab4b71b62a2dcf268fafc8decc26bd0eaa2e5c09679e566ad7f24f31ffc5fb0a'

need() { command -v "$1" >/dev/null 2>&1 || { echo "error: required command not found: $1" >&2; exit 1; }; }
need "$MCS"
need "$MONO"
need sha256sum


USE_CACHED_REFS=0
if [ -r "$ASSEMBLY" ]; then
    actual=$(sha256sum "$ASSEMBLY" | awk '{print $1}')
    RETAIL_SOURCE="$ASSEMBLY"
elif [ "${NYX_USE_CACHED_REFS:-0}" = 1 ] && [ -r "$REFS/Assembly-CSharp.dll" ] &&
    [ "$(sha256sum "$REFS/Assembly-CSharp.dll" | awk '{print $1}')" = "$EXPECTED_SHA256" ]; then
    actual="$EXPECTED_SHA256"
    RETAIL_SOURCE="$REFS/Assembly-CSharp.dll"
    USE_CACHED_REFS=1
    echo "info: game directory unavailable; using verified cached retail references"
else
    echo "error: cannot read official Assembly-CSharp.dll:" >&2
    echo "  $ASSEMBLY" >&2
    echo "Place mod-tels-src beside the The Elder Scrolls Legends folder." >&2
    echo "Cached references require explicit NYX_USE_CACHED_REFS=1; they do not verify the target installation." >&2
    exit 1
fi
if [ "$actual" != "$EXPECTED_SHA256" ]; then
    if [ -r "$BACKUP" ]; then
        backup_hash=$(sha256sum "$BACKUP" | awk '{print $1}')
        if [ "$backup_hash" = "$EXPECTED_SHA256" ]; then
            RETAIL_SOURCE="$BACKUP"
            actual="$backup_hash"
            echo "info: current game assembly is modified; building from verified retail backup"
            echo "  $BACKUP"
        elif [ "${NYX_ALLOW_UNKNOWN_ASSEMBLY:-0}" != 1 ]; then
            echo "error: retail backup hash mismatch" >&2
            echo " expected: $EXPECTED_SHA256" >&2
            echo " backup:   $backup_hash" >&2
            exit 1
        fi
    elif [ "${NYX_ALLOW_UNKNOWN_ASSEMBLY:-0}" != 1 ]; then
        echo "error: Assembly-CSharp.dll hash mismatch and no verified retail backup exists" >&2
        echo " expected: $EXPECTED_SHA256" >&2
        echo " current:  $actual" >&2
        echo "Refusing to stack the patch on an already-modified/unknown assembly." >&2
        exit 1
    fi
fi

# Build dependencies only after verifying the game assembly or cached references.
sh "$SCRIPT_DIR/tools/build-runtime-deps.sh"

if [ "$USE_CACHED_REFS" -eq 1 ]; then
    rm -f "$BUILD/NyxTESLOffline.Payload.dll" \
        "$BUILD/NyxInPlacePatcher.exe" "$BUILD/Assembly-CSharp.dll" \
        "$BUILD/manifest.txt"
else
    rm -rf "$BUILD"
    mkdir -p "$REFS"
    cp -f "$RETAIL_SOURCE" "$REFS/Assembly-CSharp.dll"
    [ -r "$MANAGED/Google.Protobuf.dll" ] && cp -f "$MANAGED/Google.Protobuf.dll" "$REFS/Google.Protobuf.dll"
    for f in "$MANAGED"/UnityEngine*.dll; do
        [ -r "$f" ] || continue
        cp -f "$f" "$REFS/"
    done
fi
found_unity=0
for f in "$REFS"/UnityEngine*.dll; do
    [ -r "$f" ] || continue
    found_unity=1
done
[ "$found_unity" -eq 1 ] || { echo "error: no UnityEngine*.dll found under $MANAGED" >&2; exit 1; }

set --
for f in "$REFS"/UnityEngine*.dll; do
    [ -r "$f" ] || continue
    set -- "$@" "-r:$f"
done
if [ -r "$REFS/Google.Protobuf.dll" ]; then
    set -- "$@" "-r:$REFS/Google.Protobuf.dll"
fi

printf '%s\n' '[1/3] compiling embedded Nyx payload'
"$MCS" -sdk:2 -target:library -optimize+ \
    -out:"$BUILD/NyxTESLOffline.Payload.dll" \
    -r:"$REFS/Assembly-CSharp.dll" \
    -r:"$CORE/0Harmony.dll" \
    "$@" \
    "$SCRIPT_DIR"/src/NyxTESLOffline/*.cs \
    "$SCRIPT_DIR"/src/NyxTESLOffline/Patches/*.cs

printf '%s\n' '[2/3] compiling Mono.Cecil in-place patcher'
"$MCS" -sdk:4 -target:exe -optimize+ \
    -out:"$BUILD/NyxInPlacePatcher.exe" \
    -r:"$CORE/Mono.Cecil.dll" \
    "$SCRIPT_DIR/patcher/NyxInPlacePatcher.cs"

printf '%s\n' '[3/3] embedding payload/dependencies and injecting bootstrap'
MONO_PATH="$CORE${MONO_PATH:+:$MONO_PATH}" \
"$MONO" "$BUILD/NyxInPlacePatcher.exe" \
    "$REFS/Assembly-CSharp.dll" \
    "$BUILD/Assembly-CSharp.dll" \
    "$BUILD/NyxTESLOffline.Payload.dll" \
    "$CORE/0Harmony.dll" \
    "$CORE/Mono.Cecil.dll" \
    "$CORE/Mono.Cecil.Rocks.dll" \
    "$CORE/MonoMod.Utils.dll" \
    "$CORE/MonoMod.RuntimeDetour.dll"

[ -s "$BUILD/Assembly-CSharp.dll" ] || { echo 'error: patched assembly was not produced' >&2; exit 1; }
patched=$(sha256sum "$BUILD/Assembly-CSharp.dll" | awk '{print $1}')
cat > "$BUILD/manifest.txt" <<EOF
NyxTESLOffline 0.6.0-inplace-alpha14-bridge18
retail_sha256=$actual
patched_sha256=$patched
bootstrap=SparkleServices.ServicesManager.Init
direct_il_patch=SPTESL.ServerCardCatalog.TransferCardToZone
payload=embedded
harmony=embedded
steam_auth=local
bepinex_runtime=no
doorstop_runtime=no
EOF

printf '\nBuilt successfully.\n'
printf '  retail:  %s\n' "$actual"
printf '  patched: %s\n' "$patched"
printf '  output:  %s\n' "$BUILD/Assembly-CSharp.dll"
# Produce or update the precompiled sibling package after a successful build.
sh "$SCRIPT_DIR/package.sh"
printf '\nBuild and packaging completed successfully. Next, run: ./install.sh\n'
