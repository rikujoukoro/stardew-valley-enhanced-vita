#!/usr/bin/env bash
# Builds the cross AOT compiler: Mono for the WSL host (x86_64 Linux) whose code generator targets
# the Vita runtime (ARMv7 hard-float, cooperative suspend, the Vita's struct layout), into
# /root/vita/mono-cross. Run inside WSL Ubuntu as root, after build-mono-vita.sh (the struct offsets
# come from that build, see mono-vita/gen-cross-offsets.sh).
# Stages: prepare | configure | make | all.
#
#   prepare    fresh source tree from the tarball (its own copy: the Vita runtime's tree is left
#              alone), the same Vita edits (mono-vita/patch-mono.py), autogen
#   configure  the struct offsets header, then a cross configure (host x86_64, target arm-vita-eabi)
#   make       the runtime libraries and the mono-sgen executable, installed as
#              /root/vita/mono-cross/bin/mono-sgen
#
# Options mirror build-mono-vita.sh wherever they change generated code: SGen without the concurrent
# collector, cooperative suspend (AOT code then carries GC safepoints, which the runtime requires),
# the same --enable-minimal set (COM changes marshalling wrappers).
set -euo pipefail

V="${MONO_VERSION:-6.14.1}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MV="$HERE/mono-vita"
TARBALL="/root/vita/src/mono-$V.tar.xz"
SRC="/root/vita/src/mono-cross-$V"
BUILD=/root/vita/build-mono-cross
PREFIX=/root/vita/mono-cross
OFFSETS=/root/vita/mono-vita/arm-vita-eabi-offsets.h
STAGE="${1:-all}"

prepare() {
	rm -rf "$SRC"
	mkdir -p "$SRC"
	tar -xJf "$TARBALL" --strip-components=1 -C "$SRC"
	python3 "$MV/patch-mono.py" "$SRC"
	(cd "$SRC" && NOCONFIGURE=1 ./autogen.sh > /dev/null)
}

configure() {
	bash "$MV/gen-cross-offsets.sh" "$OFFSETS"
	rm -rf "$BUILD"
	mkdir -p "$BUILD"
	cd "$BUILD"
	"$SRC/configure" \
		--build=x86_64-pc-linux-gnu --host=x86_64-pc-linux-gnu --target=arm-vita-eabi \
		--prefix="$PREFIX" --with-cross-offsets="$OFFSETS" \
		--disable-mcs-build --disable-support-build \
		--disable-boehm --with-sgen=yes --with-sgen-default-concurrent=no \
		--enable-cooperative-suspend \
		--disable-nls --disable-btls --disable-dtrace --disable-llvm \
		--with-ikvm-native=no --without-x \
		--enable-minimal=attach,com,perfcounters,shared_perfcounters,lldb,mdb,sgen_marksweep_conc,sockets
	grep -q 'define MONO_CROSS_COMPILE 1' config.h || { echo "not configured as a cross compiler"; exit 1; }
	grep -q -- '-DARM_FPU_VFP_HARD=1' Makefile || { echo "not configured for hard-float"; exit 1; }
}

build() {
	cd "$BUILD"
	local step
	for step in culture eglib arch utils cil sgen metadata; do
		make -j"$(nproc)" -C "mono/$step"
	done
	# version.h and cpu-arm.h are generated (BUILT_SOURCES) before mini can compile.
	make -C mono/mini version.h cpu-arm.h
	make -j"$(nproc)" -C mono/mini mono-sgen
	mkdir -p "$PREFIX/bin"
	cp mono/mini/mono-sgen "$PREFIX/bin/mono-sgen"
	"$PREFIX/bin/mono-sgen" --version | head -2
}

case "$STAGE" in
	prepare) prepare ;;
	configure) configure ;;
	make) build ;;
	all) prepare; configure; build ;;
	*) echo "unknown stage $STAGE"; exit 1 ;;
esac
