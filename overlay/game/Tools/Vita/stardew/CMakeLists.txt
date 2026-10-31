cmake_minimum_required(VERSION 3.16)

if(NOT DEFINED CMAKE_TOOLCHAIN_FILE)
	set(CMAKE_TOOLCHAIN_FILE "$ENV{VITASDK}/share/vita.toolchain.cmake" CACHE PATH "toolchain file")
endif()

project(stardew C)
include("$ENV{VITASDK}/share/vita.cmake" REQUIRED)
include("${CMAKE_CURRENT_SOURCE_DIR}/../mono-vita/vita-mono.cmake")

set(VITA_APP_NAME "Stardew Valley Enhanced")
set(VITA_TITLEID  "STDV00001")
# The game version (1.6) is in APP_VER, not the title, so the name survives updates.
set(VITA_VERSION  "01.60")
# Extended MAIN memory (~350 MB instead of ~250 MB).
set(VITA_MKSFOEX_FLAGS "${VITA_MKSFOEX_FLAGS} -d ATTRIBUTE2=12")

set(PREFIX "/root/vita/prefix" CACHE PATH "SDL3, FNA3D, MojoShader and FAudio static libraries")
set(STAGE "${CMAKE_BINARY_DIR}/stage" CACHE PATH "runtime files to package (class libraries, FNA.dll)")
set(TABLES "${CMAKE_BINARY_DIR}/tables" CACHE PATH "generated DllImport tables")

set(AOT_DIR "" CACHE PATH "AOT images from aot-stardew.sh (objects and vita_aot_modules.c); empty: JIT only")

add_executable(stardew
	main.c
	"${TABLES}/vita_sdl3_table.c"
	"${TABLES}/vita_fna3d_table.c"
	"${TABLES}/vita_faudio_table.c"
	../fna-vita/vita_sdl_overrides.c
	../fna3d-vita/vita_gl_glue.c
	../fna3d-vita/vita_glsl_rewrite.c
)
if(AOT_DIR)
	file(GLOB AOT_OBJECTS "${AOT_DIR}/*.o")
	target_sources(stardew PRIVATE "${AOT_DIR}/vita_aot_modules.c" ${AOT_OBJECTS})
else()
	target_sources(stardew PRIVATE aot_none.c)
endif()
target_include_directories(stardew PRIVATE "${PREFIX}/include" ../fna-vita)
# SDL3, FNA3D, MojoShader and FAudio are built with 4-byte enums (their structs cross into C#); C
# code using their types must agree. The rest of the SDK keeps 1-byte enums, so the linker's warning
# about the mix is expected.
target_compile_options(stardew PRIVATE -fno-short-enums)
target_link_options(stardew PRIVATE "-Wl,--no-enum-size-warning" "-Wl,--defsym,__sce_headroom=0x10000")
target_link_directories(stardew PRIVATE "${PREFIX}/lib")
# FNA's native libraries first (the tables reference them), then the Mono runtime.
target_link_libraries(stardew PRIVATE
	FNA3D mojoshader FAudio SDL3
	vitaGL vitashark SceShaccCgExt mathneon taihen_stub SceShaccCg_stub
	SceGxm_stub SceDisplay_stub SceCtrl_stub SceAppUtil_stub SceCommonDialog_stub SceIme_stub
	SceAudio_stub SceAudioIn_stub SceHid_stub SceMotion_stub SceTouch_stub SceVshBridge_stub
	ScePower_stub SceCamera_stub SceNpTrophy_stub SceAppMgr_stub stdc++
)
vita_link_mono(stardew)

vita_create_self(eboot.bin stardew UNSAFE)

file(GLOB_RECURSE STAGED_FILES RELATIVE "${STAGE}" "${STAGE}/*")
set(VPK_FILES)
foreach(f ${STAGED_FILES})
	list(APPEND VPK_FILES FILE "${STAGE}/${f}" "${f}")
endforeach()
vita_create_vpk(stardew.vpk ${VITA_TITLEID} eboot.bin
	VERSION ${VITA_VERSION}
	NAME ${VITA_APP_NAME}
	${VPK_FILES}
)
