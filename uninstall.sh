#!/bin/sh
set -eu

ROOT=$(CDPATH= cd -P "$(dirname "$0")" && pwd)
GAME_DIR=${GAME_DIR:-"$ROOT/../The Elder Scrolls Legends"}
MANAGED="$GAME_DIR/The Elder Scrolls Legends_Data/Managed"
TARGET="$MANAGED/Assembly-CSharp.dll"
DATA="$MANAGED/NyxTESLOffline-data"
RETAIL_SHA='ab4b71b62a2dcf268fafc8decc26bd0eaa2e5c09679e566ad7f24f31ffc5fb0a'
LEGACY_BRIDGE13_SHA='dc5c522a428219162a57ecb97cbf1bb51973376a8a30a1f6f472b22fe01a37cf'
LEGACY_BRIDGE14_SHA='7b2e6ca90e7c48948159d64397ae19cdaae7769031dffa8cf5b6929018fc76b7'
BACKUP="$MANAGED/Assembly-CSharp.dll.nyx-original-$RETAIL_SHA"
STATE="$MANAGED/NyxTESLOffline.install-state"

fail() { echo "Error: $*" >&2; exit 1; }
hash_file() { sha256sum "$1" | awk '{print $1}'; }
state_value() { awk -F= -v key="$1" '$1 == key { sub(/^[^=]*=/, ""); print; found=1 } END { if (!found) exit 1 }' "$STATE"; }

[ -d "$MANAGED" ] || fail "Managed directory not found: $MANAGED"
[ -f "$TARGET" ] || fail "Assembly-CSharp.dll not found: $TARGET"
current=$(hash_file "$TARGET")

if [ -f "$STATE" ]; then
    [ "$(state_value state_version)" = 1 ] || fail 'invalid or incompatible installation manifest'
    installed_sha=$(state_value installed_sha) || fail 'installed hash missing from manifest'
    backup_file=$(state_value backup_file) || fail 'backup filename missing from manifest'
    restore_sha=$(state_value retail_sha256 || printf '%s' "$RETAIL_SHA")
    case "$restore_sha" in *[!0-9a-f]*|'') fail 'invalid retail hash in installation manifest' ;; esac
    [ "${#restore_sha}" -eq 64 ] || fail 'invalid retail hash in installation manifest'
    BACKUP="$MANAGED/$backup_file"
    [ "$backup_file" = "Assembly-CSharp.dll.nyx-original-$restore_sha" ] || fail 'manifest points to an unexpected backup; restoration refused'
    [ -f "$BACKUP" ] || fail "retail backup not found: $BACKUP"
    [ "$(hash_file "$BACKUP")" = "$restore_sha" ] || fail 'backup does not match retail hash in manifest'
    [ "$current" = "$installed_sha" ] || fail "Assembly-CSharp.dll changed since installation; refusing to overwrite it (hash $current)"
    created_data_dir=$(state_value created_data_dir) || fail 'created_data_dir missing'
    created_inventory=$(state_value created_inventory_seed) || fail 'created_inventory_seed missing'
    created_campaigns=$(state_value created_campaigns_seed) || fail 'created_campaigns_seed missing'
    created_test_decks=$(state_value created_test_decks_seed || printf '0')
    created_graph=$(state_value created_graph || printf '0')
    graph_sha=$(state_value graph_sha || printf '')
    created_fx=$(state_value created_fx || printf '0')
    fx_sha=$(state_value fx_sha || printf '')
    inventory_sha=$(state_value inventory_seed_sha) || fail 'inventory_seed_sha missing'
    campaigns_sha=$(state_value campaigns_seed_sha) || fail 'campaigns_seed_sha missing'
    test_decks_sha=$(state_value recovered_test_decks_seed_sha || printf '')
else
    restore_sha=$RETAIL_SHA
    [ -f "$BACKUP" ] || fail "retail backup not found: $BACKUP"
    [ "$(hash_file "$BACKUP")" = "$RETAIL_SHA" ] || fail 'backup is not the verified retail DLL'
    case "$current" in
        "$LEGACY_BRIDGE13_SHA"|"$LEGACY_BRIDGE14_SHA") ;;
        *) fail 'manifest missing and DLL does not match a known Bridge installation; restoration refused' ;;
    esac
    created_data_dir=0
    created_inventory=0
    created_campaigns=0
    created_test_decks=0
    inventory_sha=''
    campaigns_sha=''
    test_decks_sha=''
    echo 'Previous installation has no manifest: restoring only the DLL and preserving data.'
fi

tmp=$(mktemp "$MANAGED/.Assembly-CSharp.dll.restore.XXXXXX")
trap 'rm -f "$tmp"' EXIT HUP INT TERM
cp "$BACKUP" "$tmp"
[ "$(hash_file "$tmp")" = "$restore_sha" ] || fail 'temporary backup copy failed verification'
mv -f "$tmp" "$TARGET"
trap - EXIT HUP INT TERM

if [ -f "$STATE" ]; then
    if [ "$created_inventory" = 1 ] && [ -f "$DATA/inventory-seed.json" ]; then
        if [ "$(hash_file "$DATA/inventory-seed.json")" = "$inventory_sha" ]; then
            rm -f "$DATA/inventory-seed.json"
        else
            echo 'Preserving inventory-seed.json: file changed after installation.'
        fi
    fi
    if [ "$created_campaigns" = 1 ] && [ -f "$DATA/campaigns-seed.json" ]; then
        if [ "$(hash_file "$DATA/campaigns-seed.json")" = "$campaigns_sha" ]; then
            rm -f "$DATA/campaigns-seed.json"
        else
            echo 'Preserving campaigns-seed.json: file changed after installation.'
        fi
    fi
    if [ "$created_test_decks" = 1 ] && [ -f "$DATA/recovered-test-decks.state" ]; then
        if [ "$(hash_file "$DATA/recovered-test-decks.state")" = "$test_decks_sha" ]; then
            rm -f "$DATA/recovered-test-decks.state"
        else
            echo 'Preserving recovered-test-decks.state: file changed after installation.'
        fi
    fi
    if [ "$created_graph" = 1 ] && [ -f "$DATA/retail-rule-graphs.jsonl.gz" ]; then
        if [ "$(hash_file "$DATA/retail-rule-graphs.jsonl.gz")" = "$graph_sha" ]; then
            rm -f "$DATA/retail-rule-graphs.jsonl.gz"
        else
            echo 'Preserving retail-rule-graphs.jsonl.gz: file changed after installation.'
        fi
    fi
    if [ "$created_fx" = 1 ] && [ -f "$DATA/retail-rule-fx.tsv" ]; then
        if [ "$(hash_file "$DATA/retail-rule-fx.tsv")" = "$fx_sha" ]; then
            rm -f "$DATA/retail-rule-fx.tsv"
        else
            echo 'Preserving retail-rule-fx.tsv: file changed after installation.'
        fi
    fi
    rm -f "$STATE"
    if [ "$created_data_dir" = 1 ]; then
        rmdir "$DATA" 2>/dev/null || true
    fi
fi

[ "$(hash_file "$TARGET")" = "$restore_sha" ] || fail 'retail DLL restoration failed verification'
echo "NyxTESLOffline removed. Retail DLL restored at: $TARGET"
