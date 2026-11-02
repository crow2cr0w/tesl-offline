#!/bin/sh
set -eu
export LC_ALL=C
ROOT=$(CDPATH= cd -P "$(dirname "$0")" && pwd)
PACKAGE="$ROOT/../mod-tels"
fail() { echo "Error: $*" >&2; exit 1; }
[ -s "$ROOT/build/manifest.txt" ] || fail 'run ./build.sh before packaging'
expected=$(awk -F= '$1 == "patched_sha256" { print $2 }' "$ROOT/build/manifest.txt")
[ "$(sha256sum "$ROOT/build/Assembly-CSharp.dll" | awk '{print $1}')" = "$expected" ] || fail 'build SHA-256 does not match its manifest'
mkdir -p "$PACKAGE/tools" "$PACKAGE/docs" "$PACKAGE/data"
# The sibling is an installable precompiled package, separate from source.
cp "$ROOT/docs/PRECOMPILED.en.md" "$PACKAGE/README.md"
cp "$ROOT/docs/PRECOMPILED.pt-BR.md" "$PACKAGE/README.pt-BR.md"
# README.md is the English README; do not create a duplicate README.en.md.
# Only release documentation belongs in the gameplay package.
cp "$ROOT/docs/PUBLICATION.md" "$PACKAGE/docs/PUBLICATION.md"
for file in inventory-seed.json campaigns-seed.json retail-rule-graphs.jsonl.gz retail-rule-fx.tsv; do
    cp "$ROOT/data/$file" "$PACKAGE/data/$file"
done
cp "$ROOT/build/Assembly-CSharp.dll" "$PACKAGE/"
cp "$ROOT/build/manifest.txt" "$PACKAGE/manifest.txt"
cp "$ROOT/LICENSE" "$PACKAGE/LICENSE"
mkdir -p "$PACKAGE/licenses"
cp "$ROOT/vendor-src/HarmonyX/LICENSE" "$PACKAGE/licenses/HarmonyX-MIT.txt"
cp "$ROOT/vendor-src/HarmonyX/LICENSE.Harmony" "$PACKAGE/licenses/Harmony-MIT.txt"
cp "$ROOT/vendor-src/MonoMod/LICENSE" "$PACKAGE/licenses/MonoMod-MIT.txt"
cp "$ROOT/vendor-src/Mono.Cecil/LICENSE.txt" "$PACKAGE/licenses/Mono.Cecil-MIT.txt"
cp "$ROOT/vendor-src/MonoMod/MonoMod.Common/LICENSE" "$PACKAGE/licenses/MonoMod.Common-MIT.txt"
cp "$ROOT/licenses/Microsoft.Cci.Pdb.txt" "$PACKAGE/licenses/Microsoft.Cci.Pdb.txt"
cp "$ROOT/install.sh" "$ROOT/uninstall.sh" "$ROOT/install.ps1" "$ROOT/uninstall.ps1" "$PACKAGE/"
cp "$ROOT/tools/manage-install.ps1" "$PACKAGE/tools/"
# Headless diagnostics remain in the source module, not the gameplay package.
# Compilation and patching tools belong exclusively to mod-tels-src.
(cd "$PACKAGE" && find Assembly-CSharp.dll manifest.txt README.md README.pt-BR.md LICENSE licenses install.sh uninstall.sh install.ps1 uninstall.ps1 data tools docs -type f -print | LC_ALL=C sort | while IFS= read -r file; do sha256sum "$file"; done > SHA256SUMS)
(cd "$PACKAGE" && sha256sum -c SHA256SUMS)
echo "Precompiled package ready: $PACKAGE"
