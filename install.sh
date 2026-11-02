#!/bin/sh
set -eu

ROOT=$(CDPATH= cd -P "$(dirname "$0")" && pwd)
GAME_DIR=${GAME_DIR:-"$ROOT/../The Elder Scrolls Legends"}
MANAGED="$GAME_DIR/The Elder Scrolls Legends_Data/Managed"
TARGET="$MANAGED/Assembly-CSharp.dll"
if [ -d "$ROOT/src" ]; then
    BUILT="$ROOT/build/Assembly-CSharp.dll"
    BUILD_MANIFEST="$ROOT/build/manifest.txt"
else
    BUILT="$ROOT/Assembly-CSharp.dll"
    BUILD_MANIFEST="$ROOT/manifest.txt"
fi
RETAIL_SHA='ab4b71b62a2dcf268fafc8decc26bd0eaa2e5c09679e566ad7f24f31ffc5fb0a'
BRIDGE13_SHA='dc5c522a428219162a57ecb97cbf1bb51973376a8a30a1f6f472b22fe01a37cf'
BRIDGE14_SHA='7b2e6ca90e7c48948159d64397ae19cdaae7769031dffa8cf5b6929018fc76b7'
BACKUP="$MANAGED/Assembly-CSharp.dll.nyx-original-$RETAIL_SHA"
STATE="$MANAGED/NyxTESLOffline.install-state"

fail() { echo "Error: $*" >&2; exit 1; }
hash_file() { sha256sum "$1" | awk '{print $1}'; }
state_value() { awk -F= -v key="$1" '$1 == key { sub(/^[^=]*=/, ""); print; found=1 } END { if (!found) exit 1 }' "$STATE"; }
manifest_value() { awk -F= -v key="$1" '$1 == key { sub(/^[^=]*=/, ""); print; found=1 } END { if (!found) exit 1 }' "$BUILD_MANIFEST"; }

[ -d "$MANAGED" ] || fail "Managed directory not found: $MANAGED"
[ -f "$TARGET" ] || fail "Assembly-CSharp.dll not found: $TARGET"
[ -s "$BUILT" ] || fail "build missing; run $ROOT/build.sh first"
[ -s "$BUILD_MANIFEST" ] || fail "build manifest not found: $BUILD_MANIFEST"
build_retail_sha=$(manifest_value retail_sha256) || fail 'retail hash missing from build manifest'
build_patched_sha=$(manifest_value patched_sha256) || fail 'patched hash missing from build manifest'
case "$build_retail_sha" in *[!0-9a-f]*|'') fail 'invalid retail hash in build manifest' ;; esac
[ "${#build_retail_sha}" -eq 64 ] || fail 'invalid retail hash in build manifest'
mod_sha=$(hash_file "$BUILT")
[ "$mod_sha" = "$build_patched_sha" ] || fail 'built Assembly-CSharp.dll SHA-256 does not match the manifest'
current=$(hash_file "$TARGET")
BACKUP="$MANAGED/Assembly-CSharp.dll.nyx-original-$build_retail_sha"

created_data_dir=0
created_inventory=0
created_campaigns=0
had_state=0
if [ -f "$STATE" ]; then
    had_state=1
    state_version=$(state_value state_version) || fail 'invalid installation state'
    [ "$state_version" = 1 ] || fail "unsupported installation state version: $state_version"
    previous_installed=$(state_value installed_sha) || fail 'hash missing from installation state'
    [ "$current" = "$previous_installed" ] || fail "installed DLL changed since last installation (expected $previous_installed, got $current)"
    created_data_dir=$(state_value created_data_dir) || fail 'created_data_dir missing from installation state'
    created_inventory=$(state_value created_inventory_seed) || fail 'inventory state missing'
    created_campaigns=$(state_value created_campaigns_seed) || fail 'campaigns state missing'
    previous_backup_file=$(state_value backup_file) || fail 'backup file missing from installation state'
    [ "$previous_backup_file" = "$(basename "$BACKUP")" ] || fail 'build and installation refer to different retail DLLs'
    [ -f "$BACKUP" ] || fail "retail backup missing: $BACKUP"
    [ "$(hash_file "$BACKUP")" = "$build_retail_sha" ] || fail 'existing retail backup SHA-256 does not match installation state'
fi

