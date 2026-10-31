cmake_minimum_required(VERSION 3.16)

if(NOT DEFINED CMAKE_TOOLCHAIN_FILE)
	set(CMAKE_TOOLCHAIN_FILE "$ENV{VITASDK}/share/vita.toolchain.cmake" CACHE PATH "toolchain file")
endif()

project(fnatest C)
include("$ENV{VITASDK}/share/vita.cmake" REQUIRED)
include("${CMAKE_CURRENT_SOURCE_DIR}/../mono-vita/vita-mono.cmake")

set(VITA_APP_NAME "FNA Test")
set(VITA_TITLEID  "FNAG00001")
set(VITA_VERSION  "01.00")
# Same budget the game will use: extended MAIN memory.
set(VITA_MKSFOEX_FLAGS "${VITA_MKSFOEX_FLAGS} -d ATTRIBUTE2=12")

set(PREFIX "/root/vita/prefix" CACHE PATH "SDL3, FNA3D, MojoShader and FAudio static libraries")
set(STAGE "${CMAKE_BINARY_DIR}/stage" CACHE PATH "assemblies to package")
set(TABLES "${CMAKE_BINARY_DIR}/tables" CACHE PATH "generated DllImport tables")

add_executable(fnatest
	main.c
	"${TABLES}/vita_sdl3_table.c"
	"${TABLES}/vita_fna3d_table.c"
	"${TABLES}/vita_faudio_table.c"
	../fna-vita/vita_sdl_overrides.c
	../fna3d-vita/vita_gl_glue.c
	../fna3d-vita/vita_glsl_rewrite.c
)
target_include_directories(fnatest PRIVATE "${PREFIX}/include")
# SDL3, FNA3D and MojoShader are built with 4-byte enums (their structs cross into C#); code using
# their structs must agree. The rest of the SDK keeps 1-byte enums, so the linker's warning about the
# mix is expected.
set_source_files_properties(../fna-vita/vita_sdl_overrides.c ../fna3d-vita/vita_gl_glue.c
	../fna3d-vita/vita_glsl_rewrite.c PROPERTIES COMPILE_OPTIONS "-fno-short-enums")
target_link_options(fnatest PRIVATE "-Wl,--no-enum-size-warning")
target_link_directories(fnatest PRIVATE "${PREFIX}/lib")
# FNA's native libraries first (the tables reference them), then the Mono runtime.
target_link_libraries(fnatest PRIVATE
	FNA3D mojoshader FAudio SDL3
	vitaGL vitashark SceShaccCgExt mathneon taihen_stub SceShaccCg_stub
	SceGxm_stub SceDisplay_stub SceCtrl_stub SceAppUtil_stub SceCommonDialog_stub SceIme_stub
	SceAudio_stub SceAudioIn_stub SceHid_stub SceMotion_stub SceTouch_stub SceVshBridge_stub
	ScePower_stub SceCamera_stub stdc++
)
vita_link_mono(fnatest)

vita_create_self(eboot.bin fnatest UNSAFE)

file(GLOB_RECURSE STAGED_FILES RELATIVE "${STAGE}" "${STAGE}/*")
set(VPK_FILES)
foreach(f ${STAGED_FILES})
	list(APPEND VPK_FILES FILE "${STAGE}/${f}" "${f}")
endforeach()
vita_create_vpk(fnatest.vpk ${VITA_TITLEID} eboot.bin
	VERSION ${VITA_VERSION}
	NAME ${VITA_APP_NAME}
	${VPK_FILES}
)
