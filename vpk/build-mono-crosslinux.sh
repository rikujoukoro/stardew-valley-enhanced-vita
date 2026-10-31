#!/usr/bin/env bash
# Maintainer tool: builds the cross AOT compiler for players' Linux (x86_64) the way build-mono-cross.sh builds it for
# WSL, but inside a Debian 11 chroot, so it needs glibc 2.31 and runs on most distributions (the WSL Ubuntu's own
# build needs its glibc 2.43). Run inside WSL Ubuntu as root, after build-mono-vita.sh (struct offsets). It makes the
# chroot with debootstrap the first time.
# Stages: prepare | make | all. Output: /root/vita/mono-crosslinux/bin/mono-sgen.
#
# Options are build-mono-cross.sh's (they decide the generated code).
set -euo pipefail

V="${MONO_VERSION:-6.14.1}"
TOOLS="${TOOLS:?TOOLS=<SV_PC_source/Tools/Vita as a WSL path>}"
MV="$TOOLS/mono-vita"
TARBALL="/root/vita/src/mono-$V.tar.xz"
SRC="/root/vita/src/mono-crosslinux-$V"
BUILD=/root/vita/build-mono-crosslinux
PREFIX=/root/vita/mono-crosslinux
OFFSETS=/root/vita/mono-vita/arm-vita-eabi-offsets.h
ROOT=/root/vita/chroot-bullseye
STAGE="${1:-all}"

chroot_ready() {
	if [ ! -f "$ROOT/etc/debian_version" ]; then
		command -v debootstrap > /dev/null || apt-get install -y debootstrap
		debootstrap --variant=minbase \
			--include=build-essential,autoconf,automake,libtool,cmake,python3,zlib1g-dev,gettext,pkg-config,xz-utils,file \
			bullseye "$ROOT" http://deb.debian.org/debian
	fi
	# /root/vita at the same path inside, so the configure paths are the same.
	mkdir -p "$ROOT/root/vita" "$ROOT/proc"
	mountpoint -q "$ROOT/root/vita" || mount --bind /root/vita "$ROOT/root/vita"
	mountpoint -q "$ROOT/proc" || mount -t proc proc "$ROOT/proc"
}

prepare() {
	rm -rf "$SRC"
	mkdir -p "$SRC"
	tar -xJf "$TARBALL" --strip-components=1 -C "$SRC"
	python3 "$MV/patch-mono.py" "$SRC"
	chroot_ready
	chroot "$ROOT" bash -c "cd '$SRC' && NOCONFIGURE=1 ./autogen.sh > /dev/null"
}

build() {
	[ -f "$OFFSETS" ] || { echo "no $OFFSETS (build-mono-cross.sh makes it)"; exit 1; }
	chroot_ready
	rm -rf "$BUILD"
	mkdir -p "$BUILD"
	chroot "$ROOT" bash -euo pipefail -c "
		cd '$BUILD'
		'$SRC/configure' \
			--build=x86_64-pc-linux-gnu --host=x86_64-pc-linux-gnu --target=arm-vita-eabi \
			--prefix='$PREFIX' --with-cross-offsets='$OFFSETS' \
			--disable-mcs-build --disable-support-build \
			--disable-boehm --with-sgen=yes --with-sgen-default-concurrent=no \
			--enable-cooperative-suspend \
			--disable-nls --disable-btls --disable-dtrace --disable-llvm \
			--with-ikvm-native=no --without-x \
			--enable-minimal=attach,com,perfcounters,shared_perfcounters,lldb,mdb,sgen_marksweep_conc,sockets > configure.log
		grep -q 'define MONO_CROSS_COMPILE 1' config.h || { echo 'not configured as a cross compiler'; exit 1; }
		grep -q -- '-DARM_FPU_VFP_HARD=1' Makefile || { echo 'not configured for hard-float'; exit 1; }
		for step in culture eglib arch utils cil sgen metadata; do
			make -j\$(nproc) -C mono/\$step > /dev/null
		done
		make -C mono/mini version.h cpu-arm.h > /dev/null
		make -j\$(nproc) -C mono/mini mono-sgen > /dev/null
		mkdir -p '$PREFIX/bin'
		cp mono/mini/mono-sgen '$PREFIX/bin/mono-sgen'
		strip '$PREFIX/bin/mono-sgen'
		'$PREFIX/bin/mono-sgen' --version | head -1
	"
	ls -la "$PREFIX/bin/mono-sgen"
	echo "needs: $(objdump -T "$PREFIX/bin/mono-sgen" | grep -o 'GLIBC_[0-9.]*' | sort -uV | tail -1)"
}

case "$STAGE" in
	prepare) prepare ;;
	make) build ;;
	all) prepare; build ;;
	*) echo "unknown stage $STAGE"; exit 1 ;;
esac
