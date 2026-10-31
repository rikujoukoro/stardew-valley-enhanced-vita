/* <sys/mman.h> for compiling CoreFX's System.Native only (the Mono runtime itself is built without
 * mmap). The Vita has no file mapping: these back MemoryMappedFile, which then fails with ENOSYS. */
#ifndef VITA_NATIVE_SYS_MMAN_H
#define VITA_NATIVE_SYS_MMAN_H

#include <errno.h>
#include <stddef.h>
#include <sys/types.h>

#define PROT_NONE 0x0
#define PROT_READ 0x1
#define PROT_WRITE 0x2
#define PROT_EXEC 0x4

#define MAP_SHARED 0x01
#define MAP_PRIVATE 0x02
#define MAP_ANON 0x20
#define MAP_ANONYMOUS MAP_ANON
#define MAP_FAILED ((void *)-1)

#define MS_ASYNC 1
#define MS_INVALIDATE 2
#define MS_SYNC 4

static inline void *mmap(void *addr, size_t length, int prot, int flags, int fd, off_t offset)
{
	(void)addr, (void)length, (void)prot, (void)flags, (void)fd, (void)offset;
	errno = ENOSYS;
	return MAP_FAILED;
}

static inline int munmap(void *addr, size_t length)
{
	(void)addr, (void)length;
	errno = ENOSYS;
	return -1;
}

static inline int mprotect(void *addr, size_t length, int prot)
{
	(void)addr, (void)length, (void)prot;
	errno = ENOSYS;
	return -1;
}

static inline int msync(void *addr, size_t length, int flags)
{
	(void)addr, (void)length, (void)flags;
	errno = ENOSYS;
	return -1;
}

static inline int mlock(const void *addr, size_t length)
{
	(void)addr, (void)length;
	errno = ENOSYS;
	return -1;
}

static inline int munlock(const void *addr, size_t length)
{
	(void)addr, (void)length;
	errno = ENOSYS;
	return -1;
}

#endif /* VITA_NATIVE_SYS_MMAN_H */
