// Stardew Valley launcher for the PS Vita: embeds the Vita Mono runtime, serves SDL3, FNA3D and FAudio
// to FNA's [DllImport] from statically linked tables, and runs the player's own game build from
// ux0:data/StardewValley (Stardew Valley.exe, its DLLs and Content/; nothing of the game is in the VPK).
// The VPK carries only this runtime: Mono's class libraries and FNA.dll.
//
// Launcher log: ux0:data/StardewValley/vita-log.txt (runtime, SDL and vitaGL messages, memory every
// 10 s). The game's own logs are in ux0:data/StardewValley/ErrorLogs/ (SV_LOGFILE=1).

#include <SDL3/SDL.h>
#include <mono/jit/jit.h>
#include <mono/metadata/assembly.h>
#include <mono/metadata/class.h>
#include <mono/metadata/debug-helpers.h>
#include <mono/metadata/exception.h>
#include <mono/metadata/image.h>
#include <mono/metadata/object.h>
#include <mono/metadata/profiler.h>
#include <mono/metadata/mono-gc.h>
#include <mono/utils/mono-logger.h>
#include <psp2/io/fcntl.h>
#include <psp2/io/stat.h>
#include <psp2/kernel/processmgr.h>
#include <psp2/kernel/sysmem.h>
#include <psp2/power.h>
#include <psp2/appmgr.h>
#include <psp2/ime_dialog.h>
#include <psp2/touch.h>
#include <malloc.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <vitaGL.h>
#include "vita_native.h"
#include "vita_sdl_overrides.h"
#include "vita_shim.h"

#define GAME_DIR "ux0:data/StardewValley"
#define GAME_EXE GAME_DIR "/Stardew Valley.exe"
#define LOG_PATH GAME_DIR "/vita-log.txt"
#define MEMORY_LOG_INTERVAL_US (10 * 1000 * 1000)

// MAIN memory is ~350 MB with ATTRIBUTE2=12. The newlib heap holds Mono's GC heap, metadata and the
// loaded assemblies; outside it stay vitaGL's 64 MB RAM pool, JIT code (kubridge, 4 MB arenas),
// thread stacks and SDL. Textures go to vitaGL's CDRAM pool, a separate 112 MB.
unsigned int _newlib_heap_size_user = 230 * 1024 * 1024;
// JIT frames and the game's deep call chains (XML serialization of saves) run on the main thread.
unsigned int sceUserMainThreadStackSize = 4 * 1024 * 1024;

extern void mono_threads_set_default_stacksize(unsigned int stacksize);
// Takes the list as is: mono_set_assemblies_path splits at ':', which every Vita path contains.
extern void mono_set_assemblies_path_direct(char **path);
extern const VitaNativeSymbol vita_sdl3_symbols[];
extern const VitaNativeSymbol vita_fna3d_symbols[];
extern const VitaNativeSymbol vita_faudio_symbols[];
extern int VitaGLGlue_IsInitialized(void);
// Refreshes each frame stays up while vsync is on (fna3d-vita/vita_gl_glue.c): the game's 30 fps cap sets 2.
extern void VitaGLGlue_SetVsyncInterval(int interval);
extern int VitaGLGlue_GetVsyncInterval(void);
extern void VitaGLGlue_TakeSwapStats(uint64_t *total_us, uint32_t *count);
extern int FNA3D_VitaThread_TakeStats(uint64_t *busy_us, uint64_t *wait_us);
// What the render thread replayed: draw calls, SpriteBatch sprites, render target switches applied, SpriteBatch
// batches, switches dropped with nothing drawn, and the time spent in SpriteBatch replays (us).
extern void FNA3D_VitaThread_TakeCounts(uint32_t *out, uint64_t *sprite_us);

static SceUInt64 start_time;
static int restart_requested;
static volatile int observers_stopping;
static pthread_t monitor_handle, prof_sampler_handle;
static int monitor_started, prof_sampler_started;
static int vita_port_request_restart(void)
{
	SceIoStat stat;
	if (sceIoGetstat("app0:eboot.bin", &stat) < 0) return -1;
	restart_requested = 1;
	return 0;
}
static void vita_port_cancel_ime(void)
{
	if (sceImeDialogGetStatus() == SCE_COMMON_DIALOG_STATUS_RUNNING) sceImeDialogAbort();
	if (sceImeDialogGetStatus() == SCE_COMMON_DIALOG_STATUS_FINISHED) sceImeDialogTerm();
}
static pthread_mutex_t log_lock = PTHREAD_MUTEX_INITIALIZER;

static void log_line(const char *fmt, ...)
{
	char line[4096]; // unhandled-exception stack traces arrive as one message
	int prefix = snprintf(line, sizeof(line), "[%7.2f] ", (double)(sceKernelGetProcessTimeWide() - start_time) / 1000000.0);
	va_list args;
	va_start(args, fmt);
	int length = vsnprintf(line + prefix, sizeof(line) - prefix - 1, fmt, args);
	va_end(args);
	if (length < 0) {
		return;
	}
	length += prefix;
	if (length > (int)sizeof(line) - 2) {
		length = (int)sizeof(line) - 2;
	}
	if (line[length - 1] != '\n') {
		line[length++] = '\n';
	}
	pthread_mutex_lock(&log_lock);
	SceUID fd = sceIoOpen(LOG_PATH, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_APPEND, 0666);
	if (fd >= 0) {
		sceIoWrite(fd, line, (SceSize)length);
		sceIoClose(fd);
	}
	pthread_mutex_unlock(&log_lock);
}

static void log_memory(const char *when)
{
	log_line("[clocks %s] arm %d MHz, bus %d MHz, gpu %d MHz, xbar %d MHz", when,
		scePowerGetArmClockFrequency(), scePowerGetBusClockFrequency(),
		scePowerGetGpuClockFrequency(), scePowerGetGpuXbarClockFrequency());
	SceKernelFreeMemorySizeInfo info;
	info.size = sizeof(info);
	if (sceKernelGetFreeMemorySize(&info) != 0) {
		return;
	}
	struct mallinfo heap = mallinfo();
	log_line("[mem %s] kernel free: user %d KB, cdram %d KB, phycont %d KB; newlib heap in use %d KB of %u KB; "
		"JIT code %u KB; vitaGL free: cdram %u KB, ram %u KB",
		when, info.size_user / 1024, info.size_cdram / 1024, info.size_phycont / 1024, heap.uordblks / 1024,
		_newlib_heap_size_user / 1024, (unsigned)(vita_exec_reserved() / 1024),
		VitaGLGlue_IsInitialized() ? (unsigned)(vglMemFree(VGL_MEM_VRAM) / 1024) : 0,
		VitaGLGlue_IsInitialized() ? (unsigned)(vglMemFree(VGL_MEM_RAM) / 1024) : 0);
}

// --- Startup profile: where the time goes before the first frame -----------------------------------
// Counts JIT compiles, exceptions and collections, and names every method whose compile takes longer
// than SLOW_JIT_US. Logged with the memory line every 10 s.

#define SLOW_JIT_US (250 * 1000)
// With a file or folder named jit-log in the game folder, every method compiled after the first
// JIT_LOG_AFTER_US (startup and the title screen) gets a "jit" line: the code that stalls play the
// first time it runs, candidates for aot-stardew.sh.
#define JIT_LOG_PATH GAME_DIR "/jit-log"
#define JIT_LOG_AFTER_US (20ULL * 1000000ULL)
static int jit_logging;
#define MAX_JIT_NESTING 16
#define MAX_LOGGED_EXCEPTIONS 40

struct _MonoProfiler {
	int unused;
};

typedef struct JitStack {
	int depth;
	SceUInt64 start[MAX_JIT_NESTING];
} JitStack;

static MonoProfiler profiler;
static pthread_key_t jit_stack_key;
static volatile unsigned jit_methods, jit_slow_methods, exceptions_thrown, gc_count;
static volatile SceUInt64 jit_time_us, gc_time_us, gc_start, gc_pause_start, gc_pause_us, gc_pause_max_us;

static JitStack *jit_stack(void)
{
	JitStack *stack = (JitStack *)pthread_getspecific(jit_stack_key);
	if (stack == NULL) {
		stack = (JitStack *)calloc(1, sizeof(JitStack));
		pthread_setspecific(jit_stack_key, stack);
	}
	return stack;
}

