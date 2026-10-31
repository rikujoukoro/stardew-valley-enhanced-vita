// Sampling profiler for the Vita runtime (build-mono-vita.sh sampler -> libvita-sampler.a).
//
// Every interval a native thread asks the main thread for a sample, and the main thread takes it
// itself at its next GC safepoint: it walks its own managed stack with Mono's unwinder, the top
// frame gets a "self" sample, every method on the stack an "inclusive" one. Nothing is instrumented,
// so the game runs at nearly full speed and small methods are not inflated the way the call
// instrumentation of main.c's profile-calls inflates them.
//
// Why not stop the main thread from outside (mono_thread_info_safe_suspend_and_run): that path
// aborted on the Vita ("Cannot read suspend state ... ASYNC_SUSPEND_REQUESTED") - the GC never waits
// for a running thread to park, so this port had never exercised it. The safepoint poll the JIT
// emits is used instead: setting mono_polling_required (as a stop-the-world does, under the same
// suspend lock) makes every safepoint call mono_threads_state_poll, which the host links wrapped
// (-Wl,--wrap=mono_threads_state_poll, mono-vita/vita-mono.cmake).
//
// Samples land on safepoints (method prologs, loop back edges). Time in native code (P/Invoke: vitaGL,
// SDL, FAudio) or in runtime code without safepoints (JIT compiling, GC) has no safepoint to stop at:
// those requests time out and are counted as "outside managed code".
//
// Compiled with libmini's flags (it needs mini.h); the host only sees vita_sampler_start.

#include <config.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <pthread.h>
#include <psp2/kernel/threadmgr.h>
#include <psp2/kernel/processmgr.h>
#include "mini.h"
#include <mono/metadata/appdomain.h>
#include <mono/metadata/class.h>
#include <mono/metadata/image.h>
#include <mono/metadata/threads-types.h>
#include <mono/metadata/threads.h>
#include <mono/utils/mono-threads.h>
#include <mono/utils/mono-threads-api.h>
#include <mono/utils/mono-threads-coop.h>

#define SAMPLER_CAPACITY 8192
#define SAMPLER_MAX_FRAMES 64
#define SAMPLER_MAX_VISITED 256
#define SAMPLER_START_DELAY_US (15U * 1000000U)
#define SAMPLER_WAIT_US 8000U
#define SAMPLER_WAIT_STEP_US 250U
#define SAMPLER_REPORT_LINES 30
#define SAMPLER_REPORT_US (10ULL * 1000000ULL)
#define SAMPLER_FIRST_FRAMES_LOGGED 8

typedef void (*VitaSamplerLog)(const char *fmt, ...);

int vita_sampler_start(unsigned sample_interval_us, VitaSamplerLog log);
void __real_mono_threads_state_poll(void);
void __wrap_mono_threads_state_poll(void);

typedef struct SamplerEntry {
	MonoMethod *method;
	uint32_t self;
	uint32_t inclusive;
} SamplerEntry;

typedef struct SamplerWalk {
	MonoMethod *frames[SAMPLER_MAX_FRAMES];
	int count;
	int visited;
} SamplerWalk;

static SamplerEntry *entries;
static volatile int sampler_stopping;
static pthread_t sampler_handle;
static int sampler_started;
static MonoNativeThreadId target;
static unsigned interval_us;
static VitaSamplerLog log_fn;
// The sampler thread sets pending; the main thread takes the sample and clears it. busy covers the
// main thread's use of the table, so the sampler thread only reads it (to report) when it is 0.
static volatile int sample_pending, sample_busy;
static uint32_t samples, empty_samples, outside_samples, lost_frames;
// Main-thread time the samples themselves took (the sampler's cost to the game).
static SceUInt64 sample_us;

static void take_sample_walk(void);

