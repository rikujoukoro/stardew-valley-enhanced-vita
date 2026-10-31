#!/usr/bin/env python3
"""Applies the PS Vita port edits to a pristine Mono source tree.

Usage: patch-mono.py <mono-source-dir>

Each edit names the file, an anchor that must occur exactly once, and what replaces or follows it.
A missing or ambiguous anchor stops the script, so a Mono update shows exactly which edit needs
rework. Edits are idempotent: a tree that already has an edit is left alone.

Design notes (see README.md): GC suspension is fully cooperative (--enable-cooperative-suspend), so
nothing here emulates POSIX signals; newlib gaps are filled by vita-shim/, not by editing Mono.
Parts of this port follow findings published by Duro02/vita-mono-port (MIT).
"""
import sys
from pathlib import Path

EDITS = []


def edit(path, anchor, replacement=None, after=None, before=None, why=""):
    EDITS.append(dict(path=path, anchor=anchor, replacement=replacement, after=after, before=before, why=why))


# --- configure.ac --------------------------------------------------------------------------------

edit("configure.ac",
     why="host: PS Vita (VitaSDK newlib + pthread-embedded); single process, no async signals, no dlopen",
     anchor="\t*-*-*netbsd*)\n\t\tCPPFLAGS=\"$CPPFLAGS -D_REENTRANT -DGC_NETBSD_THREADS -D_GNU_SOURCE\"",
     before="""\tarm*-vita*-eabi*)
\t\tdnl PS Vita (VitaSDK, newlib + pthread-embedded): one process, no asynchronous signals, no
\t\tdnl dynamic loading. Build with --enable-cooperative-suspend: GC suspension never signals.
\t\tCPPFLAGS="$CPPFLAGS -DHOST_VITA"
\t\tlibgc_threads=pthreads
\t\tneed_link_unlink=yes
\t\twith_tls=pthread
\t\twith_sigaltstack=no
\t\tuse_sigposix=yes
\t\twith_sgen_default_concurrent=no
\t\t;;
""")

edit("configure.ac",
     why="target: 32-bit ARM EABI hard-float, AOT supported",
     anchor="\tarm*-netbsd*-eabi*)\n\t\tTARGET=ARM;\n\t\tarch_target=arm;",
     before="""\tarm*-vita*-eabi*)
\t\tTARGET=ARM;
\t\tarch_target=arm;
\t\tACCESS_UNALIGNED="no"
\t\tAOT_SUPPORTED="yes"
\t\tCPPFLAGS="$CPPFLAGS -D__ARM_EABI__"
\t\t;;
""")

edit("configure.ac",
     why="cross compiler (host != target) targeting the Vita: the AOT compiler (build-mono-cross.sh). It must "
         "generate code for the runtime's ABI (hard-float, no unaligned access); TARGET_VITA selects the "
         "Vita's offsets in the header from gen-cross-offsets.sh",
     anchor="   arm*-netbsd*-eabi*)\n\t\tTARGET=ARM;\n\t\tCPPFLAGS=\"$CPPFLAGS -D__ARM_EABI__\"\n\t\t;;",
     before="""   arm*-vita*-eabi*)
\t\tTARGET=ARM;
\t\tarm_fpu=VFP_HARD
\t\tACCESS_UNALIGNED="no"
\t\tCPPFLAGS="$CPPFLAGS -D__ARM_EABI__ -DTARGET_VITA"
\t\t;;
""")


edit("mono/metadata/w32socket.c",
     why="PS Vita newlib lacks IPv6 headers; define dummy constants and struct ipv6_mreq so IPv4-only socket icalls compile",
     anchor="#include <mono/metadata/w32socket.h>",
     after="""

#ifdef HOST_VITA
/* VITA_DUMMY_IPV6: PS Vita has no IPv6 headers in newlib */
#include <netinet/in.h>
#ifndef IPV6_UNICAST_HOPS
#define IPV6_UNICAST_HOPS 16
#define IPV6_MULTICAST_IF 17
#define IPV6_MULTICAST_HOPS 18
#define IPV6_MULTICAST_LOOP 19
#define IPV6_JOIN_GROUP 20
#define IPV6_LEAVE_GROUP 21
#define IPV6_V6ONLY 27

struct ipv6_mreq {
	struct in6_addr ipv6mr_multiaddr;
	unsigned int ipv6mr_interface;
};
#endif
#endif""")

# --- mono/eglib ----------------------------------------------------------------------------------