static void on_jit_begin(MonoProfiler *prof, MonoMethod *method)
{
	(void)prof;
	(void)method;
	JitStack *stack = jit_stack();
	if (stack != NULL && stack->depth < MAX_JIT_NESTING) {
		stack->start[stack->depth] = sceKernelGetProcessTimeWide();
	}
	if (stack != NULL) {
		stack->depth++;
	}
}

// jit-log lines are collected here and written in one go (memory_monitor every 10 s, or when full):
// loading a save compiles thousands of methods, and a file append each took minutes on the card.
#define JIT_LOG_BUFFER_SIZE (64 * 1024)
static char jit_log_buffer[JIT_LOG_BUFFER_SIZE];
static int jit_log_used;
static pthread_mutex_t jit_log_lock = PTHREAD_MUTEX_INITIALIZER;

static void jit_log_flush_locked(void)
{
	if (jit_log_used == 0) {
		return;
	}
	pthread_mutex_lock(&log_lock);
	SceUID fd = sceIoOpen(LOG_PATH, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_APPEND, 0666);
	if (fd >= 0) {
		sceIoWrite(fd, jit_log_buffer, (SceSize)jit_log_used);
		sceIoClose(fd);
	}
	pthread_mutex_unlock(&log_lock);
	jit_log_used = 0;
}

static void jit_log_flush(void)
{
	pthread_mutex_lock(&jit_log_lock);
	jit_log_flush_locked();
	pthread_mutex_unlock(&jit_log_lock);
}

static void jit_log_add(SceUInt64 elapsed_us, const char *name)
{
	char line[512];
	int length = snprintf(line, sizeof(line), "[%7.2f] jit %.1f ms: %s\n",
		(double)(sceKernelGetProcessTimeWide() - start_time) / 1000000.0, elapsed_us / 1000.0, name);
	if (length <= 0) {
		return;
	}
	if (length >= (int)sizeof(line)) {
		length = (int)sizeof(line) - 1;
		line[length - 1] = '\n';
	}
	pthread_mutex_lock(&jit_log_lock);
	if (jit_log_used + length > JIT_LOG_BUFFER_SIZE) {
		jit_log_flush_locked();
	}
	memcpy(jit_log_buffer + jit_log_used, line, (size_t)length);
	jit_log_used += length;
	pthread_mutex_unlock(&jit_log_lock);
}

static void jit_end(MonoMethod *method, const char *how)
{
	JitStack *stack = jit_stack();
	if (stack == NULL || stack->depth == 0) {
		return;
	}
	stack->depth--;
	if (stack->depth >= MAX_JIT_NESTING) {
		return;
	}
	SceUInt64 elapsed = sceKernelGetProcessTimeWide() - stack->start[stack->depth];
	__sync_fetch_and_add(&jit_methods, 1);
	if (stack->depth == 0) {
		// Outermost compile only, so nested compiles are not counted twice.
		jit_time_us += elapsed;
	}
	if (elapsed >= SLOW_JIT_US) {
		__sync_fetch_and_add(&jit_slow_methods, 1);
		char *name = mono_method_full_name(method, 1);
		log_line("slow JIT%s: %llu ms %s", how, elapsed / 1000ULL, name != NULL ? name : "?");
		mono_free(name);
	} else if (jit_logging && sceKernelGetProcessTimeWide() - start_time >= JIT_LOG_AFTER_US) {
		char *name = mono_method_full_name(method, 0);
		jit_log_add(elapsed, name != NULL ? name : "?");
		mono_free(name);
	}
}

// Profile-guided AOT (aot-stardew.sh PROFILE=...): with a file or folder named aot-profile in the game
// folder, Mono's AOT profiler records every JIT-compiled method in stardew.aotprofile. The runtime never
// shuts down on the Vita, so the file is rewritten from the JIT callback at most once a minute (a
// short stall: ~15k methods are formatted), and once more when Main returns.
#define AOT_PROFILE_PATH GAME_DIR "/stardew.aotprofile"
#define AOT_PROFILE_SAVE_US (60ULL * 1000000ULL)
void mono_profiler_init_aot(const char *desc);
void mono_profiler_aot_vita_save(void);
static int aot_profiling;
static SceUInt64 aot_profile_saved;

static void start_aot_profile(void)
{
	SceIoStat aot_profile;
	if (sceIoGetstat(GAME_DIR "/aot-profile", &aot_profile) < 0) {
		return;
	}
	mono_profiler_init_aot("aot:output=" AOT_PROFILE_PATH);
	aot_profiling = 1;
	aot_profile_saved = sceKernelGetProcessTimeWide();
	log_line("aot-profile found: JIT-compiled methods are recorded in " AOT_PROFILE_PATH);
}

static void save_aot_profile(int force)
{
	if (!aot_profiling) {
		return;
	}
	SceUInt64 now = sceKernelGetProcessTimeWide();
	SceUInt64 last = aot_profile_saved;
	if (!force && (now - last < AOT_PROFILE_SAVE_US || !__sync_bool_compare_and_swap(&aot_profile_saved, last, now))) {
		return;
	}
	aot_profile_saved = now;
	mono_profiler_aot_vita_save();
	log_line("AOT profile saved (%llu ms)", (sceKernelGetProcessTimeWide() - now) / 1000ULL);
}

static void on_jit_done(MonoProfiler *prof, MonoMethod *method, MonoJitInfo *jinfo)
{
	(void)prof;
	(void)jinfo;
	jit_end(method, "");
	save_aot_profile(0);
}

static void on_jit_failed(MonoProfiler *prof, MonoMethod *method)
{
	(void)prof;
	jit_end(method, " (failed)");
}

static void on_exception_throw(MonoProfiler *prof, MonoObject *exception)
{
	(void)prof;
	unsigned count = __sync_add_and_fetch(&exceptions_thrown, 1);
	if (count <= MAX_LOGGED_EXCEPTIONS) {
		MonoClass *klass = mono_object_get_class(exception);
		log_line("exception thrown #%u: %s.%s", count, mono_class_get_namespace(klass), mono_class_get_name(klass));
	}
}

static void on_gc_event(MonoProfiler *prof, MonoProfilerGCEvent event, uint32_t generation, mono_bool is_serial)
{
	(void)prof;
	(void)generation;
	(void)is_serial;
	if (event == MONO_GC_EVENT_START) {
		gc_start = sceKernelGetProcessTimeWide();
		gc_count++;
	} else if (event == MONO_GC_EVENT_END && gc_start != 0) {
		gc_time_us += sceKernelGetProcessTimeWide() - gc_start;
		gc_start = 0;
	} else if (event == MONO_GC_EVENT_PRE_STOP_WORLD) {
		// The whole pause, stopping and restarting the other threads included (START..END is only the
		// collection itself).
		gc_pause_start = sceKernelGetProcessTimeWide();
	} else if (event == MONO_GC_EVENT_POST_START_WORLD && gc_pause_start != 0) {
		SceUInt64 pause = sceKernelGetProcessTimeWide() - gc_pause_start;
		gc_pause_start = 0;
		gc_pause_us += pause;
		if (pause > gc_pause_max_us) {
			gc_pause_max_us = pause;
		}
		if (pause >= 30000) {
			log_line("GC pause %llu ms (generation %u)", pause / 1000ULL, generation);
		}
	}
}

// --- Sampling profiler (on when a folder or file named profile-calls is in the game folder) -------
// Methods of the game's own assemblies are instrumented: entering and leaving one pushes and pops
// it on a shadow stack of the main thread. A native thread on another core looks at that stack every
// millisecond: the top method gets a "self" sample, every method on the stack an "inclusive" one.
// Names are resolved when a method is compiled (on its JIT thread, attached to Mono); the sampler
// only reads this file's own tables. Instrumentation slows the game down several times; the
// proportions are what count.

#define PROFILE_CALLS_PATH GAME_DIR "/profile-calls"
#define PROF_CAPACITY 16384
#define PROF_STACK_DEPTH 256
#define PROF_REPORT_LINES 25
#define PROF_SAMPLE_US 1000

typedef struct ProfEntry {
	MonoMethod *method;
	char name[88];
	volatile uint32_t self_samples;
	volatile uint32_t inclusive_samples;
} ProfEntry;

static int profiling;
static pthread_t main_thread;
static ProfEntry *prof_entries;
static pthread_mutex_t prof_lock = PTHREAD_MUTEX_INITIALIZER;
static ProfEntry *volatile prof_stack[PROF_STACK_DEPTH];
static volatile int prof_depth;
static volatile uint32_t prof_samples, prof_idle_samples;

