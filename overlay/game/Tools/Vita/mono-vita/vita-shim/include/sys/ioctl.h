/* The Vita has no terminals or ioctl-driven devices for Mono to query. */
#ifndef VITA_SHIM_SYS_IOCTL_H
#define VITA_SHIM_SYS_IOCTL_H

#include <errno.h>

struct winsize {
	unsigned short ws_row;
	unsigned short ws_col;
	unsigned short ws_xpixel;
	unsigned short ws_ypixel;
};

#define TIOCGWINSZ 0x5413
#define FIONREAD 0x541B
#define FIONBIO 0x5421

static inline int ioctl(int fd, unsigned long request, ...)
{
	(void)fd;
	(void)request;
	errno = ENOTTY;
	return -1;
}

#endif /* VITA_SHIM_SYS_IOCTL_H */
