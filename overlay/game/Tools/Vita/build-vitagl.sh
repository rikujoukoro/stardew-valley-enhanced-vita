#!/usr/bin/env bash
# Builds vitaGL from source into $WORK/prefix/lib, which the launcher links before the SDK's lib (the
# vdpm vitaGL package stays installed and untouched). Run inside WSL Ubuntu as root.
#   bash build-vitagl.sh [commit]   default: 76f43e6 (2026-10-22). The vdpm package is 2bdbe89; the
#                                   vertex layout cache, precomputed glClear/blit draws and "don't
#                                   cache failed shader compilations" came after it. Pass 2bdbe89 to
#                                   go back (it has no layout cache: that flag is then ignored).
#
# The vdpm package is built with NO_DEBUG=1 only. On top of that:
#   HAVE_SHADER_CACHE=1         every GLSL shader MojoShader hands vitaGL was compiled by SceShaccCg on
#                               each launch and on each effect's first use; compiled programs are now
#                               kept in ux0:data/shader_cache/STDV00001 (a few KB each).
#   HAVE_VERTEX_LAYOUT_CACHE=1  each draw ran the vertex program through the GXM shader patcher again;
#                               the layout cache keeps the patched program for an unchanged layout.
# The speedhacks are left off: DRAW/MATH only touch the fixed-function path FNA does not use, and
# INDICES/BUFFERS/SAMPLERS can bring back the dropped-batch and black-tile bugs fixed earlier.
set -euo pipefail

export VITASDK=/usr/local/vitasdk
export PATH="$VITASDK/bin:$PATH"
COMMIT="${1:-76f43e6}"
WORK=/root/vita
PREFIX="$WORK/prefix"
SRC="$WORK/vitaGL-build"
mkdir -p "$PREFIX/lib" "$PREFIX/include"

if [ ! -d "$SRC/.git" ]; then
	git clone https://github.com/Rinnegatamante/vitaGL "$SRC"
fi
git -C "$SRC" fetch --quiet origin "$COMMIT" || true
git -C "$SRC" checkout --quiet --force "$COMMIT"
# Port edits (fna-vita/patch-vitagl.py): CDRAM kept free for render targets.
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
python3 "$HERE/fna-vita/patch-vitagl.py" "$SRC"

make -C "$SRC" clean > /dev/null
# NO_SPLASHSCREEN=1 removes vitaGL boot splash; loading remains visible without it.
make -C "$SRC" -j"$(nproc)" NO_DEBUG=1 HAVE_SHADER_CACHE=1 HAVE_VERTEX_LAYOUT_CACHE=1 NO_SPLASHSCREEN=1
cp "$SRC/libvitaGL.a" "$PREFIX/lib/"
cp "$SRC/source/vitaGL.h" "$PREFIX/include/"

ls -la "$PREFIX/lib/libvitaGL.a"
git -C "$SRC" log -1 --format='vitaGL %h %ci'
