// Mono-on-Vita launcher test (milestone 3): embeds the Vita build of the Mono runtime and runs
// app0:hello.exe, whose checks land in ux0:data/monotest/managed.txt.
//
// Execution mode: "interp" or "jit", MONOTEST_DEFAULT_MODE unless ux0:data/monotest/mode.txt says
// otherwise. Mono's own log and print output, timings and free memory go to the log file.

#include <mono/jit/jit.h>
#include <mono/metadata/assembly.h>
#include <mono/metadata/mono-gc.h>
#include <mono/utils/mono-logger.h>
#include <psp2/kernel/processmgr.h>
#include <psp2/kernel/sysmem.h>
#include <psp2/io/fcntl.h>
#include <psp2/io/stat.h>
#include <malloc.h>
#include <pthread.h>
#include <stdint.h>
#include <sys/time.h>
#include <sys/timeb.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "vita_native.h"
#include "vita_shim.h"

#ifndef MONOTEST_DEFAULT_MODE
#define MONOTEST_DEFAULT_MODE "interp"
#endif

#ifndef DATA_DIR
#define DATA_DIR "ux0:data/monotest"
#endif
#define LOG_PATH DATA_DIR "/log.txt"

// newlib's heap is carved out at startup and does not grow. With extended memory (ATTRIBUTE2=12)
// about 229 MB of MAIN is free; Mono's GC heap, metadata and the class libraries live here.
unsigned int _newlib_heap_size_user = 192 * 1024 * 1024;
// The interpreter recurses on the native stack for every managed call.
unsigned int sceUserMainThreadStackSize = 2 * 1024 * 1024;

// Not in Mono's public headers.
extern void mono_threads_set_default_stacksize(unsigned int stacksize);

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

// Experiment: how VM-domain write access behaves across threads and blocks on the device. Each
// step is logged before it runs, so a data abort leaves the failing step as the last log line.
typedef int (*ReturnInt)(void);

static int emit_return_42(void *code)
{
	uint32_t *words = (uint32_t *)code;
	words[0] = 0xE3A0002A; // mov r0, #42
	words[1] = 0xE12FFF1E; // bx lr
	vita_exec_flush(code, 8);
	return ((ReturnInt)code)();
}

typedef struct {
	void *volatile code;
	volatile int entered;
	int result;
} ExecProbe;

// Opens the domain first, then waits for main to hand over the code address.
static void *exec_probe_thread(void *arg)
{
	ExecProbe *probe = (ExecProbe *)arg;
	vita_exec_thread_enter();
	probe->entered = 1;
	while (probe->code == NULL) {
		sceKernelDelayThread(1000);
	}
	probe->result = emit_return_42(probe->code);
	return NULL;
}

static void exec_experiment(void)
{
	pthread_t thread;
	ExecProbe probe;
	void *first = vita_exec_alloc(64);
	log_line("exec: backend: %s", vita_exec_backend());
	log_line("exec: main thread writes and runs code in block 1 (%p)", first);
	log_line("exec: -> %d", emit_return_42(first));

	memset(&probe, 0, sizeof(probe));
	log_line("exec: second thread (after vita_exec_thread_enter) writes and runs code in block 1");
	pthread_create(&thread, NULL, exec_probe_thread, &probe);
	probe.code = (char *)first + 16;
	pthread_join(thread, NULL);
	log_line("exec: -> %d", probe.result);

	// A thread that opened the domain before a new VM block existed, writing into that block.
	memset(&probe, 0, sizeof(probe));
	pthread_create(&thread, NULL, exec_probe_thread, &probe);
	while (!probe.entered) {
		sceKernelDelayThread(1000);
	}
	void *second = vita_exec_alloc(4 * 1024 * 1024); // more than is left: new memory (block or commit)
	log_line("exec: new executable memory %p; a thread that started before it existed writes into it", second);
	probe.code = second;
	pthread_join(thread, NULL);
	log_line("exec: -> %d", probe.result);
	// The probe code is not freed: an emulator may keep running its stale translation when Mono
	// later writes other code at the same address.
	log_line("exec: experiment done, %u KB of executable memory reserved", (unsigned)(vita_exec_reserved() / 1024));
}