static uint32_t prof_hash(MonoMethod *method)
{
	uint32_t h = (uint32_t)(uintptr_t)method;
	h ^= h >> 15;
	h *= 0x2c1b3c6dU;
	h ^= h >> 12;
	return h & (PROF_CAPACITY - 1);
}

// Lock-free lookup: entries are only ever added (under prof_lock), method first-written last.
static ProfEntry *prof_find(MonoMethod *method)
{
	for (uint32_t i = prof_hash(method), n = 0; n < PROF_CAPACITY; i = (i + 1) & (PROF_CAPACITY - 1), n++) {
		MonoMethod *m = prof_entries[i].method;
		if (m == method) {
			return &prof_entries[i];
		}
		if (m == NULL) {
			return NULL;
		}
	}
	return NULL;
}

static MonoProfilerCallInstrumentationFlags prof_filter(MonoProfiler *prof, MonoMethod *method)
{
	(void)prof;
	MonoClass *klass = mono_method_get_class(method);
	const char *image = mono_image_get_name(mono_class_get_image(klass));
	if (image == NULL || (strcmp(image, "Stardew Valley") != 0 && strcmp(image, "xTile") != 0 &&
		strcmp(image, "FNA") != 0 && strcmp(image, "StardewValley.GameData") != 0)) {
		return MONO_PROFILER_CALL_INSTRUMENTATION_NONE;
	}
	pthread_mutex_lock(&prof_lock);
	if (prof_find(method) == NULL) {
		for (uint32_t i = prof_hash(method), n = 0; n < PROF_CAPACITY; i = (i + 1) & (PROF_CAPACITY - 1), n++) {
			if (prof_entries[i].method == NULL) {
				snprintf(prof_entries[i].name, sizeof(prof_entries[i].name), "%s:%s", mono_class_get_name(klass), mono_method_get_name(method));
				__sync_synchronize();
				prof_entries[i].method = method;
				break;
			}
		}
	}
	pthread_mutex_unlock(&prof_lock);
	return MONO_PROFILER_CALL_INSTRUMENTATION_ENTER | MONO_PROFILER_CALL_INSTRUMENTATION_LEAVE |
		MONO_PROFILER_CALL_INSTRUMENTATION_TAIL_CALL | MONO_PROFILER_CALL_INSTRUMENTATION_EXCEPTION_LEAVE;
}

static void prof_enter(MonoProfiler *prof, MonoMethod *method, MonoProfilerCallContext *context)
{
	(void)prof;
	(void)context;
	if (!pthread_equal(pthread_self(), main_thread)) {
		return;
	}
	int depth = prof_depth;
	if (depth < PROF_STACK_DEPTH) {
		prof_stack[depth] = prof_find(method);
	}
	prof_depth = depth + 1;
}

static void prof_pop(void)
{
	if (pthread_equal(pthread_self(), main_thread) && prof_depth > 0) {
		prof_depth--;
	}
}

static void prof_leave(MonoProfiler *prof, MonoMethod *method, MonoProfilerCallContext *context)
{
	(void)prof;
	(void)method;
	(void)context;
	prof_pop();
}

static void prof_tail_call(MonoProfiler *prof, MonoMethod *method, MonoMethod *target)
{
	(void)prof;
	(void)method;
	(void)target;
	prof_pop();
}

static void prof_exception_leave(MonoProfiler *prof, MonoMethod *method, MonoObject *exception)
{
	(void)prof;
	(void)method;
	(void)exception;
	prof_pop();
}

static void *prof_sampler(void *arg)
{
	(void)arg;
	for (;;) {
		sceKernelDelayThread(PROF_SAMPLE_US);
		if (observers_stopping) break;
		int depth = prof_depth;
		prof_samples++;
		if (depth <= 0) {
			prof_idle_samples++;
			continue;
		}
		if (depth > PROF_STACK_DEPTH) {
			depth = PROF_STACK_DEPTH;
		}
		ProfEntry *top = prof_stack[depth - 1];
		if (top != NULL) {
			top->self_samples++;
		}
		for (int i = 0; i < depth; i++) {
			ProfEntry *entry = prof_stack[i];
			// Count a recursive method once per sample.
			int seen = 0;
			for (int j = 0; j < i && !seen; j++) {
				seen = prof_stack[j] == entry;
			}
			if (entry != NULL && !seen) {
				entry->inclusive_samples++;
			}
		}
	}
	return NULL;
}

static int prof_compare_self(const void *a, const void *b)
{
	return (int)(*(ProfEntry *const *)b)->self_samples - (int)(*(ProfEntry *const *)a)->self_samples;
}

static int prof_compare_inclusive(const void *a, const void *b)
{
	return (int)(*(ProfEntry *const *)b)->inclusive_samples - (int)(*(ProfEntry *const *)a)->inclusive_samples;
}

// Native only: reads and resets this file's counters.
static void prof_report(void)
{
	static ProfEntry *sorted[PROF_CAPACITY];
	int count = 0;
	for (int i = 0; i < PROF_CAPACITY; i++) {
		if (prof_entries[i].method != NULL && prof_entries[i].inclusive_samples > 0) {
			sorted[count++] = &prof_entries[i];
		}
	}
	uint32_t samples = prof_samples;
	uint32_t idle = prof_idle_samples;
	if (samples == 0) {
		return;
	}
	log_line("[calls] %u samples, %u%% outside the game's code (runtime, GC, swap, other assemblies)",
		samples, idle * 100 / samples);
	qsort(sorted, count, sizeof(sorted[0]), prof_compare_self);
	for (int i = 0; i < count && i < PROF_REPORT_LINES; i++) {
		log_line("[calls] self %5.1f%% incl %5.1f%%  %s", sorted[i]->self_samples * 100.0 / samples,
			sorted[i]->inclusive_samples * 100.0 / samples, sorted[i]->name);
	}
	qsort(sorted, count, sizeof(sorted[0]), prof_compare_inclusive);
	for (int i = 0; i < count && i < PROF_REPORT_LINES; i++) {
		log_line("[calls] incl %5.1f%% self %5.1f%%  %s", sorted[i]->inclusive_samples * 100.0 / samples,
			sorted[i]->self_samples * 100.0 / samples, sorted[i]->name);
	}
	for (int i = 0; i < count; i++) {
		sorted[i]->self_samples = 0;
		sorted[i]->inclusive_samples = 0;
	}
	prof_samples = 0;
	prof_idle_samples = 0;
}

static void install_profiler(void)
{
	pthread_key_create(&jit_stack_key, free);
	MonoProfilerHandle handle = mono_profiler_create(&profiler);
	SceIoStat jit_log;
	if (sceIoGetstat(JIT_LOG_PATH, &jit_log) >= 0) {
		jit_logging = 1;
		log_line("jit-log found: methods compiled after the first %llu s are logged", JIT_LOG_AFTER_US / 1000000ULL);
	}
	SceIoStat profile_calls;
	if (sceIoGetstat(PROFILE_CALLS_PATH, &profile_calls) >= 0) {
		prof_entries = (ProfEntry *)calloc(PROF_CAPACITY, sizeof(ProfEntry));
		if (prof_entries != NULL) {
			profiling = 1;
			main_thread = pthread_self();
			mono_profiler_set_call_instrumentation_filter_callback(handle, prof_filter);
			mono_profiler_set_method_enter_callback(handle, prof_enter);
			mono_profiler_set_method_leave_callback(handle, prof_leave);
			mono_profiler_set_method_tail_call_callback(handle, prof_tail_call);
			mono_profiler_set_method_exception_leave_callback(handle, prof_exception_leave);
			if (pthread_create(&prof_sampler_handle, NULL, prof_sampler, NULL) == 0)
				prof_sampler_started = 1;
			log_line("profile-calls found: sampling the game's methods (the game runs slower)");
		}
	}
	mono_profiler_set_jit_begin_callback(handle, on_jit_begin);
	mono_profiler_set_jit_done_callback(handle, on_jit_done);
	mono_profiler_set_jit_failed_callback(handle, on_jit_failed);
	mono_profiler_set_exception_throw_callback(handle, on_exception_throw);
	mono_profiler_set_gc_event_callback(handle, on_gc_event);
}

// --- Stack sampling (on when a folder or file named profile-samples is in the game folder) ----------
// mono-vita/vita-sampler.c has the main thread walk its own managed stack at its next safepoint every
// SAMPLE_INTERVAL_US; every 10 s it logs the methods most often on top ("self") and on the stack
// ("incl"), [samples] lines, with what the sampling itself cost. Unlike profile-calls nothing is
// instrumented: the game runs at almost full speed and the proportions hold.
#define PROFILE_SAMPLES_PATH GAME_DIR "/profile-samples"
#define SAMPLE_INTERVAL_US 10000
int vita_sampler_start(unsigned sample_interval_us, void (*log)(const char *fmt, ...));
void vita_sampler_stop(void);