// Ring of the latest samples' top frames (a few KB, no allocation): vita_sampler_spike looks back in it
// to say which methods were running during a slow Update or Draw. Both the writer (the sample walk) and
// the reader (the spike call) run on the main thread, so it needs no locking.
#define SAMPLER_RING 128
#define SAMPLER_RING_FRAMES 6
#define SAMPLER_SPIKE_LINES 5
#define SAMPLER_SPIKE_SLACK_US 10000ULL

typedef struct SamplerRingEntry {
	SceUInt64 time_us;
	MonoMethod *frames[SAMPLER_RING_FRAMES];
	int count;
} SamplerRingEntry;

static SamplerRingEntry ring[SAMPLER_RING];
static unsigned ring_head;
// The first sample's frames, logged once to show what the unwinder sees from inside the poll.
static SamplerWalk first_walk;
static volatile int first_walk_taken;

static uint32_t sampler_hash(MonoMethod *method)
{
	uint32_t h = (uint32_t)(uintptr_t)method;
	h ^= h >> 15;
	h *= 0x2c1b3c6dU;
	h ^= h >> 12;
	return h & (SAMPLER_CAPACITY - 1);
}

static SamplerEntry *sampler_entry(MonoMethod *method)
{
	for (uint32_t i = sampler_hash(method), n = 0; n < SAMPLER_CAPACITY; i = (i + 1) & (SAMPLER_CAPACITY - 1), n++) {
		if (entries[i].method == method) {
			return &entries[i];
		}
		if (entries[i].method == NULL) {
			entries[i].method = method;
			return &entries[i];
		}
	}
	return NULL;
}

#define SAMPLER_PAIRS 256
#define SAMPLER_PAIR_LINES 12

typedef struct SamplerPair {
	MonoMethod *wrapper;
	MonoMethod *caller;
	uint32_t count;
} SamplerPair;

static SamplerPair pairs[SAMPLER_PAIRS];

static SamplerPair *sampler_pair(MonoMethod *wrapper, MonoMethod *caller)
{
	for (int i = 0; i < SAMPLER_PAIRS; i++) {
		if (pairs[i].wrapper == wrapper && pairs[i].caller == caller) {
			return &pairs[i];
		}
		if (pairs[i].wrapper == NULL) {
			pairs[i].wrapper = wrapper;
			pairs[i].caller = caller;
			return &pairs[i];
		}
	}
	return NULL;
}

static int compare_pairs(const void *a, const void *b)
{
	return (int)((const SamplerPair *)b)->count - (int)((const SamplerPair *)a)->count;
}

// Call edges (callee <- caller) of every sample: the flat incl list says Dictionary.FindEntry is hot,
// the edges say for whom.
#define SAMPLER_EDGES 2048
#define SAMPLER_EDGE_LINES 48
// Edges above this share are the trunk (RunLoop <- Tick ...) everyone knows.
#define SAMPLER_EDGE_TRUNK_PERCENT 30

typedef struct SamplerEdge {
	MonoMethod *callee;
	MonoMethod *caller;
	uint32_t count;
} SamplerEdge;

static SamplerEdge edges[SAMPLER_EDGES];

static SamplerEdge *sampler_edge(MonoMethod *callee, MonoMethod *caller)
{
	uint32_t i = (((uint32_t)(uintptr_t)callee >> 3) ^ ((uint32_t)(uintptr_t)caller * 2654435761U)) % SAMPLER_EDGES;
	for (int probe = 0; probe < SAMPLER_EDGES; probe++, i = (i + 1) % SAMPLER_EDGES) {
		if (edges[i].callee == callee && edges[i].caller == caller) {
			return &edges[i];
		}
		if (edges[i].callee == NULL) {
			edges[i].callee = callee;
			edges[i].caller = caller;
			return &edges[i];
		}
	}
	return NULL;
}

static int compare_edges(const void *a, const void *b)
{
	return (int)((const SamplerEdge *)b)->count - (int)((const SamplerEdge *)a)->count;
}

