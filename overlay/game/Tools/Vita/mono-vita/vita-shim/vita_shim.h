/* Vita-only helpers the patched Mono sources call (see patch-mono.py). */
#ifndef VITA_SHIM_H
#define VITA_SHIM_H

#include <stddef.h>

/* Executable memory for JIT code and trampolines (VM-domain memory blocks). */
void *vita_exec_alloc(size_t size);
void vita_exec_free(void *pointer, size_t size);
int vita_exec_owns(const void *pointer);
/* Gives the calling thread write access to executable memory; Mono calls it when a thread
 * attaches. Threads that never attach to Mono do not need it. */
void vita_exec_thread_enter(void);
/* Makes freshly written code visible to instruction fetch. */
void vita_exec_flush(void *pointer, size_t size);
size_t vita_exec_in_use(void);
size_t vita_exec_reserved(void);
/* Arenas that are plain memory because no VM domain was available (Vita3K); 0 on the device. */
int vita_exec_fallback_arenas(void);
/* Which mechanism provides executable memory: kubridge (required on the device for JIT and
 * trampolines from several threads), the VM-domain fallback, or plain memory in an emulator. */
const char *vita_exec_backend(void);
/* Frees the executable memory blocks just before exit. No managed or JIT code may run afterwards. */
void vita_exec_shutdown(void);

/* Managed code sees device paths as "/ux0:/data/x" (see vita_paths.c); this returns the device form
 * ("ux0:/data/x") as a pointer into `path`. */
const char *vita_device_path(const char *path);

#endif /* VITA_SHIM_H */
