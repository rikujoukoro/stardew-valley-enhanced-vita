#!/usr/bin/env bash
# Builds monotest.vpk (MONO00001, interpreter) and monotest-jit.vpk (MONO00002, JIT): the Vita Mono
# runtime plus hello.exe and the class libraries it loads. Copies both VPKs to $1 if given. Run inside
# WSL as root, after build-mono-host.sh and build-mono-vita.sh.
set -euo pipefail

export VITASDK=/usr/local/vitasdk
export PATH="$VITASDK/bin:$PATH"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOST=/root/vita/mono-host
BUILD=/root/vita/build-monotest
STAGE="$BUILD/stage"

rm -rf "$BUILD"
mkdir -p "$STAGE/lib/mono/4.5" "$STAGE/etc/mono/4.5"
cp "$HERE/monotest/Hello.cs" "$BUILD/"

# hello.exe, checked on the host first: it must pass there before it means anything on the Vita.
"$HOST/bin/mcs" -nologo -optimize -out:"$STAGE/hello.exe" -r:System.Xml.dll "$BUILD/Hello.cs"
mkdir -p "$BUILD/host-run"
MONO_LOG_LEVEL=info MONO_LOG_MASK=asm "$HOST/bin/mono" "$STAGE/hello.exe" "$BUILD/host-run" > "$BUILD/host-run/asm.log" 2>&1 \
	&& host_status=0 || host_status=$?
echo "host run: Main returned $host_status (42 = all checks passed)"
tail -3 "$BUILD/host-run/managed.txt"

# Package exactly the class libraries the host run loaded.
grep -oE "Image addref [^ ]+\[[^]]*\] -> /root/vita/mono-host/lib/mono/4.5/[^ ]+\.dll" "$BUILD/host-run/asm.log" \
	| sed -E 's|.* -> ||' | sort -u > "$BUILD/assemblies.txt" || true
if [ ! -s "$BUILD/assemblies.txt" ]; then
	# Fallback when the log format differs: the libraries hello.exe references directly.
	printf '%s\n' mscorlib.dll System.dll System.Core.dll System.Xml.dll System.Configuration.dll Mono.Security.dll \
		| sed "s|^|$HOST/lib/mono/4.5/|" > "$BUILD/assemblies.txt"
fi
while read -r dll; do
	cp -L "$dll" "$STAGE/lib/mono/4.5/"
done < "$BUILD/assemblies.txt"
cp "$HOST/etc/mono/config" "$STAGE/etc/mono/config"
cp "$HOST/etc/mono/4.5/machine.config" "$STAGE/etc/mono/4.5/machine.config"
echo "packaged:"; ls -la "$STAGE/lib/mono/4.5/"

cmake -S "$HERE/monotest" -B "$BUILD" -G Ninja \
	-DCMAKE_TOOLCHAIN_FILE="$VITASDK/share/vita.toolchain.cmake" \
	-DCMAKE_BUILD_TYPE=Release
cmake --build "$BUILD" --parallel
ls -la "$BUILD/monotest.vpk" "$BUILD/monotest-jit.vpk"
if [ -n "${1:-}" ]; then
	cp "$BUILD/monotest.vpk" "$BUILD/monotest-jit.vpk" "$1/"
	echo "copied to $1/monotest.vpk and $1/monotest-jit.vpk"
fi
