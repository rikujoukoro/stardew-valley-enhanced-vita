/* POSIX pieces the Mono runtime needs that VitaSDK's newlib does not provide.
 *
 * The Vita has one process, no asynchronous signals and no dynamic loading, so most of these are
 * honest "not supported" answers. Mono is built without mmap (its malloc-based mono_valloc is used),
 * cooperative suspend (no signals) and no pthread_condattr_setclock, which keeps this file small.
 *
 * The only real subsystem here is executable memory for JIT code and trampolines: ordinary memory is
 * not executable on the device (Vita3K does not enforce this). Mono writes code from any thread, so
 * the memory has to be writable and executable for every thread at once. kubridge (bythos14,
 * v0.3.1+) provides that: ranges reserved with kuKernelMemReserve and committed RWX with
 * kuKernelMemCommit, carved by a small first-fit allocator. Without kubridge the fallback
 * is a VM-domain block (sceKernelAllocMemBlockForVM): only the thread that opened the domain can write
 * it, which is enough for the main thread alone (found on the device; Duro02/vita-mono-port first
 * used VM domains for Mono).
 */

#include "vita_shim.h"

#include <errno.h>
#include <malloc.h>
#include <pthread.h>
#include <pwd.h>
#include <signal.h>
#include <stdlib.h>
#include <string.h>
#include <sys/types.h>
#include <unistd.h>
#include <psp2/kernel/sysmem.h>
#include <kubridge.h>

/* --- Memory ------------------------------------------------------------------------------------ */

/* newlib declares posix_memalign but its libc does not implement it. */
int posix_memalign(void **memptr, size_t alignment, size_t size)
{
	void *p = memalign(alignment, size);
	if (p == NULL) {
		return ENOMEM;
	}
	*memptr = p;
	return 0;
}

int getpagesize(void)
{
	return 4096;
}

/* --- Executable memory ------------------------------------------------------------------------- */

/* kubridge arenas are a kuKernelMemReserve range committed RWX once, whole, with pages kubridge
 * allocates itself, and decommitted once, whole, before exit. Verified on the device with exectest
 * (both threads write and run code, release returns cleanly). Not used, from device runs:
 *   - a direct RWX block type: the kernel rejects 0x0C20D070 with 0x80024B01;
 *   - a range grown by several commits and released with one decommit: froze the console;
 *   - exiting with a committed range: froze the console;
 *   - a view committed onto an ordinary block's pages (HAS_BASE), decommitted and then freed with
 *     the block: the console rebooted shortly after (pages apparently freed twice).
 * Arenas (kubridge and VM-domain alike) are allocated in 1 MB granules, at least 4 MB at a time
 * (VM-domain blocks reject smaller sizes with ILLEGAL_MEMBLOCK_SIZE). */
#define EXEC_ARENA_MIN (4 * 1024 * 1024)
#define EXEC_GRANULE (1024 * 1024)
#define EXEC_ALIGN 16
#define MAX_EXEC_ARENAS 16

enum {
	EXEC_BACKEND_NONE,
	EXEC_BACKEND_KUBRIDGE,
	EXEC_BACKEND_VM_DOMAIN,
	EXEC_BACKEND_PLAIN
};

typedef struct ExecRange {
	char *base;
	size_t size;
	struct ExecRange *next;
} ExecRange;

typedef struct ExecArena {
	SceUID uid;    /* the executable range (kubridge: the reservation) */
	int committed; /* kubridge: decommit before freeing */
	char *base;
	size_t size;
} ExecArena;

static ExecArena exec_arenas[MAX_EXEC_ARENAS];
static int exec_arena_count;
static ExecRange *exec_free; /* sorted by address, coalesced */
static pthread_mutex_t exec_lock = PTHREAD_MUTEX_INITIALIZER;
static size_t exec_in_use;
static int exec_fallback_arenas;
static int exec_backend = EXEC_BACKEND_NONE;

