#!/usr/bin/env bash
# Builds FNA3D + MojoShader as static Vita libraries on top of vitaGL, into /root/vita/prefix.
# Run inside WSL Ubuntu as root, after build-sdl3.sh.
#
# The FNA checkout is left untouched: the sources are copied to /root/vita/src/FNA3D and two local
# Vita edits are applied there (these are port glue, never sent upstream):
#   - OpenGL PrepareWindowAttributes returns no SDL_WINDOW_OPENGL flag (vitaGL owns the display;
#     our SDL3 has no GL backend and would refuse the window),
#   - the SDL_GPU driver and MojoShader's SDL_GPU backend are not compiled (no SDL_GPU on the Vita).
# fna-vita/patch-fna3d.py then gives each render target texture a framebuffer of its own (vitaGL
# rebuilds its GXM render target whenever a framebuffer's attachment changes size).
# SDL_GL_* calls are redirected to vitaGL by force-including fna3d-vita/vita_gl_glue.h.
set -euo pipefail

export VITASDK=/usr/local/vitasdk
export PATH="$VITASDK/bin:$PATH"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GLUE="$HERE/fna3d-vita"
FNA3D_SRC="$HERE/../../../FNA/lib/FNA3D"
WORK=/root/vita/src/FNA3D
BUILD=/root/vita/build-fna3d
PREFIX=/root/vita/prefix

rm -rf "$WORK" "$BUILD"
mkdir -p "$WORK"
cp -r "$FNA3D_SRC/CMakeLists.txt" "$FNA3D_SRC/include" "$FNA3D_SRC/src" "$FNA3D_SRC/MojoShader" "$FNA3D_SRC/cmake" "$WORK/"
# CMAKE_C_FLAGS cannot carry a path with spaces, so the forced-include header lives in the work tree.
cp "$GLUE/vita_gl_glue.h" "$WORK/vita_gl_glue.h"
find "$WORK" -type f \( -name '*.c' -o -name '*.h' -o -name '*.txt' -o -name '*.cmake' \) -exec sed -i 's/\r$//' {} +

OPENGL="$WORK/src/FNA3D_Driver_OpenGL.c"
[ "$(grep -c '^	\*flags = SDL_WINDOW_OPENGL;$' "$OPENGL")" = 1 ] || { echo "SDL_WINDOW_OPENGL line not found"; exit 1; }
sed -i 's|^	\*flags = SDL_WINDOW_OPENGL;$|	*flags = 0; /* Vita: vitaGL owns the display */|' "$OPENGL"
[ "$(grep -c 'add_definitions(-DFNA3D_DRIVER_SDL)' "$WORK/CMakeLists.txt")" = 1 ] || { echo "FNA3D_DRIVER_SDL line not found"; exit 1; }
sed -i 's|add_definitions(-DFNA3D_DRIVER_SDL)|# Vita: no SDL_GPU driver|' "$WORK/CMakeLists.txt"
[ "$(grep -c '^	MojoShader/mojoshader_sdlgpu.c$' "$WORK/CMakeLists.txt")" = 1 ] || { echo "mojoshader_sdlgpu.c line not found"; exit 1; }
sed -i '/^	MojoShader\/mojoshader_sdlgpu.c$/d' "$WORK/CMakeLists.txt"
# FNA3D on a render thread of its own (fna3d-vita/FNA3D_VitaThread.c).
cp "$GLUE/FNA3D_VitaThread.c" "$WORK/src/"
sed -i 's/\r$//' "$WORK/src/FNA3D_VitaThread.c"
python3 "$HERE/fna-vita/patch-fna3d.py" "$WORK"

# -fno-short-enums: FNA's C# side marshals every FNA3D/MojoShader enum (in structs too) as a 4-byte
# int, while arm-vita-eabi defaults to 1-byte enums.
# Only the GLSL 1.20 profile is used; drop the others to save code size.
PROFILES="-DSUPPORT_PROFILE_HLSL=0 -DSUPPORT_PROFILE_METAL=0 -DSUPPORT_PROFILE_SPIRV=0 -DSUPPORT_PROFILE_GLSPIRV=0 \
-DSUPPORT_PROFILE_ARB1=0 -DSUPPORT_PROFILE_ARB1_NV=0 -DSUPPORT_PROFILE_GLSLES=0 -DSUPPORT_PROFILE_GLSLES3=0"

cmake -S "$WORK" -B "$BUILD" -G Ninja \
	-DCMAKE_TOOLCHAIN_FILE="$VITASDK/share/vita.toolchain.cmake" \
	-DCMAKE_BUILD_TYPE=Release \
	-DCMAKE_PREFIX_PATH="$PREFIX" \
	-DBUILD_SHARED_LIBS=OFF \
	-DBUILD_SDL3=ON \
	-DCMAKE_C_FLAGS="-include $WORK/vita_gl_glue.h $PROFILES -fno-short-enums"
cmake --build "$BUILD" --parallel

mkdir -p "$PREFIX/lib" "$PREFIX/include/FNA3D"
cp "$BUILD/libFNA3D.a" "$BUILD/libmojoshader.a" "$PREFIX/lib/"
cp "$WORK/include/"*.h "$WORK/MojoShader/mojoshader.h" "$PREFIX/include/FNA3D/"
ls -la "$PREFIX/lib/libFNA3D.a" "$PREFIX/lib/libmojoshader.a"