edit("mono/eglib/gmisc-unix.c",
     why="Vita device paths (app0:/..., ux0:/...) are absolute; otherwise Mono prefixes the cwd",
     anchor="\tg_return_val_if_fail (filename != NULL, FALSE);\n\n\treturn (*filename == '/');",
     replacement="""\tg_return_val_if_fail (filename != NULL, FALSE);

#ifdef HOST_VITA
\t/* "<device>:" before any separator, as in app0:/ or ux0:data/. */
\tfor (const char *c = filename; *c && *c != '/'; c++) {
\t\tif (*c == ':')
\t\t\treturn TRUE;
\t}
#endif
\treturn (*filename == '/');""")

# --- mono/utils ----------------------------------------------------------------------------------

edit("mono/utils/networking-posix.c",
     why="PS Vita newlib lacks SIOCGIFCONF and getifaddrs; provide fallback mono_get_local_interfaces returning NULL (like Windows)",
     anchor="""\tfreeifaddrs (ifap);
\t*interface_count = if_count;
\treturn result;
}

#endif""",
     replacement="""\tfreeifaddrs (ifap);
\t*interface_count = if_count;
\treturn result;
}

#else

void *
mono_get_local_interfaces (int family, int *interface_count)
{
\t*interface_count = 0;
\treturn NULL;
}

#endif""")

edit("mono/utils/mono-sigcontext.h",
     why="no signal contexts are ever delivered on the Vita; a register layout only so ARM code compiles",
     anchor="#elif defined(__NetBSD__)\n\ttypedef ucontext_t arm_ucontext;\n\n\t#define UCONTEXT_REG_PC(ctx) (((ucontext_t*)(ctx))->uc_mcontext.__gregs[_REG_PC])",
     before="""#elif defined(HOST_VITA)
\t/* PS Vita: no asynchronous signals, so no signal context ever reaches Mono. */
\ttypedef struct arm_ucontext {
\t\tunsigned long r [16];
\t\tunsigned long cpsr;
\t} arm_ucontext;
\t#define UCONTEXT_REG_R0(ctx) (((arm_ucontext*)(ctx))->r [0])
\t#define UCONTEXT_REG_R1(ctx) (((arm_ucontext*)(ctx))->r [1])
\t#define UCONTEXT_REG_R2(ctx) (((arm_ucontext*)(ctx))->r [2])
\t#define UCONTEXT_REG_R3(ctx) (((arm_ucontext*)(ctx))->r [3])
\t#define UCONTEXT_REG_R4(ctx) (((arm_ucontext*)(ctx))->r [4])
\t#define UCONTEXT_REG_R5(ctx) (((arm_ucontext*)(ctx))->r [5])
\t#define UCONTEXT_REG_R6(ctx) (((arm_ucontext*)(ctx))->r [6])
\t#define UCONTEXT_REG_R7(ctx) (((arm_ucontext*)(ctx))->r [7])
\t#define UCONTEXT_REG_R8(ctx) (((arm_ucontext*)(ctx))->r [8])
\t#define UCONTEXT_REG_R9(ctx) (((arm_ucontext*)(ctx))->r [9])
\t#define UCONTEXT_REG_R10(ctx) (((arm_ucontext*)(ctx))->r [10])
\t#define UCONTEXT_REG_R11(ctx) (((arm_ucontext*)(ctx))->r [11])
\t#define UCONTEXT_REG_R12(ctx) (((arm_ucontext*)(ctx))->r [12])
\t#define UCONTEXT_REG_SP(ctx) (((arm_ucontext*)(ctx))->r [13])
\t#define UCONTEXT_REG_LR(ctx) (((arm_ucontext*)(ctx))->r [14])
\t#define UCONTEXT_REG_PC(ctx) (((arm_ucontext*)(ctx))->r [15])
\t#define UCONTEXT_REG_CPSR(ctx) (((arm_ucontext*)(ctx))->cpsr)
""")

edit("mono/utils/mono-threads-posix-signals.c",
     why="no signal-based suspend backend on the Vita (cooperative suspend only)",
     anchor="#if defined(USE_POSIX_BACKEND)\n",
     replacement="#if defined(USE_POSIX_BACKEND) && !defined(HOST_VITA)\n")