static void exec_free_insert(char *base, size_t size)
{
	ExecRange **link = &exec_free;
	ExecRange *range;
	while (*link != NULL && (*link)->base < base) {
		link = &(*link)->next;
	}
	/* Merge with the following range. */
	if (*link != NULL && base + size == (*link)->base) {
		(*link)->base = base;
		(*link)->size += size;
		range = *link;
	} else {
		range = (ExecRange *)malloc(sizeof(ExecRange));
		if (range == NULL) {
			return; /* the range is lost, not corrupted */
		}
		range->base = base;
		range->size = size;
		range->next = *link;
		*link = range;
	}
	/* Merge with the preceding range. */
	if (link != &exec_free) {
		ExecRange *previous = exec_free;
		while (previous->next != range) {
			previous = previous->next;
		}
		if (previous->base + previous->size == range->base) {
			previous->size += range->size;
			previous->next = range->next;
			free(range);
		}
	}
}

static void exec_add_arena(SceUID uid, int committed, void *base, size_t size)
{
	exec_arenas[exec_arena_count].uid = uid;
	exec_arenas[exec_arena_count].committed = committed;
	exec_arenas[exec_arena_count].base = (char *)base;
	exec_arenas[exec_arena_count].size = size;
	exec_arena_count++;
	exec_free_insert((char *)base, size);
}

/* One reservation, committed RWX whole; see the comment at EXEC_ARENA_MIN. */
static int exec_grow_kubridge(size_t size)
{
	void *base = NULL;
	SceUID uid = kuKernelMemReserve(&base, size, SCE_KERNEL_MEMBLOCK_TYPE_USER_RW);
	if (uid < 0 || base == NULL) {
		return 0;
	}
	if (kuKernelMemCommit(base, size, KU_KERNEL_PROT_READ | KU_KERNEL_PROT_WRITE | KU_KERNEL_PROT_EXEC, NULL) < 0) {
		sceKernelFreeMemBlock(uid);
		return 0;
	}
	exec_add_arena(uid, 1, base, size);
	return 1;
}

static int exec_grow(size_t needed)
{
	size_t size = needed < EXEC_ARENA_MIN ? EXEC_ARENA_MIN : (needed + EXEC_GRANULE - 1) & ~(size_t)(EXEC_GRANULE - 1);
	void *base = NULL;
	SceUID uid;
	if (exec_arena_count == MAX_EXEC_ARENAS) {
		return 0;
	}
	/* kubridge first: RWX for every thread. Decided on the first block, then kept. */
	if (exec_backend == EXEC_BACKEND_NONE || exec_backend == EXEC_BACKEND_KUBRIDGE) {
		if (exec_grow_kubridge(size)) {
			exec_backend = EXEC_BACKEND_KUBRIDGE;
			return 1;
		}
		if (exec_backend == EXEC_BACKEND_KUBRIDGE) {
			return 0;
		}
		exec_backend = EXEC_BACKEND_VM_DOMAIN;
	}
	uid = sceKernelAllocMemBlockForVM("mono-exec", size);
	if (uid >= 0 && sceKernelGetMemBlockBase(uid, &base) >= 0 && base != NULL) {
		/* Gives write access to the calling thread only, and only one thread can hold the domain
		 * open: other threads writing code fault. */
		sceKernelOpenVMDomain();
	} else {
		/* No VM domain (Vita3K does not implement one, and does not enforce execute permission
		 * either): plain memory. On the device this code would fault when executed. */
		if (uid >= 0) {
			sceKernelFreeMemBlock(uid);
		}
		uid = sceKernelAllocMemBlock("mono-exec-fallback", SCE_KERNEL_MEMBLOCK_TYPE_USER_RW, size, NULL);
		if (uid < 0 || sceKernelGetMemBlockBase(uid, &base) < 0 || base == NULL) {
			if (uid >= 0) {
				sceKernelFreeMemBlock(uid);
			}
			return 0;
		}
		exec_fallback_arenas++;
		exec_backend = EXEC_BACKEND_PLAIN;
	}
	exec_add_arena(uid, 0, base, size);
	return 1;
}

void *vita_exec_alloc(size_t size)
{
	ExecRange **link;
	char *result = NULL;
	size = (size + EXEC_ALIGN - 1) & ~(size_t)(EXEC_ALIGN - 1);
	pthread_mutex_lock(&exec_lock);
	for (int attempt = 0; attempt < 2 && result == NULL; attempt++) {
		for (link = &exec_free; *link != NULL; link = &(*link)->next) {
			ExecRange *range = *link;
			if (range->size >= size) {
				result = range->base;
				range->base += size;
				range->size -= size;
				if (range->size == 0) {
					*link = range->next;
					free(range);
				}
				break;
			}
		}
		if (result == NULL && !exec_grow(size)) {
			break;
		}
	}
	if (result != NULL) {
		exec_in_use += size;
		memset(result, 0, size);
	}
	pthread_mutex_unlock(&exec_lock);
	return result;
}

