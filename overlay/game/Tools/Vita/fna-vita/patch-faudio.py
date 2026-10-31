#!/usr/bin/env python3
"""Applies the PS Vita port edits to a copy of FAudio's sources (build-faudio.sh's work tree).

Usage: patch-faudio.py <faudio-source-dir>

Each edit names the file, an anchor that must occur exactly once, and what replaces it. A missing or
ambiguous anchor stops the script; an edit already present is left alone.
"""
import sys
from pathlib import Path

EDITS = []


def edit(path, anchor, replacement, why):
    EDITS.append(dict(path=path, anchor=anchor, replacement=replacement, why=why))


# A SoundBank keeps its Cues in a singly linked list, oldest first (MAX_INSTANCE_BEHAVIOR_REPLACE_OLDEST
# relies on that order). Every FACTSoundBank_Prepare walked the whole list to append; Stardew Valley
# never disposes its Cues, so until the GC finalizes them the list holds every Cue played since, and
# on the Vita the walk cost a tenth of the frame time. A tail pointer makes appending O(1).

edit("src/FACT_internal.h",
     why="tail of the SoundBank's Cue list",
     anchor="\tFACTCue *cueList;\n",
     replacement="\tFACTCue *cueList;\n\tFACTCue *cueListTail; /* Vita port: O(1) appends */\n")

edit("src/FACT.c",
     why="append Cues through the tail pointer",
     anchor="""	/* Add to the SoundBank Cue list */
	if (pSoundBank->cueList == NULL)
	{
		pSoundBank->cueList = *ppCue;
	}
	else
	{
		latest = pSoundBank->cueList;
		while (latest->next != NULL)
		{
			latest = latest->next;
		}
		latest->next = *ppCue;
	}
""",
     replacement="""	/* Add to the SoundBank Cue list (Vita port: through the tail pointer) */
	(void) latest;
	if (pSoundBank->cueList == NULL)
	{
		pSoundBank->cueList = *ppCue;
	}
	else
	{
		pSoundBank->cueListTail->next = *ppCue;
	}
	pSoundBank->cueListTail = *ppCue;
""")

edit("src/FACT.c",
     why="keep the tail pointer when a Cue is removed",
     anchor="""		if (cue == pCue)
		{
			if (cue == prev) /* First in list */
			{
				pCue->parentBank->cueList = cue->next;
			}
			else
			{
				prev->next = cue->next;
			}
			break;
		}""",
     replacement="""		if (cue == pCue)
		{
			if (pCue->parentBank->cueListTail == pCue) /* Vita port */
			{
				pCue->parentBank->cueListTail = (cue == prev) ? NULL : prev;
			}
			if (cue == prev) /* First in list */
			{
				pCue->parentBank->cueList = cue->next;
			}
			else
			{
				prev->next = cue->next;
			}
			break;
		}""")

edit("src/FACT_internal.c",
     why="new SoundBanks start with an empty tail",
     anchor="\tsb->cueList = NULL;\n",
     replacement="\tsb->cueList = NULL;\n\tsb->cueListTail = NULL;\n")

# Both of the game's wave banks are streaming banks (stage-stardew.ps1: 460 MB would not fit in RAM).
# Preparing a streamed wave reads its first buffer on the calling thread - the game's main thread, for
# every footstep, axe swing and grass rustle - from the memory card, under the bank's file lock that
# the mixer thread also holds while it streams music. The Vita sampler put a tenth of the main thread
# there, and tool swings stalled 100-600 ms. These first-buffer reads now go through a small LRU
# cache of whole requests (the same sound asks for the same offset and length every time); the
# mixer thread's continuation reads never touch it.