edit("mono/utils/mono-threads-posix-signals.c",
     why="Vita stand-ins: no signals exist; cooperative suspend never asks for them",
     anchor="#else\n\n#include <mono/utils/mono-compiler.h>\n\nMONO_EMPTY_SOURCE_FILE (mono_threads_posix_signals);",
     replacement="""#elif defined(HOST_VITA)

#include "mono-threads.h"

/* PS Vita: no asynchronous signals. Cooperative suspend (the only mode this port builds) never
 * signals a thread; these exist so the POSIX backend links. */
gint
mono_threads_suspend_search_alternative_signal (void)
{
\treturn -1;
}

void
mono_threads_suspend_init_signals (void)
{
}

gint
mono_threads_suspend_get_suspend_signal (void)
{
\treturn -1;
}

gint
mono_threads_suspend_get_restart_signal (void)
{
\treturn -1;
}

gint
mono_threads_suspend_get_abort_signal (void)
{
\treturn -1;
}

#else

#include <mono/utils/mono-compiler.h>

MONO_EMPTY_SOURCE_FILE (mono_threads_posix_signals);""")

edit("mono/utils/networking-posix.c",
     why="newlib has no getprotobyname: use the standard protocol numbers",
     anchor="\treturn *proto;\n}\n\n#endif\n\nint\nmono_networking_get_tcp_protocol (void)",
     replacement="""\treturn *proto;
}

#else

/* No protocol database (PS Vita): the well-known numbers. */
static int
fetch_protocol (const char *proto_name, int *cache, int *proto, int default_val)
{
\t*proto = default_val;
\t*cache = 1;
\treturn *proto;
}

#endif

int
mono_networking_get_tcp_protocol (void)""")

edit("mono/metadata/console-unix.c",
     why="no terminal on the Vita: use the null console driver (same icalls)",
     anchor="#include <config.h>\n#include <glib.h>\n#include <stdio.h>\n#include <string.h>\n#include <fcntl.h>\n",
     replacement="#include <config.h>\n#ifdef HOST_VITA\n/* PS Vita: no terminal; the null driver provides the same icalls. */\n#include \"console-null.c\"\n#else\n#include <glib.h>\n#include <stdio.h>\n#include <string.h>\n#include <fcntl.h>\n")

edit("mono/metadata/console-unix.c",
     why="end of the non-Vita console driver",
     anchor="\t\tmono_atexit (tty_teardown);\n\t}\n\n\treturn TRUE;\n}\n",
     after="#endif /* HOST_VITA */\n")

edit("mono/utils/dlmalloc.c",
     why="dlmalloc is not used on the Vita (mono-codeman.c); keep it compilable without mmap/sbrk",
     anchor="#ifndef HAVE_MMAP\n#define HAVE_MMAP 1\n#endif  /* HAVE_MMAP */",
     before="""#ifdef HOST_VITA
/* PS Vita: the code manager allocates from VM-domain memory instead (mono-codeman.c). */
#define HAVE_MMAP 0
#define HAVE_MORECORE 0
#define LACKS_SYS_MMAN_H
#endif
""")

edit("mono/utils/mono-mmap.c",
     why="no mmap: the malloc-based mono_valloc; a placement hint cannot be honoured, so ignore it",
     anchor="\tg_assert (addr == NULL);\n\treturn mono_valloc_aligned (length, mono_pagesize (), flags, type);",
     replacement="#ifndef HOST_VITA\n\tg_assert (addr == NULL);\n#endif\n\treturn mono_valloc_aligned (length, mono_pagesize (), flags, type);")

edit("mono/utils/mono-mmap.c",
     why="executable allocations (JIT code, trampolines) come from VM-domain memory",
     anchor="\tvoid *res = NULL;\n\tif (posix_memalign (&res, alignment, size))\n\t\treturn NULL;",
     replacement="""\tvoid *res = NULL;
#ifdef HOST_VITA
\t/* Ordinary memory is not executable on the Vita. */
\tif (flags & MONO_MMAP_EXEC)
\t\treturn vita_exec_alloc (size);
#endif
\tif (posix_memalign (&res, alignment, size))
\t\treturn NULL;""")

edit("mono/utils/mono-mmap.c",
     why="free executable allocations back to the VM-domain arena",
     anchor="mono_vfree (void *addr, size_t length, MonoMemAccountType type)\n{\n\tg_free (addr);",
     replacement="""mono_vfree (void *addr, size_t length, MonoMemAccountType type)
{
#ifdef HOST_VITA
\tif (vita_exec_owns (addr)) {
\t\tvita_exec_free (addr, length);
\t\treturn 0;
\t}
#endif
\tg_free (addr);""")