static void start_sampler(void)
{
	SceIoStat profile_samples;
	if (sceIoGetstat(PROFILE_SAMPLES_PATH, &profile_samples) < 0) {
		return;
	}
	if (vita_sampler_start(SAMPLE_INTERVAL_US, log_line)) {
		log_line("profile-samples found: sampling the main thread's stack every %d ms", SAMPLE_INTERVAL_US / 1000);
	} else {
		log_line("profile-samples found, but the sampler did not start");
	}
}

// FAudio's first-buffer read cache (fna-vita/patch-faudio.py): hits, misses, miss ms, slowest miss.
extern void FACT_vita_take_read_stats(uint32_t *stats);
// Refills that found their wave already silent (an audible drop in that sound).
extern uint32_t FACT_vita_take_underruns(void);
// The longest a playing wave's refill waited for the stream reader (ms); past a buffer's length it ran dry.
extern uint32_t FACT_vita_take_refill_wait(void);
// The mixer callbacks (patch-faudio.py): slowest, longest gap, average (ms), count; then the mix content:
// holes of silence, longest hole (ms), repeated buffers, end of the last hole (ms, SDL clock).
extern void FAudio_vita_take_mix_stats(uint32_t *ms);
// The mix's level before the limiter (patch-faudio.py, FAudio_vita_limit): peak x1000, samples over full
// scale, largest step between two samples x1000, and when that step came (ms, SDL clock).
extern void FAudio_vita_take_level_stats(uint32_t *out);
// SDL's Vita audio output (sdl-vita/patch-sdl-audio.py): outputs, empty-port outputs, longest wait, longest gap (us).
extern uint32_t SDL_vita_audio_stats[4];
// The rate SDL's Vita driver opened the output port at: 48000 main port, less BGM port.
extern int SDL_vita_audio_rate;
// The sound capture (patch-faudio.py): 0 off, 1 recording, 2 written; start in SDL ticks.
extern int FAudio_vita_capture_state;
extern uint32_t FAudio_vita_capture_start_ms;

static void log_profile(void)
{
	uint32_t reads[4];
	FACT_vita_take_read_stats(reads);
	uint32_t underruns = FACT_vita_take_underruns();
	uint32_t refill_wait = FACT_vita_take_refill_wait();
	uint32_t mix[8];
	FAudio_vita_take_mix_stats(mix);
	if (reads[0] + reads[1] > 0 || underruns > 0 || refill_wait > 0) {
		log_line("[sound] wave starts: %u from the read cache, %u read from the card by the first-buffer reader (%u ms, slowest %u ms); refills too late: %u, longest refill wait %u ms",
			reads[0], reads[1], reads[2], reads[3], underruns, refill_wait);
	}
	// A callback is due every ~93 ms (4096 frames at 44.1 kHz); a longer gap means the device ran dry.
	log_line("[sound] mixer: %u callbacks, mix avg %u ms, slowest %u ms, longest gap %u ms", mix[3], mix[2], mix[0], mix[1]);
	// What the mix holds: silence between two sounding buffers (under 5 s) is a hole made before the
	// device; a buffer the same as the one before is a stutter.
	if (mix[4] > 0 || mix[6] > 0) {
		log_line("[sound] mix content: %u holes of silence (longest %u ms, last ended at %.2f s), %u repeated buffers",
			mix[4], mix[5], mix[7] / 1000.0, mix[6]);
	}
	// Over full scale the limiter turned the mix down (it would have clipped: a crack in the speaker); a
	// step over ~0.3 between neighbouring samples is a click made in the mix itself.
	uint32_t level[4];
	FAudio_vita_take_level_stats(level);
	log_line("[sound] mix level: peak %.2f, %u samples over full scale (limited), largest step %.2f at %.2f s",
		level[0] / 1000.0, level[1], level[2] / 1000.0, level[3] / 1000.0);
	// The audio port itself (sdl-vita/patch-sdl-audio.py): an output that didn't have to wait found the
	// port empty, i.e. the speaker had run dry.
	log_line("[sound] port (%s, %d Hz): %u outputs, %u found it empty (heard as a gap), longest wait %u ms, longest between outputs %u ms",
		SDL_vita_audio_rate >= 48000 ? "main" : "BGM", SDL_vita_audio_rate,
		SDL_vita_audio_stats[0], SDL_vita_audio_stats[1], SDL_vita_audio_stats[2] / 1000, SDL_vita_audio_stats[3] / 1000);
	memset(SDL_vita_audio_stats, 0, sizeof(SDL_vita_audio_stats));
	static int capture_reported;
	if (FAudio_vita_capture_state != capture_reported) {
		capture_reported = FAudio_vita_capture_state;
		log_line("[sound] capture %s; it started %u ms after SDL started (log time %.2f s)",
			capture_reported == 1 ? "recording 60 s to sound-capture.wav" : "written to sound-capture.wav",
			FAudio_vita_capture_start_ms, (double)FAudio_vita_capture_start_ms / 1000.0);
	}
	uint64_t swap_us = 0;
	uint32_t swaps = 0;
	VitaGLGlue_TakeSwapStats(&swap_us, &swaps);
	SceUInt64 pause_max = gc_pause_max_us;
	gc_pause_max_us = 0;
	// FNA3D's render thread (fna3d-vita/FNA3D_VitaThread.c): its replay time per frame (swap included) and
	// how long the game's main thread waited for it per frame.
	uint64_t render_busy_us = 0, render_wait_us = 0;
	int render_thread = FNA3D_VitaThread_TakeStats(&render_busy_us, &render_wait_us);
	uint32_t counts[5];
	uint64_t sprite_us = 0;
	FNA3D_VitaThread_TakeCounts(counts, &sprite_us);
	char render_text[320];
	if (render_thread) {
		// Per frame: draw calls, sprites and SpriteBatch replay time show whether the render thread is
		// held up by the number of draws (texture switches) or by the GPU (swap wait).
		double f = swaps ? (double)swaps : 1.0;
		snprintf(render_text, sizeof(render_text), "render thread busy %.1f ms/frame, game waited for it %.1f ms/frame; "
			"per frame %.0f draws, %.0f sprites in %.1f batches (%.1f ms), %.1f render target switches (%.1f more dropped, nothing drawn)",
			swaps ? (double)render_busy_us / swaps / 1000.0 : 0.0, swaps ? (double)render_wait_us / swaps / 1000.0 : 0.0,
			counts[0] / f, counts[1] / f, counts[3] / f, sprite_us / f / 1000.0, counts[2] / f, counts[4] / f);
	} else {
		snprintf(render_text, sizeof(render_text), "no render thread");
	}
	log_line("[profile] JIT: %u methods, %llu ms (%u slow); exceptions thrown: %u; GCs: %u, %llu ms, "
		"paused %llu ms (longest in the last 10 s %llu ms); frames presented %u, swap wait avg %.1f ms; %s",
		jit_methods, jit_time_us / 1000ULL, jit_slow_methods, exceptions_thrown, gc_count, gc_time_us / 1000ULL,
		gc_pause_us / 1000ULL, pause_max / 1000ULL, swaps, swaps ? (double)swap_us / swaps / 1000.0 : 0.0, render_text);
}

// Native only (never attached to Mono): reads kernel and allocator counters.
static void *memory_monitor(void *arg)
{
	(void)arg;
	for (;;) {
		for (unsigned waited = 0; waited < MEMORY_LOG_INTERVAL_US && !observers_stopping; waited += 100000U)
			usleep(100000U);
		if (observers_stopping) break;
		log_memory("periodic");
		log_profile();
		if (jit_logging) {
			jit_log_flush();
		}
		if (profiling) {
			prof_report();
		}
	}
	return NULL;
}

// With aot-log, Mono reports every method lookup too; only the image-level lines are kept.
static int aot_log_filter;

static void mono_log(const char *domain, const char *level, const char *message, mono_bool fatal, void *user_data)
{
	(void)user_data;
	if (aot_log_filter && message != NULL && (strstr(message, "FOUND method") != NULL || strstr(message, "NOT FOUND") != NULL
		|| strstr(message, "FOUND function") != NULL || strstr(message, "wants to load image") != NULL)) {
		return;
	}
	log_line("mono[%s/%s]%s %s", domain ? domain : "-", level ? level : "-", fatal ? " FATAL" : "", message);
}