int vita_exec_owns(const void *pointer)
{
	const char *p = (const char *)pointer;
	for (int i = 0; i < exec_arena_count; i++) {
		if (p >= exec_arenas[i].base && p < exec_arenas[i].base + exec_arenas[i].size) {
			return 1;
		}
	}
	return 0;
}

void vita_exec_free(void *pointer, size_t size)
{
	size = (size + EXEC_ALIGN - 1) & ~(size_t)(EXEC_ALIGN - 1);
	pthread_mutex_lock(&exec_lock);
	exec_free_insert((char *)pointer, size);
	exec_in_use -= size;
	pthread_mutex_unlock(&exec_lock);
}

/* Nothing to do with kubridge (RWX for every thread). With the VM-domain fallback this cannot help
 * either (the domain is held by the thread that opened it); it stays a hook for both cases. */
void vita_exec_thread_enter(void)
{
}

/* __builtin___clear_cache is a no-op for arm-vita-eabi: the kernel has to clean the data cache and
 * invalidate the instruction cache. */
void vita_exec_flush(void *pointer, size_t size)
{
	const char *p = (const char *)pointer;
	if (exec_backend == EXEC_BACKEND_KUBRIDGE) {
		kuKernelFlushCaches(pointer, (SceSize)size);
		return;
	}
	for (int i = 0; i < exec_arena_count; i++) {
		const char *start = exec_arenas[i].base, *end = start + exec_arenas[i].size;
		const char *from = p > start ? p : start;
		const char *to = p + size < end ? p + size : end;
		if (from < to) {
			sceKernelSyncVMDomain(exec_arenas[i].uid, (void *)from, (SceSize)(to - from));
		}
	}
}

size_t vita_exec_in_use(void)
{
	return exec_in_use;
}

/* RAM backing executable memory. */
size_t vita_exec_reserved(void)
{
	size_t total = 0;
	for (int i = 0; i < exec_arena_count; i++) {
		total += exec_arenas[i].size;
	}
	return total;
}

int vita_exec_fallback_arenas(void)
{
	return exec_fallback_arenas;
}

/* Releases every arena before exit: kubridge ranges are decommitted exactly as committed (one call,
 * whole range) before the reservation is freed. Executable memory must not be used afterwards. */
void vita_exec_shutdown(void)
{
	pthread_mutex_lock(&exec_lock);
	for (int i = 0; i < exec_arena_count; i++) {
		if (exec_arenas[i].committed) {
			kuKernelMemDecommit(exec_arenas[i].base, exec_arenas[i].size);
		}
		sceKernelFreeMemBlock(exec_arenas[i].uid);
	}
	exec_arena_count = 0;
	pthread_mutex_unlock(&exec_lock);
}

const char *vita_exec_backend(void)
{
	switch (exec_backend) {
	case EXEC_BACKEND_KUBRIDGE:
		return "kubridge RWX";
	case EXEC_BACKEND_VM_DOMAIN:
		return "VM domain (no kubridge: only the main thread can write code)";
	case EXEC_BACKEND_PLAIN:
		return "plain memory (emulator only)";
	default:
		return "not used yet";
	}
}

/* --- Signals: none are ever delivered asynchronously --------------------------------------------- */

int sigaction(int signum, const struct sigaction *act, struct sigaction *oldact)
{
	(void)signum;
	(void)act;
	if (oldact != NULL) {
		memset(oldact, 0, sizeof(*oldact));
	}
	return 0;
}

int sigprocmask(int how, const sigset_t *set, sigset_t *oldset)
{
	(void)how;
	(void)set;
	if (oldset != NULL) {
		sigemptyset(oldset);
	}
	return 0;
}

int sigsuspend(const sigset_t *mask)
{
	(void)mask;
	errno = EINTR;
	return -1;
}

