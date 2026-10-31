// Executable-memory experiment for the Mono port (no Mono): which kubridge path gives memory that
// every thread can write and execute, and which one can be released without freezing the console.
// Steps run from safest to riskiest; each is logged before and after to ux0:data/exectest/log.txt,
// so after a freeze the last line names the step. Requires kubridge (bythos14 v0.3.1+).

#include <kubridge.h>
#include <psp2/io/fcntl.h>
#include <psp2/io/stat.h>
#include <psp2/kernel/processmgr.h>
#include <psp2/kernel/sysmem.h>
#include <psp2/kernel/threadmgr.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#define LOG_PATH "ux0:data/exectest/log.txt"
#define MB (1024 * 1024)
#define RWX (KU_KERNEL_PROT_READ | KU_KERNEL_PROT_WRITE | KU_KERNEL_PROT_EXEC)

static void log_line(const char *fmt, ...)
{
	char line[512];
	va_list args;
	va_start(args, fmt);
	int length = vsnprintf(line, sizeof(line) - 2, fmt, args);
	va_end(args);
	if (length < 0) {
		return;
	}
	if (length > (int)sizeof(line) - 2) {
		length = (int)sizeof(line) - 2;
	}
	line[length++] = '\n';
	SceUID fd = sceIoOpen(LOG_PATH, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_APPEND, 0666);
	if (fd >= 0) {
		sceIoWrite(fd, line, (SceSize)length);
		sceIoClose(fd);
	}
}

typedef int (*ReturnInt)(void);

// Writes "mov r0, #value; bx lr" at code and runs it.
static int emit_and_run(void *code, int value)
{
	uint32_t *words = (uint32_t *)code;
	words[0] = 0xE3A00000 | (value & 0xFF);
	words[1] = 0xE12FFF1E;
	kuKernelFlushCaches(code, 8);
	return ((ReturnInt)code)();
}

typedef struct {
	void *code;
	int result;
} Probe;

static void *probe_thread(void *arg)
{
	Probe *probe = (Probe *)arg;
	probe->result = emit_and_run(probe->code, 43);
	return NULL;
}

// Main thread writes and runs code at code, then a second thread does the same at code + 16.
static void write_from_two_threads(const char *what, void *code)
{
	pthread_t thread;
	Probe probe = { (char *)code + 16, -1 };
	log_line("%s: main thread writes and runs code at %p", what, code);
	log_line("%s: -> %d (expect 42)", what, emit_and_run(code, 42));
	log_line("%s: second thread writes and runs code", what);
	pthread_create(&thread, NULL, probe_thread, &probe);
	pthread_join(thread, NULL);
	log_line("%s: -> %d (expect 43)", what, probe.result);
}

int main(void)
{
	sceIoMkdir("ux0:data/exectest", 0777);
	sceIoRemove(LOG_PATH);
	log_line("exectest start, built %s %s", __DATE__, __TIME__);

	// 1. A plain RWX memory block (protection type 0x70, the type kubridge's sysmem patch maps RWX).
	{
		SceKernelAllocMemBlockKernelOpt opt;
		memset(&opt, 0, sizeof(opt));
		opt.size = sizeof(opt);
		log_line("1: kuKernelAllocMemBlock(type 0x0C20D070, 1 MB)");
		SceUID uid = kuKernelAllocMemBlock("exectest-rwx", 0x0C20D070, MB, &opt);
		log_line("1: -> 0x%08X", (unsigned)uid);
		if (uid >= 0) {
			void *base = NULL;
			sceKernelGetMemBlockBase(uid, &base);
			write_from_two_threads("1", base);
			log_line("1: free -> 0x%08X", (unsigned)sceKernelFreeMemBlock(uid));
		}
	}

	// 2. A reservation committed RWX onto the physical pages of an ordinary block (HAS_BASE).
	{
		void *phys = NULL, *view = NULL;
		SceUID physUid = sceKernelAllocMemBlock("exectest-phys", SCE_KERNEL_MEMBLOCK_TYPE_USER_RW, MB, NULL);
		sceKernelGetMemBlockBase(physUid, &phys);
		log_line("2: physical block 0x%08X at %p", (unsigned)physUid, phys);
		SceUID viewUid = kuKernelMemReserve(&view, MB, SCE_KERNEL_MEMBLOCK_TYPE_USER_RW);
		log_line("2: kuKernelMemReserve -> 0x%08X at %p", (unsigned)viewUid, view);
		if (physUid >= 0 && viewUid >= 0) {
			KuKernelMemCommitOpt opt;
			memset(&opt, 0, sizeof(opt));
			opt.size = sizeof(opt);
			opt.attr = KU_KERNEL_MEM_COMMIT_ATTR_HAS_BASE;
			opt.baseBlock = physUid;
			opt.baseOffset = 0;
			int r = kuKernelMemCommit(view, 64 * 1024, RWX, &opt);
			log_line("2: kuKernelMemCommit(64 KB, RWX, base) -> 0x%08X", (unsigned)r);
			if (r >= 0) {
				write_from_two_threads("2", view);
				log_line("2: the physical block reads back 0x%08X (expect 0xE3A0002A)", *(uint32_t *)phys);
				log_line("2: kuKernelMemDecommit(64 KB)");
				log_line("2: -> 0x%08X", (unsigned)kuKernelMemDecommit(view, 64 * 1024));
			}
			log_line("2: free reservation");
			log_line("2: -> 0x%08X", (unsigned)sceKernelFreeMemBlock(viewUid));
		}
		if (physUid >= 0) {
			log_line("2: free physical block -> 0x%08X", (unsigned)sceKernelFreeMemBlock(physUid));
		}
	}

	// 3. A reservation committed RWX with its own pages (what froze the console on release).
	{
		void *view = NULL;
		SceUID viewUid = kuKernelMemReserve(&view, MB, SCE_KERNEL_MEMBLOCK_TYPE_USER_RW);
		log_line("3: kuKernelMemReserve -> 0x%08X at %p", (unsigned)viewUid, view);
		if (viewUid >= 0) {
			int r = kuKernelMemCommit(view, 64 * 1024, RWX, NULL);
			log_line("3: kuKernelMemCommit(64 KB, RWX) -> 0x%08X", (unsigned)r);
			if (r >= 0) {
				write_from_two_threads("3", view);
				log_line("3: kuKernelMemDecommit(64 KB)");
				log_line("3: -> 0x%08X", (unsigned)kuKernelMemDecommit(view, 64 * 1024));
			}
			log_line("3: free reservation");
			log_line("3: -> 0x%08X", (unsigned)sceKernelFreeMemBlock(viewUid));
		}
	}

	log_line("exectest end, exiting");
	sceKernelExitProcess(0);
	return 0;
}