edit("mono/utils/mono-mmap.c",
     why="declarations of the Vita executable-memory helpers",
     anchor="/* dummy malloc-based implementation */",
     before="#ifdef HOST_VITA\n#include \"vita_shim.h\"\n#endif\n\n")

edit("mono/utils/mono-mmap.c",
     why="mono_setmmapjit only exists in the mmap implementation; MAP_JIT is an Apple notion",
     anchor="/* dummy malloc-based implementation */\nint\nmono_pagesize (void)\n{\n\treturn 4096;\n}\n",
     after="\nvoid\nmono_setmmapjit (int flag)\n{\n}\n")

edit("mono/metadata/w32file-unix.c",
     why="newlib's stat has no inode numbers, so every file would share one share-mode entry",
     anchor="""{
\tgboolean file_already_shared;
\tguint32 file_existing_share, file_existing_access;

\tfile_already_shared = file_share_get (statbuf->st_dev, statbuf->st_ino, sharemode, fileaccess, &file_existing_share, &file_existing_access, share_info);""",
     replacement="""{
\tgboolean file_already_shared;
\tguint32 file_existing_share, file_existing_access;

#ifdef HOST_VITA
\t/* PS Vita: stat reports no inode numbers (every file would look like the same one), and a
\t * single process needs no FileShare emulation: each open gets an entry of its own, keyed by a
\t * unique fake inode, so opens never conflict and the handle bookkeeping stays as usual. */
\tstatic gint32 vita_fake_inode;
\tfile_share_get (statbuf->st_dev, ((guint64) 1 << 40) | (guint32) mono_atomic_inc_i32 (&vita_fake_inode), sharemode, fileaccess, &file_existing_share, &file_existing_access, share_info);
\treturn TRUE;
#endif
\tfile_already_shared = file_share_get (statbuf->st_dev, statbuf->st_ino, sharemode, fileaccess, &file_existing_share, &file_existing_access, share_info);""")

edit("mono/metadata/w32file-unix.c",
     why="same for deletes",
     anchor="""{
\tgboolean file_already_shared;
\tguint32 file_existing_share, file_existing_access;

\tfile_already_shared = file_share_get (statbuf->st_dev, statbuf->st_ino, FILE_SHARE_DELETE, GENERIC_READ, &file_existing_share, &file_existing_access, share_info);""",
     replacement="""{
\tgboolean file_already_shared;
\tguint32 file_existing_share, file_existing_access;

#ifdef HOST_VITA
\t*share_info = NULL;
\treturn TRUE;
#endif
\tfile_already_shared = file_share_get (statbuf->st_dev, statbuf->st_ino, FILE_SHARE_DELETE, GENERIC_READ, &file_existing_share, &file_existing_access, share_info);""")

edit("mono/metadata/w32file-unix.c",
     why="newlib's fcntl does not know the standard streams: Console's static constructor would throw",
     anchor="\tif (flags == -1) {\n\t\t/* Invalid fd.  Not really much point checking for EBADF",
     before="""#ifdef HOST_VITA
\t/* PS Vita: fds 0-2 are always open (kernel tty handles behind newlib), but fcntl fails on them.
\t * An invalid console handle makes System.Console's static constructor throw an ArgumentException
\t * (Console.Open only catches IOException), so the first Console use would end the process. */
\tif (flags == -1 && fd >= 0 && fd <= 2)
\t\tflags = fd == 0 ? O_RDONLY : O_WRONLY;
#endif
""")

edit("mono/utils/mono-os-mutex.h",
     why="pthread-embedded measures timed waits against the wall clock (_ftime), not CLOCK_MONOTONIC",
     anchor="#if !defined(CLOCK_MONOTONIC) || defined(HOST_DARWIN) || defined(HOST_ANDROID) || defined(HOST_WASM)\n#define BROKEN_CLOCK_SOURCE",
     replacement="#if !defined(CLOCK_MONOTONIC) || defined(HOST_DARWIN) || defined(HOST_ANDROID) || defined(HOST_WASM) || defined(HOST_VITA)\n#define BROKEN_CLOCK_SOURCE")

edit("mono/metadata/w32file-unix.c",
     why="no statfs on the Vita: report an unknown file system type",
     anchor="#if defined (HOST_DARWIN) || defined (__linux__) || defined(HOST_BSD) || defined(__FreeBSD_kernel__) || defined(__HAIKU__) || defined(_AIX)\nstatic gchar*\nget_fstypename",
     replacement="#if defined (HOST_DARWIN) || defined (__linux__) || defined(HOST_BSD) || defined(__FreeBSD_kernel__) || defined(__HAIKU__) || defined(_AIX) || defined(HOST_VITA)\nstatic gchar*\nget_fstypename")

