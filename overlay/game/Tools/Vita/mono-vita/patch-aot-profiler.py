#!/usr/bin/env python3
"""Writes the PS Vita copy of Mono's AOT profiler (mono/profiler/aot.c), built by build-mono-vita.sh.

Usage: patch-aot-profiler.py <mono/profiler/aot.c> <output aot.c>

The profiler records every method the JIT compiles, in the format `--aot=profile=` reads, so the AOT
compiler can compile just those (aot-stardew.sh). Upstream it writes the file when the runtime shuts
down or on a socket command; the Vita launcher exits without a runtime shutdown and has no sockets,
so this copy drops the socket helper and exports mono_profiler_aot_vita_save () for the launcher to
call whenever it likes. Anchors must occur exactly once.
"""
import sys
from pathlib import Path

EDITS = [
    dict(why="no sockets on the Vita",
         anchor="#ifndef HOST_WIN32\n#include <sys/socket.h>\n#else\n",
         replacement="#if defined (HOST_VITA)\n/* PS Vita: no sockets, see mono_profiler_aot_vita_save (). */\n#elif !defined (HOST_WIN32)\n#include <sys/socket.h>\n#else\n"),
    dict(why="the socket helper thread is not built",
         anchor="static void *\nhelper_thread (void *arg)\n{",
         replacement="#ifndef HOST_VITA\nstatic void *\nhelper_thread (void *arg)\n{"),
    dict(why="start_helper_thread does nothing (duration= and port= are not supported)",
         anchor="""	if (!mono_native_thread_create (&thread_id, helper_thread, NULL)) {
		mono_profiler_printf_err ("Could not start aot profiler helper thread");
		exit (1);
	}
}
""",
         replacement="""	if (!mono_native_thread_create (&thread_id, helper_thread, NULL)) {
		mono_profiler_printf_err ("Could not start aot profiler helper thread");
		exit (1);
	}
}
#else
static void
start_helper_thread (void)
{
}
#endif /* HOST_VITA */
"""),
    dict(why="the output file is reopened for every save: close the one init opened",
         anchor="""		if (!aot_profiler.outfile && aot_profiler.outfile_name) {
			mono_profiler_printf_err ("Could not create AOT profiler output file '%s': %s", aot_profiler.outfile_name, g_strerror (errno));
			exit (1);
		}
""",
         replacement="""		if (!aot_profiler.outfile && aot_profiler.outfile_name) {
			mono_profiler_printf_err ("Could not create AOT profiler output file '%s': %s", aot_profiler.outfile_name, g_strerror (errno));
			exit (1);
		}
#ifdef HOST_VITA
		fclose (aot_profiler.outfile);
		aot_profiler.outfile = NULL;
#endif
"""),
]

APPENDIX = """
#ifdef HOST_VITA
/*
 * mono_profiler_aot_vita_save:
 *
 *   Writes every method JIT-compiled so far to the output file, replacing its contents. The
 * launcher calls it periodically: on the Vita the process ends without a runtime shutdown.
 */
MONO_API void
mono_profiler_aot_vita_save (void);

void
mono_profiler_aot_vita_save (void)
{
	FILE *file = fopen (aot_profiler.outfile_name, "wb");
	if (file)
		prof_save (&aot_profiler, file); /* closes the file */
}
#endif /* HOST_VITA */
"""


def main(source, output):
    text = Path(source).read_text()
    for e in EDITS:
        count = text.count(e["anchor"])
        if count != 1:
            sys.exit(f"{source}: anchor found {count} times ({e['why']})")
        text = text.replace(e["anchor"], e["replacement"])
    Path(output).write_text(text + APPENDIX)
    print(f"{output}: {len(EDITS)} edits")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
