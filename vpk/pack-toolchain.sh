#!/usr/bin/env bash
# Maintainer tool, inside the WSL that builds the port: packs the native toolchain build-vpk.sh needs into one
# archive for players (no game files in it): /root/vita/prefix, mono-vita, mono-cross and the parts of mono-host
# the AOT compiler and the VPK take (class libraries in 4.5 and the gac its symlinks point into, config). VitaSDK is installed separately.
#
# The archive carries /root/vita/licenses: every component's license text and, for vitaGL (LGPL-3.0, linked
# statically into eboot.bin), the complete source of the exact commit the library was built from, so whoever
# receives libvitaGL.a also receives its source and can rebuild and relink it (LGPL-3.0 section 4).
#
#   bash vpk/pack-toolchain.sh <output.tar.xz>
#   bash vpk/pack-toolchain.sh --licenses-only     (just /root/vita/licenses, for pack-toolchain-zip.sh)
set -euo pipefail
OUT="${1:?usage: pack-toolchain.sh <output.tar.xz> | --licenses-only}"
[ "$OUT" = --licenses-only ] || OUT="$(realpath "$OUT")"
V=/root/vita
L="$V/licenses"
VITAGL_COMMIT=76f43e6

rm -rf "$L"
mkdir -p "$L"
[ "$(git -C "$V/vitaGL-src" rev-parse --short=7 HEAD)" = "$VITAGL_COMMIT" ] || { echo "vitaGL-src is not at $VITAGL_COMMIT"; exit 1; }
git -C "$V/vitaGL-src" archive --format=tar --prefix="vitaGL-$VITAGL_COMMIT/" "$VITAGL_COMMIT" | xz -9 > "$L/vitaGL-$VITAGL_COMMIT-source.tar.xz"
cp "$V/vitaGL-src/COPYING.LESSER" "$L/vitaGL-COPYING.LESSER"
cp "$V/vitaGL-src/COPYING" "$L/vitaGL-COPYING"
cp "$V/SDL/LICENSE.txt" "$L/SDL3-LICENSE.txt"
cp "$V/src/mono-vita-6.14.1/LICENSE" "$L/Mono-LICENSE"
cp "$V/src/FNA3D/MojoShader/LICENSE.txt" "$L/MojoShader-LICENSE.txt"
# libvitaGL.a is upstream plus our edits (patch-vitagl.py) built with build-vitagl.sh's options: both belong to
# its corresponding source.
TOOLS="$(dirname "$(realpath "$0")")/../overlay/game/Tools/Vita"
cp "$TOOLS/build-vitagl.sh" "$L/vitaGL-build-vitagl.sh"
cp "$TOOLS/fna-vita/patch-vitagl.py" "$L/vitaGL-patch-vitagl.py"
# The trimmed FNA3D/FAudio source copies have no license file: these are upstream's, from the commits FNA pins.
cp "$(dirname "$(realpath "$0")")/licenses/FNA3D-LICENSE.txt" "$(dirname "$(realpath "$0")")/licenses/FAudio-LICENSE.txt" "$L/"
cat > "$L/README.txt" <<EOF
Third-party components in this toolchain archive

- vitaGL $VITAGL_COMMIT (Rinnegatamante): LGPL-3.0 (vitaGL-COPYING.LESSER, with the GPL-3.0 it refers to in
  vitaGL-COPYING). prefix/lib/libvitaGL.a was built from vitaGL-$VITAGL_COMMIT-source.tar.xz, the complete
  source of that commit, with our edits applied by vitaGL-patch-vitagl.py and built by vitaGL-build-vitagl.sh
  (NO_DEBUG=1 HAVE_SHADER_CACHE=1 HAVE_VERTEX_LAYOUT_CACHE=1 NO_SPLASHSCREEN=1; both are also in the builder
  repository). stardew.vpk is linked on the player's own machine (the builder on Windows, vpk/build-vpk.sh in WSL), so a modified
  vitaGL can be built and relinked the same way.
- SDL 3 (release-3.4.16): zlib (SDL3-LICENSE.txt)
- Mono 6.14.1, with the patches in Tools/Vita/mono-vita: MIT, some parts 3-clause BSD (Mono-LICENSE)
- FNA3D and MojoShader: zlib (MojoShader-LICENSE.txt, FNA3D-LICENSE.txt)
- FAudio: zlib (FAudio-LICENSE.txt)
EOF
ls -la "$L"
[ "$OUT" != --licenses-only ] || exit 0
tar -C / -cJf "$OUT" \
	root/vita/prefix root/vita/mono-vita root/vita/mono-cross \
	root/vita/mono-host/lib/mono/4.5 root/vita/mono-host/lib/mono/gac root/vita/mono-host/etc root/vita/licenses
ls -la "$OUT"
sha256sum "$OUT"