static gboolean sampler_frame(MonoStackFrameInfo *frame, MonoContext *ctx, gpointer data)
{
	(void)ctx;
	SamplerWalk *walk = (SamplerWalk *)data;
	// Every frame counts toward the cap, not only managed ones: an unwind that kept returning a
	// trampoline or native frame would otherwise never end.
	if (++walk->visited > SAMPLER_MAX_VISITED) {
		lost_frames++;
		return TRUE;
	}
	if ((frame->type != FRAME_TYPE_MANAGED && frame->type != FRAME_TYPE_MANAGED_TO_NATIVE) || frame->ji == NULL) {
		return FALSE;
	}
	MonoMethod *method = frame->method;
	if (method == NULL) {
		return FALSE;
	}
	// The icall wrapper of the safepoint poll is where every sample is taken: the method that
	// polled is the one below it.
	if (walk->count == 0 && method->wrapper_type != MONO_WRAPPER_NONE && method->name != NULL &&
		strstr(method->name, "mono_threads_state_poll") != NULL) {
		return FALSE;
	}
	if (walk->count == SAMPLER_MAX_FRAMES) {
		lost_frames++;
		return TRUE;
	}
	walk->frames[walk->count++] = method;
	return FALSE;
}

// On the main thread, at a safepoint: its own stack, so the unwinder can run normally. Async context
// anyway: the safepoint may be inside runtime code holding a lock the AOT lookups would take.
static void take_sample(void)
{
	SceUInt64 start = sceKernelGetProcessTimeWide();
	take_sample_walk();
	sample_us += sceKernelGetProcessTimeWide() - start;
}

static void take_sample_walk(void)
{
	SamplerWalk walk;
	walk.count = 0;
	walk.visited = 0;
	MonoThreadUnwindState state;
	if (mono_thread_state_init_from_current(&state)) {
		mono_thread_info_set_is_async_context(TRUE);
		mono_walk_stack_with_state(sampler_frame, &state, MONO_UNWIND_SIGNAL_SAFE, &walk);
		mono_thread_info_set_is_async_context(FALSE);
	}
	samples++;
	if (!first_walk_taken) {
		first_walk = walk;
		__sync_synchronize();
		first_walk_taken = 1;
	}
	if (walk.count == 0) {
		empty_samples++;
		return;
	}
	SamplerRingEntry *slot = &ring[ring_head++ % SAMPLER_RING];
	slot->count = walk.count < SAMPLER_RING_FRAMES ? walk.count : SAMPLER_RING_FRAMES;
	for (int i = 0; i < slot->count; i++) {
		slot->frames[i] = walk.frames[i];
	}
	slot->time_us = sceKernelGetProcessTimeWide();
	SamplerEntry *top = sampler_entry(walk.frames[0]);
	if (top != NULL) {
		top->self++;
	}
	// A wrapper on top (an emulated 64-bit division, an allocation...) says little by itself: count
	// which method called it too.
	if (walk.count > 1 && walk.frames[0]->wrapper_type != MONO_WRAPPER_NONE) {
		SamplerPair *pair = sampler_pair(walk.frames[0], walk.frames[1]);
		if (pair != NULL) {
			pair->count++;
		}
	}
	for (int i = 0; i < walk.count; i++) {
		int seen = 0;
		for (int j = 0; j < i && !seen; j++) {
			seen = walk.frames[j] == walk.frames[i];
		}
		if (!seen) {
			SamplerEntry *entry = sampler_entry(walk.frames[i]);
			if (entry != NULL) {
				entry->inclusive++;
			}
		}
	}
	for (int i = 0; i + 1 < walk.count; i++) {
		int seen = 0;
		for (int j = 0; j < i && !seen; j++) {
			seen = walk.frames[j] == walk.frames[i] && walk.frames[j + 1] == walk.frames[i + 1];
		}
		if (!seen) {
			SamplerEdge *edge = sampler_edge(walk.frames[i], walk.frames[i + 1]);
			if (edge != NULL) {
				edge->count++;
			}
		}
	}
}