edit("src/FACT_internal.c",
     why="first-buffer read cache (definitions; the original reader becomes the uncached path)",
     anchor="static inline void FACT_INTERNAL_ReadFile(\n",
     replacement="""static inline void FACT_INTERNAL_ReadFileUncached(
	FACTReadFileCallback pReadFile,
	FACTGetOverlappedResultCallback pGetOverlappedResult,
	void* io,
	uint32_t offset,
	uint32_t packetSize,
	uint8_t **packetBuffer,
	uint32_t *packetBufferLen,
	FAudioReallocFunc pRealloc,
	void* dst,
	uint32_t len
);

/* Vita port: first-buffer read cache, see patch-faudio.py. */
#define VITA_READ_CACHE_BYTES (6 * 1024 * 1024)
#define VITA_READ_CACHE_ENTRIES 512
#define VITA_READ_CACHE_MAX_REQUEST (256 * 1024)

typedef struct VitaReadCacheEntry
{
	void *io;
	uint32_t offset;
	uint32_t len;
	uint32_t lastUse;
	uint8_t *data;
} VitaReadCacheEntry;

static VitaReadCacheEntry vitaReadCache[VITA_READ_CACHE_ENTRIES];
static uint32_t vitaReadCacheBytes, vitaReadCacheClock;
/* The thread preparing a wave (FACT.c), 0 otherwise: its reads use the cache. */
volatile uint64_t FACT_INTERNAL_vitaFirstBufferThread;
/* The first-fill reader thread, and 1 once it runs: its reads (waves' first buffers) use the cache
 * too. Set by that reader (FACT_INTERNAL_vitaStreamReader); the refill reader's reads never do. */
static uint64_t vitaReaderThread;
static volatile int vitaReaderFirstFill;
/* The refill reader, and whether the wave it is refilling is short enough (VITA_CACHED_WAVE_BYTES)
 * for its refills to go through the read cache too: a short sound played again and again (dozens of
 * starts in 10 s in town) read every refill from the card, and those reads queued the music's refill
 * behind them for up to 3.7 s - holes in the music. */
static uint64_t vitaRefillThread;
static volatile int vitaRefillCacheable;
#define VITA_CACHED_WAVE_BYTES (384 * 1024)
/* Set while the preparing thread fills a wave's first buffer with the reader running: a miss then
 * reads nothing and sets vitaFirstBufferMissed, and the reader does the fill instead - the main
 * thread never waits on the card. */
static volatile int vitaDeferMisses, vitaFirstBufferMissed;
/* Main thread and reader both use the cache; NULL (no locking) until the reader starts. */
static FAudioMutex vitaReadCacheLock;
/* hits, misses, milliseconds spent in misses, slowest miss */
static uint32_t vitaReadStats[4];
static void FACT_INTERNAL_vitaCacheInsert(void *io, uint32_t offset, void *dst, uint32_t len);

void FACT_vita_take_read_stats(uint32_t *stats)
{
	FAudio_memcpy(stats, vitaReadStats, sizeof(vitaReadStats));
	FAudio_zero(vitaReadStats, sizeof(vitaReadStats));
}

static inline void FACT_INTERNAL_ReadFile(
	FACTReadFileCallback pReadFile,
	FACTGetOverlappedResultCallback pGetOverlappedResult,
	void* io,
	uint32_t offset,
	uint32_t packetSize,
	uint8_t **packetBuffer,
	uint32_t *packetBufferLen,
	FAudioReallocFunc pRealloc,
	void* dst,
	uint32_t len
) {
	uint32_t i, start, elapsed, victim;
	uint64_t thread = FAudio_PlatformGetThreadID();
	int preparing = FACT_INTERNAL_vitaFirstBufferThread != 0 && FACT_INTERNAL_vitaFirstBufferThread == thread;
	if (	!(preparing || (vitaReaderFirstFill && vitaReaderThread == thread) || (vitaRefillCacheable && vitaRefillThread == thread)) ||
		len == 0 ||
		len > VITA_READ_CACHE_MAX_REQUEST	)
	{
		FACT_INTERNAL_ReadFileUncached(pReadFile, pGetOverlappedResult, io, offset, packetSize, packetBuffer, packetBufferLen, pRealloc, dst, len);
		return;
	}
	if (vitaReadCacheLock != NULL)
	{
		FAudio_PlatformLockMutex(vitaReadCacheLock);
	}
	vitaReadCacheClock += 1;
	for (i = 0; i < VITA_READ_CACHE_ENTRIES; i += 1)
	{
		if (	vitaReadCache[i].data != NULL &&
			vitaReadCache[i].io == io &&
			vitaReadCache[i].offset == offset &&
			vitaReadCache[i].len == len	)
		{
			FAudio_memcpy(dst, vitaReadCache[i].data, len);
			vitaReadCache[i].lastUse = vitaReadCacheClock;
			vitaReadStats[0] += 1;
			if (vitaReadCacheLock != NULL)
			{
				FAudio_PlatformUnlockMutex(vitaReadCacheLock);
			}
			return;
		}
	}
	if (vitaReadCacheLock != NULL)
	{
		FAudio_PlatformUnlockMutex(vitaReadCacheLock);
	}
	if (preparing && vitaDeferMisses)
	{
		/* Not cached: the reader thread reads it (FACT_INTERNAL_OnBufferEnd) */
		vitaFirstBufferMissed = 1;
		return;
	}
	start = FAudio_timems();
	FACT_INTERNAL_ReadFileUncached(pReadFile, pGetOverlappedResult, io, offset, packetSize, packetBuffer, packetBufferLen, pRealloc, dst, len);
	elapsed = FAudio_timems() - start;
	vitaReadStats[1] += 1;
	vitaReadStats[2] += elapsed;
	if (elapsed > vitaReadStats[3])
	{
		vitaReadStats[3] = elapsed;
	}
	if (vitaReadCacheLock != NULL)
	{
		FAudio_PlatformLockMutex(vitaReadCacheLock);
	}
	FACT_INTERNAL_vitaCacheInsert(io, offset, dst, len);
	if (vitaReadCacheLock != NULL)
	{
		FAudio_PlatformUnlockMutex(vitaReadCacheLock);
	}
}

/* Under vitaReadCacheLock: keeps a copy of a first-buffer read. */
static void FACT_INTERNAL_vitaCacheInsert(void *io, uint32_t offset, void *dst, uint32_t len)
{
	uint32_t i, victim;
	for (i = 0; i < VITA_READ_CACHE_ENTRIES; i += 1)
	{
		if (	vitaReadCache[i].data != NULL &&
			vitaReadCache[i].io == io &&
			vitaReadCache[i].offset == offset &&
			vitaReadCache[i].len == len	)
		{
			return;
		}
	}
	/* Make room: free slots first, then the least recently used, until the budget holds. */
	for (;;)
	{
		victim = VITA_READ_CACHE_ENTRIES;
		for (i = 0; i < VITA_READ_CACHE_ENTRIES; i += 1)
		{
			if (vitaReadCache[i].data == NULL)
			{
				if (vitaReadCacheBytes + len <= VITA_READ_CACHE_BYTES)
				{
					victim = i;
					break;
				}
			}
			else if (victim == VITA_READ_CACHE_ENTRIES || vitaReadCache[i].lastUse < vitaReadCache[victim].lastUse)
			{
				victim = i;
			}
		}
		if (victim == VITA_READ_CACHE_ENTRIES)
		{
			return;
		}
		if (vitaReadCache[victim].data == NULL)
		{
			break;
		}
		vitaReadCacheBytes -= vitaReadCache[victim].len;
		FAudio_free(vitaReadCache[victim].data);
		vitaReadCache[victim].data = NULL;
	}
	vitaReadCache[victim].data = (uint8_t*) FAudio_malloc(len);
	if (vitaReadCache[victim].data == NULL)
	{
		return;
	}
	FAudio_memcpy(vitaReadCache[victim].data, dst, len);
	vitaReadCache[victim].io = io;
	vitaReadCache[victim].offset = offset;
	vitaReadCache[victim].len = len;
	vitaReadCache[victim].lastUse = vitaReadCacheClock;
	vitaReadCacheBytes += len;
}

static inline void FACT_INTERNAL_ReadFileUncached(
""")

# The cache's misses still waited 100-460 ms when the mixer thread was streaming music: both use the
# bank's one file handle under its lock, and a music read holds it for a whole packet. Misses read
# through a second handle of their own instead.

