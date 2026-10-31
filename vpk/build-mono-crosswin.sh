#!/usr/bin/env bash
# Maintainer tool: builds the cross AOT compiler as a Windows program (mono-sgen.exe, x86_64 mingw), the
# same compiler build-mono-cross.sh builds for Linux (code generator for the Vita runtime: ARMv7 hard-float,
# cooperative suspend, the Vita's struct layout), so players can AOT-compile on Windows without WSL.
# Run inside WSL Ubuntu as root with mingw-w64 installed, after build-mono-vita.sh (struct offsets).
# Stages: prepare | configure | make | all. Output: /root/vita/mono-crosswin/bin/mono-sgen.exe.
#
# Options are build-mono-cross.sh's (they decide the generated code); only --host differs.
set -euo pipefail

V="${MONO_VERSION:-6.14.1}"
TOOLS="${TOOLS:?TOOLS=<SV_PC_source/Tools/Vita as a WSL path>}"
MV="$TOOLS/mono-vita"
TARBALL="/root/vita/src/mono-$V.tar.xz"
SRC="/root/vita/src/mono-crosswin-$V"
BUILD=/root/vita/build-mono-crosswin
PREFIX=/root/vita/mono-crosswin
OFFSETS=/root/vita/mono-vita/arm-vita-eabi-offsets.h
STAGE="${1:-all}"

prepare() {
	rm -rf "$SRC"
	mkdir -p "$SRC"
	tar -xJf "$TARBALL" --strip-components=1 -C "$SRC"
	python3 "$MV/patch-mono.py" "$SRC"
	windows_host_edits
	(cd "$SRC" && NOCONFIGURE=1 ./autogen.sh > /dev/null)
}

# Edits a Windows-hosted cross compiler needs on top of the Vita ones; none changes generated code.
windows_host_edits() {
	python3 - "$SRC" <<'EOF'
import sys, pathlib
src = pathlib.Path(sys.argv[1])
def edit(rel, old, new, count):
	p = src / rel
	s = p.read_text()
	if s.count(old) != count:
		sys.exit(f"{rel}: expected {count} of {old!r}, found {s.count(old)}")
	p.write_text(s.replace(old, new))
# The soft debugger's signal checks (siginfo_t): only the device runtime ever calls them.
edit("mono/mini/mini-arm.c", "#ifdef HOST_VITA\n\treturn FALSE;\n#else\n\tsiginfo_t",
	"#if defined(HOST_VITA) || defined(HOST_WIN32)\n\treturn FALSE;\n#else\n\tsiginfo_t", 2)
# The AOT writer picks its read-only sections by the compiler's own OS (__linux__), not by the target: on
# Windows it would put them in .text. The Vita is an ELF target like Linux, so take the Linux sections.
edit("mono/mini/aot-compiler.c", "#if defined(TARGET_ANDROID) || defined(__linux__)\n#define RODATA_REL_SECT",
	"#if defined(TARGET_ANDROID) || defined(__linux__) || defined(HOST_WIN32)\n#define RODATA_REL_SECT", 1)
edit("mono/mini/aot-compiler.c", "#if defined(__linux__)\n#define RODATA_SECT",
	"#if defined(__linux__) || defined(HOST_WIN32)\n#define RODATA_SECT", 1)
edit("mono/mini/aot-compiler.c", "#if defined(TARGET_ANDROID) || defined(__linux__)\n\tgboolean is_func = FALSE;",
	"#if defined(TARGET_ANDROID) || defined(__linux__) || defined(HOST_WIN32)\n\tgboolean is_func = FALSE;", 1)
# networking-posix.c's no-interfaces fallback would clash with networking-windows.c's definition.
edit("mono/utils/networking-posix.c", "#else\n\nvoid *\nmono_get_local_interfaces",
	"#elif !defined(HOST_WIN32)\n\nvoid *\nmono_get_local_interfaces", 1)
EOF
}

configure() {
	[ -f "$OFFSETS" ] || { echo "no $OFFSETS (build-mono-cross.sh makes it)"; exit 1; }
	rm -rf "$BUILD"
	mkdir -p "$BUILD"
	cd "$BUILD"
	"$SRC/configure" \
		--build=x86_64-pc-linux-gnu --host=x86_64-w64-mingw32 --target=arm-vita-eabi \
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
	for step in culture eglib zlib arch utils cil sgen metadata; do
		make -j"$(nproc)" -C "mono/$step"
	done
	make -C mono/mini version.h cpu-arm.h
	make -j"$(nproc)" -C mono/mini mono-sgen.exe
	# mono/mini/mono-sgen.exe is libtool's wrapper; the program and its runtime DLL are in .libs.
	rm -rf "$PREFIX/bin"
	mkdir -p "$PREFIX/bin"
	cp mono/mini/.libs/mono-sgen.exe mono/mini/.libs/libmonosgen-2.0.dll "$PREFIX/bin/"
	x86_64-w64-mingw32-strip "$PREFIX/bin/mono-sgen.exe" "$PREFIX/bin/libmonosgen-2.0.dll"
	# Only Windows' own DLLs besides libmonosgen-2.0.dll (no mingw runtime to ship).
	x86_64-w64-mingw32-objdump -p "$PREFIX/bin/mono-sgen.exe" "$PREFIX/bin/libmonosgen-2.0.dll" | grep 'DLL Name' | sort -u
	ls -l "$PREFIX/bin"
}

case "$STAGE" in
	prepare) prepare ;;
	configure) configure ;;
	make) build ;;
	all) prepare; configure; build ;;
	*) echo "unknown stage $STAGE"; exit 1 ;;
esac