edit("mono/utils/mono-threads-posix.c",
     why="no <sys/mman.h> on the Vita; this file only uses mono_mprotect",
     anchor="#include <pthread.h>\n\n#include <sys/mman.h>\n",
     replacement="#include <pthread.h>\n\n#ifndef HOST_VITA\n#include <sys/mman.h>\n#endif\n")

edit("mono/utils/mono-threads-posix.c",
     why="stack bounds of the current thread from the kernel (the GC scans exactly this range); vita_shim.h also declares vita_exec_thread_enter, used below",
     anchor="#endif /* defined(_POSIX_VERSION) */\n\n#if defined(USE_POSIX_BACKEND)",
     before="""#ifdef HOST_VITA
#include <psp2/kernel/threadmgr.h>
#include "vita_shim.h"

/* PS Vita: every pthread is a kernel thread whose stack the kernel reports. */
void
mono_threads_platform_get_stack_bounds (guint8 **staddr, size_t *stsize)
{
\tSceKernelThreadInfo info;

\tmemset (&info, 0, sizeof (info));
\tinfo.size = sizeof (info);
\tif (sceKernelGetThreadInfo (sceKernelGetThreadId (), &info) == 0 && info.stack && info.stackSize > 0) {
\t\t*staddr = (guint8 *) info.stack;
\t\t*stsize = (size_t) info.stackSize;
\t\treturn;
\t}
\t/* Should not happen; a range ending at the current frame keeps the GC scanning live data. */
\t*staddr = (guint8 *) __builtin_frame_address (0) - 64 * 1024;
\t*stsize = 128 * 1024;
}
#endif

""")

edit("mono/utils/mono-hwcap-arm.c",
     why="no auxv or /proc/cpuinfo; the Vita's CPU is a fixed Cortex-A9 MPCore",
     anchor="\t/* TODO: Find a way to detect features like Thumb and VFP. */\n#elif defined (_WIN32)",
     replacement="""\t/* TODO: Find a way to detect features like Thumb and VFP. */
#elif defined (HOST_VITA)
\t/* PS Vita: quad-core ARM Cortex-A9 MPCore (ARMv7-A, Thumb-2, VFPv3-D32, NEON). */
\tmono_hwcap_arm_is_v5 = TRUE;
\tmono_hwcap_arm_is_v6 = TRUE;
\tmono_hwcap_arm_is_v7 = TRUE;
\tmono_hwcap_arm_has_vfp = TRUE;
\tmono_hwcap_arm_has_vfp3 = TRUE;
\tmono_hwcap_arm_has_vfp3_d16 = TRUE;
\tmono_hwcap_arm_has_thumb = TRUE;
\tmono_hwcap_arm_has_thumb2 = TRUE;
#elif defined (_WIN32)""")

edit("mono/utils/mono-threads-posix.c",
     why="VM-domain access (writing JIT code and trampolines) is granted per thread: open it on attach",
     anchor="mono_threads_suspend_register (MonoThreadInfo *info)\n{\n#if defined (HOST_ANDROID)\n\tinfo->native_handle = gettid ();\n#endif\n}",
     replacement="""mono_threads_suspend_register (MonoThreadInfo *info)
{
#if defined (HOST_ANDROID)
\tinfo->native_handle = gettid ();
#endif
#ifdef HOST_VITA
\t/* sceKernelOpenVMDomain only affects the calling thread; every thread that may emit
\t * trampolines or JIT code opens it when it joins the runtime. */
\tvita_exec_thread_enter ();
#endif
}""")

edit("mono/utils/mono-codeman.c",
     why="dynamic code (DynamicMethod, compiled expressions) needs executable memory too",
     anchor="""#else
\tmono_codeman_enable_write ();
\tgpointer res = dlmemalign (MIN_ALIGN, n);
\tmono_codeman_disable_write ();
\treturn res;
#endif""",
     replacement="""#elif defined (HOST_VITA)
\t/* VM-domain memory; the size is kept in front of the block for mono_codeman_free. */
\tguint8 *block = (guint8 *) vita_exec_alloc (n + 16);
\tif (!block)
\t\treturn NULL;
\t*(gsize *) block = n + 16;
\treturn block + 16;
#else
\tmono_codeman_enable_write ();
\tgpointer res = dlmemalign (MIN_ALIGN, n);
\tmono_codeman_disable_write ();
\treturn res;
#endif""")

