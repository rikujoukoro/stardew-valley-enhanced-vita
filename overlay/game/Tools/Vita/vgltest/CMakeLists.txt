cmake_minimum_required(VERSION 3.16)

if(NOT DEFINED CMAKE_TOOLCHAIN_FILE)
	set(CMAKE_TOOLCHAIN_FILE "$ENV{VITASDK}/share/vita.toolchain.cmake" CACHE PATH "toolchain file")
endif()

project(vgltest C)
include("$ENV{VITASDK}/share/vita.cmake" REQUIRED)

set(VITA_APP_NAME "vitaGL Test")
set(VITA_TITLEID  "VGLT00001")
set(VITA_VERSION  "01.00")
# Same budget the game will use: extended MAIN memory.
set(VITA_MKSFOEX_FLAGS "${VITA_MKSFOEX_FLAGS} -d ATTRIBUTE2=12")

add_executable(vgltest main.c)
# vitaGL with its runtime GLSL compiler (vitaShaRK -> SceShaccCg) and the GXM/system stubs it uses.
target_link_libraries(vgltest PRIVATE
	vitaGL vitashark SceShaccCgExt mathneon taihen_stub
	SceShaccCg_stub SceKernelDmacMgr_stub SceGxm_stub SceDisplay_stub SceCtrl_stub
	SceAppMgr_stub SceCommonDialog_stub SceSysmodule_stub SceIme_stub SceLibKernel_stub
	stdc++ pthread m c
)

vita_create_self(eboot.bin vgltest UNSAFE)
vita_create_vpk(vgltest.vpk ${VITA_TITLEID} eboot.bin
	VERSION ${VITA_VERSION}
	NAME ${VITA_APP_NAME}
)