static void mono_print(const char *string, mono_bool is_stdout)
{
	log_line("%s %s", is_stdout ? "stdout:" : "stderr:", string);
}

static void sdl_log(void *userdata, int category, SDL_LogPriority priority, const char *message)
{
	(void)userdata;
	log_line("SDL[%d/%d] %s", category, (int)priority, message);
}

static void set_clocks(void)
{
	// Keep the existing default; allow measured 333/444 MHz comparisons without rebuilding.
	// Kernel plugins may override the request, so always log the actual clock below.
	int requested = 500;
	SceUID config = sceIoOpen(GAME_DIR "/vita-cpu-mhz", SCE_O_RDONLY, 0);
	if (config >= 0) {
		char text[32];
		int bytes = sceIoRead(config, text, sizeof(text) - 1);
		sceIoClose(config);
		if (bytes > 0) {
			text[bytes] = '\0';
			int value;
			char extra;
			if (sscanf(text, "%d %c", &value, &extra) == 1 && (value == 333 || value == 444 || value == 500)) {
				requested = value;
			} else {
				log_line("vita-cpu-mhz ignored: expected 333, 444 or 500");
			}
		}
	}
	if (scePowerSetArmClockFrequency(requested) < 0 && requested == 500) {
		scePowerSetArmClockFrequency(444);
	}
	log_line("requested CPU clock: %d MHz", requested);
	scePowerSetBusClockFrequency(222);
	scePowerSetGpuClockFrequency(222);
	scePowerSetGpuXbarClockFrequency(166);
	log_line("clocks: arm %d MHz, bus %d MHz, gpu %d MHz, xbar %d MHz", scePowerGetArmClockFrequency(),
		scePowerGetBusClockFrequency(), scePowerGetGpuClockFrequency(), scePowerGetGpuXbarClockFrequency());
}

// --- Touch screen corners: the official Vita release shifted the toolbar with them --------------
// Served to the game as [DllImport("VitaPort")] (the name is from when the rear panel did this; the
// official release leaves the rear panel unused). SDL already samples the front panel; this reads its
// latest report. A finger that lands in the bottom-left corner is a tap for LT, in the bottom-right
// corner for RT; a resting finger counts once, when it lands, and touches elsewhere do nothing.

static int vita_port_rear_touch_taps(void)
{
	static int left_x = -1, right_x, bottom_y, left_min_x, right_max_x;
	static uint8_t previous_ids[SCE_TOUCH_MAX_REPORT];
	static int previous_count;
	if (left_x < 0) {
		SceTouchPanelInfo info;
		int min_x = 0, max_x = 1919, min_y = 0, max_y = 1087;
		if (sceTouchGetPanelInfo(SCE_TOUCH_PORT_FRONT, &info) == 0) {
			min_x = info.minAaX;
			max_x = info.maxAaX;
			min_y = info.minAaY;
			max_y = info.maxAaY;
		}
		// Deadzone on outermost ~6% of width (~60px) to prevent palm/thumb touches during analog movement
		int deadzone = (max_x - min_x) / 16;
		left_min_x = min_x + deadzone;
		right_max_x = max_x - deadzone;
		// Active corner tap area: between deadzone (6%) and outer fifth (20%) of width, lowest third of height
		left_x = min_x + (max_x - min_x) / 5;
		right_x = max_x - (max_x - min_x) / 5;
		bottom_y = max_y - (max_y - min_y) / 3;
	}
	SceTouchData data;
	if (sceTouchPeek(SCE_TOUCH_PORT_FRONT, &data, 1) < 1) {
		return 0;
	}
	int taps = 0;
	int count = (int)data.reportNum;
	if (count > SCE_TOUCH_MAX_REPORT) {
		count = SCE_TOUCH_MAX_REPORT;
	}
	for (int i = 0; i < count; i++) {
		int seen = 0;
		for (int j = 0; j < previous_count; j++) {
			if (previous_ids[j] == data.report[i].id) {
				seen = 1;
				break;
			}
		}
		if (!seen && data.report[i].y >= bottom_y) {
			if (data.report[i].x >= left_min_x && data.report[i].x <= left_x) {
				taps |= 1;
			} else if (data.report[i].x >= right_x && data.report[i].x <= right_max_x) {
				taps |= 2;
			}
		}
	}
	for (int i = 0; i < count; i++) {
		previous_ids[i] = data.report[i].id;
	}
	previous_count = count;
	return taps;
}

// Front touch screen tracking for UI menus (coordinates scaled to 960x544)
static int vita_port_front_touch(int *x, int *y)
{
	static SceTouchPanelInfo info;
	static int info_inited = 0;
	if (!info_inited) {
		if (sceTouchGetPanelInfo(SCE_TOUCH_PORT_FRONT, &info) == 0) {
			info_inited = 1;
		}
	}
	SceTouchData data;
	if (sceTouchPeek(SCE_TOUCH_PORT_FRONT, &data, 1) < 1 || data.reportNum < 1) {
		return 0;
	}
	int raw_x = data.report[0].x;
	int raw_y = data.report[0].y;
	int min_x = info_inited ? info.minAaX : 0;
	int max_x = info_inited ? info.maxAaX : 1919;
	int min_y = info_inited ? info.minAaY : 0;
	int max_y = info_inited ? info.maxAaY : 1087;
	int span_x = max_x - min_x;
	int span_y = max_y - min_y;
	if (span_x <= 0) span_x = 1919;
	if (span_y <= 0) span_y = 1087;

	int sx = (raw_x - min_x) * 960 / span_x;
	int sy = (raw_y - min_y) * 544 / span_y;
	if (sx < 0) sx = 0; else if (sx >= 960) sx = 959;
	if (sy < 0) sy = 0; else if (sy >= 544) sy = 543;
	if (x) *x = sx;
	if (y) *y = sy;
	return 1;
}

// --- Gamepad: resolve SDL mappings in one binding pass per state -------------------------------
// out[0] is the SDL_GamepadButton bitmask; out[1..6] are SDL_GamepadAxis values. The helper uses
// SDL's Vita port-local snapshot function while preserving the normal FNA P/Invoke ABI.
extern void VitaSDL_GetGamepadSnapshot(SDL_Gamepad *gamepad, int32_t *out);

static void vita_port_gamepad_read(SDL_Gamepad *gamepad, int32_t *out)
{
	VitaSDL_GetGamepadSnapshot(gamepad, out);
}

// --- console.txt off the main thread --------------------------------------------------------
// The game's console output (fps lines, spike lines, FNA's messages) went straight to the memory card with
// AutoFlush: each line held the main thread for the card write, 50-130 ms at times (spike stacks showed
// StreamWriter.Flush in update spikes every 10 s). Lines now go into a 64 KB ring (a copy) and a native
// thread, never attached to Mono, appends them to the file about once a second. A full ring drops lines.
#define CONSOLE_PATH GAME_DIR "/ErrorLogs/console.txt"
#define CONSOLE_RING (64 * 1024)
static char console_ring[CONSOLE_RING];
static size_t console_head, console_tail; // write at head, the writer takes from tail (both wrap)
static pthread_mutex_t console_lock = PTHREAD_MUTEX_INITIALIZER;
static int console_started;

static void *console_writer(void *arg)
{
	(void)arg;
	static char chunk[CONSOLE_RING];
	int truncate = 1;
	// The last run's console stays as console-prev.txt (see vita-log-prev.txt in main).
	sceIoRemove(GAME_DIR "/ErrorLogs/console-prev.txt");
	sceIoRename(CONSOLE_PATH, GAME_DIR "/ErrorLogs/console-prev.txt");
	for (;;) {
		usleep(1000 * 1000);
		pthread_mutex_lock(&console_lock);
		size_t n = 0;
		while (console_tail != console_head) {
			chunk[n++] = console_ring[console_tail % CONSOLE_RING];
			console_tail++;
		}
		pthread_mutex_unlock(&console_lock);
		if (n == 0 && !truncate) {
			continue;
		}
		SceUID fd = sceIoOpen(CONSOLE_PATH, SCE_O_WRONLY | SCE_O_CREAT | (truncate ? SCE_O_TRUNC : SCE_O_APPEND), 0666);
		if (fd >= 0) {
			truncate = 0;
			if (n > 0) {
				sceIoWrite(fd, chunk, (SceSize)n);
			}
			sceIoClose(fd);
		}
	}
	return NULL;
}