edit("src/FAudio_platform_sdl3.c",
     why="remember each file stream's path, so first-buffer reads can open a second handle",
     anchor="""FAudioIOStream* FAudio_fopen(const char *path)
{
	FAudioIOStream *io = (FAudioIOStream*) FAudio_malloc(
		sizeof(FAudioIOStream)
	);
	SDL_IOStream *stream = SDL_IOFromFile(path, "rb");
	io->data = stream;
	io->read = FAudio_INTERNAL_ioread;
	io->seek = FAudio_INTERNAL_ioseek;
	io->close = FAudio_INTERNAL_ioclose;
	io->lock = FAudio_PlatformCreateMutex();
	return io;
}""",
     replacement="""/* Vita port: a second, private handle per file stream for FACT's first-buffer reads
 * (FACT_internal.c), so they never wait on the mixer thread's streaming under io->lock.
 * Both stream readers use it (first fills, and refills of short waves through the read cache), so
 * a seek and its read happen under the handle's own lock: without it one reader's seek moved the
 * other's read to another sound's data, the read cache kept that under the sound's own key, and
 * every later play of the sound (a pickaxe hit on stone in the mines) was a loud burst of noise.
 */
#define VITA_SIDE_STREAMS 8
static struct
{
	FAudioIOStream *io;
	char *path;
	SDL_IOStream *side;
	SDL_Mutex *lock;
} vitaSideStreams[VITA_SIDE_STREAMS];

/* Reads len bytes at offset through the stream's private handle; 0 if there is none or the read
 * came up short (the caller then reads through the stream itself). */
int FAudio_vita_side_read(void *io, uint32_t offset, void *dst, uint32_t len)
{
	int i, ok = 0;
	for (i = 0; i < VITA_SIDE_STREAMS; i += 1)
	{
		if (vitaSideStreams[i].io == io)
		{
			if (vitaSideStreams[i].lock == NULL)
			{
				return 0;
			}
			SDL_LockMutex(vitaSideStreams[i].lock);
			if (vitaSideStreams[i].side == NULL)
			{
				vitaSideStreams[i].side = SDL_IOFromFile(vitaSideStreams[i].path, "rb");
			}
			if (	vitaSideStreams[i].side != NULL &&
				SDL_SeekIO(vitaSideStreams[i].side, offset, SDL_IO_SEEK_SET) == (Sint64) offset	)
			{
				ok = SDL_ReadIO(vitaSideStreams[i].side, dst, len) == len;
			}
			SDL_UnlockMutex(vitaSideStreams[i].lock);
			return ok;
		}
	}
	return 0;
}

FAudioIOStream* FAudio_fopen(const char *path)
{
	FAudioIOStream *io = (FAudioIOStream*) FAudio_malloc(
		sizeof(FAudioIOStream)
	);
	SDL_IOStream *stream = SDL_IOFromFile(path, "rb");
	int i;
	io->data = stream;
	io->read = FAudio_INTERNAL_ioread;
	io->seek = FAudio_INTERNAL_ioseek;
	io->close = FAudio_INTERNAL_ioclose;
	io->lock = FAudio_PlatformCreateMutex();
	for (i = 0; i < VITA_SIDE_STREAMS; i += 1)
	{
		if (vitaSideStreams[i].io == NULL)
		{
			vitaSideStreams[i].path = SDL_strdup(path);
			vitaSideStreams[i].side = NULL;
			vitaSideStreams[i].lock = SDL_CreateMutex();
			vitaSideStreams[i].io = io;
			break;
		}
	}
	return io;
}""")

edit("src/FAudio_platform_sdl3.c",
     why="forget a closed stream's private handle",
     anchor="""void FAudio_close(FAudioIOStream *io)
{
	io->close(io->data);""",
     replacement="""void FAudio_close(FAudioIOStream *io)
{
	int i;
	for (i = 0; i < VITA_SIDE_STREAMS; i += 1)
	{
		if (vitaSideStreams[i].io == io)
		{
			vitaSideStreams[i].io = NULL;
			if (vitaSideStreams[i].lock != NULL)
			{
				/* A read in progress finishes first */
				SDL_LockMutex(vitaSideStreams[i].lock);
				SDL_UnlockMutex(vitaSideStreams[i].lock);
				SDL_DestroyMutex(vitaSideStreams[i].lock);
			}
			if (vitaSideStreams[i].side != NULL)
			{
				SDL_CloseIO(vitaSideStreams[i].side);
			}
			SDL_free(vitaSideStreams[i].path);
			vitaSideStreams[i].path = NULL;
			vitaSideStreams[i].side = NULL;
			vitaSideStreams[i].lock = NULL;
		}
	}
	io->close(io->data);""")

edit("src/FACT_internal.c",
     why="first-buffer cache misses read through the private handle",
     anchor="""	start = FAudio_timems();
	FACT_INTERNAL_ReadFileUncached(pReadFile, pGetOverlappedResult, io, offset, packetSize, packetBuffer, packetBufferLen, pRealloc, dst, len);
	elapsed = FAudio_timems() - start;""",
     replacement="""	start = FAudio_timems();
	{
		extern int FAudio_vita_side_read(void *io, uint32_t offset, void *dst, uint32_t len);
		if (!FAudio_vita_side_read(io, offset, dst, len))
		{
			FACT_INTERNAL_ReadFileUncached(pReadFile, pGetOverlappedResult, io, offset, packetSize, packetBuffer, packetBufferLen, pRealloc, dst, len);
		}
	}
	elapsed = FAudio_timems() - start;""")

# Streaming off the mixer thread. A streamed wave plays one-second buffers and FACT read the next one
# from the card inside OnBufferEnd, on the mixer thread: when the card was busy (the game loading a
# texture, a sound starting) that read took 200-675 ms and the whole mix stopped - the audio drops
# heard together with frame drops. Now each streamed wave keeps two buffers in flight (its stream
# cache holds two halves) and OnBufferEnd only queues the refill for a reader thread. Preparing a
# wave takes its first buffer from the read cache on the calling thread (the game's main thread)
# and queues the rest; a first buffer not in the cache is read by a reader thread too (into the
# cache), since music and ambience starts still held the main thread 100-200 ms. Refills and first
# fills have a reader thread each, so a slow first read never holds up a playing wave's next buffer.
# The refill reader counts refills that found the wave already silent (FACT_vita_take_underruns) and
# the longest a refill waited (FACT_vita_take_refill_wait), both logged by the launcher.

