/* Vita device paths for managed code.
 *
 * Vita paths start with a device ("ux0:/data/x", "app0:file"), which .NET's Unix Path class does not
 * consider rooted: Path.GetFullPath prefixes the current directory, again on every call
 * ("app0:/app0:/ux0:..."). Managed code therefore sees a Unix-like view with the device as the first
 * component ("/ux0:/data/x"), and the libc entry points that take paths (wrapped at link time with
 * -Wl,--wrap, see vita-mono.cmake) translate back:
 *   "/ux0:/data/x"           -> "ux0:/data/x"
 *   "app0:/ux0:data/x"       -> "ux0:data/x"   (a device path made relative by mistake)
 * getcwd reports "/app0:/" so relative managed paths resolve into the same view.
 */

#include "vita_shim.h"

#include <dirent.h>
#include <fcntl.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/time.h>
#include <utime.h>
#include <unistd.h>

/* Length of a "<device>:" prefix at s (letters and digits, then ':'), or 0. */
static size_t device_prefix_length(const char *s)
{
	size_t n = 0;
	while ((s[n] >= 'a' && s[n] <= 'z') || (s[n] >= 'A' && s[n] <= 'Z') || (s[n] >= '0' && s[n] <= '9')) {
		n++;
	}
	return (n >= 2 && n <= 16 && s[n] == ':') ? n + 1 : 0;
}

const char *vita_device_path(const char *path)
{
	if (path == NULL) {
		return NULL;
	}
	for (;;) {
		size_t device;
		const char *rest;
		if (path[0] == '/' && device_prefix_length(path + 1) != 0) {
			path++;
		}
		device = device_prefix_length(path);
		if (device == 0) {
			return path;
		}
		rest = path + device;
		if (*rest == '/') {
			rest++;
		}
		/* Another device right after this one: the outer one was prepended by mistake. */
		if (device_prefix_length(rest) != 0 || (rest[0] == '/' && device_prefix_length(rest + 1) != 0)) {
			path = rest;
			continue;
		}
		return path;
	}
}

int __real_open(const char *path, int flags, ...);
int __wrap_open(const char *path, int flags, ...)
{
	int mode = 0;
	if (flags & O_CREAT) {
		va_list args;
		va_start(args, flags);
		mode = va_arg(args, int);
		va_end(args);
	}
	return __real_open(vita_device_path(path), flags, mode);
}

FILE *__real_fopen(const char *path, const char *mode);
FILE *__wrap_fopen(const char *path, const char *mode)
{
	return __real_fopen(vita_device_path(path), mode);
}

int __real_stat(const char *path, struct stat *buf);
int __wrap_stat(const char *path, struct stat *buf)
{
	return __real_stat(vita_device_path(path), buf);
}

int __real_lstat(const char *path, struct stat *buf);
int __wrap_lstat(const char *path, struct stat *buf)
{
	return __real_lstat(vita_device_path(path), buf);
}

int __real_access(const char *path, int mode);
int __wrap_access(const char *path, int mode)
{
	return __real_access(vita_device_path(path), mode);
}

int __real_mkdir(const char *path, mode_t mode);
int __wrap_mkdir(const char *path, mode_t mode)
{
	return __real_mkdir(vita_device_path(path), mode);
}

int __real_rmdir(const char *path);
int __wrap_rmdir(const char *path)
{
	return __real_rmdir(vita_device_path(path));
}

int __real_unlink(const char *path);
int __wrap_unlink(const char *path)
{
	return __real_unlink(vita_device_path(path));
}

int __real_remove(const char *path);
int __wrap_remove(const char *path)
{
	return __real_remove(vita_device_path(path));
}

int __real_rename(const char *from, const char *to);
int __wrap_rename(const char *from, const char *to)
{
	return __real_rename(vita_device_path(from), vita_device_path(to));
}

DIR *__real_opendir(const char *path);
DIR *__wrap_opendir(const char *path)
{
	return __real_opendir(vita_device_path(path));
}

int __real_chdir(const char *path);
int __wrap_chdir(const char *path)
{
	return __real_chdir(vita_device_path(path));
}

int __real_chmod(const char *path, mode_t mode);
int __wrap_chmod(const char *path, mode_t mode)
{
	return __real_chmod(vita_device_path(path), mode);
}

int __real_truncate(const char *path, off_t length);
int __wrap_truncate(const char *path, off_t length)
{
	return __real_truncate(vita_device_path(path), length);
}

int __real_utime(const char *path, const struct utimbuf *times);
int __wrap_utime(const char *path, const struct utimbuf *times)
{
	return __real_utime(vita_device_path(path), times);
}

int __real_utimes(const char *path, const struct timeval times[2]);
int __wrap_utimes(const char *path, const struct timeval times[2])
{
	return __real_utimes(vita_device_path(path), times);
}

char *__real_realpath(const char *path, char *resolved);
char *__wrap_realpath(const char *path, char *resolved)
{
	return __real_realpath(vita_device_path(path), resolved);
}

/* "app0:/" -> "/app0:/": relative managed paths then resolve to rooted ones. */
char *__real_getcwd(char *buf, size_t size);
char *__wrap_getcwd(char *buf, size_t size)
{
	char *result = __real_getcwd(buf, size);
	size_t length;
	if (result == NULL || result[0] == '/') {
		return result;
	}
	length = strlen(result);
	if (buf == NULL) {
		/* getcwd(NULL, ...) allocated the result: replace it with a longer copy. */
		char *rooted = (char *)malloc(length + 2);
		if (rooted == NULL) {
			return result;
		}
		rooted[0] = '/';
		memcpy(rooted + 1, result, length + 1);
		free(result);
		return rooted;
	}
	if (length + 2 > size) {
		return result; /* no room: leave it as the kernel reported it */
	}
	memmove(result + 1, result, length + 1);
	result[0] = '/';
	return result;
}
