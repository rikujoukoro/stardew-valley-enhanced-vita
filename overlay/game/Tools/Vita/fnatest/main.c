// FNA-on-Vita launcher test: embeds the Vita Mono runtime, serves SDL3, FNA3D and FAudio to FNA's
// [DllImport] from statically linked tables, and runs app0:fnatest.exe (an FNA Game).
// Runtime log: ux0:data/fnatest/log.txt; the game's own log: ux0:data/fnatest/fna.txt.

#include <mono/jit/jit.h>
#include <mono/metadata/assembly.h>
#include <mono/metadata/mono-gc.h>
#include <mono/utils/mono-logger.h>
#include <psp2/io/fcntl.h>
#include <psp2/io/stat.h>
#include <psp2/kernel/processmgr.h>
#include <psp2/kernel/sysmem.h>
#include <malloc.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "vita_native.h"
#include "vita_shim.h"

#define DATA_DIR "ux0:data/fnatest"
#define LOG_PATH DATA_DIR "/log.txt"

// The newlib heap holds Mono's GC heap, metadata and class libraries. vitaGL's pools (textures,
// buffers: 32 MB MAIN + CDRAM + 16 MB phycont by default) and JIT code (kubridge) live outside it.
unsigned int _newlib_heap_size_user = 160 * 1024 * 1024;
// The interpreter and deep managed call chains recurse on the native stack.
unsigned int sceUserMainThreadStackSize = 2 * 1024 * 1024;

extern void mono_threads_set_default_stacksize(unsigned int stacksize);
extern const VitaNativeSymbol vita_sdl3_symbols[];
extern const VitaNativeSymbol vita_fna3d_symbols[];
extern const VitaNativeSymbol vita_faudio_symbols[];

static void log_line(const char *fmt, ...)
{
	char line[1024];
	va_list args;
	va_start(args, fmt);
	int length = vsnprintf(line, sizeof(line) - 1, fmt, args);
	va_end(args);
	if (length < 0) {
		return;
	}
	if (length > (int)sizeof(line) - 2) {
		length = (int)sizeof(line) - 2;
	}
	if (length == 0 || line[length - 1] != '\n') {
		line[length++] = '\n';
	}
	SceUID fd = sceIoOpen(LOG_PATH, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_APPEND, 0666);
	if (fd >= 0) {
		sceIoWrite(fd, line, (SceSize)length);
		sceIoClose(fd);
	}
}

static void log_free_memory(const char *when)
{
	SceKernelFreeMemorySizeInfo info;
	info.size = sizeof(info);
	if (sceKernelGetFreeMemorySize(&info) == 0) {
		struct mallinfo heap = mallinfo();
		log_line("[%s] kernel free: user %d KB, cdram %d KB, phycont %d KB; newlib heap in use %d KB",
			when, info.size_user / 1024, info.size_cdram / 1024, info.size_phycont / 1024, heap.uordblks / 1024);
	}
}

static void mono_log(const char *domain, const char *level, const char *message, mono_bool fatal, void *user_data)
{
	(void)user_data;
	log_line("mono[%s/%s]%s %s", domain ? domain : "-", level ? level : "-", fatal ? " FATAL" : "", message);
}

static void mono_print(const char *string, mono_bool is_stdout)
{
	log_line("%s %s", is_stdout ? "stdout:" : "stderr:", string);
}

int main(void)
{
	sceIoMkdir(DATA_DIR, 0777);
	sceIoRemove(LOG_PATH);
	sceIoRemove(DATA_DIR "/fna.txt");
	log_line("fnatest start, built %s %s", __DATE__, __TIME__);
	log_free_memory("start");

	mono_trace_set_log_handler(mono_log, NULL);
	mono_trace_set_print_handler(mono_print);
	mono_trace_set_printerr_handler(mono_print);
	mono_threads_set_default_stacksize(1024 * 1024);
	// No SIGSEGV is ever delivered on the Vita: JIT code must check for null explicitly.
	setenv("MONO_DEBUG", "explicit-null-checks", 1);

	vita_native_install();
	vita_native_register_library("SDL3", vita_sdl3_symbols);
	vita_native_register_library("FNA3D", vita_fna3d_symbols);
	vita_native_register_library("FAudio", vita_faudio_symbols);

	mono_set_dirs("app0:/lib", "app0:/etc");
	mono_set_assemblies_path("app0:/lib/mono/4.5");

	SceUInt64 start = sceKernelGetProcessTimeWide();
	MonoDomain *domain = mono_jit_init_version("fnatest", "v4.0.30319");
	if (domain == NULL) {
		log_line("mono_jit_init_version failed");
		return 1;
	}
	log_line("runtime up in %llu ms", (sceKernelGetProcessTimeWide() - start) / 1000ULL);
	log_free_memory("after init");

	MonoAssembly *assembly = mono_domain_assembly_open(domain, "app0:/fnatest.exe");
	if (assembly == NULL) {
		log_line("could not open app0:/fnatest.exe");
		return 1;
	}
	// Managed code sees device paths rooted at "/" (see vita_paths.c).
	char *argv[] = { (char *)"/app0:/fnatest.exe", (char *)"/" DATA_DIR };
	int result = mono_jit_exec(domain, assembly, 2, argv);
	log_line("Main returned %d (42 = clean run) after %llu ms", result, (sceKernelGetProcessTimeWide() - start) / 1000ULL);
	log_line("GC heap %lld KB, used %lld KB; executable memory %u KB (%s)",
		(long long)mono_gc_get_heap_size() / 1024, (long long)mono_gc_get_used_size() / 1024,
		(unsigned)(vita_exec_reserved() / 1024), vita_exec_backend());
	log_free_memory("end");
	// Exiting with kubridge memory still committed freezes the console.
	vita_exec_shutdown();
	log_line("fnatest end, exiting");
	sceKernelExitProcess(0);
	return 0;
}