void __wrap_mono_threads_state_poll(void)
{
	if (G_UNLIKELY(sample_pending) && mono_native_thread_id_equals(mono_native_thread_id_get(), target)) {
		sample_busy = 1;
		__sync_synchronize();
		if (sample_pending) {
			sample_pending = 0;
			take_sample();
		}
		__sync_synchronize();
		sample_busy = 0;
	}
	__real_mono_threads_state_poll();
}

static int compare_self(const void *a, const void *b)
{
	return (int)(*(SamplerEntry *const *)b)->self - (int)(*(SamplerEntry *const *)a)->self;
}

static int compare_inclusive(const void *a, const void *b)
{
	return (int)(*(SamplerEntry *const *)b)->inclusive - (int)(*(SamplerEntry *const *)a)->inclusive;
}

static void method_name(MonoMethod *method, char *out, size_t size)
{
	// The last part of the namespace is enough to tell StardewValley.Menus from StardewValley.Network.
	MonoClass *klass = method->klass;
	const char *space = klass != NULL ? m_class_get_name_space(klass) : NULL;
	const char *dot = space != NULL ? strrchr(space, '.') : NULL;
	snprintf(out, size, "%s%s%s:%s", dot != NULL ? dot + 1 : (space != NULL ? space : ""),
		space != NULL && space[0] != '\0' ? "." : "", klass != NULL ? m_class_get_name(klass) : "?",
		method->name != NULL ? method->name : "?");
}

// On the sampler thread, with no sample being taken: names are only read from metadata.
static void sampler_report(SceUInt64 elapsed_us)
{
	static SamplerEntry *sorted[SAMPLER_CAPACITY];
	char name[160];
	int count = 0;
	for (int i = 0; i < SAMPLER_CAPACITY; i++) {
		if (entries[i].method != NULL && entries[i].inclusive > 0) {
			sorted[count++] = &entries[i];
		}
	}
	uint32_t requested = samples + outside_samples;
	if (requested == 0) {
		return;
	}
	// Shares are of all requests: what the managed lines leave to 100% was spent outside managed code.
	log_fn("[samples] %u requests in %llu ms: %u%% outside managed code (native, JIT, GC), %u without a managed "
		"frame, %u stacks cut; sampling cost the main thread %llu ms (%.1f%%)", requested, elapsed_us / 1000ULL,
		outside_samples * 100 / requested, empty_samples, lost_frames, sample_us / 1000ULL,
		elapsed_us ? sample_us * 100.0 / elapsed_us : 0.0);
	qsort(sorted, count, sizeof(sorted[0]), compare_self);
	for (int i = 0; i < count && i < SAMPLER_REPORT_LINES; i++) {
		method_name(sorted[i]->method, name, sizeof(name));
		log_fn("[samples] self %5.1f%% incl %5.1f%%  %s", sorted[i]->self * 100.0 / requested,
			sorted[i]->inclusive * 100.0 / requested, name);
	}
	qsort(sorted, count, sizeof(sorted[0]), compare_inclusive);
	for (int i = 0; i < count && i < SAMPLER_REPORT_LINES * 2; i++) {
		method_name(sorted[i]->method, name, sizeof(name));
		log_fn("[samples] incl %5.1f%% self %5.1f%%  %s", sorted[i]->inclusive * 100.0 / requested,
			sorted[i]->self * 100.0 / requested, name);
	}
	qsort(pairs, SAMPLER_PAIRS, sizeof(pairs[0]), compare_pairs);
	for (int i = 0; i < SAMPLER_PAIRS && i < SAMPLER_PAIR_LINES && pairs[i].count > 0; i++) {
		char caller[160];
		method_name(pairs[i].wrapper, name, sizeof(name));
		method_name(pairs[i].caller, caller, sizeof(caller));
		log_fn("[samples] wrapper %5.1f%%  %s  <-  %s", pairs[i].count * 100.0 / requested, name, caller);
	}
	memset(pairs, 0, sizeof(pairs));
	qsort(edges, SAMPLER_EDGES, sizeof(edges[0]), compare_edges);
	for (int i = 0, lines = 0; i < SAMPLER_EDGES && lines < SAMPLER_EDGE_LINES && edges[i].count * 100 >= requested; i++) {
		if (edges[i].count * 100 > requested * SAMPLER_EDGE_TRUNK_PERCENT) {
			continue;
		}
		char caller[160];
		method_name(edges[i].callee, name, sizeof(name));
		method_name(edges[i].caller, caller, sizeof(caller));
		log_fn("[samples] edge %5.1f%%  %s  <-  %s", edges[i].count * 100.0 / requested, name, caller);
		lines++;
	}
	memset(edges, 0, sizeof(edges));
	for (int i = 0; i < count; i++) {
		sorted[i]->self = 0;
		sorted[i]->inclusive = 0;
	}
	samples = empty_samples = outside_samples = lost_frames = 0;
	sample_us = 0;
}