int sigaltstack(const stack_t *ss, stack_t *old_ss)
{
	(void)ss;
	if (old_ss != NULL) {
		memset(old_ss, 0, sizeof(*old_ss));
		old_ss->ss_flags = SS_DISABLE;
	}
	return 0;
}

/* --- Threads ----------------------------------------------------------------------------------- */

int pthread_setname_np(pthread_t thread, const char *name)
{
	(void)thread;
	(void)name;
	return 0;
}

/* --- Processes, users, links, dynamic loading: not on this platform ---------------------------- */

pid_t waitpid(pid_t pid, int *status, int options)
{
	(void)pid;
	(void)options;
	if (status != NULL) {
		*status = 0;
	}
	errno = ECHILD;
	return -1;
}

int dup2(int oldfd, int newfd)
{
	(void)oldfd;
	(void)newfd;
	errno = ENOSYS;
	return -1;
}

ssize_t readlink(const char *path, char *buf, size_t bufsiz)
{
	(void)path;
	(void)buf;
	(void)bufsiz;
	errno = EINVAL;
	return -1;
}

int symlink(const char *target, const char *linkpath)
{
	(void)target;
	(void)linkpath;
	errno = ENOSYS;
	return -1;
}

struct passwd *getpwnam(const char *name)
{
	(void)name;
	return NULL;
}

struct passwd *getpwuid(uid_t uid)
{
	(void)uid;
	return NULL;
}

int getpwuid_r(uid_t uid, struct passwd *pwd, char *buf, size_t buflen, struct passwd **result)
{
	(void)uid;
	(void)pwd;
	(void)buf;
	(void)buflen;
	*result = NULL;
	return ENOENT;
}

int getpwnam_r(const char *name, struct passwd *pwd, char *buf, size_t buflen, struct passwd **result)
{
	(void)name;
	(void)pwd;
	(void)buf;
	(void)buflen;
	*result = NULL;
	return ENOENT;
}

/* --- Files ------------------------------------------------------------------------------------- */

/* One process, no other lockers: every advisory lock is granted (FileShare emulation uses these). */
int flock(int fd, int operation)
{
	(void)fd;
	(void)operation;
	return 0;
}

void sync(void)
{
}

/* fnmatch for directory enumeration patterns: '*', '?', '[...]' (with '!' or '^' negation and
 * ranges) and backslash escapes. Flags are ignored; Mono passes none that change these rules. */
static int fnmatch_here(const char *pattern, const char *string)
{
	for (;;) {
		char p = *pattern++;
		switch (p) {
		case '\0':
			return *string == '\0';
		case '?':
			if (*string++ == '\0') {
				return 0;
			}
			break;
		case '*':
			while (*pattern == '*') {
				pattern++;
			}
			if (*pattern == '\0') {
				return 1;
			}
			for (; *string != '\0'; string++) {
				if (fnmatch_here(pattern, string)) {
					return 1;
				}
			}
			return 0;
		case '[': {
			char c = *string++;
			int negate = (*pattern == '!' || *pattern == '^');
			int matched = 0;
			if (c == '\0') {
				return 0;
			}
			if (negate) {
				pattern++;
			}
			do {
				char low = *pattern++;
				char high = low;
				if (low == '\0') {
					return 0;
				}
				if (*pattern == '-' && pattern[1] != ']' && pattern[1] != '\0') {
					high = pattern[1];
					pattern += 2;
				}
				if (c >= low && c <= high) {
					matched = 1;
				}
			} while (*pattern != ']');
			pattern++;
			if (matched == negate) {
				return 0;
			}
			break;
		}
		case '\\':
			if (*pattern != '\0') {
				p = *pattern++;
			}
			/* fall through */
		default:
			if (*string++ != p) {
				return 0;
			}
			break;
		}
	}
}

int fnmatch(const char *pattern, const char *string, int flags)
{
	(void)flags;
	return fnmatch_here(pattern, string) ? 0 : 1; /* FNM_NOMATCH */
}

void *dlopen(const char *filename, int flags)
{
	(void)filename;
	(void)flags;
	return NULL;
}

void *dlsym(void *handle, const char *symbol)
{
	(void)handle;
	(void)symbol;
	return NULL;
}

int dlclose(void *handle)
{
	(void)handle;
	return -1;
}

char *dlerror(void)
{
	return (char *)"dynamic loading is not supported on the Vita";
}
