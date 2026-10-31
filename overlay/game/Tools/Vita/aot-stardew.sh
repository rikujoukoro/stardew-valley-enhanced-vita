#!/usr/bin/env bash
# AOT-compiles assemblies for the Vita with the cross compiler (build-mono-cross.sh) into objects that
# build-stardew.sh links into the eboot (AOT_DIR=/root/vita/build-aot bash build-stardew.sh ...).
# Run inside WSL as root.
#
#   aot-stardew.sh <game folder> [assembly ...]
#
# <game folder> is the staged ux0:data/StardewValley (stage-stardew.ps1's vita-install\StardewValley,
# as a /mnt/c path): Mono uses an image only for the very assembly it was compiled from (same GUID),
# so these must be the files that will run. The class libraries and FNA.dll are taken from where
# build-stardew.sh takes them. Assemblies are named as Mono names them (no extension); by default all
# of the game's and the runtime's that the game loads.
#
# The code is compiled the way the Vita runtime runs: explicit null checks (no SIGSEGV on the Vita)
# and, from the cross compiler's configuration, GC safepoints for cooperative suspend.
#
# Profile-guided: AOT_PROFILE=<file> (stardew.aotprofile, recorded on the device with the aot-profile
# switch, see stardew/main.c) and PROFILE_ONLY="System:System.Xml:Stardew Valley" (':'-separated)
# compile only the profiled methods of those assemblies; everything else they contain stays JIT.
# Fully compiled, the game's and the runtime's assemblies come to ~50 MB of eboot.
set -euo pipefail

export VITASDK=/usr/local/vitasdk
export PATH="$VITASDK/bin:$PATH"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CROSS=/root/vita/mono-cross/bin/mono-sgen
HOST=/root/vita/mono-host
FNA_DLL="$HERE/../../../FNA/bin/Release/net4.0/FNA.dll"
OUT=/root/vita/build-aot
LIBS="$OUT/assemblies"

GAME="${1:?usage: aot-stardew.sh <game folder> [assembly ...]}"
shift
# The default leaves out FNA and xTile (drawing code that runs thousands of times a frame) and, with a
# profile, the game's per-frame code: Mono's AOT code checks static constructors on every static field
# access and can't inline across them, which made per-frame code about twice as slow as the JIT's.
if [ $# -gt 0 ]; then
	ASSEMBLIES=("$@")
else
	ASSEMBLIES=(mscorlib System System.Core System.Xml StardewValley.GameData BmFont "Stardew Valley")
fi
# Classes of the game whose profiled methods are precompiled (GAME_AOT_CLASSES overrides): code that
# runs rarely and stalls the first time it is compiled (tools: per swing, not per frame).
DEFAULT_GAME_AOT_CLASSES='^(StardewValley\.Menus\.|StardewValley\.Tools\.|Microsoft\.Xml\.Serialization\.GeneratedAssembly\.|StardewValley\.SaveSerialization|StardewValley\.SaveMigrations|StardewValley\.Events\.|StardewValley\.Quests\.|StardewValley\.SpecialOrders|StardewValley\.Minigames\.|StardewValley\.Triggers|StardewValley\.Internal\.|StardewValley\.ItemTypeDefinitions\.|StardewValley\.TokenizableStrings\.|StardewValley\.Logging\.|StardewValley\.Mods\.|StardewValley\.Delegates\.|StardewValley\.(Event|SaveGame|DebugCommands|GameStateQuery|LocalizedContentManager|Program|GameRunner)(/|$))'

rm -rf "$OUT"
mkdir -p "$LIBS"
# One folder with exactly the files the device loads, so references resolve to them.
for dll in mscorlib System System.Core System.Xml System.Numerics System.Configuration System.Security Mono.Security; do
	cp -L "$HOST/lib/mono/4.5/$dll.dll" "$LIBS/"
done
cp "$FNA_DLL" "$LIBS/FNA.dll"
for f in "$GAME"/*.dll "$GAME"/*.exe; do
	cp "$f" "$LIBS/"
done

PROFILE_ARGS=""
if [ -n "${AOT_PROFILE:-}" ]; then
	# Only the game's rarely run code (see filter-aotprofile.py): its per-frame code runs about twice
	# as fast JIT-compiled.
	python3 "$HERE/filter-aotprofile.py" "$AOT_PROFILE" "$OUT/stardew.aotprofile" "Stardew Valley" "${GAME_AOT_CLASSES:-$DEFAULT_GAME_AOT_CLASSES}"
	PROFILE_ARGS=",profile=$OUT/stardew.aotprofile,profile-only"
fi

symbols=()
for name in "${ASSEMBLIES[@]}"; do
	file="$LIBS/$name.dll"
	[ -f "$file" ] || file="$LIBS/$name.exe"
	[ -f "$file" ] || { echo "no assembly $name in $LIBS"; exit 1; }
	extra=""
	if [ -n "$PROFILE_ARGS" ] && [[ ":${PROFILE_ONLY:-}:" == *":$name:"* ]]; then
		extra="$PROFILE_ARGS"
	fi
	# The symbol aot-compiler.c emits: non-alphanumerics become '_'.
	symbol="mono_aot_module_$(printf '%s' "$name" | sed 's/[^A-Za-z0-9_]/_/g')_info"
	base="$OUT/$(printf '%s' "$name" | sed 's/[^A-Za-z0-9_.]/_/g')"
	start=$(date +%s)
	# MONO_GC_PARAMS: configure makes the concurrent collector the x86_64 Linux default, which the
	# compiler (built without it, like the Vita runtime) can't start with; the collector the compiler
	# itself runs on doesn't change the code it generates.
	MONO_PATH="$LIBS" MONO_DEBUG=explicit-null-checks MONO_GC_PARAMS=major=marksweep "$CROSS" \
		--aot=static,asmonly,nodebug,mtriple=armv7-vita-eabi,outfile="$base.s$extra" "$file" > "$base.log" 2>&1 \
		|| { tail -20 "$base.log"; echo "AOT compilation of $name failed (log: $base.log)"; exit 1; }
	arm-vita-eabi-gcc -c "$base.s" -o "$base.o"
	rm "$base.s"
	symbols+=("$symbol")
	printf '%-24s %6d KB  %4d s  %s\n' "$name" $(( $(stat -c %s "$base.o") / 1024 )) $(( $(date +%s) - start )) \
		"$(grep -m1 -o 'Compiled: [0-9]*/[0-9]*' "$base.log" || true)"
done

{
	echo "/* Generated by aot-stardew.sh: the AOT images linked into the eboot (see main.c). */"
	echo "#include <stddef.h>"
	for s in "${symbols[@]}"; do echo "extern void *$s;"; done
	echo "void **const vita_aot_modules[] = {"
	for s in "${symbols[@]}"; do echo "	&$s,"; done
	echo "	NULL"
	echo "};"
} > "$OUT/vita_aot_modules.c"
echo "AOT images in $OUT: ${#symbols[@]}"