static int ring_same_stack(const SamplerRingEntry *a, const SamplerRingEntry *b)
{
	if (a->count != b->count) {
		return 0;
	}
	for (int i = 0; i < a->count; i++) {
		if (a->frames[i] != b->frames[i]) {
			return 0;
		}
	}
	return 1;
}

// Called on the main thread right after an Update or Draw took elapsed_ms: the samples of that time
// (plus a slack, since the first one lands up to an interval after the start), one line per distinct
// top-of-stack, most frequent first. The report above only shows averages; a spike hides in them.
void vita_sampler_spike(unsigned elapsed_ms, int draw)
{
	if (entries == NULL || log_fn == NULL) {
		return;
	}
	SceUInt64 now = sceKernelGetProcessTimeWide();
	SceUInt64 window = (SceUInt64)elapsed_ms * 1000ULL + SAMPLER_SPIKE_SLACK_US;
	int picked[SAMPLER_RING], counts[SAMPLER_RING];
	int groups = 0, total = 0;
	for (int i = 0; i < SAMPLER_RING; i++) {
		const SamplerRingEntry *e = &ring[i];
		if (e->count == 0 || e->time_us + window < now) {
			continue;
		}
		total++;
		int g = 0;
		while (g < groups && !ring_same_stack(&ring[picked[g]], e)) {
			g++;
		}
		if (g == groups) {
			picked[groups] = i;
			counts[groups++] = 0;
		}
		counts[g]++;
	}
	if (total == 0) {
		log_fn("[spike stacks] %s spike %u ms: no samples (time in native code, JIT or GC)", draw ? "draw" : "update",
			elapsed_ms);
		return;
	}
	char name[160], line[SAMPLER_RING_FRAMES * 170 + 64];
	for (int lines = 0; lines < SAMPLER_SPIKE_LINES; lines++) {
		int best = -1;
		for (int g = 0; g < groups; g++) {
			if (counts[g] > 0 && (best < 0 || counts[g] > counts[best])) {
				best = g;
			}
		}
		if (best < 0) {
			break;
		}
		const SamplerRingEntry *e = &ring[picked[best]];
		size_t used = (size_t)snprintf(line, sizeof(line), "[spike stacks] %s spike %u ms: %d/%d samples: ",
			draw ? "draw" : "update", elapsed_ms, counts[best], total);
		for (int i = 0; i < e->count && used < sizeof(line) - 1; i++) {
			method_name(e->frames[i], name, sizeof(name));
			used += (size_t)snprintf(line + used, sizeof(line) - used, "%s%s", i > 0 ? " <- " : "", name);
		}
		log_fn("%s", line);
		counts[best] = 0;
	}
}