// C# (VitaPlatform.ConsoleWriter): queues text for console.txt.
static void vita_port_console_write(const char *text)
{
	if (text == NULL) {
		return;
	}
	pthread_mutex_lock(&console_lock);
	if (!console_started) {
		console_started = 1;
		pthread_t writer;
		if (pthread_create(&writer, NULL, console_writer, NULL) == 0) {
			pthread_detach(writer);
		}
	}
	for (const char *c = text; *c != '\0' && console_head - console_tail < CONSOLE_RING; c++) {
		console_ring[console_head % CONSOLE_RING] = *c;
		console_head++;
	}
	pthread_mutex_unlock(&console_lock);
}

// Free vitaGL memory (VRAM and RAM pools) in KB, for the game's cached pictures (Game1): it allocates them
// only with room to spare, since vitaGL running out of memory takes the game down.
static int vita_port_gl_free_kb(void)
{
	if (!VitaGLGlue_IsInitialized()) {
		return 0;
	}
	return (int)((vglMemFree(VGL_MEM_VRAM) + vglMemFree(VGL_MEM_RAM)) / 1024);
}

// --- Real trophies through the NoTrpDrm plugin ---------------------------------------------------
// The game's achievements are Vita-style toasts in C# for everyone (StardewValley.SDKs/VitaTrophies.cs). With
// Rinnegatamante's NoTrpDrm in config.txt (*main), SceShell skips the NP signature checks, so our own unsigned pack
// sce_sys/trophy/STDV00001_00/TROPHY.TRP (stardew/trophies/make-trophies.py) works and the same unlocks become real
// trophies. All NP calls run on one native thread, never attached to Mono; the game only sets a bit and returns.
// Without the plugin sceNpTrophyCreateContext fails, the thread logs it and ends, and nothing else changes.
#include <psp2/common_dialog.h>
#include <psp2/np/common.h>
#include <psp2/sysmodule.h>

// The SDK has stubs but no header for sceNpTrophy: declared as Rinnegatamante's ports do (not_a_hero-vita, loader/trophies.c).
typedef struct {
	int sdkVersion;
	SceCommonDialogParam commonParam;
	int context;
	int options;
	uint8_t reserved[128];
} TrophySetupDialogParam;

#define TROPHY_MAX 128
typedef struct {
	uint32_t bits[TROPHY_MAX / 32];
} TrophyFlags;

int sceNpTrophyInit(void *option);
int sceNpTrophyCreateContext(int *context, const SceNpCommunicationId *comm_id, const SceNpCommunicationSignature *signature, uint64_t options);
int sceNpTrophyCreateHandle(int *handle);
int sceNpTrophyDestroyHandle(int handle);
int sceNpTrophyUnlockTrophy(int context, int handle, int trophy_id, int *platinum_id);
int sceNpTrophyGetTrophyUnlockState(int context, int handle, TrophyFlags *flags, uint32_t *count);
int sceNpTrophySetupDialogInit(TrophySetupDialogParam *param);
int sceNpTrophySetupDialogGetStatus(void);
int sceNpTrophySetupDialogTerm(void);

static TrophyFlags trophy_requested, trophy_done; // done: unlocked before, or already handed to sceNpTrophyUnlockTrophy
static pthread_mutex_t trophy_lock = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t trophy_wake = PTHREAD_COND_INITIALIZER;
static int trophy_started;
static volatile int trophy_dialog_active, trophy_dialog_polled;

static int trophy_flag(const TrophyFlags *flags, int id)
{
	return (flags->bits[id / 32] >> (id % 32)) & 1;
}

static void trophy_set_flag(TrophyFlags *flags, int id)
{
	flags->bits[id / 32] |= 1U << (id % 32);
}

// For fna3d-vita/vita_gl_glue.c, before each vglSwapBuffers: whether a common dialog besides the IME is open, which vitaGL
// then has to update (vglSwapBuffers(GL_TRUE)). The trophy setup dialog only starts once the glue has asked, since a
// dialog nobody updates never finishes and would keep the IME from opening.
int vita_port_common_dialog_active(void)
{
	trophy_dialog_polled = 1;
	// The render replay can have no SDL_Window. Query the actual native dialog as well,
	// otherwise an invisible IME still consumes every button except the sticks.
	return trophy_dialog_active || sceImeDialogGetStatus() == SCE_COMMON_DIALOG_STATUS_RUNNING;
}

// The setup dialog installs the pack on first use (a short "preparing trophies" screen) and is required before
// unlocking; once installed it closes by itself within a few frames.
static int trophy_setup_dialog(int context)
{
	if (!trophy_dialog_polled) {
		log_line("trophies: waiting for the renderer to update common dialogs (vita_port_common_dialog_active)");
		while (!trophy_dialog_polled) {
			usleep(1000 * 1000);
		}
	}
	TrophySetupDialogParam param;
	memset(&param, 0, sizeof(param));
	_sceCommonDialogSetMagicNumber(&param.commonParam);
	param.sdkVersion = PSP2_SDK_VERSION;
	param.context = context;
	int result = -1;
	// Busy while the IME is open: try again for a minute.
	for (int attempt = 0; attempt < 60; attempt++) {
		trophy_dialog_active = 1;
		result = sceNpTrophySetupDialogInit(&param);
		if (result >= 0) {
			break;
		}
		trophy_dialog_active = 0;
		usleep(1000 * 1000);
	}
	if (result < 0) {
		log_line("trophies: sceNpTrophySetupDialogInit failed (0x%08X)", result);
		return result;
	}
	while (sceNpTrophySetupDialogGetStatus() == SCE_COMMON_DIALOG_STATUS_RUNNING) {
		usleep(16 * 1000);
	}
	sceNpTrophySetupDialogTerm();
	trophy_dialog_active = 0;
	return 0;
}

static void *trophy_worker(void *arg)
{
	(void)arg;
	int context = -1;
	int result = sceSysmoduleLoadModule(SCE_SYSMODULE_NP_TROPHY);
	if (result >= 0) {
		result = sceNpTrophyInit(NULL);
	}
	if (result >= 0) {
		// The pack's folder in sce_sys/trophy: the title id with _00. NoTrpDrm skips the signature check; this one
		// carries only the header the ports above use.
		static const SceNpCommunicationId comm_id = { "STDV00001", 0, 0, 0 };
		static const SceNpCommunicationSignature signature = { { 0xb9, 0xdd, 0xe1, 0x3b, 0x01, 0x00 } };
		result = sceNpTrophyCreateContext(&context, &comm_id, &signature, 0);
	}
	if (result < 0) {
		log_line("trophies: off (0x%08X), NoTrpDrm not installed or the firmware is not one it supports", result);
		return NULL;
	}
	if (trophy_setup_dialog(context) < 0) {
		return NULL;
	}
	{
		FILE *trp = fopen("app0:sce_sys/trophy/STDV00001_00/TROPHY.TRP", "rb");
		if (trp) {
			uint8_t hdr[0x40];
			if (fread(hdr, 1, sizeof(hdr), trp) == sizeof(hdr)) {
				uint32_t entry_count = ((uint32_t)hdr[0x10] << 24) | ((uint32_t)hdr[0x11] << 16) | ((uint32_t)hdr[0x12] << 8) | (uint32_t)hdr[0x13];
				for (uint32_t e = 0; e < entry_count && e < 64; e++) {
					uint8_t ent[0x40];
					if (fread(ent, 1, sizeof(ent), trp) != sizeof(ent)) {
						break;
					}
					if (strcmp((const char *)ent, "ICON0.PNG") == 0) {
						uint32_t off = ((uint32_t)ent[0x24] << 24) | ((uint32_t)ent[0x25] << 16) | ((uint32_t)ent[0x26] << 8) | (uint32_t)ent[0x27];
						uint32_t sz = ((uint32_t)ent[0x2C] << 24) | ((uint32_t)ent[0x2D] << 16) | ((uint32_t)ent[0x2E] << 8) | (uint32_t)ent[0x2F];
						if (sz > 0 && sz < 512 * 1024 && fseek(trp, (long)off, SEEK_SET) == 0) {
							uint8_t *buf = (uint8_t *)malloc(sz);
							if (buf && fread(buf, 1, sz, trp) == sz) {
								static const char *const dirs[] = {
									"ur0:user/00/trophy/conf/STDV00001_00",
									"ur0:user/00/trophy/data/STDV00001_00",
									"ux0:user/00/trophy/conf/STDV00001_00",
									"ux0:user/00/trophy/data/STDV00001_00"
								};
								for (int d = 0; d < 4; d++) {
									SceIoStat dst_dir_st;
									if (sceIoGetstat(dirs[d], &dst_dir_st) >= 0) {
										char icon_path[128];
										snprintf(icon_path, sizeof(icon_path), "%s/ICON0.PNG", dirs[d]);
										SceIoStat icon_st;
										if (sceIoGetstat(icon_path, &icon_st) < 0 || (uint32_t)icon_st.st_size != sz) {
											FILE *out = fopen(icon_path, "wb");
											if (out) {
												fwrite(buf, 1, sz, out);
												fclose(out);
												log_line("trophies: updated %s (%u bytes)", icon_path, (unsigned)sz);
											}
										}
									}
								}
							}
							free(buf);
						}
						break;
					}
				}
			}
			fclose(trp);
		}
	}
	int handle;
	TrophyFlags unlocked;
	uint32_t count = 0;
	memset(&unlocked, 0, sizeof(unlocked));
	if (sceNpTrophyCreateHandle(&handle) >= 0) {
		sceNpTrophyGetTrophyUnlockState(context, handle, &unlocked, &count);
		sceNpTrophyDestroyHandle(handle);
	}
	int have = 0;
	pthread_mutex_lock(&trophy_lock);
	for (int i = 0; i < TROPHY_MAX / 32; i++) {
		trophy_done.bits[i] |= unlocked.bits[i];
		have += __builtin_popcount(unlocked.bits[i]);
	}
	pthread_mutex_unlock(&trophy_lock);
	log_line("trophies: on, %d of %u unlocked", have, (unsigned)count);
	for (;;) {
		int id = -1;
		pthread_mutex_lock(&trophy_lock);
		while (id < 0) {
			for (int i = 1; i < TROPHY_MAX && id < 0; i++) {
				if (trophy_flag(&trophy_requested, i) && !trophy_flag(&trophy_done, i)) {
					id = i;
				}
			}
			if (id < 0) {
				pthread_cond_wait(&trophy_wake, &trophy_lock);
			}
		}
		trophy_set_flag(&trophy_done, id);
		pthread_mutex_unlock(&trophy_lock);
		int platinum = -1;
		result = sceNpTrophyCreateHandle(&handle);
		if (result >= 0) {
			result = sceNpTrophyUnlockTrophy(context, handle, id, &platinum);
			sceNpTrophyDestroyHandle(handle);
		}
		log_line("trophies: unlock %d: 0x%08X%s", id, result, platinum >= 0 ? " (and the platinum)" : "");
	}
	return NULL;
}

