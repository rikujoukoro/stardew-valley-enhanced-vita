/* Mono dl fallback serving [DllImport] from statically linked tables (see vita_native.h). */

#include "vita_native.h"

#include <mono/utils/mono-dl-fallback.h>
#include <string.h>

#define MAX_LIBRARIES 16

typedef struct Library {
	const char *name;
	const VitaNativeSymbol *table;
} Library;

static Library libraries[MAX_LIBRARIES];
static int library_count;

static int vita_libc_uname(void *buf)
{
	if (!buf) {
		return -1;
	}
	memset(buf, 0, 512);
	strcpy((char *)buf, "Linux");
	return 0;
}

static int vita_libc_getifaddrs(void **ifap)
{
	if (ifap) {
		*ifap = NULL;
	}
	return 0;
}

static void vita_libc_freeifaddrs(void *ifap)
{
	(void)ifap;
}

static int vita_libc_if_nametoindex(const char *ifname)
{
	(void)ifname;
	return 0;
}

static int vita_libc_gethostname(char *name, size_t len)
{
	if (name && len > 0) {
		strncpy(name, "psvita", len - 1);
		name[len - 1] = '\0';
	}
	return 0;
}

static int vita_libc_getdomainname(char *name, size_t len)
{
	if (name && len > 0) {
		name[0] = '\0';
	}
	return 0;
}

static const VitaNativeSymbol vita_libc_symbols[] = {
	{ "uname", (void *)vita_libc_uname },
	{ "getifaddrs", (void *)vita_libc_getifaddrs },
	{ "freeifaddrs", (void *)vita_libc_freeifaddrs },
	{ "if_nametoindex", (void *)vita_libc_if_nametoindex },
	{ "gethostname", (void *)vita_libc_gethostname },
	{ "getdomainname", (void *)vita_libc_getdomainname },
	{ NULL, NULL }
};

int vita_native_register_library(const char *name, const VitaNativeSymbol *table)
{
	if (library_count == MAX_LIBRARIES) {
		return 0;
	}
	libraries[library_count].name = name;
	libraries[library_count].table = table;
	library_count++;
	return 1;
}

static int ends_with(const char *s, size_t length, const char *suffix)
{
	size_t n = strlen(suffix);
	return length >= n && memcmp(s + length - n, suffix, n) == 0;
}

/* "./libSystem.Native.so" -> "System.Native" (as a length-limited view into `path`). */
static const char *library_base_name(const char *path, size_t *length)
{
	const char *slash = strrchr(path, '/');
	const char *name = slash ? slash + 1 : path;
	size_t n = strlen(name);
	if (ends_with(name, n, ".so")) {
		n -= 3;
	} else if (ends_with(name, n, ".dll")) {
		n -= 4;
	} else if (ends_with(name, n, ".dylib")) {
		n -= 6;
	}
	if (n > 3 && memcmp(name, "lib", 3) == 0) {
		name += 3;
		n -= 3;
	}
	*length = n;
	return name;
}

static void *fallback_load(const char *name, int flags, char **err, void *user_data)
{
	size_t length;
	const char *base = library_base_name(name, &length);
	(void)flags;
	(void)user_data;
	*err = NULL;
	for (int i = 0; i < library_count; i++) {
		if ((strlen(libraries[i].name) == length && memcmp(libraries[i].name, base, length) == 0) ||
		    strcmp(libraries[i].name, name) == 0) {
			return (void *)&libraries[i];
		}
	}
	return NULL;
}

static void *fallback_symbol(void *handle, const char *name, char **err, void *user_data)
{
	const Library *library = (const Library *)handle;
	(void)user_data;
	*err = NULL;
	for (const VitaNativeSymbol *s = library->table; s->name != NULL; s++) {
		if (strcmp(s->name, name) == 0) {
			return s->address;
		}
	}
	return NULL;
}

static void *fallback_close(void *handle, void *user_data)
{
	(void)handle;
	(void)user_data;
	return NULL;
}

void vita_native_install(void)
{
	vita_native_register_library("System.Native", vita_system_native_symbols);
	vita_native_register_library("libc", vita_libc_symbols);
	vita_native_register_library("c", vita_libc_symbols);
	mono_dl_fallback_register(fallback_load, fallback_symbol, fallback_close, NULL);
}
