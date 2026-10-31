cmake_minimum_required(VERSION 3.16)

if(NOT DEFINED CMAKE_TOOLCHAIN_FILE)
	set(CMAKE_TOOLCHAIN_FILE "$ENV{VITASDK}/share/vita.toolchain.cmake" CACHE PATH "toolchain file")
endif()

project(exectest C)
include("$ENV{VITASDK}/share/vita.cmake" REQUIRED)

add_executable(exectest main.c)
target_link_libraries(exectest PRIVATE kubridge_stub_weak pthread SceLibKernel_stub SceKernelThreadMgr_stub c)

vita_create_self(eboot.bin exectest UNSAFE)
vita_create_vpk(exectest.vpk EXEC00001 eboot.bin VERSION 01.00 NAME "Exec Memory Test")