static void log_first_walk(void)
{
	char name[160];
	log_fn("[samples] first sample: %d managed frames of %d visited", first_walk.count, first_walk.visited);
	for (int i = 0; i < first_walk.count && i < SAMPLER_FIRST_FRAMES_LOGGED; i++) {
		method_name(first_walk.frames[i], name, sizeof(name));
		log_fn("[samples]   #%d %s", i, name);
	}
}

// One request: raise the poll flag under the suspend lock (a stop-the-world holds it throughout, so
// this never clears the flag under a collection), wait for the main thread to take the sample, lower
// the flag again.
static void request_sample(void)
{
	mono_thread_info_suspend_lock();
	sample_pending = 1;
	mono_polling_required = 1;
	mono_thread_info_suspend_unlock();
	MONO_ENTER_GC_SAFE;
	for (unsigned waited = 0; sample_pending && waited < SAMPLER_WAIT_US; waited += SAMPLER_WAIT_STEP_US) {
		sceKernelDelayThread(SAMPLER_WAIT_STEP_US);
	}
	MONO_EXIT_GC_SAFE;
	mono_thread_info_suspend_lock();
	if (sample_pending) {
		sample_pending = 0;
		outside_samples++;
	}
	mono_polling_required = 0;
	mono_thread_info_suspend_unlock();
	while (sample_busy) {
		sceKernelDelayThread(50);
	}
}

static void *sampler_thread(void *arg)
{
	(void)arg;
	// Attached for the suspend lock; while waiting the thread stays in a GC-safe region, so a
	// stop-the-world never waits for it.
	mono_thread_internal_attach(mono_get_root_domain());
	log_fn("[samples] sampler thread attached; first request in %u s", SAMPLER_START_DELAY_US / 1000000U);
	MONO_ENTER_GC_SAFE;
	// The game reaches its title screen first: if sampling ever hangs, the log tells it apart from a
	// startup problem.
	for (unsigned waited = 0; waited < SAMPLER_START_DELAY_US && !sampler_stopping; waited += 100000U)
		sceKernelDelayThread(100000U);
	MONO_EXIT_GC_SAFE;
	SceUInt64 report_start = sceKernelGetProcessTimeWide();
	int first_logged = 0;
	while (!sampler_stopping) {
		request_sample();
		if (!first_logged && first_walk_taken) {
			log_first_walk();
			first_logged = 1;
		}
		MONO_ENTER_GC_SAFE;
		SceUInt64 now = sceKernelGetProcessTimeWide();
		if (now - report_start >= SAMPLER_REPORT_US) {
			sampler_report(now - report_start);
			report_start = now;
		}
		sceKernelDelayThread(interval_us);
		MONO_EXIT_GC_SAFE;
	}
	mono_thread_internal_detach(mono_thread_current());
	return NULL;
}

// Called on the main thread once the runtime is up.
int vita_sampler_start(unsigned sample_interval_us, VitaSamplerLog log)
{
	if (sampler_started) return 1;
	sampler_stopping = 0;
	entries = (SamplerEntry *)calloc(SAMPLER_CAPACITY, sizeof(SamplerEntry));
	if (entries == NULL) {
		return 0;
	}
	target = mono_native_thread_id_get();
	interval_us = sample_interval_us;
	log_fn = log;
	pthread_attr_t attr;
	pthread_attr_init(&attr);
	pthread_attr_setstacksize(&attr, 256 * 1024);
	int created = pthread_create(&sampler_handle, &attr, sampler_thread, NULL) == 0;
	pthread_attr_destroy(&attr);
	if (!created) {
		return 0;
	}
	sampler_started = 1;
	return 1;
}

// Called before Mono cleanup, while the runtime's suspend locks and metadata still exist.
void vita_sampler_stop(void)
{
	if (!sampler_started) return;
	sampler_stopping = 1;
	pthread_join(sampler_handle, NULL);
	sampler_started = 0;
}