edit("mono/utils/mono-codeman.c",
     why="free dynamic code back to the VM-domain arena",
     anchor="""#else
\tmono_codeman_enable_write ();
\tdlfree (p);
\tmono_codeman_disable_write ();
#endif""",
     replacement="""#elif defined (HOST_VITA)
\tguint8 *block = (guint8 *) p - 16;
\tvita_exec_free (block, *(gsize *) block);
#else
\tmono_codeman_enable_write ();
\tdlfree (p);
\tmono_codeman_disable_write ();
#endif""")

edit("mono/utils/mono-codeman.c",
     why="declarations of the Vita executable-memory helpers",
     anchor="#include \"dlmalloc.h\"",
     after="\n#ifdef HOST_VITA\n#include \"vita_shim.h\"\n#endif")

# --- mono/sgen -----------------------------------------------------------------------------------

edit("mono/sgen/sgen-conf.h",
     why="serial sweep: no sweep worker thread (its stack costs RAM, and it is the reported hang on the Vita)",
     anchor="#if defined (HOST_WASM)\n#define DEFAULT_MAJOR SGEN_MAJOR_SERIAL\n#define DEFAULT_SWEEP_MODE SGEN_SWEEP_SERIAL",
     replacement="#if defined (HOST_WASM) || defined (HOST_VITA)\n#define DEFAULT_MAJOR SGEN_MAJOR_SERIAL\n#define DEFAULT_SWEEP_MODE SGEN_SWEEP_SERIAL")

edit("mono/sgen/sgen-marksweep.c",
     why="major blocks come from one malloc'd run; freeing a block in its middle would corrupt the heap",
     anchor="#if defined(HOST_WIN32) || defined(HOST_ORBIS) || defined (HOST_WASM)\n\t\t/*\n\t\t * sgen_free_os_memory () asserts in mono_vfree ()",
     replacement="#if defined(HOST_WIN32) || defined(HOST_ORBIS) || defined (HOST_WASM) || defined (HOST_VITA)\n\t\t/*\n\t\t * PS Vita: mono_valloc is posix_memalign there, so the same applies.\n\t\t * sgen_free_os_memory () asserts in mono_vfree ()")

# --- external/corefx (System.Native, built by build-mono-vita.sh's native stage) -----------------

edit("external/corefx/src/Native/Unix/System.Native/pal_io.c",
     why="newlib-vita defines POLLPRI as POLLIN, which would duplicate a case label",
     anchor="""                pollEvents[i].TriggeredEvents = PAL_POLLIN;
                break;
            case POLLPRI:
                pollEvents[i].TriggeredEvents = PAL_POLLPRI;
                break;""",
     replacement="""                pollEvents[i].TriggeredEvents = PAL_POLLIN;
                break;
#if POLLPRI != POLLIN
            case POLLPRI:
                pollEvents[i].TriggeredEvents = PAL_POLLPRI;
                break;
#endif""")

# --- mono/mini -----------------------------------------------------------------------------------

edit("mono/mini/mini-runtime.c",
     why="newlib's siginfo_t has no si_addr (and no SIGSEGV is ever delivered on the Vita)",
     anchor="#if defined(HAVE_SIG_INFO)\n#if !defined(HOST_WIN32)\n\tfault_addr = info->si_addr;",
     replacement="#if defined(HAVE_SIG_INFO)\n#if !defined(HOST_WIN32) && !defined(HOST_VITA)\n\tfault_addr = info->si_addr;")

edit("mono/mini/mini-runtime.c",
     why="newlib's siginfo_t has no si_addr",
     anchor="#elif defined (HAVE_SIG_INFO)\n\tgpointer const debug_fault_addr = MONO_SIG_HANDLER_GET_INFO ()->si_addr;",
     replacement="#elif defined (HOST_VITA)\n\tgpointer const debug_fault_addr = NULL;\n#elif defined (HAVE_SIG_INFO)\n\tgpointer const debug_fault_addr = MONO_SIG_HANDLER_GET_INFO ()->si_addr;")

