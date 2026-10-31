#!/usr/bin/env bash
# Cross-builds the Mono runtime (libmonosgen-2.0, static) for the PS Vita into /root/vita/mono-vita.
# Run inside WSL Ubuntu as root, after install-vitasdk.sh.
# Stages: prepare | configure | make | all, and shim | native | aotprof | sampler to rebuild just those parts.
#
#   prepare    fresh source tree from the tarball, Vita edits (mono-vita/patch-mono.py), autogen
#   configure  cross-configure into /root/vita/build-mono-vita
#   make       build the runtime libraries, the shim and System.Native, install them under
#              /root/vita/mono-vita
#
# Configuration choices are explained in mono-vita/README.md.
set -euo pipefail

V="${MONO_VERSION:-6.14.1}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MV="$HERE/mono-vita"
TARBALL="/root/vita/src/mono-$V.tar.xz"
SRC="/root/vita/src/mono-vita-$V"
BUILD=/root/vita/build-mono-vita
PREFIX=/root/vita/mono-vita
STAGE="${1:-all}"
# Compiler flags cannot carry the checkout's path (it has spaces): shim and native glue are used from
# copies.
SHIM=/root/vita/src/vita-shim
NATIVE=/root/vita/src/vita-native

export VITASDK=/usr/local/vitasdk
export PATH="$VITASDK/bin:$PATH"

rm -rf "$SHIM" "$NATIVE"
cp -r "$MV/vita-shim" "$SHIM"
cp -r "$MV/vita-native" "$NATIVE"

# Preprocessor settings shared by the runtime (through configure) and System.Native.
# Constants newlib leaves out: SSIZE_MAX, PATH_MAX, and the SA_* flags (sigaction is a no-op in the
# shim, so their values only need to exist). _POSIX_VERSION selects Mono's POSIX threads, dl and log
# code; newlib's headers never test it.
VITA_CPPFLAGS="-I$SHIM/include -I$SHIM -DSSIZE_MAX=2147483647 -DPATH_MAX=1024 -DSA_SIGINFO=0x2 -DSA_RESTART=0x4 -D_POSIX_VERSION=200112L"

prepare() {
	rm -rf "$SRC"
	mkdir -p "$SRC"
	tar -xJf "$TARBALL" --strip-components=1 -C "$SRC"
	python3 "$MV/patch-mono.py" "$SRC"
	(cd "$SRC" && NOCONFIGURE=1 ./autogen.sh > /dev/null)
}

configure() {
	rm -rf "$BUILD"
	mkdir -p "$BUILD"
	cd "$BUILD"
	# Forced configure answers (newlib-vita specifics):
	#   mmap                       absent: Mono uses its malloc-based mono_valloc and read-based file maps
	#   pthread_condattr_setclock  newlib's timed waits ignore CLOCK_MONOTONIC: use realtime deadlines
	#   getrusage                  returns -1 on the Vita and Mono treats that as fatal
	#   strerror_r                 newlib declares the GNU (char *) variant; the cross check guesses wrong
	env \
		CC=arm-vita-eabi-gcc CXX=arm-vita-eabi-g++ AR=arm-vita-eabi-ar RANLIB=arm-vita-eabi-ranlib \
		STRIP=arm-vita-eabi-strip OBJDUMP=arm-vita-eabi-objdump \
		CPPFLAGS="$VITA_CPPFLAGS" \
		CFLAGS="-O2 -g -ffunction-sections -fdata-sections" \
		ac_cv_func_mmap=no ac_cv_header_sys_mman_h=no \
		ac_cv_func_pthread_condattr_setclock=no \
		ac_cv_func_getrusage=no \
		ac_cv_func_strerror_r_char_p=yes \
		ac_cv_struct_tm_gmtoff=yes \
		"$SRC/configure" \
		--host=arm-vita-eabi --build=x86_64-pc-linux-gnu --prefix="$PREFIX" \
		--disable-shared --enable-static \
		--disable-mcs-build --disable-support-build --disable-executables \
		--disable-boehm --with-sgen=yes --with-sgen-default-concurrent=no \
		--enable-cooperative-suspend \
		--with-tls=pthread --with-sigaltstack=no \
		--disable-nls --disable-btls --disable-dtrace --disable-llvm \
		--with-ikvm-native=no --without-x \
		--enable-minimal=attach,com,perfcounters,shared_perfcounters,lldb,mdb,sgen_marksweep_conc
}

