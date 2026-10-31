cmake_minimum_required(VERSION 3.16)

if(NOT DEFINED CMAKE_TOOLCHAIN_FILE)
	set(CMAKE_TOOLCHAIN_FILE "$ENV{VITASDK}/share/vita.toolchain.cmake" CACHE PATH "toolchain file")
endif()

project(sdl3test C)
include("$ENV{VITASDK}/share/vita.cmake" REQUIRED)

# EXTENDED_MEMORY=ON sets ATTRIBUTE2=12 in param.sfo, which is reported to raise the MAIN memory
# budget from 256 MiB to 365 MiB. Each variant gets its own title ID so both can be installed.
option(EXTENDED_MEMORY "Request the extended memory budget (ATTRIBUTE2=12)" OFF)
if(EXTENDED_MEMORY)
	set(VITA_APP_NAME "SDL3 Test ExtMem")
	set(VITA_TITLEID  "SDLT00002")
	set(SDL3TEST_VARIANT "extended memory (ATTRIBUTE2=12)")
	set(VITA_MKSFOEX_FLAGS "${VITA_MKSFOEX_FLAGS} -d ATTRIBUTE2=12")
else()
	set(VITA_APP_NAME "SDL3 Test")
	set(VITA_TITLEID  "SDLT00001")
	set(SDL3TEST_VARIANT "default memory")
endif()
set(VITA_VERSION "01.00")

# SDL3 built from source by build-sdl3.sh into /root/vita/prefix.
find_package(SDL3 REQUIRED CONFIG)

add_executable(sdl3test main.c)
target_compile_definitions(sdl3test PRIVATE SDL3TEST_VARIANT="${SDL3TEST_VARIANT}")
target_link_libraries(sdl3test PRIVATE SDL3::SDL3-static)

vita_create_self(eboot.bin sdl3test)
vita_create_vpk(sdl3test.vpk ${VITA_TITLEID} eboot.bin
	VERSION ${VITA_VERSION}
	NAME ${VITA_APP_NAME}
)