edit("mono/mini/mini-arm.h",
     why="newlib's sigaction has no SA_SIGINFO handlers; like watchOS, use single-argument handlers",
     anchor="#if defined(HOST_WATCHOS)\n#undef MONO_ARCH_USE_SIGACTION\n#endif",
     replacement="#if defined(HOST_WATCHOS) || defined(HOST_VITA)\n#undef MONO_ARCH_USE_SIGACTION\n#endif")

edit("mono/mini/mini-arm.c",
     why="no signals and no soft debugger on the Vita: never a single-step event",
     anchor="\tsiginfo_t *sinfo = (siginfo_t*)info;\n\n\tif (!ss_trigger_page)\n\t\treturn FALSE;\n\n\t/* Sometimes the address is off by 4 */",
     replacement="#ifdef HOST_VITA\n\treturn FALSE;\n#else\n\tsiginfo_t *sinfo = (siginfo_t*)info;\n\n\tif (!ss_trigger_page)\n\t\treturn FALSE;\n\n\t/* Sometimes the address is off by 4 */")

edit("mono/mini/mini-arm.c",
     why="end of the single-step check",
     anchor="\tif (sinfo->si_addr >= ss_trigger_page && (guint8*)sinfo->si_addr <= (guint8*)ss_trigger_page + 128)\n\t\treturn TRUE;\n\telse\n\t\treturn FALSE;\n}",
     replacement="\tif (sinfo->si_addr >= ss_trigger_page && (guint8*)sinfo->si_addr <= (guint8*)ss_trigger_page + 128)\n\t\treturn TRUE;\n\telse\n\t\treturn FALSE;\n#endif\n}")

edit("mono/mini/mini-arm.c",
     why="no signals and no soft debugger on the Vita: never a breakpoint event",
     anchor="\tsiginfo_t *sinfo = (siginfo_t*)info;\n\n\tif (!ss_trigger_page)\n\t\treturn FALSE;\n\n\tif (sinfo->si_signo == DBG_SIGNAL) {",
     replacement="#ifdef HOST_VITA\n\treturn FALSE;\n#else\n\tsiginfo_t *sinfo = (siginfo_t*)info;\n\n\tif (!ss_trigger_page)\n\t\treturn FALSE;\n\n\tif (sinfo->si_signo == DBG_SIGNAL) {")

edit("mono/mini/mini-arm.c",
     why="end of the breakpoint check",
     anchor="\t\telse\n\t\t\treturn FALSE;\n\t} else {\n\t\treturn FALSE;\n\t}\n}",
     replacement="\t\telse\n\t\t\treturn FALSE;\n\t} else {\n\t\treturn FALSE;\n\t}\n#endif\n}")

edit("mono/mini/decompose.c",
     why="Cortex-A9 has no divide instruction, so every 32-bit / and % calls an emulation helper. The helper "
         "raised DivideByZero/Overflow itself, which needs an icall wrapper around each call (5% of the main "
         "thread on the farm, mostly Dictionary lookups). Emit those checks inline, as for hardware division, "
         "so mini-runtime.c can call the helpers directly",
     anchor="\tcase OP_IREM_UN:\n\t\tif (cfg->backend->emulate_div && mono_arch_opcode_needs_emulation (cfg, ins->opcode))\n\t\t\temulate = TRUE;\n",
     after="""#if defined (HOST_VITA) || defined (TARGET_VITA)
		if (emulate && cfg->backend->need_div_check) {
			int reg1 = alloc_ireg (cfg);
			int reg2 = alloc_ireg (cfg);
			/* b == 0 */
			MONO_EMIT_NEW_ICOMPARE_IMM (cfg, ins->sreg2, 0);
			MONO_EMIT_NEW_COND_EXC (cfg, IEQ, "DivideByZeroException");
			if (ins->opcode == OP_IDIV || ins->opcode == OP_IREM) {
				/* b == -1 && a == 0x80000000 */
				MONO_EMIT_NEW_ICOMPARE_IMM (cfg, ins->sreg2, -1);
				MONO_EMIT_NEW_UNALU (cfg, OP_ICEQ, reg1, -1);
				MONO_EMIT_NEW_ICOMPARE_IMM (cfg, ins->sreg1, 0x80000000);
				MONO_EMIT_NEW_UNALU (cfg, OP_ICEQ, reg2, -1);
				MONO_EMIT_NEW_BIALU (cfg, OP_IAND, reg1, reg1, reg2);
				MONO_EMIT_NEW_ICOMPARE_IMM (cfg, reg1, 1);
				MONO_EMIT_NEW_COND_EXC (cfg, IEQ, "OverflowException");
			}
		}
#endif
""")