shim() {
	local src
	mkdir -p "$BUILD/vita-shim" "$PREFIX/lib" "$PREFIX/include/vita"
	rm -f "$BUILD/vita-shim"/*.o "$PREFIX/lib/libvitashim.a"
	for src in "$SHIM"/*.c; do
		arm-vita-eabi-gcc -O2 -g -Wall -I"$SHIM" -c "$src" -o "$BUILD/vita-shim/$(basename "${src%.c}").o"
	done
	arm-vita-eabi-ar rcs "$PREFIX/lib/libvitashim.a" "$BUILD/vita-shim"/*.o
	cp "$SHIM/vita_shim.h" "$PREFIX/include/vita/"
}

build() {
	# The shim is compiled with the runtime so the archive set is complete.
	shim
	mkdir -p "$PREFIX/include"
	cd "$BUILD"
	# Same order as mono/Makefile's SUBDIRS. Some of these Makefiles return 0 even when a sub-make
	# stops, so success is judged by the archive each step must produce.
	local step
	for step in culture:libmono-culture.la eglib:libeglib.la arch:all utils:libmonoutils.la cil:all \
		sgen:libmonosgen.la metadata:libmonoruntimesgen.la; do
		make -j"$(nproc)" -C "mono/${step%%:*}" "${step#*:}"
	done
	# version.h and cpu-arm.h are generated (BUILT_SOURCES) before the library can compile.
	make -C mono/mini version.h cpu-arm.h
	make -j"$(nproc)" -C mono/mini libmonosgen-2.0.la
	for archive in mono/culture/.libs/libmono-culture.a mono/eglib/.libs/libeglib.a \
		mono/utils/.libs/libmonoutils.a mono/sgen/.libs/libmonosgen.a \
		mono/metadata/.libs/libmonoruntimesgen.a mono/mini/.libs/libmonosgen-2.0.a; do
		[ -f "$archive" ] || { echo "missing $archive"; exit 1; }
	done
	cp mono/mini/.libs/libmonosgen-2.0.a "$PREFIX/lib/"
	# Public embedding headers, laid out as installed: $PREFIX/include/mono-2.0/mono/{jit,metadata,utils}.
	make -C mono/utils install-libmonoutilsincludeHEADERS
	make -C mono/metadata install-libmonoruntimeincludeHEADERS
	make -C mono/mini install-libmonoincludeHEADERS
	native
	aotprof
	sampler
	ls -la "$PREFIX/lib/" "$PREFIX/include/mono-2.0/mono"
}

# System.Native (CoreFX's PAL, which mscorlib P/Invokes for file IO and more), compiled outside Mono's
# automake (it has no Vita platform list) with the runtime's config.h, plus the table that serves it
# to DllImport (vita-native/). Networking, process and console parts are left out.
native() {
	local out="$BUILD/vita-native" cx="$SRC/external/corefx/src/Native/Unix" src
	rm -rf "$out"
	mkdir -p "$out"
	# newlib ships getline only as __getline.
	local flags="-O2 -g -std=gnu99 -I$NATIVE/include $VITA_CPPFLAGS -DHOST_VITA -D_GNU_SOURCE -Dgetline=__getline -I$BUILD -I$SRC -I$SRC/mono -I$SRC/mono/eglib \
		-I$BUILD/mono/eglib -I$SRC/mono/native -I$cx/Common -I$cx/System.Native -I$PREFIX/include/mono-2.0"
	for src in "$SRC/mono/native/mono-native-platform.c" "$SRC/mono/native/pal-icalls.c" \
		"$NATIVE/vita_platform_type.c" \
		"$cx/System.Native/pal_errno.c" "$cx/System.Native/pal_io.c" "$cx/System.Native/pal_time.c" \
		"$cx/System.Native/pal_random.c" "$cx/System.Native/pal_memory.c" "$cx/System.Native/pal_uid.c"; do
		arm-vita-eabi-gcc $flags -c "$src" -o "$out/$(basename "${src%.c}").o"
	done
	# Name -> address table of everything System.Native exports.
	arm-vita-eabi-nm -g --defined-only "$out"/*.o | awk '$2 == "T" { print $3 }' \
		| grep -E '^(SystemNative_|mono_native_|mono_pal_)' | sort -u > "$out/symbols.txt"
	{
		echo '/* Generated by build-mono-vita.sh from the System.Native objects. */'
		echo '#include "vita_native.h"'
		sed 's/.*/extern void &(void);/' "$out/symbols.txt"
		echo 'const VitaNativeSymbol vita_system_native_symbols[] = {'
		sed 's/.*/\t{ "&", (void *)\&& },/' "$out/symbols.txt"
		echo '	{ 0, 0 }'
		echo '};'
	} > "$out/vita_native_table.c"
	arm-vita-eabi-gcc $flags -I"$NATIVE" -c "$out/vita_native_table.c" -o "$out/vita_native_table.o"
	arm-vita-eabi-gcc $flags -I"$NATIVE" -c "$NATIVE/vita_native.c" -o "$out/vita_native.o"
	rm -f "$PREFIX/lib/libvitanative.a"
	arm-vita-eabi-ar rcs "$PREFIX/lib/libvitanative.a" "$out"/*.o
	mkdir -p "$PREFIX/include/vita"
	cp "$NATIVE/vita_native.h" "$SHIM/vita_shim.h" "$PREFIX/include/vita/"
	echo "System.Native: $(wc -l < "$out/symbols.txt") exported functions"
}

# Mono's AOT profiler (records JIT-compiled methods for profile-guided AOT, see aot-stardew.sh) as a
# separate archive the launcher links: a Vita copy without sockets that the launcher saves on demand
# (mono-vita/patch-aot-profiler.py). Compiled with the metadata library's flags.
aotprof() {
	local out="$BUILD/vita-aotprof" flags
	rm -rf "$out"
	mkdir -p "$out"
	python3 "$MV/patch-aot-profiler.py" "$SRC/mono/profiler/aot.c" "$out/aot.c"
	cp "$SRC/mono/profiler/aot.h" "$SRC/mono/profiler/helper.h" "$out/"
	flags="$(make -s -C "$BUILD/mono/metadata" --eval='vita-print-flags: ; @echo $(DEFS) $(DEFAULT_INCLUDES) $(INCLUDES) $(AM_CPPFLAGS) $(CPPFLAGS) $(libmonoruntimesgen_la_CPPFLAGS) $(libmonoruntimesgen_la_CFLAGS) $(AM_CFLAGS) $(CFLAGS)' vita-print-flags | tail -n 1)"
	(cd "$BUILD/mono/metadata" && arm-vita-eabi-gcc $flags -I"$out" -c "$out/aot.c" -o "$out/aot.o")
	rm -f "$PREFIX/lib/libmono-profiler-aot.a"
	arm-vita-eabi-ar rcs "$PREFIX/lib/libmono-profiler-aot.a" "$out/aot.o"
	echo "AOT profiler: $PREFIX/lib/libmono-profiler-aot.a"
}

# Sampling profiler (mono-vita/vita-sampler.c, the profile-samples switch of stardew/main.c), built with
# libmini's flags: it walks stacks with the JIT's unwinder.
sampler() {
	local out="$BUILD/vita-sampler" flags
	rm -rf "$out"
	mkdir -p "$out"
	cp "$MV/vita-sampler.c" "$out/"
	flags="$(make -s -C "$BUILD/mono/mini" --eval='vita-print-flags: ; @echo $(DEFS) $(DEFAULT_INCLUDES) $(INCLUDES) $(AM_CPPFLAGS) $(CPPFLAGS) $(libmini_la_CFLAGS) $(CFLAGS)' vita-print-flags | tail -n 1)"
	(cd "$BUILD/mono/mini" && arm-vita-eabi-gcc $flags -I"$SRC/mono/mini" -c "$out/vita-sampler.c" -o "$out/vita-sampler.o")
	rm -f "$PREFIX/lib/libvita-sampler.a"
	arm-vita-eabi-ar rcs "$PREFIX/lib/libvita-sampler.a" "$out/vita-sampler.o"
	echo "Sampler: $PREFIX/lib/libvita-sampler.a"
}

case "$STAGE" in
	prepare) prepare ;;
	configure) configure ;;
	make) build ;;
	shim) shim ;;
	native) native ;;
	aotprof) aotprof ;;
	sampler) sampler ;;
	all) prepare; configure; build ;;
	*) echo "unknown stage $STAGE"; exit 1 ;;
esac