edit("src/FACT_internal.h",
     why="per-wave state of the background streaming reader",
     anchor="\tuint32_t streamOffset;\n\tuint8_t *streamCache;\n",
     replacement="""	uint32_t streamOffset;
	uint8_t *streamCache;
	/* Vita port: buffers in flight per streamed wave. A buffer is a second, but the last one before a
	 * loop restarts is only what is left of the wave: with two in flight, that short tail was all the
	 * time a refill had, and a refill the card was slow with (150-450 ms) left the music and the
	 * ambience silent for a moment at their loop points. With three, a whole second is always queued
	 * behind the one playing; ~48 KB more per streamed wave playing. */
#define VITA_STREAM_BUFFERS 4
	/* Vita port: streamCache holds VITA_STREAM_BUFFERS buffers; which one is filled next, whether the
	 * next fill is the wave's first one, and whether the wave is being destroyed (no more refills). */
	uint8_t vitaHalf;
	uint8_t vitaFirstFill;
	uint8_t vitaDead;
""")

edit("src/FACT.c",
     why="room for VITA_STREAM_BUFFERS stream buffers per wave",
     anchor="""		(*ppWave)->streamCache = (uint8_t*) pWaveBank->parentEngine->pMalloc(
			(*ppWave)->streamSize
		);""",
     replacement="""		(*ppWave)->streamCache = (uint8_t*) pWaveBank->parentEngine->pMalloc(
			(*ppWave)->streamSize * VITA_STREAM_BUFFERS
		);
		(*ppWave)->vitaHalf = 0;
		(*ppWave)->vitaFirstFill = 1;
		(*ppWave)->vitaDead = 0;""")

edit("src/FACT.c",
     why="start the streaming reader with the engine",
     anchor="""	pEngine->initialized = true;
	pEngine->apiThread = FAudio_PlatformCreateThread(""",
     replacement="""	pEngine->initialized = true;
	{
		extern void FACT_INTERNAL_vitaStreamInit(void);
		FACT_INTERNAL_vitaStreamInit();
	}
	pEngine->apiThread = FAudio_PlatformCreateThread(""")

edit("src/FACT.c",
     why="no refill may touch a wave once it is being destroyed",
     anchor="\tFAudioVoice_DestroyVoice(pWave->voice);\n\tif (pWave->streamCache != NULL)",
     replacement="""	{
		extern void FACT_INTERNAL_vitaStreamForget(FACTWave *wave);
		FACT_INTERNAL_vitaStreamForget(pWave);
	}
	FAudioVoice_DestroyVoice(pWave->voice);
	if (pWave->streamCache != NULL)""")