static void trophy_start_locked(void)
{
	if (trophy_started) {
		return;
	}
	trophy_started = 1;
	pthread_t worker;
	pthread_attr_t attr;
	pthread_attr_init(&attr);
	pthread_attr_setstacksize(&attr, 64 * 1024);
	if (pthread_create(&worker, &attr, trophy_worker, NULL) == 0) {
		pthread_detach(worker);
	}
	pthread_attr_destroy(&attr);
}

// C# (VitaTrophies.Initialize): starts the trophy thread at game start, so the setup dialog runs on the title screen.
static void vita_port_trophy_start(void)
{
	pthread_mutex_lock(&trophy_lock);
	trophy_start_locked();
	pthread_mutex_unlock(&trophy_lock);
}

// C# (VitaTrophies): asks for trophy `id` of TROPHY.TRP and returns at once. 0 is the platinum, which the system awards.
static void vita_port_trophy_unlock(int id)
{
	if (id <= 0 || id >= TROPHY_MAX) {
		return;
	}
	pthread_mutex_lock(&trophy_lock);
	trophy_start_locked();
	trophy_set_flag(&trophy_requested, id);
	pthread_cond_signal(&trophy_wake);
	pthread_mutex_unlock(&trophy_lock);
}

// mono-vita/vita-sampler.c: logs the sampled stacks of the last elapsed_ms ([spike stacks] lines); a no-op
// unless profile-samples started the sampler.
void vita_sampler_spike(unsigned elapsed_ms, int draw);

static const VitaNativeSymbol vita_port_symbols[] = {
	{ "vita_port_request_restart", (void *)&vita_port_request_restart },
	{ "vita_port_cancel_ime", (void *)&vita_port_cancel_ime },
	{ "vita_sampler_spike", (void *)&vita_sampler_spike },
	{ "vita_port_rear_touch_taps", (void *)&vita_port_rear_touch_taps },
	{ "vita_port_front_touch", (void *)&vita_port_front_touch },
	{ "vita_port_gamepad_read", (void *)&vita_port_gamepad_read },
	{ "vita_port_set_vsync_interval", (void *)&VitaGLGlue_SetVsyncInterval },
	{ "vita_port_get_vsync_interval", (void *)&VitaGLGlue_GetVsyncInterval },
	{ "vita_port_gl_free_kb", (void *)&vita_port_gl_free_kb },
	{ "vita_port_console_write", (void *)&vita_port_console_write },
	{ "vita_port_trophy_start", (void *)&vita_port_trophy_start },
	{ "vita_port_trophy_unlock", (void *)&vita_port_trophy_unlock },
	{ 0, 0 }
};

// Runtime options to try without a new build: the words of mono-options.txt in the game folder go to
// mono_jit_parse_options, like mono's command line (for example -O=all to change JIT optimizations).
static void apply_mono_options(void)
{
	static char text[512];
	static char *argv[16];
	SceUID fd = sceIoOpen(GAME_DIR "/mono-options.txt", SCE_O_RDONLY, 0);
	if (fd < 0) {
		return;
	}
	int length = sceIoRead(fd, text, sizeof(text) - 1);
	sceIoClose(fd);
	if (length <= 0) {
		return;
	}
	text[length] = '\0';
	int argc = 0;
	char *save = NULL;
	for (char *word = strtok_r(text, " \t\r\n", &save); word != NULL && argc < 16; word = strtok_r(NULL, " \t\r\n", &save)) {
		argv[argc++] = word;
		log_line("mono option: %s", word);
	}
	if (argc > 0) {
		mono_jit_parse_options(argc, argv);
	}
}

// AOT images linked into the eboot (aot-stardew.sh writes the list, aot_none.c is the empty one): each
// entry is the address of a mono_aot_module_<assembly>_info symbol, which holds the image's file info.
extern void **const vita_aot_modules[];

// Mono uses a registered image for an assembly of that name whose GUID matches; any other assembly,
// or a game build other than the one compiled, is JIT-compiled as before.
static void register_aot_modules(void)
{
	int count = 0;
	for (void **const *module = vita_aot_modules; *module != NULL; module++) {
		mono_aot_register_module((void **)**module);
		count++;
	}
	log_line("AOT images linked: %d", count);
	// Which images load (or why one is unusable) only shows at debug level, along with every method
	// looked up: a switch, not a default.
	SceIoStat aot_log;
	if (count > 0 && sceIoGetstat(GAME_DIR "/aot-log", &aot_log) >= 0) {
		// Not MONO_LOG_LEVEL/MONO_LOG_MASK: the logger read the environment when main() installed its
		// handler, so they would come too late.
		mono_trace_set_level_string("debug");
		mono_trace_set_mask_string("aot");
		aot_log_filter = 1;
		log_line("aot-log found: Mono logs which AOT images load");
	}
}

// gc-params.txt in the game folder: its first line becomes MONO_GC_PARAMS (e.g. nursery-size=16m), to try
// collector settings without a new build.
static void apply_gc_params(void)
{
	static char text[256];
	SceUID fd = sceIoOpen(GAME_DIR "/gc-params.txt", SCE_O_RDONLY, 0);
	if (fd < 0) {
		return;
	}
	int length = sceIoRead(fd, text, sizeof(text) - 1);
	sceIoClose(fd);
	if (length <= 0) {
		return;
	}
	text[length] = '\0';
	text[strcspn(text, "\r\n")] = '\0';
	if (text[0] != '\0') {
		setenv("MONO_GC_PARAMS", text, 1);
		log_line("MONO_GC_PARAMS=%s", text);
	}
}

static int quit(int status)
{
	// Exiting with kubridge memory still committed freezes the console.
	vita_exec_shutdown();
	log_line("launcher exiting (%d)", status);
	if (restart_requested && status == 0) {
		int result = sceAppMgrLoadExec("app0:eboot.bin", NULL, NULL);
		log_line("restart failed: 0x%08x", result);
	}
	sceKernelExitProcess(status);
	return status;
}

