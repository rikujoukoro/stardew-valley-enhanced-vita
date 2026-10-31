# Links a Vita executable against the Mono runtime built by build-mono-vita.sh:
#   include(path/to/vita-mono.cmake)
#   vita_link_mono(<target>)
# Headers: <mono/jit/jit.h> and friends, plus vita_shim.h / vita_native.h.

set(VITA_MONO_PREFIX "/root/vita/mono-vita" CACHE PATH "install prefix of build-mono-vita.sh")

# libc entry points that take paths; vita_paths.c translates managed "/ux0:/..." paths.
set(VITA_MONO_WRAPPED_PATH_FUNCTIONS
	open fopen stat lstat access mkdir rmdir unlink remove rename opendir chdir chmod truncate
	utime utimes realpath getcwd)

function(vita_link_mono target)
	target_include_directories(${target} PRIVATE
		"${VITA_MONO_PREFIX}/include/mono-2.0" "${VITA_MONO_PREFIX}/include/vita")
	target_link_directories(${target} PRIVATE "${VITA_MONO_PREFIX}/lib")
	set(wraps "")
	foreach(f ${VITA_MONO_WRAPPED_PATH_FUNCTIONS})
		string(APPEND wraps ",--wrap=${f}")
	endforeach()
	# The JIT's safepoint poll, where vita-sampler.c takes its samples (on the main thread itself).
	string(APPEND wraps ",--wrap=mono_threads_state_poll")
	target_link_options(${target} PRIVATE "-Wl${wraps}")
	# mono-profiler-aot and vita-sampler (build-mono-vita.sh aotprof, sampler) are only pulled in by
	# hosts that use them.
	target_link_libraries(${target} PRIVATE
		vitanative mono-profiler-aot vita-sampler monosgen-2.0 vitashim
		# Weak: the app still starts without kubridge.skprx and falls back (vita_exec_backend()).
		kubridge_stub_weak
		SceKernelDmacMgr_stub SceLibKernel_stub SceKernelThreadMgr_stub SceSysmodule_stub
		SceAppMgr_stub SceRtc_stub
		z pthread m c)
endfunction()