edit("src/FACT_internal.c",
     why="OnBufferEnd queues refills for the streaming reader; the old body becomes the refill",
     anchor="void FACT_INTERNAL_OnBufferEnd(FAudioVoiceCallback *callback, void* pContext)\n{",
     replacement="""/* Vita port: streaming readers, see patch-faudio.py. */
#define VITA_STREAM_QUEUE 128
static void FACT_INTERNAL_vitaFill(FAudioVoiceCallback *callback, void* pContext);
static FACTWave *vitaStreamQueue[VITA_STREAM_QUEUE];
/* When each queued fill was queued (ms), for the refill wait below */
static uint32_t vitaStreamQueuedAt[VITA_STREAM_QUEUE];
static uint32_t vitaStreamHead, vitaStreamTail;
/* The wave each reader is filling: [0] the refill reader, [1] the first-fill reader */
static FACTWave *vitaStreamReading[2];
static FAudioMutex vitaStreamLock;
extern volatile uint64_t FACT_INTERNAL_vitaFirstBufferThread;
static volatile uint32_t vitaUnderruns, vitaRefillWaitMax;

/* Refills that came too late (the wave had run dry) since the last call. */
uint32_t FACT_vita_take_underruns(void)
{
	uint32_t n = vitaUnderruns;
	vitaUnderruns = 0;
	return n;
}

/* The longest a refill waited in the queue (ms) since the last call: a streamed wave plays its other
 * buffer meanwhile, and runs dry if this outlasts it. */
uint32_t FACT_vita_take_refill_wait(void)
{
	uint32_t n = vitaRefillWaitMax;
	vitaRefillWaitMax = 0;
	return n;
}

/* Under vitaStreamLock: takes a queued fill of one kind - first fills (firstFills) or refills of waves
 * already playing - whose wave the other reader isn't filling, the rest keeping their order. First
 * fills go oldest first. Refills go to the wave with the most refills queued, oldest first among
 * equals: each queued refill is a buffer that wave has already played, so it has the fewest left and
 * runs dry first (a long piece of music behind a run of short sounds). A wave's own fills keep their
 * order: both are first fills until the first one starts, and from then on the other reader skips
 * the wave until it is done. */
static FACTWave *FACT_INTERNAL_vitaStreamTake(int firstFills, uint32_t *queuedAt)
{
	uint32_t k, m, j, best = 0, bestCount = 0;
	int found = 0;
	while (vitaStreamHead != vitaStreamTail && vitaStreamQueue[vitaStreamHead % VITA_STREAM_QUEUE] == NULL)
	{
		vitaStreamHead += 1;
	}
	for (k = vitaStreamHead; k != vitaStreamTail; k += 1)
	{
		uint32_t count = 0;
		FACTWave *wave = vitaStreamQueue[k % VITA_STREAM_QUEUE];
		if (	wave == NULL ||
			(wave->vitaFirstFill != 0) != (firstFills != 0) ||
			wave == vitaStreamReading[firstFills ? 0 : 1]	)
		{
			continue;
		}
		if (firstFills)
		{
			int isShort = wave->parentBank->entries[wave->index].PlayRegion.dwLength <= VITA_CACHED_WAVE_BYTES;
			if (!found || (isShort && !bestCount))
			{
				best = k;
				bestCount = (uint32_t)isShort;
				found = 1;
				if (isShort)
				{
					break;
				}
			}
			continue;
		}
		for (j = vitaStreamHead; j != vitaStreamTail; j += 1)
		{
			if (vitaStreamQueue[j % VITA_STREAM_QUEUE] == wave)
			{
				count += 1;
			}
		}
		if (!found || count > bestCount)
		{
			best = k;
			bestCount = count;
			found = 1;
		}
	}
	if (found)
	{
		FACTWave *wave = vitaStreamQueue[best % VITA_STREAM_QUEUE];
		k = best;
		*queuedAt = vitaStreamQueuedAt[k % VITA_STREAM_QUEUE];
		for (m = k; m != vitaStreamHead; m -= 1)
		{
			vitaStreamQueue[m % VITA_STREAM_QUEUE] = vitaStreamQueue[(m - 1) % VITA_STREAM_QUEUE];
			vitaStreamQueuedAt[m % VITA_STREAM_QUEUE] = vitaStreamQueuedAt[(m - 1) % VITA_STREAM_QUEUE];
		}
		vitaStreamHead += 1;
		return wave;
	}
	return NULL;
}

/* Two readers. One read at a time held the queue: a sound starting with its first buffer not in the
 * read cache waited on the card for up to 490 ms (a thunderclap in a storm), and the rain's or the
 * music's next buffer waited behind it - the whole mix went silent for a moment just before the
 * thunder. Refills of playing waves (data = 0) and first fills (data = 1) now each have a thread;
 * first fills read through the bank's second handle (FAudio_vita_side_read), so the two reads don't
 * share a file position or lock. */
static int32_t FAUDIOCALL FACT_INTERNAL_vitaStreamReader(void *data)
{
	int firstFills = data != NULL;
	FACTWave *wave;
	uint32_t queuedAt = 0, waited;
	FAudio_PlatformThreadPriority(FAUDIO_THREAD_PRIORITY_HIGH);
	{
		/* The third core, with the mixer, away from the game's main thread (FAudio_platform_sdl3.c) */
		extern int sceKernelChangeThreadCpuAffinityMask(int thid, int cpuAffinityMask);
		sceKernelChangeThreadCpuAffinityMask(0, 0x40000); /* SCE_KERNEL_CPU_MASK_USER_2 */
	}
	if (firstFills)
	{
		/* Its reads go through the read cache (FACT_INTERNAL_ReadFile) */
		vitaReaderThread = FAudio_PlatformGetThreadID();
		vitaReaderFirstFill = 1;
	}
	else
	{
		vitaRefillThread = FAudio_PlatformGetThreadID();
	}
	for (;;)
	{
		FAudio_PlatformLockMutex(vitaStreamLock);
		wave = FACT_INTERNAL_vitaStreamTake(firstFills, &queuedAt);
		vitaStreamReading[firstFills] = wave;
		FAudio_PlatformUnlockMutex(vitaStreamLock);
		if (wave == NULL)
		{
			FAudio_sleep(firstFills ? 1 : 2);
			continue;
		}
		if (!firstFills && !(wave->state & FACT_STATE_STOPPED))
		{
			FAudioVoiceState vs;
			FACTWaveBankEntry *e = &wave->parentBank->entries[wave->index];
			waited = FAudio_timems() - queuedAt;
			if (waited > vitaRefillWaitMax)
			{
				vitaRefillWaitMax = waited;
			}
			/* A refill finding nothing left to play: the wave went silent waiting for us */
			FAudioSourceVoice_GetState(wave->voice, &vs, FAUDIO_VOICE_NOSAMPLESPLAYED);
			if (vs.BuffersQueued == 0 && wave->streamOffset < e->PlayRegion.dwOffset + e->PlayRegion.dwLength)
			{
				vitaUnderruns += 1;
			}
		}
		if (!firstFills)
		{
			vitaRefillCacheable = wave->parentBank->entries[wave->index].PlayRegion.dwLength <= VITA_CACHED_WAVE_BYTES;
		}
		FACT_INTERNAL_vitaFill(&wave->callback.callback, NULL);
		vitaRefillCacheable = 0;
		FAudio_PlatformLockMutex(vitaStreamLock);
		vitaStreamReading[firstFills] = NULL;
		FAudio_PlatformUnlockMutex(vitaStreamLock);
	}
	return 0;
}

void FACT_INTERNAL_vitaStreamInit(void)
{
	if (vitaStreamLock != NULL)
	{
		return;
	}
	vitaReadCacheLock = FAudio_PlatformCreateMutex();
	vitaStreamLock = FAudio_PlatformCreateMutex();
	FAudio_PlatformCreateThread(FACT_INTERNAL_vitaStreamReader, "FACT stream reader", NULL);
	FAudio_PlatformCreateThread(FACT_INTERNAL_vitaStreamReader, "FACT first-buffer reader", (void*) 1);
}

/* Drops the wave's queued refills and waits out one in progress; later OnBufferEnds queue nothing. */
void FACT_INTERNAL_vitaStreamForget(FACTWave *wave)
{
	uint32_t i;
	if (vitaStreamLock == NULL)
	{
		return;
	}
	FAudio_PlatformLockMutex(vitaStreamLock);
	wave->vitaDead = 1;
	for (i = vitaStreamHead; i != vitaStreamTail; i += 1)
	{
		if (vitaStreamQueue[i % VITA_STREAM_QUEUE] == wave)
		{
			vitaStreamQueue[i % VITA_STREAM_QUEUE] = NULL;
		}
	}
	while (vitaStreamReading[0] == wave || vitaStreamReading[1] == wave)
	{
		FAudio_PlatformUnlockMutex(vitaStreamLock);
		FAudio_sleep(1);
		FAudio_PlatformLockMutex(vitaStreamLock);
	}
	FAudio_PlatformUnlockMutex(vitaStreamLock);
}

static void FACT_INTERNAL_vitaStreamQueueFill(FACTWave *wave)
{
	int queued = 0;
	FAudio_PlatformLockMutex(vitaStreamLock);
	if (wave->vitaDead)
	{
		queued = 1;
	}
	else if (vitaStreamTail - vitaStreamHead < VITA_STREAM_QUEUE)
	{
		vitaStreamQueue[vitaStreamTail % VITA_STREAM_QUEUE] = wave;
		vitaStreamQueuedAt[vitaStreamTail % VITA_STREAM_QUEUE] = FAudio_timems();
		vitaStreamTail += 1;
		queued = 1;
	}
	FAudio_PlatformUnlockMutex(vitaStreamLock);
	if (!queued)
	{
		/* Queue full: refill here, as FACT always did. */
		FACT_INTERNAL_vitaFill(&wave->callback.callback, NULL);
	}
}

void FACT_INTERNAL_OnBufferEnd(FAudioVoiceCallback *callback, void* pContext)
{
	FACTWave *wave = ((FACTWaveCallback*) callback)->wave;
	if (vitaStreamLock == NULL)
	{
		FACT_INTERNAL_vitaFill(callback, pContext);
		return;
	}
	if (	FACT_INTERNAL_vitaFirstBufferThread != 0 &&
		FACT_INTERNAL_vitaFirstBufferThread == FAudio_PlatformGetThreadID()	)
	{
		/* Preparing: the first buffer now if it is in the read cache, the others in the background.
		 * Not cached, the readers do them all: the sound starts a read later, but the game doesn't
		 * stop for it. Each buffer that ends queues one refill, so the wave keeps
		 * VITA_STREAM_BUFFERS in flight from here on. */
		int extra;
		vitaDeferMisses = 1;
		FACT_INTERNAL_vitaFill(callback, pContext);
		vitaDeferMisses = 0;
		if (wave->vitaFirstFill)
		{
			FACT_INTERNAL_vitaStreamQueueFill(wave);
		}
		for (extra = 2; extra < VITA_STREAM_BUFFERS; extra += 1)
		{
			FACT_INTERNAL_vitaStreamQueueFill(wave);
		}
	}
	FACT_INTERNAL_vitaStreamQueueFill(wave);
}

static void FACT_INTERNAL_vitaFill(FAudioVoiceCallback *callback, void* pContext)
{""")

