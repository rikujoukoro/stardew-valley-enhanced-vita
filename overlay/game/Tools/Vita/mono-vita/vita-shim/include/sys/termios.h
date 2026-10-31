/* newlib-vita's <termios.h> includes <sys/termios.h>, which VitaSDK does not ship. The Vita has no
 * terminal; this is the minimal definition Mono's console code compiles against. */
#ifndef VITA_SHIM_SYS_TERMIOS_H
#define VITA_SHIM_SYS_TERMIOS_H

#define NCCS 32

typedef unsigned int tcflag_t;
typedef unsigned char cc_t;
typedef unsigned int speed_t;

struct termios {
	tcflag_t c_iflag;
	tcflag_t c_oflag;
	tcflag_t c_cflag;
	tcflag_t c_lflag;
	cc_t c_cc[NCCS];
	speed_t c_ispeed;
	speed_t c_ospeed;
};

#define TCSANOW 0
#define TCSADRAIN 1
#define TCSAFLUSH 2

#define IGNBRK 0x0001
#define BRKINT 0x0002
#define IGNPAR 0x0004
#define PARMRK 0x0008
#define INPCK 0x0010
#define ISTRIP 0x0020
#define INLCR 0x0040
#define IGNCR 0x0080
#define ICRNL 0x0100
#define IXON 0x0400
#define IXOFF 0x1000
#define IXANY 0x0800
#define OPOST 0x0001
#define ISIG 0x0001
#define ICANON 0x0002
#define ECHO 0x0008
#define ECHOE 0x0010
#define ECHOK 0x0020
#define ECHONL 0x0040
#define NOFLSH 0x0080
#define TOSTOP 0x0100
#define IEXTEN 0x8000

#define VEOF 0
#define VEOL 1
#define VERASE 2
#define VKILL 3
#define VINTR 4
#define VQUIT 5
#define VMIN 6
#define VTIME 7
#define VSUSP 8

#define TCIFLUSH 0
#define TCOFLUSH 1
#define TCIOFLUSH 2

int tcgetattr(int fd, struct termios *termios_p);
int tcsetattr(int fd, int optional_actions, const struct termios *termios_p);

#endif /* VITA_SHIM_SYS_TERMIOS_H */
