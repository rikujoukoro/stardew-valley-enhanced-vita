cmake_minimum_required(VERSION 3.16)

if(NOT DEFINED CMAKE_TOOLCHAIN_FILE)
	set(CMAKE_TOOLCHAIN_FILE "$ENV{VITASDK}/share/vita.toolchain.cmake" CACHE PATH "toolchain file")
endif()

project(monotest C)
include("$ENV{VITASDK}/share/vita.cmake" REQUIRED)
include("${CMAKE_CURRENT_SOURCE_DIR}/../mono-vita/vita-mono.cmake")

set(VITA_VERSION "01.00")
# Same budget the game will use: extended MAIN memory.
set(VITA_MKSFOEX_FLAGS "${VITA_MKSFOEX_FLAGS} -d ATTRIBUTE2=12")

set(STAGE "${CMAKE_BINARY_DIR}/stage" CACHE PATH "hello.exe and the class libraries to package")

# Everything under the stage directory goes into the VPK at the same relative path.
file(GLOB_RECURSE STAGED_FILES RELATIVE "${STAGE}" "${STAGE}/*")
set(VPK_FILES)
foreach(f ${STAGED_FILES})
	list(APPEND VPK_FILES FILE "${STAGE}/${f}" "${f}")
endforeach()

# One VPK per execution mode, so no file has to be edited on the device to switch.
function(monotest_variant name title_id mode)
	add_executable(${name} main.c)
	target_compile_definitions(${name} PRIVATE MONOTEST_DEFAULT_MODE="${mode}" DATA_DIR="ux0:data/${name}")
	vita_link_mono(${name})
	vita_create_self(${name}.self ${name} UNSAFE)
	vita_create_vpk(${name}.vpk ${title_id} ${name}.self
		VERSION ${VITA_VERSION}
		NAME "Mono Test (${mode})"
		${VPK_FILES}
	)
endfunction()

monotest_variant(monotest MONO00001 interp)
monotest_variant(monotest-jit MONO00002 jit)