edit("src/FACT_internal.c",
     why="refills alternate between the two stream buffers",
     anchor="""	/* Assign buffer memory */
	buffer.pAudioData = c->wave->streamCache;
	buffer.AudioBytes = FAudio_min(
		c->wave->streamSize,
		left
	);""",
     replacement="""	/* Assign buffer memory: the VITA_STREAM_BUFFERS parts of streamCache in turn (Vita port) */
	buffer.pAudioData = c->wave->streamCache + c->wave->vitaHalf * c->wave->streamSize;
	c->wave->vitaHalf = (uint8_t) ((c->wave->vitaHalf + 1) % VITA_STREAM_BUFFERS);
	buffer.AudioBytes = FAudio_min(
		c->wave->streamSize,
		left
	);
	/* The first buffer is a whole one too. It was a quarter second while the main thread read it from
	 * the card; now misses go to the reader, and a quarter second left the second read 250 ms, less
	 * than the card takes while the game loads (up to 400 ms): the sound cut out right after starting. */
	c->wave->vitaFirstFill = 0;""")

edit("src/FACT_internal.c",
     why="read into the buffer being filled; a deferred first read leaves the wave as it was",
     anchor="\t\tc->wave->streamCache,\n\t\tbuffer.AudioBytes\n\t);\n\tc->wave->streamOffset += buffer.AudioBytes;",
     replacement="""		(void*) buffer.pAudioData,
		buffer.AudioBytes
	);
	if (vitaFirstBufferMissed && FACT_INTERNAL_vitaFirstBufferThread == FAudio_PlatformGetThreadID())
	{
		/* Not in the read cache: the first-fill reader fills this one (FACT_INTERNAL_OnBufferEnd).
		 * Only the preparing thread's own miss: a reader's fill running meanwhile must not take it. */
		vitaFirstBufferMissed = 0;
		c->wave->vitaHalf = (uint8_t) ((c->wave->vitaHalf + VITA_STREAM_BUFFERS - 1) % VITA_STREAM_BUFFERS);
		c->wave->vitaFirstFill = 1;
		return;
	}
	c->wave->streamOffset += buffer.AudioBytes;""")

edit("src/FACT.c",
     why="mark the first-buffer read of a wave being prepared for the read cache",
     anchor="\t\t/* Read and submit first buffer from the WaveBank */\n\t\tFACT_INTERNAL_OnBufferEnd(&(*ppWave)->callback.callback, NULL);\n",
     replacement="""		/* Read and submit first buffer from the WaveBank */
		{
			/* Vita port: through the first-buffer read cache (FACT_internal.c) */
			extern volatile uint64_t FACT_INTERNAL_vitaFirstBufferThread;
			FACT_INTERNAL_vitaFirstBufferThread = FAudio_PlatformGetThreadID();
			FACT_INTERNAL_OnBufferEnd(&(*ppWave)->callback.callback, NULL);
			FACT_INTERNAL_vitaFirstBufferThread = 0;
		}
""")


# Mixer timing: how long each SDL audio callback mixes, and the longest gap between callbacks. With
# 2048-frame device buffers (main.c) a callback is due every ~43 ms; a gap well past that is audio the
# device ran out of. Logged with the [sound] line (FAudio_vita_take_mix_stats).

edit("src/FAudio_platform_sdl3.c",
     why="time the mixer callbacks",
     anchor="""	SDLAudioDevice *dev = (SDLAudioDevice*) userdata;

	if (!dev->audio->active)
	{
		/* Nothing to do, SDL will fill in for us */
		return;
	}

	while (additional_amount > 0)
	{
		FAudio_zero(dev->stagingBuffer, dev->stagingLen);
		FAudio_INTERNAL_UpdateEngine(dev->audio, dev->stagingBuffer);
		SDL_PutAudioStreamData(
			stream,
			dev->stagingBuffer,
			(int)dev->stagingLen
		);
		additional_amount -= (int)dev->stagingLen;
	}
}""",
     replacement="""	SDLAudioDevice *dev = (SDLAudioDevice*) userdata;
	uint64_t start = SDL_GetTicksNS(), took;
	static int pinned;

	if (!pinned)
	{
		/* Vita port: the audio device thread on the third core only, away from the game's main thread:
		 * sharing a core, each took time from the other. SDL's Vita driver raises it only one step above
		 * the thread that opened it (the main thread); 64 is SDL's own time-critical value, the top of the
		 * user range, so the stream reader and Mono's threads on that core never hold it up. */
		pinned = 1;
		sceKernelChangeThreadCpuAffinityMask(0, SCE_KERNEL_CPU_MASK_USER_2);
		sceKernelChangeThreadPriority(0, 64);
	}

	if (!dev->audio->active)
	{
		/* Nothing to do, SDL will fill in for us */
		return;
	}

	if (vitaMixLastStart != 0 && start - vitaMixLastStart > vitaMixStats[1])
	{
		vitaMixStats[1] = start - vitaMixLastStart;
	}
	vitaMixLastStart = start;
	while (additional_amount > 0)
	{
		FAudio_zero(dev->stagingBuffer, dev->stagingLen);
		FAudio_INTERNAL_UpdateEngine(dev->audio, dev->stagingBuffer);
		FAudio_vita_limit(dev->stagingBuffer, (uint32_t) (dev->stagingLen / sizeof(float)), start);
		FAudio_vita_capture(dev->stagingBuffer, (uint32_t) (dev->stagingLen / sizeof(float)));
		FAudio_vita_check_content(dev->stagingBuffer, (uint32_t) (dev->stagingLen / sizeof(float)), start);
		SDL_PutAudioStreamData(
			stream,
			dev->stagingBuffer,
			(int)dev->stagingLen
		);
		additional_amount -= (int)dev->stagingLen;
	}
	took = SDL_GetTicksNS() - start;
	vitaMixStats[2] += took;
	vitaMixStats[3] += 1;
	if (took > vitaMixStats[0])
	{
		vitaMixStats[0] = took;
	}
}""")

