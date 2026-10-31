#!/usr/bin/env bash
# Builds Mono for the WSL host (x86_64 Linux) into /root/vita/mono-host.
# Run inside WSL Ubuntu as root. The host build provides the net_4_x class libraries (mscorlib,
# System, ...) and the C# compiler at exactly the version the Vita runtime is built from, and later
# the base for the cross AOT compiler.
set -euo pipefail

V="${MONO_VERSION:-6.14.1}"
SRC="/root/vita/src/mono-$V"
BUILD=/root/vita/build-mono-host
PREFIX=/root/vita/mono-host

apt-get install -y build-essential autoconf automake libtool cmake pkg-config python3 gettext zlib1g-dev > /dev/null

rm -rf "$BUILD"
mkdir -p "$BUILD"
cd "$BUILD"
"$SRC/configure" --prefix="$PREFIX" --disable-nls --with-mcs-docs=no --disable-boehm --with-ikvm-native=no
make -j"$(nproc)"
make install
"$PREFIX/bin/mono" --version | head -1
ls "$PREFIX/lib/mono/4.5/mscorlib.dll"
