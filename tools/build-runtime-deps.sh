#!/bin/sh
# Build the five embedded runtime libraries from the pinned vendored sources.
# Requires Mono (mcs) and the Roslyn compiler distributed with the .NET SDK.
# This build does not download packages or use NuGet.
set -eu
export LC_ALL=C

ROOT=$(CDPATH= cd -P "$(dirname "$0")/.." && pwd)
VENDOR="$ROOT/vendor-src"
OUT="$ROOT/tools/runtime-embed"
MCS=${MCS:-mcs}
DOTNET=${DOTNET:-dotnet}

fail() { printf 'error: %s\n' "$*" >&2; exit 1; }
command -v "$MCS" >/dev/null 2>&1 || fail 'Mono C# compiler (mcs) not found'
command -v "$DOTNET" >/dev/null 2>&1 || fail '.NET SDK (dotnet) not found'
command -v sha256sum >/dev/null 2>&1 || fail 'sha256sum not found'

sdk_line=$($DOTNET --list-sdks | tail -n 1)
[ -n "$sdk_line" ] || fail 'no .NET SDK installed (dotnet --list-sdks is empty)'
sdk_version=${sdk_line%% *}
sdk_base=$(printf '%s\n' "$sdk_line" | sed -n 's/^[^[]*\[\([^]]*\)\].*$/\1/p')
ROSLYN="$sdk_base/$sdk_version/Roslyn/bincore/csc.dll"
[ -f "$ROSLYN" ] || fail "Roslyn compiler not found: $ROSLYN"

REFDIR=${MONO_REFERENCE_DIR:-}
if [ -z "$REFDIR" ]; then
    for candidate in /usr/lib/mono/3.5-api /usr/lib/mono/2.0-api; do
        if [ -f "$candidate/mscorlib.dll" ]; then REFDIR=$candidate; break; fi
    done
fi
[ -f "$REFDIR/mscorlib.dll" ] || fail 'Mono CLR 2.x references not found; set MONO_REFERENCE_DIR (3.5-api or 2.0-api)'
# The original embedded assemblies target CLR 2.x. Do not silently switch to CLR 4.x.
if [ -f "$REFDIR/System.Core.dll" ]; then
    CORE_REF="$REFDIR/System.Core.dll"
elif [ -f /usr/lib/mono/3.5-api/System.Core.dll ]; then
    CORE_REF=/usr/lib/mono/3.5-api/System.Core.dll
else
    fail 'System.Core.dll (.NET 3.5) not found'
fi

TMP=$(mktemp -d "$ROOT/tools/.runtime-build.XXXXXXXX")
trap 'rm -rf -- "$TMP"' EXIT HUP INT TERM

# Local assembly references: CLR 2.x / .NET 3.5 and built dependencies.
set -- -nologo -langversion:latest -target:library -optimize+ -nostdlib+
for lib in mscorlib System System.Xml System.Data System.Configuration \
    System.Runtime.Serialization Microsoft.CSharp System.Numerics; do
    [ -f "$REFDIR/$lib.dll" ] && set -- "$@" "\"-r:$REFDIR/$lib.dll\""
done
set -- "$@" "\"-r:$CORE_REF\""
printf '%s\n' "$@" > "$TMP/common.rsp"

# Generate response files for Roslyn. Source paths are quoted to handle spaces.
add_sources() {
    target=$1
    shift
    for dir in "$@"; do
        [ -d "$dir" ] || fail "missing vendored directory: $dir"
        find "$dir" -type f -name '*.cs' -print | LC_ALL=C sort | while IFS= read -r source; do
            printf '"%s"\n' "$source"
        done >> "$target"
    done
}

CE="$VENDOR/Mono.Cecil"
MM="$VENDOR/MonoMod"
HX="$VENDOR/HarmonyX"