edit("src/FAudio_platform_sdl3.c",
     why="mixer timing state",
     anchor="/* Mixer Thread */\n",
     replacement="""/* Mixer Thread */

#include <psp2/kernel/threadmgr.h>

/* Vita port: slowest mix, longest gap between callbacks, total mix time (ns), callbacks */
static uint64_t vitaMixStats[4];
static uint64_t vitaMixLastStart;

/* Vita port: a file named sound-capture in the game folder (holding a delay in seconds) records 60 s of the
 * final mix, as heard, to sound-capture.wav (16-bit), to find out what an audible dropout is. 10 MB while recording; the
 * file is written from the mixer thread once full (one long callback: an audible gap at the very end). The
 * capture start is in ms since the engine opened (vitaCaptureStartMs), logged by the launcher. */
static int16_t *vitaCapture;
static uint32_t vitaCaptureLen, vitaCapturePos, vitaCaptureFreq, vitaCaptureChannels;
static int vitaCaptureState; /* 0 not checked, 3 waiting, 1 recording, 2 done or off */
static uint64_t vitaCaptureDelayUntil;
uint32_t FAudio_vita_capture_start_ms;
int FAudio_vita_capture_state;

static void FAudio_vita_capture(const float *mix, uint32_t samples)
{
	uint32_t i;
	if (vitaCaptureState == 0)
	{
		/* Checked once, on the first callback (the title screen): the file holds the seconds to wait before
		 * recording, so the capture can cover play after a save has loaded. */
		SDL_IOStream *probe = SDL_IOFromFile("ux0:data/StardewValley/sound-capture", "rb");
		vitaCaptureState = 2;
		if (probe != NULL)
		{
			char text[16] = { 0 };
			SDL_ReadIO(probe, text, sizeof(text) - 1);
			SDL_CloseIO(probe);
			vitaCaptureDelayUntil = SDL_GetTicks() + (uint64_t) SDL_atoi(text) * 1000;
			vitaCaptureState = 3;
		}
	}
	if (vitaCaptureState == 3 && SDL_GetTicks() >= vitaCaptureDelayUntil)
	{
		vitaCaptureState = 2;
		vitaCaptureLen = vitaCaptureFreq * vitaCaptureChannels * 60;
		vitaCapture = (int16_t*) SDL_malloc(vitaCaptureLen * sizeof(int16_t));
		if (vitaCapture != NULL)
		{
			vitaCapturePos = 0;
			vitaCaptureState = 1;
			FAudio_vita_capture_start_ms = (uint32_t) SDL_GetTicks();
			FAudio_vita_capture_state = 1;
		}
	}
	if (vitaCaptureState != 1)
	{
		return;
	}
	for (i = 0; i < samples && vitaCapturePos < vitaCaptureLen; i += 1)
	{
		float s = mix[i] * 32767.0f;
		vitaCapture[vitaCapturePos++] = (int16_t) (s > 32767.0f ? 32767.0f : (s < -32768.0f ? -32768.0f : s));
	}
	if (vitaCapturePos >= vitaCaptureLen)
	{
		SDL_IOStream *out = SDL_IOFromFile("ux0:data/StardewValley/sound-capture.wav", "wb");
		if (out != NULL)
		{
			uint32_t bytes = vitaCaptureLen * 2, rate = vitaCaptureFreq * vitaCaptureChannels * 2;
			uint16_t fmt[2] = { 1, (uint16_t) vitaCaptureChannels }, align[2] = { (uint16_t) (vitaCaptureChannels * 2), 16 };
			uint32_t riff = 36 + bytes, sixteen = 16;
			SDL_WriteIO(out, "RIFF", 4); SDL_WriteIO(out, &riff, 4); SDL_WriteIO(out, "WAVEfmt ", 8);
			SDL_WriteIO(out, &sixteen, 4); SDL_WriteIO(out, fmt, 4); SDL_WriteIO(out, &vitaCaptureFreq, 4);
			SDL_WriteIO(out, &rate, 4); SDL_WriteIO(out, align, 4); SDL_WriteIO(out, "data", 4);
			SDL_WriteIO(out, &bytes, 4); SDL_WriteIO(out, vitaCapture, bytes);
			SDL_CloseIO(out);
		}
		SDL_free(vitaCapture);
		vitaCapture = NULL;
		vitaCaptureState = 2;
		FAudio_vita_capture_state = 2;
	}
}

/* Vita port: what the mix itself holds. The device was never starved while a dropout was heard, so a
 * dropout is either in the mix (silence in the middle of sound, or the same buffer twice) or after it.
 * Per mixed buffer: whether it is all zero, and a checksum against the one before. A silent stretch
 * between two sounding buffers, shorter than 5 s, is counted as a hole (music between songs and quiet
 * scenes are silent for longer); vitaContent = holes, longest hole (ms), repeated buffers, and when the
 * last hole ended (ms on SDL's clock, which starts with the launcher's log clock to within a second). */
static uint32_t vitaContent[4];
static int vitaContentSounding;
static uint64_t vitaSilenceStart;
static uint32_t vitaLastSum, vitaLastSumValid;

static void FAudio_vita_check_content(const float *mix, uint32_t samples, uint64_t now)
{
	uint32_t i, sum = 0;
	int silent = 1;
	for (i = 0; i < samples; i += 1)
	{
		uint32_t bits;
		SDL_memcpy(&bits, &mix[i], sizeof(bits));
		if ((bits & 0x7FFFFFFF) != 0)
		{
			silent = 0;
		}
		sum = (sum << 1 | sum >> 31) ^ bits;
	}
	if (silent)
	{
		if (vitaContentSounding)
		{
			vitaContentSounding = 0;
			vitaSilenceStart = now;
		}
		vitaLastSumValid = 0;
		return;
	}
	if (vitaLastSumValid && sum == vitaLastSum)
	{
		vitaContent[2] += 1;
	}
	vitaLastSum = sum;
	vitaLastSumValid = 1;
	if (!vitaContentSounding && vitaSilenceStart != 0)
	{
		uint32_t hole = (uint32_t) ((now - vitaSilenceStart) / 1000000);
		if (hole < 5000)
		{
			vitaContent[0] += 1;
			if (hole > vitaContent[1])
			{
				vitaContent[1] = hole;
			}
			vitaContent[3] = (uint32_t) (now / 1000000);
		}
	}
	vitaContentSounding = 1;
}

/* Vita port: the mix's level, before it becomes 16-bit samples. Sounds that add up past full scale (an
 * explosion over the music in the mines) were cut off flat by the conversion, and the speaker gave a
 * harsh crack. A limiter now turns the whole mix down just enough for the loudest frame (at once) and
 * back up over ~100 ms, so loud moments stay loud without clipping; below 0.95 nothing changes.
 * vitaLevel = peak (x1000), samples that were over full scale, the largest step between two samples
 * (x1000: a click in the mix itself), and when that step came (ms, SDL clock). The 8192-sample
 * buffers take ~0.1 ms more when loud and a peak scan otherwise. */
static uint32_t vitaLevel[4];
static float vitaLimitGain = 1.0f;
static float vitaLastLeft, vitaLastRight;

static void FAudio_vita_limit(float *mix, uint32_t samples, uint64_t now)
{
	uint32_t i, over = 0;
	float peak = 0.0f, step = 0.0f, last[2];
	last[0] = vitaLastLeft;
	last[1] = vitaLastRight;
	for (i = 0; i < samples; i += 1)
	{
		float s = mix[i], a = s < 0.0f ? -s : s, d = s - last[i & 1];
		if (a > peak)
		{
			peak = a;
		}
		if (a > 1.0f)
		{
			over += 1;
		}
		d = d < 0.0f ? -d : d;
		if (d > step)
		{
			step = d;
		}
		last[i & 1] = s;
	}
	vitaLastLeft = last[0];
	vitaLastRight = last[1];
	if ((uint32_t) (peak * 1000.0f) > vitaLevel[0])
	{
		vitaLevel[0] = (uint32_t) (peak * 1000.0f);
	}
	vitaLevel[1] += over;
	if ((uint32_t) (step * 1000.0f) > vitaLevel[2])
	{
		vitaLevel[2] = (uint32_t) (step * 1000.0f);
		vitaLevel[3] = (uint32_t) (now / 1000000);
	}
	if (peak <= 0.95f && vitaLimitGain >= 0.9999f)
	{
		vitaLimitGain = 1.0f;
		return;
	}
	/* Stereo frames: one gain for both channels, so the image doesn't move. */
	for (i = 0; i + 1 < samples; i += 2)
	{
		float l = mix[i], r = mix[i + 1];
		float a = l < 0.0f ? -l : l, b = r < 0.0f ? -r : r;
		float m = a > b ? a : b;
		if (m * vitaLimitGain > 0.95f)
		{
			vitaLimitGain = 0.95f / m;
		}
		mix[i] = l * vitaLimitGain;
		mix[i + 1] = r * vitaLimitGain;
		/* Back up with a ~100 ms time constant at 44.1 kHz. */
		vitaLimitGain += (1.0f - vitaLimitGain) * 0.000227f;
	}
}

/* Fills out[0..3] with vitaLevel (see FAudio_vita_limit), then starts over. */
void FAudio_vita_take_level_stats(uint32_t *out)
{
	out[0] = vitaLevel[0];
	out[1] = vitaLevel[1];
	out[2] = vitaLevel[2];
	out[3] = vitaLevel[3];
	FAudio_zero(vitaLevel, sizeof(vitaLevel));
}

/* Fills ms[0..3]: slowest mix, longest gap, average mix (ms), callbacks; ms[4..7]: vitaContent; then
 * starts over. */
void FAudio_vita_take_mix_stats(uint32_t *ms)
{
	ms[0] = (uint32_t) (vitaMixStats[0] / 1000000);
	ms[1] = (uint32_t) (vitaMixStats[1] / 1000000);
	ms[2] = vitaMixStats[3] ? (uint32_t) (vitaMixStats[2] / vitaMixStats[3] / 1000000) : 0;
	ms[3] = (uint32_t) vitaMixStats[3];
	ms[4] = vitaContent[0];
	ms[5] = vitaContent[1];
	ms[6] = vitaContent[2];
	ms[7] = vitaContent[3];
	FAudio_zero(vitaMixStats, sizeof(vitaMixStats));
	FAudio_zero(vitaContent, sizeof(vitaContent));
}
""")


edit("src/FAudio_platform_sdl3.c",
     why="remember the mix format for the sound capture",
     anchor="\tresult->stagingBuffer = (float*) SDL_malloc(result->stagingLen);\n",
     replacement="""	result->stagingBuffer = (float*) SDL_malloc(result->stagingLen);
	vitaCaptureFreq = spec.freq;
	vitaCaptureChannels = spec.channels;
""")


def apply(root):
    applied = skipped = 0
    for e in EDITS:
        path = root / e["path"]
        text = path.read_text()
        if e["replacement"] in text:
            skipped += 1
            continue
        count = text.count(e["anchor"])
        if count != 1:
            sys.exit(f"{e['path']}: anchor found {count} times ({e['why']})")
        path.write_text(text.replace(e["anchor"], e["replacement"]))
        applied += 1
        print(f"patched {e['path']}: {e['why']}")
    print(f"{applied} edits applied, {skipped} already present")


if __name__ == "__main__":
    apply(Path(sys.argv[1]))