if [ "$had_state" -eq 0 ]; then case "$current" in
    "$build_retail_sha")
        if [ -e "$BACKUP" ]; then
            [ "$(hash_file "$BACKUP")" = "$build_retail_sha" ] || fail "existing backup SHA-256 mismatch: $BACKUP"
        else
            cp -p "$TARGET" "$BACKUP"
        fi
        ;;
    "$BRIDGE13_SHA"|"$BRIDGE14_SHA"|"$mod_sha")
        [ -e "$BACKUP" ] || fail 'previous patch found without a verified retail backup; restore the game before installing'
        [ "$(hash_file "$BACKUP")" = "$build_retail_sha" ] || fail 'existing retail backup does not match the build reference DLL'
        ;;
    *) fail "game DLL does not match the build base (game $current; base $build_retail_sha). Rebuild for this installation." ;;
esac; fi

DATA="$MANAGED/NyxTESLOffline-data"
if [ ! -d "$DATA" ]; then
    mkdir -p "$DATA"
    created_data_dir=1
fi
for pair in 'inventory-seed.json:created_inventory' 'campaigns-seed.json:created_campaigns'; do
    seed=${pair%%:*}
    flag=${pair#*:}
    if [ ! -e "$DATA/$seed" ]; then
        cp "$ROOT/data/$seed" "$DATA/$seed"
        case "$flag" in
            created_inventory) created_inventory=1 ;;
            created_campaigns) created_campaigns=1 ;;
        esac
    fi
done
graph_source_sha=$(hash_file "$ROOT/data/retail-rule-graphs.jsonl.gz")
created_graph=0
if [ "$had_state" -eq 1 ]; then created_graph=$(state_value created_graph || printf '0'); fi
if [ -f "$DATA/retail-rule-graphs.jsonl.gz" ]; then
    previous_graph_sha=$(state_value graph_sha || printf '%s' "$graph_source_sha")
    [ "$(hash_file "$DATA/retail-rule-graphs.jsonl.gz")" = "$previous_graph_sha" ] || fail 'installed retail rule graph was modified; refusing to overwrite'
else created_graph=1; fi
cp "$ROOT/data/retail-rule-graphs.jsonl.gz" "$DATA/retail-rule-graphs.jsonl.gz"
fx_source_sha=$(hash_file "$ROOT/data/retail-rule-fx.tsv")
created_fx=0
if [ "$had_state" -eq 1 ]; then created_fx=$(state_value created_fx || printf '0'); fi
if [ -f "$DATA/retail-rule-fx.tsv" ]; then
    previous_fx_sha=$(state_value fx_sha || printf '%s' "$fx_source_sha")
    [ "$(hash_file "$DATA/retail-rule-fx.tsv")" = "$previous_fx_sha" ] || fail 'installed retail FX mapping was modified; refusing to overwrite'
else created_fx=1; fi
cp "$ROOT/data/retail-rule-fx.tsv" "$DATA/retail-rule-fx.tsv"

if [ "$current" != "$mod_sha" ]; then
    tmp=$(mktemp "$MANAGED/.Assembly-CSharp.dll.nyx.XXXXXX")
    trap 'rm -f "$tmp"' EXIT HUP INT TERM
    cp "$BUILT" "$tmp"
    [ "$(hash_file "$tmp")" = "$mod_sha" ] || fail 'temporary copy SHA-256 mismatch'
    mv -f "$tmp" "$TARGET"
    trap - EXIT HUP INT TERM
fi

state_tmp=$(mktemp "$MANAGED/.NyxTESLOffline.install-state.XXXXXX")
trap 'rm -f "$state_tmp"' EXIT HUP INT TERM
{
    printf '%s\n' 'state_version=1'
    printf 'installed_sha=%s\n' "$mod_sha"
    printf '%s\n' "backup_file=$(basename "$BACKUP")"
    printf 'retail_sha256=%s\n' "$build_retail_sha"
    printf 'created_data_dir=%s\n' "$created_data_dir"
    printf 'created_inventory_seed=%s\n' "$created_inventory"
    printf 'created_campaigns_seed=%s\n' "$created_campaigns"
    printf 'created_graph=%s\n' "$created_graph"
    printf 'graph_sha=%s\n' "$graph_source_sha"
    printf 'created_fx=%s\n' "$created_fx"
    printf 'fx_sha=%s\n' "$fx_source_sha"
    printf 'inventory_seed_sha=%s\n' "$(hash_file "$ROOT/data/inventory-seed.json")"
    printf 'campaigns_seed_sha=%s\n' "$(hash_file "$ROOT/data/campaigns-seed.json")"
} > "$state_tmp"
mv -f "$state_tmp" "$STATE"
trap - EXIT HUP INT TERM

[ "$(hash_file "$TARGET")" = "$mod_sha" ] || fail 'final DLL verification failed'
echo "NyxTESLOffline installed in: $GAME_DIR"
echo "Assembly-CSharp.dll SHA-256: $mod_sha"
echo "Retail DLL backup: $BACKUP"
echo "Uninstall state: $STATE"
