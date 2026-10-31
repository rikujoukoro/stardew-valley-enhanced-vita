cmake_minimum_required(VERSION 3.16)

if(NOT DEFINED CMAKE_TOOLCHAIN_FILE)
	set(CMAKE_TOOLCHAIN_FILE "$ENV{VITASDK}/share/vita.toolchain.cmake" CACHE PATH "toolchain file")
endif()

project(fna3dtest C)
include("$ENV{VITASDK}/share/vita.cmake" REQUIRED)

set(VITA_APP_NAME "FNA3D Test")
set(VITA_TITLEID  "FNAT00001")
set(VITA_VERSION  "01.00")
# Same budget the game will use: extended MAIN memory.
set(VITA_MKSFOEX_FLAGS "${VITA_MKSFOEX_FLAGS} -d ATTRIBUTE2=12")

# FNA3D, MojoShader and SDL3 static libraries from build-sdl3.sh / build-fna3d.sh.
set(PREFIX "/root/vita/prefix" CACHE PATH "Vita library prefix")
# SpriteEffect.fxb is copied next to the build by build-fna3dtest.sh.
set(SPRITE_EFFECT "${CMAKE_BINARY_DIR}/SpriteEffect.fxb" CACHE FILEPATH "FNA SpriteEffect bytecode")

add_executable(fna3dtest main.c ../fna3d-vita/vita_gl_glue.c ../fna3d-vita/vita_glsl_rewrite.c)
target_include_directories(fna3dtest PRIVATE "${PREFIX}/include")
# Same as FNA3D's own build: mojoshader.h without the generated version header, with the effect API
# (and so the same MOJOSHADER_effect layout FNA3D was compiled with).
target_compile_definitions(fna3dtest PRIVATE MOJOSHADER_NO_VERSION_INCLUDE MOJOSHADER_EFFECT_SUPPORT)
# FNA3D, MojoShader and SDL3 use 4-byte enums (see build-fna3d.sh); their structs must agree.
target_compile_options(fna3dtest PRIVATE -fno-short-enums)
target_link_options(fna3dtest PRIVATE "-Wl,--no-enum-size-warning")
target_link_directories(fna3dtest PRIVATE "${PREFIX}/lib")
target_link_libraries(fna3dtest PRIVATE
	FNA3D mojoshader SDL3
	vitaGL vitashark SceShaccCgExt mathneon taihen_stub
	SceShaccCg_stub SceKernelDmacMgr_stub SceGxm_stub SceDisplay_stub SceCtrl_stub
	SceAppMgr_stub SceAppUtil_stub SceCommonDialog_stub SceSysmodule_stub SceIme_stub SceLibKernel_stub
	SceAudio_stub SceAudioIn_stub SceHid_stub SceMotion_stub SceTouch_stub SceVshBridge_stub ScePower_stub SceCamera_stub
	stdc++ pthread m c
)

vita_create_self(eboot.bin fna3dtest UNSAFE)
vita_create_vpk(fna3dtest.vpk ${VITA_TITLEID} eboot.bin
	VERSION ${VITA_VERSION}
	NAME ${VITA_APP_NAME}
	FILE "${SPRITE_EFFECT}" SpriteEffect.fxb
)