edit("mono/mini/mini-runtime.c",
     why="the 32-bit division helpers without icall wrappers: decompose.c checks their operands inline, so "
         "they never raise an exception",
     anchor="""	register_opcode_emulation (OP_IDIV, __emul_op_idiv, mono_icall_sig_int32_int32_int32, mono_idiv, FALSE);
	register_opcode_emulation (OP_IDIV_UN, __emul_op_idiv_un, mono_icall_sig_int32_int32_int32, mono_idiv_un, FALSE);
	register_opcode_emulation (OP_IREM, __emul_op_irem, mono_icall_sig_int32_int32_int32, mono_irem, FALSE);
	register_opcode_emulation (OP_IREM_UN, __emul_op_irem_un, mono_icall_sig_int32_int32_int32, mono_irem_un, FALSE);""",
     replacement="""#if defined (HOST_VITA) || defined (TARGET_VITA)
	register_opcode_emulation (OP_IDIV, __emul_op_idiv, mono_icall_sig_int32_int32_int32, mono_idiv, TRUE);
	register_opcode_emulation (OP_IDIV_UN, __emul_op_idiv_un, mono_icall_sig_int32_int32_int32, mono_idiv_un, TRUE);
	register_opcode_emulation (OP_IREM, __emul_op_irem, mono_icall_sig_int32_int32_int32, mono_irem, TRUE);
	register_opcode_emulation (OP_IREM_UN, __emul_op_irem_un, mono_icall_sig_int32_int32_int32, mono_irem_un, TRUE);
#else
	register_opcode_emulation (OP_IDIV, __emul_op_idiv, mono_icall_sig_int32_int32_int32, mono_idiv, FALSE);
	register_opcode_emulation (OP_IDIV_UN, __emul_op_idiv_un, mono_icall_sig_int32_int32_int32, mono_idiv_un, FALSE);
	register_opcode_emulation (OP_IREM, __emul_op_irem, mono_icall_sig_int32_int32_int32, mono_irem, FALSE);
	register_opcode_emulation (OP_IREM_UN, __emul_op_irem_un, mono_icall_sig_int32_int32_int32, mono_irem_un, FALSE);
#endif""")

edit("mono/mini/mini-runtime.c",
     why="64-bit multiply (mono_llmult: a * b, it cannot throw) without an icall wrapper: 1.5% of the main thread",
     anchor="\tregister_opcode_emulation (OP_LMUL, __emul_lmul, mono_icall_sig_long_long_long, mono_llmult, FALSE);",
     replacement="""#if defined (HOST_VITA) || defined (TARGET_VITA)
	register_opcode_emulation (OP_LMUL, __emul_lmul, mono_icall_sig_long_long_long, mono_llmult, TRUE);
#else
	register_opcode_emulation (OP_LMUL, __emul_lmul, mono_icall_sig_long_long_long, mono_llmult, FALSE);
#endif""")

edit("mono/mini/mini-arm.c",
     why="__builtin___clear_cache is a no-op for arm-vita-eabi; sync the VM domain instead",
     anchor="#elif __APPLE__\n\tsys_icache_invalidate (code, size);\n#else",
     replacement="#elif __APPLE__\n\tsys_icache_invalidate (code, size);\n#elif defined (HOST_VITA)\n\tvita_exec_flush (code, size);\n#else")

edit("mono/mini/mini-arm.c",
     why="declaration of vita_exec_flush",
     anchor="#if __APPLE__\nG_BEGIN_DECLS\nvoid sys_icache_invalidate (void *start, size_t len);\nG_END_DECLS\n#endif",
     after="\n\n#ifdef HOST_VITA\n#include \"vita_shim.h\"\n#endif")


def apply(root):
    applied = skipped = 0
    for e in EDITS:
        path = root / e["path"]
        text = path.read_text()
        new = e["replacement"] if e["replacement"] is not None else (e["before"] or "") + e["anchor"] + (e["after"] or "")
        if new in text and new != e["anchor"]:
            skipped += 1
            continue
        count = text.count(e["anchor"])
        if count != 1:
            sys.exit(f"{e['path']}: anchor found {count} times ({e['why']})")
        path.write_text(text.replace(e["anchor"], new))
        applied += 1
        print(f"patched {e['path']}: {e['why']}")
    print(f"{applied} edits applied, {skipped} already present")


if __name__ == "__main__":
    apply(Path(sys.argv[1]))