printf '%s\n' '[runtime 1/5] Mono.Cecil 0.10.4'
"$MCS" -sdk:2 -target:library -optimize+ -define:NET_3_5 \
    -keyfile:"$CE/cecil.snk" -out:"$TMP/Mono.Cecil.dll" \
    "$CE/ProjectInfo.cs" \
    "$CE"/Mono/*.cs "$CE"/Mono.Cecil/*.cs \
    "$CE"/Mono.Cecil.Cil/*.cs "$CE"/Mono.Cecil.Metadata/*.cs \
    "$CE"/Mono.Cecil.PE/*.cs "$CE"/Mono.Collections.Generic/*.cs \
    "$CE"/Mono.Security.Cryptography/*.cs

printf '%s\n' '[runtime 2/5] Mono.Cecil.Rocks 0.10.4'
"$MCS" -sdk:2 -target:library -optimize+ -define:NET_3_5,INSIDE_ROCKS \
    -r:"$CORE_REF" \
    -keyfile:"$CE/cecil.snk" -out:"$TMP/Mono.Cecil.Rocks.dll" \
    -r:"$TMP/Mono.Cecil.dll" \
    "$CE/ProjectInfo.cs" "$CE"/rocks/Mono.Cecil.Rocks/*.cs

# These projects share source folders, as specified by their upstream .csproj.
printf '%s\n' '[runtime 3/5] MonoMod.Utils 22.1.29.1'
cat > "$TMP/monomod-version.cs" <<'EOF'
using System.Reflection;
[assembly: AssemblyVersion("22.1.29.1")]
[assembly: AssemblyFileVersion("22.1.29.1")]
EOF
cp "$TMP/common.rsp" "$TMP/utils.rsp"
printf '%s\n' '-unsafe+' '-define:NET35,NETFRAMEWORK,NETFRAMEWORK3,CECIL0_10,MONOMOD_UTILS' >> "$TMP/utils.rsp"
for arg in "-out:$TMP/MonoMod.Utils.dll" "-r:$TMP/Mono.Cecil.dll" \
    "-r:$TMP/Mono.Cecil.Rocks.dll" "$TMP/monomod-version.cs"; do
    printf '"%s"\n' "$arg" >> "$TMP/utils.rsp"
done
add_sources "$TMP/utils.rsp" "$MM/MonoMod.Utils" "$MM/MonoMod.Common/Utils" "$MM/MonoMod.Common/Shared"
"$DOTNET" "$ROSLYN" "@$TMP/utils.rsp"

printf '%s\n' '[runtime 4/5] MonoMod.RuntimeDetour 22.1.29.1'
cp "$TMP/common.rsp" "$TMP/detour.rsp"
printf '%s\n' '-unsafe+' '-define:NET35,NETFRAMEWORK,NETFRAMEWORK3,CECIL0_10,MONOMOD_RUNTIMEDETOUR' >> "$TMP/detour.rsp"
for arg in "-out:$TMP/MonoMod.RuntimeDetour.dll" "-r:$TMP/Mono.Cecil.dll" \
    "-r:$TMP/Mono.Cecil.Rocks.dll" "-r:$TMP/MonoMod.Utils.dll" \
    "$TMP/monomod-version.cs"; do
    printf '"%s"\n' "$arg" >> "$TMP/detour.rsp"
done
add_sources "$TMP/detour.rsp" "$MM/MonoMod.RuntimeDetour" "$MM/MonoMod.Common/RuntimeDetour" "$MM/MonoMod.Common/Shared"
"$DOTNET" "$ROSLYN" "@$TMP/detour.rsp"

printf '%s\n' '[runtime 5/5] HarmonyX 2.9.0'
cat > "$TMP/harmony-version.cs" <<'EOF'
using System.Reflection;
[assembly: AssemblyVersion("2.9.0.0")]
[assembly: AssemblyFileVersion("2.9.0.0")]
EOF
cp "$TMP/common.rsp" "$TMP/harmony.rsp"
printf '%s\n' '-unsafe+' '-define:NET35' >> "$TMP/harmony.rsp"
for arg in "-out:$TMP/0Harmony.dll" "-r:$TMP/Mono.Cecil.dll" \
    "-r:$TMP/Mono.Cecil.Rocks.dll" "-r:$TMP/MonoMod.Utils.dll" \
    "-r:$TMP/MonoMod.RuntimeDetour.dll" "$TMP/harmony-version.cs"; do
    printf '"%s"\n' "$arg" >> "$TMP/harmony.rsp"
done
add_sources "$TMP/harmony.rsp" "$HX/Harmony"
"$DOTNET" "$ROSLYN" "@$TMP/harmony.rsp"

for name in Mono.Cecil.dll Mono.Cecil.Rocks.dll MonoMod.Utils.dll \
    MonoMod.RuntimeDetour.dll 0Harmony.dll; do
    [ -s "$TMP/$name" ] || fail "compiler did not create $name"
done
mkdir -p "$OUT"
for name in Mono.Cecil.dll Mono.Cecil.Rocks.dll MonoMod.Utils.dll \
    MonoMod.RuntimeDetour.dll 0Harmony.dll; do
    cp -f -- "$TMP/$name" "$OUT/$name"
done
(cd "$ROOT" && for name in Mono.Cecil.dll Mono.Cecil.Rocks.dll \
    MonoMod.Utils.dll MonoMod.RuntimeDetour.dll 0Harmony.dll; do
    sha256sum "tools/runtime-embed/$name"
done) > "$OUT/SHA256SUMS"
(cd "$ROOT" && sha256sum -c "$OUT/SHA256SUMS")
printf '%s\n' "Runtime libraries built from vendored source: $OUT"