static void read_mode(char *mode, size_t size)
{
	strncpy(mode, MONOTEST_DEFAULT_MODE, size);
	SceUID fd = sceIoOpen(DATA_DIR "/mode.txt", SCE_O_RDONLY, 0);
	if (fd < 0) {
		return;
	}
	int n = sceIoRead(fd, mode, size - 1);
	sceIoClose(fd);
	if (n <= 0) {
		strncpy(mode, MONOTEST_DEFAULT_MODE, size);
		return;
	}
	mode[n] = '\0';
	for (int i = 0; i < n; i++) {
		if (mode[i] == '\r' || mode[i] == '\n' || mode[i] == ' ') {
			mode[i] = '\0';
		}
	}
}

int main(void)
{
	char mode[16];
	sceIoMkdir(DATA_DIR, 0777);
	sceIoRemove(LOG_PATH);
	sceIoRemove(DATA_DIR "/managed.txt");
	read_mode(mode, sizeof(mode));
	log_line("monotest start, mode=%s, built %s %s", mode, __DATE__, __TIME__);
	log_free_memory("start");
	exec_experiment();
	{
		// Mono's timed waits use gettimeofday deadlines; pthread-embedded measures them with _ftime.
		struct timeval tv;
		struct timeb tb;
		gettimeofday(&tv, NULL);
		ftime(&tb);
		log_line("clocks: gettimeofday %lld.%03ld, ftime %lld.%03u",
			(long long)tv.tv_sec, (long)(tv.tv_usec / 1000), (long long)tb.time, (unsigned)tb.millitm);
	}

	mono_trace_set_log_handler(mono_log, NULL);
	mono_trace_set_print_handler(mono_print);
	mono_trace_set_printerr_handler(mono_print);
	mono_threads_set_default_stacksize(1024 * 1024);
	// [DllImport("System.Native")] resolves against the statically linked table.
	vita_native_install();
	// No SIGSEGV is ever delivered on the Vita: JIT code must check for null explicitly.
	setenv("MONO_DEBUG", "explicit-null-checks", 1);

	if (strcmp(mode, "jit") != 0) {
		mono_jit_set_aot_mode(MONO_AOT_MODE_INTERP_ONLY);
	}
	mono_set_dirs("app0:/lib", "app0:/etc");
	// There is no GAC in the VPK: framework assemblies are probed in the profile directory.
	mono_set_assemblies_path("app0:/lib/mono/4.5");

	SceUInt64 start = sceKernelGetProcessTimeWide();
	MonoDomain *domain = mono_jit_init_version("monotest", "v4.0.30319");
	SceUInt64 init_done = sceKernelGetProcessTimeWide();
	if (domain == NULL) {
		log_line("mono_jit_init_version failed");
		return 1;
	}
	log_line("runtime up in %llu ms", (init_done - start) / 1000ULL);
	log_free_memory("after init");

	MonoAssembly *assembly = mono_domain_assembly_open(domain, "app0:/hello.exe");
	if (assembly == NULL) {
		log_line("could not open app0:/hello.exe");
		return 1;
	}
	// Managed code sees device paths rooted at "/" (see vita_paths.c).
	char *argv[] = { (char *)"/app0:/hello.exe", (char *)"/" DATA_DIR };
	int result = mono_jit_exec(domain, assembly, 2, argv);
	SceUInt64 done = sceKernelGetProcessTimeWide();
	log_line("Main returned %d (42 = all checks passed) after %llu ms", result, (done - init_done) / 1000ULL);
	log_line("GC heap %lld KB, used %lld KB; exec memory in use %u KB of %u KB reserved, fallback arenas %d",
		(long long)mono_gc_get_heap_size() / 1024, (long long)mono_gc_get_used_size() / 1024,
		(unsigned)(vita_exec_in_use() / 1024), (unsigned)(vita_exec_reserved() / 1024), vita_exec_fallback_arenas());
	log_free_memory("end");
	// Logged around the release and exit, which once froze the console (kubridge reservations).
	log_line("releasing executable memory");
	vita_exec_shutdown();
	log_line("monotest end (executable memory released), exiting");
	sceKernelExitProcess(0);
	return 0;
}
