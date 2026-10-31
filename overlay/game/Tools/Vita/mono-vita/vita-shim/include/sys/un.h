/* Unix domain sockets do not exist on the Vita; Mono only needs the type to compile. */
#ifndef VITA_SHIM_SYS_UN_H
#define VITA_SHIM_SYS_UN_H

#include <sys/socket.h>

struct sockaddr_un {
	sa_family_t sun_family;
	char sun_path[108];
};

#endif /* VITA_SHIM_SYS_UN_H */