// exit() (Environment.Exit, Mono's fatal paths) runs this last, as it is registered first. newlib's
// exit went on to hang after the atexit handlers once (stdio cleanup with other threads still
// running), so the process ends right here.
static void quit_at_exit(void)
{
	log_line("exit() called");
	quit(0);
}

// Replaces Mono's default (print, then exit()): the game has already written its own error report
// from AppDomain.UnhandledException by now.
static void unhandled_exception(MonoObject *exception, void *user_data)
{
	(void)user_data;
	MonoObject *inner = NULL;
	MonoString *text = mono_object_to_string(exception, &inner);
	char *utf8 = (text != NULL && inner == NULL) ? mono_string_to_utf8(text) : NULL;
	log_line("unhandled exception: %s", utf8 != NULL ? utf8 : "(ToString failed)");
	if (utf8 != NULL) {
		mono_free(utf8);
	}
	quit(1);
}

int main(void)
{
	start_time = sceKernelGetProcessTimeWide();
	SceIoStat stat;
	if (sceIoGetstat(GAME_EXE, &stat) < 0) {
		// Nothing to log into either if the folder is missing; the game must be installed first.
		sceIoMkdir(GAME_DIR, 0777);
		log_line("missing " GAME_EXE ": copy the game build and Content/ into " GAME_DIR " (see Tools/Vita/README.md)");
		return quit(1);
	}
	// The last run's log stays as vita-log-prev.txt: after a crash the game is usually started again before the
	// log is looked at, and that start used to wipe the only record of the crash.
	sceIoRemove(GAME_DIR "/vita-log-prev.txt");
	sceIoRename(LOG_PATH, GAME_DIR "/vita-log-prev.txt");
	sceIoRemove(LOG_PATH);
	log_line("Stardew Valley launcher, built %s %s", __DATE__, __TIME__);
	set_clocks();
	log_memory("start");
	atexit(quit_at_exit);

	SDL_SetLogOutputFunction(sdl_log, NULL);
	SDL_SetLogPriorities(SDL_LOG_PRIORITY_INFO);
	mono_trace_set_log_handler(mono_log, NULL);
	mono_trace_set_print_handler(mono_print);
	mono_trace_set_printerr_handler(mono_print);
	mono_threads_set_default_stacksize(2 * 1024 * 1024);

	// No SIGSEGV is ever delivered on the Vita: JIT code must check for null explicitly.
	setenv("MONO_DEBUG", "explicit-null-checks", 1);
	// Mono's XmlSerializer would otherwise compile serializers with mcs in a child process.
	setenv("MONO_XMLSERIALIZER_THS", "no", 1);
	// Saves, startup preferences and logs: ApplicationData/LocalApplicationData + "StardewValley",
	// i.e. next to the game files in ux0:data/StardewValley (managed paths are rooted, see vita_paths.c).
	setenv("HOME", "/" GAME_DIR, 1);
	setenv("XDG_CONFIG_HOME", "/ux0:data", 1);
	setenv("XDG_DATA_HOME", "/ux0:data", 1);
	setenv("SV_LOGFILE", "1", 1);
	// Textures outgrew CDRAM (96 MB) plus the default 32 MB RAM pool in play, and vitaGL then sleeps a
	// second per failed allocation (0-1 fps). ~65 MB of MAIN stayed free outside the newlib heap.
	// A hint, not setenv: SDL keeps its own environment and does not see newlib's.
	SDL_SetHint("FNA3D_VITAGL_RAM_POOL_MB", "64");
	// The audio port's buffers: SDL's default 1024 frames at 48 kHz, two of them, left 43 ms for the mixer
	// thread; it was heard dropping out when frame rates fell (lightning in a storm). 2048 frames (85 ms in
	// all) costs 16 KB and some latency nobody notices in this game. With 2048 the mixer callbacks were never
	// late (longest gap 46 ms) yet recordings still showed ~100 ms dips, so the output side between callbacks
	// stalls too: 4096 frames (170 ms in all).
	SDL_SetHint(SDL_HINT_AUDIO_DEVICE_SAMPLE_FRAMES, "4096");
	// Not SDL_AUDIO_FREQUENCY=48000: tried on 2026-10-25, FAudio then mixed at 48 kHz and resampled every
	// 44.1 kHz voice, which cost the main thread enough to drop 30 fps to 20-25. FAudio keeps mixing at
	// 44.1 kHz; SDL's Vita driver (sdl-vita/patch-sdl-audio.py) always opens the 48 kHz main port and SDL
	// resamples the one mix on the audio thread, pinned to the third core with the mixer (patch-faudio.py).
	// Touch screen taps shift the toolbar (vita_port_rear_touch_taps) and are nothing else: without this SDL
	// also turns them into mouse clicks, and a tap beside the toolbar would click the world under it.
	SDL_SetHint(SDL_HINT_TOUCH_MOUSE_EVENTS, "0");
	// SpriteBatch appends to its vertex buffer with NoOverwrite. Through glBufferSubData, vitaGL copies
	// the whole buffer (196 KB) into a fresh allocation whenever the GPU may still read it: dozens of
	// times a frame, which filled the RAM pool, and once an allocation failed the update was dropped
	// (the end of a batch vanished: text boxes, garbled bus scene). glMapBufferRange hands out the
	// buffer memory itself, which is what NoOverwrite promises is safe.
	// A folder (or file) named no-map-buffers in the game folder switches back to the copying path,
	// as a fallback that needs no new build.
	SceIoStat no_map_buffers;
	if (sceIoGetstat(GAME_DIR "/no-map-buffers", &no_map_buffers) < 0) {
		SDL_SetHint("FNA3D_OPENGL_ALLOW_MAP_UNSYNCHRONIZED", "1");
	} else {
		log_line("no-map-buffers found: SpriteBatch uses Discard + glBufferSubData");
	}
	// Relative game paths ("Content/XACT/...") and FNA's TitleLocation both point at the game folder.
	chdir(GAME_DIR);
	vita_sdl_base_path = GAME_DIR "/";

	vita_native_install();
	vita_native_register_library("SDL3", vita_sdl3_symbols);
	vita_native_register_library("FNA3D", vita_fna3d_symbols);
	vita_native_register_library("FAudio", vita_faudio_symbols);
	vita_native_register_library("VitaPort", vita_port_symbols);

	mono_set_dirs("app0:/lib", "app0:/etc");
	static char *assemblies_path[] = { "app0:/lib/mono/4.5", "app0:/lib/fna", GAME_DIR, NULL };
	mono_set_assemblies_path_direct(assemblies_path);

	apply_mono_options();
	apply_gc_params();
	register_aot_modules();
	start_aot_profile();
	install_profiler();
	MonoDomain *domain = mono_jit_init_version("Stardew Valley", "v4.0.30319");
	if (domain == NULL) {
		log_line("mono_jit_init_version failed");
		return quit(1);
	}
	mono_install_unhandled_exception_hook(unhandled_exception, NULL);
	log_line("runtime up");
	log_memory("after init");

	MonoAssembly *assembly = mono_domain_assembly_open(domain, GAME_EXE);
	if (assembly == NULL) {
		log_line("could not open " GAME_EXE);
		return quit(1);
	}
	log_line("game assembly loaded");

	if (pthread_create(&monitor_handle, NULL, memory_monitor, NULL) == 0)
		monitor_started = 1;
	start_sampler();
	char *argv[] = { (char *)"/" GAME_EXE };
	int result = mono_jit_exec(domain, assembly, 1, argv);
	log_line("Main returned %d", result);
	save_aot_profile(1);
	log_line("GC heap %lld KB, used %lld KB; executable memory %u KB (%s)",
		(long long)mono_gc_get_heap_size() / 1024, (long long)mono_gc_get_used_size() / 1024,
		(unsigned)(vita_exec_reserved() / 1024), vita_exec_backend());
	log_memory("end");
	log_profile();
	// Main returning does not stop managed workers in an embedded Mono runtime.
	// A restart core caught one still in mono_codegen after vita_exec_shutdown unmapped its code.
	// Stop our runtime observers first, then let Mono join/abort workers and its finalizer before
	// releasing kubridge executable memory. Native audio/console threads are left to process exit.
	log_line("shutdown: stopping runtime observers");
	observers_stopping = 1;
	vita_sampler_stop();
	if (prof_sampler_started) pthread_join(prof_sampler_handle, NULL);
	if (monitor_started) pthread_join(monitor_handle, NULL);
	log_line("shutdown: cleaning up Mono workers and finalizer");
	mono_jit_cleanup(domain);
	log_line("shutdown: Mono cleanup complete; releasing executable memory");
	return quit(0);
}
