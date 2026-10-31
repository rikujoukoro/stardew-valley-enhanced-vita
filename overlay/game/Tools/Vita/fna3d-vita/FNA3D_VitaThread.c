/* FNA3D on its own thread for the PS Vita port.
 *
 * The game's main thread used to do every GL call itself: on the farm at 67% about 7 ms a frame went
 * to the sprite batches' uploads and draw calls, render target switches, clears and the swap, on the
 * one core that also runs the game. This puts a proxy device in front of the real driver: the main
 * thread writes the calls into a ring buffer and goes on with the next update, and a render thread on
 * the second core replays them on the real (OpenGL / vitaGL) device in the same order.
 *
 * - The real device is created on the render thread, so the OpenGL driver's own thread checks
 *   (ForceToMainThread) see every call coming from its thread.
 * - Calls the main thread needs nothing back from are queued, with whatever they point to copied
 *   (vertex data, states, rectangles, the effect's parameter values).
 * - Calls that return something, or read or write caller memory later (texture data, buffer reads,
 *   effect creation, queries), run on the render thread while the caller waits ("sync" calls). They
 *   are rare during play. Backbuffer format and size and the feature queries are cached, since FNA
 *   asks for some of them on every Clear.
 * - ApplyEffect: FNA reads the effect's state changes right after the call. They are worked out here
 *   on the caller's thread the way MOJOSHADER_effectBeginPass does (they only point into the pass and
 *   its shaders); passes this can't follow (preshaders, a pass without both shaders) are sync calls.
 *   The parameter values are copied when queued and swapped in around the real call, so the main
 *   thread can already write the next values (SpriteBatch's matrix) meanwhile.
 * - At most one frame is in flight: queueing a swap waits for the previous one to have run.
 * - Log messages from the render thread are passed to the main thread (FNA's log hooks are managed
 *   callbacks, which must not run on a thread Mono doesn't know).
 *
 * A file or folder named vita-gl-same-thread in the game folder keeps the device on the calling
 * thread, as before (to compare).
 */

#include <SDL3/SDL.h>
#include "FNA3D_Driver.h"
#include "mojoshader.h"

#include <psp2/kernel/threadmgr.h>
#include <psp2/kernel/processmgr.h>
#include <psp2/io/stat.h>
#include <string.h>
#include <stdlib.h>

extern void FNA3D_VitaThread_CallLog(int level, const char *msg);

#define RING_SIZE (4 * 1024 * 1024)
#define RING_MASK (RING_SIZE - 1)

enum
{
	CMD_PAD = 0,
	CMD_SYNC,
	CMD_SWAP,
	CMD_CLEAR,
	CMD_DRAWINDEXED,
	CMD_DRAWINSTANCED,
	CMD_DRAW,
	CMD_VIEWPORT,
	CMD_SCISSOR,
	CMD_BLENDFACTOR,
	CMD_MULTISAMPLEMASK,
	CMD_REFERENCESTENCIL,
	CMD_BLENDSTATE,
	CMD_DEPTHSTENCILSTATE,
	CMD_RASTERIZERSTATE,
	CMD_VERIFYSAMPLER,
	CMD_VERIFYVERTEXSAMPLER,
	CMD_VERTEXBINDINGS,
	CMD_RENDERTARGETS,
	CMD_RESOLVETARGET,
	CMD_DISPOSETEXTURE,
	CMD_DISPOSERENDERBUFFER,
	CMD_DISPOSEVERTEXBUFFER,
	CMD_DISPOSEINDEXBUFFER,
	CMD_DISPOSEEFFECT,
	CMD_DISPOSEQUERY,
	CMD_VERTEXDATA,
	CMD_TECHNIQUE,
	CMD_APPLYEFFECT,
	CMD_SPRITEBATCH
};

typedef struct Cmd
{
	uint32_t type;
	uint32_t size; /* whole command, header included, multiple of 8 */
} Cmd;

typedef struct SyncCall
{
	void (*fn)(void *args);
	void *args;
	volatile int32_t done;
} SyncCall;

typedef struct EffectEntry
{
	FNA3D_Effect *effect;
	MOJOSHADER_effect *mojo;
	MOJOSHADER_effectTechnique *technique; /* as the main thread last set it */
	void **values; /* each parameter's own value memory, which FNA writes: the render thread swaps
	                * copies into params[i].value.values while it applies, so this side never
	                * reads that field */
} EffectEntry;

typedef struct Proxy
{
	FNA3D_Device *real;
	FNA3D_Device device;

	uint8_t *ring;
	volatile uint32_t writePos; /* main thread */
	volatile uint32_t readPos; /* render thread */
	SDL_Mutex *lock; /* producers: the main thread, now and then a loading thread */

	volatile int32_t swapsPending;
	volatile int32_t quit;
	SceUID thread;

	/* Cached answers */
	int32_t backbufferWidth, backbufferHeight;
	FNA3D_SurfaceFormat backbufferFormat;
	FNA3D_DepthFormat backbufferDepthFormat;
	int32_t backbufferMultiSampleCount;
	uint8_t supportsDXT1, supportsS3TC, supportsBC7, supportsInstancing, supportsNoOverwrite, supportsSRGB;
	int32_t maxTextures, maxVertexTextures;

	/* Effects, for ApplyEffect's parameters and state changes */
	EffectEntry *effects;
	int32_t effectCount, effectCapacity;

	/* The render thread's own state changes struct for the real ApplyEffect */
	MOJOSHADER_effectStateChanges renderStateChanges;
} Proxy;

static Proxy *proxy;
static SceUID renderThreadId = -1;

/* For the launcher's profile line: render thread time spent replaying, and main thread time spent
 * waiting for it (room in the ring, a sync call, the previous frame's swap), in microseconds. */
static volatile uint64_t statBusyUs, statWaitUs;

int FNA3D_VitaThread_TakeStats(uint64_t *busyUs, uint64_t *waitUs)
{
	*busyUs = statBusyUs;
	*waitUs = statWaitUs;
	statBusyUs = 0;
	statWaitUs = 0;
	return proxy != NULL;
}

/* What the render thread replayed since the last call, for the launcher's profile line: draw calls,
 * SpriteBatch sprites, render target switches applied, SpriteBatch batches, switches dropped, and the
 * time spent in SpriteBatch replays (sort, vertices, upload, draws; microseconds). Render thread only
 * writes them. */
static volatile uint32_t statDraws, statSprites, statTargets, statBatches;
static volatile uint64_t statSpriteUs;
static volatile uint32_t statTargetsDropped; /* switches dropped with nothing drawn (Replay) */

void FNA3D_VitaThread_TakeCounts(uint32_t *out, uint64_t *spriteUs)
{
	out[0] = statDraws;
	out[1] = statSprites;
	out[2] = statTargets;
	out[3] = statBatches;
	out[4] = statTargetsDropped;
	*spriteUs = statSpriteUs;
	statDraws = 0;
	statSprites = 0;
	statTargets = 0;
	statBatches = 0;
	statTargetsDropped = 0;
	statSpriteUs = 0;
}

/* Logs from the render thread */
#define LOG_SLOTS 32
#define LOG_LENGTH 256
static SDL_Mutex *logLock;
static char logText[LOG_SLOTS][LOG_LENGTH];
static int32_t logLevel[LOG_SLOTS];
static volatile int32_t logCount;

int FNA3D_VitaThread_DeferLog(int level, const char *msg)
{
	if (renderThreadId < 0 || sceKernelGetThreadId() != renderThreadId || logLock == NULL)
	{
		return 0;
	}
	SDL_LockMutex(logLock);
	if (logCount < LOG_SLOTS)
	{
		SDL_strlcpy(logText[logCount], msg, LOG_LENGTH);
		logLevel[logCount] = level;
		logCount += 1;
	}
	SDL_UnlockMutex(logLock);
	return 1;
}

static void FlushLogs(void)
{
	int32_t i, n;
	static char text[LOG_SLOTS][LOG_LENGTH];
	static int32_t level[LOG_SLOTS];
	if (logCount == 0 || logLock == NULL)
	{
		return;
	}
	SDL_LockMutex(logLock);
	n = logCount;
	SDL_memcpy(text, logText, sizeof(logText[0]) * n);
	SDL_memcpy(level, logLevel, sizeof(logLevel[0]) * n);
	logCount = 0;
	SDL_UnlockMutex(logLock);
	for (i = 0; i < n; i += 1)
	{
		FNA3D_VitaThread_CallLog(level[i], text[i]);
	}
}

/* Ring buffer */

static inline uint32_t Align8(uint32_t n)
{
	return (n + 7) & ~7u;
}

static void Wait(void)
{
	sceKernelDelayThread(50);
}

/* The main thread waiting on the render thread (counted for the stats) */
static void MainWait(void)
{
	SceUInt64 start = sceKernelGetProcessTimeWide();
	sceKernelDelayThread(50);
	statWaitUs += sceKernelGetProcessTimeWide() - start;
}

/* Under lock: room for a command of size bytes, contiguous. Returns where to write it. */
static uint8_t* Reserve(uint32_t size)
{
	uint32_t w = proxy->writePos;
	uint32_t tail = RING_SIZE - (w & RING_MASK);
	uint32_t need = (size > tail) ? (size + tail) : size;
	while (RING_SIZE - (w - __atomic_load_n(&proxy->readPos, __ATOMIC_ACQUIRE)) < need)
	{
		MainWait();
	}
	if (size > tail)
	{
		Cmd *pad = (Cmd*) (proxy->ring + (w & RING_MASK));
		pad->type = CMD_PAD;
		pad->size = tail;
		w += tail;
		__atomic_store_n(&proxy->writePos, w, __ATOMIC_RELEASE);
	}
	return proxy->ring + (w & RING_MASK);
}

static void Publish(uint32_t size)
{
	__atomic_store_n(&proxy->writePos, proxy->writePos + size, __ATOMIC_RELEASE);
}

/* Queues a command with payload bytes after the header; returns the payload to fill in, under lock.
 * The caller fills it and calls Commit. */
static void* Begin(uint32_t type, uint32_t payload, uint32_t *size)
{
	Cmd *cmd;
	*size = Align8(sizeof(Cmd) + payload);
	SDL_LockMutex(proxy->lock);
	cmd = (Cmd*) Reserve(*size);
	cmd->type = type;
	cmd->size = *size;
	return cmd + 1;
}

static void Commit(uint32_t size)
{
	Publish(size);
	SDL_UnlockMutex(proxy->lock);
}

/* Runs fn(args) on the render thread after everything queued before it, and waits. */
static void RunSync(void (*fn)(void*), void *args)
{
	uint32_t size;
	SyncCall call;
	SyncCall **slot;
	if (renderThreadId >= 0 && sceKernelGetThreadId() == renderThreadId)
	{
		fn(args);
		return;
	}
	call.fn = fn;
	call.args = args;
	call.done = 0;
	slot = (SyncCall**) Begin(CMD_SYNC, sizeof(SyncCall*), &size);
	*slot = &call;
	Commit(size);
	while (!__atomic_load_n(&call.done, __ATOMIC_ACQUIRE))
	{
		MainWait();
	}
	FlushLogs();
}

/* Effects table (main thread, under lock) */

static EffectEntry* FindEffect(FNA3D_Effect *effect)
{
	int32_t i;
	for (i = 0; i < proxy->effectCount; i += 1)
	{
		if (proxy->effects[i].effect == effect)
		{
			return &proxy->effects[i];
		}
	}
	return NULL;
}

static void AddEffect(FNA3D_Effect *effect, MOJOSHADER_effect *mojo)
{
	EffectEntry *entry;
	if (effect == NULL || mojo == NULL)
	{
		return;
	}
	SDL_LockMutex(proxy->lock);
	if (proxy->effectCount == proxy->effectCapacity)
	{
		proxy->effectCapacity = (proxy->effectCapacity == 0) ? 16 : proxy->effectCapacity * 2;
		proxy->effects = (EffectEntry*) SDL_realloc(proxy->effects, sizeof(EffectEntry) * proxy->effectCapacity);
	}
	entry = &proxy->effects[proxy->effectCount++];
	entry->effect = effect;
	entry->mojo = mojo;
	entry->technique = (MOJOSHADER_effectTechnique*) mojo->current_technique;
	entry->values = (void**) SDL_malloc(sizeof(void*) * (mojo->param_count > 0 ? mojo->param_count : 1));
	{
		uint32_t i;
		for (i = 0; i < mojo->param_count; i += 1)
		{
			entry->values[i] = mojo->params[i].value.values;
		}
	}
	SDL_UnlockMutex(proxy->lock);
}

static void RemoveEffect(FNA3D_Effect *effect)
{
	EffectEntry *entry;
	SDL_LockMutex(proxy->lock);
	entry = FindEffect(effect);
	if (entry != NULL)
	{
		SDL_free(entry->values);
		*entry = proxy->effects[--proxy->effectCount];
	}
	SDL_UnlockMutex(proxy->lock);
}

/* Render thread */

static void ReplayApplyEffect(uint8_t *p)
{
	FNA3D_Effect *effect = *(FNA3D_Effect**) p;
	MOJOSHADER_effect *mojo = *(MOJOSHADER_effect**) (p + 8);
	uint32_t pass = *(uint32_t*) (p + 16);
	uint32_t count = *(uint32_t*) (p + 20);
	uint8_t *values = p + 24;
	void *saved[64];
	void **savedHeap = NULL;
	void **keep = saved;
	uint32_t i;
	if (count > 64)
	{
		savedHeap = (void**) SDL_malloc(sizeof(void*) * count);
		keep = savedHeap;
	}
	/* The values as they were when queued (the main thread may already be writing the next ones) */
	for (i = 0; i < count; i += 1)
	{
		MOJOSHADER_effectValue *value = &mojo->params[i].value;
		keep[i] = value->values;
		value->values = values;
		values += Align8(value->value_count * 4);
	}
	proxy->real->ApplyEffect(proxy->real->driverData, effect, pass, &proxy->renderStateChanges);
	for (i = 0; i < count; i += 1)
	{
		mojo->params[i].value.values = keep[i];
	}
	SDL_free(savedHeap);
}

/* SpriteBatch's flush on the render thread (FNA3D_VitaSpriteBatch): FNA's SpriteBatch.FlushBatch
 * (sort, vertices, upload, one draw per texture run) in C, with the same sort keys, vertex math and
 * draw calls. The structs match FNA's SpriteBatch.SpriteInfo and VertexPositionColorTexture4. */

typedef struct VitaSpriteInfo
{
	int32_t textureHash;
	float sourceX, sourceY, sourceW, sourceH;
	float destinationX, destinationY, destinationW, destinationH;
	uint32_t color;
	float originX, originY;
	float rotationSin, rotationCos;
	float depth;
	int32_t effects;
} VitaSpriteInfo;

typedef struct VitaVertex
{
	float x, y, z;
	uint32_t color;
	float u, v;
} VitaVertex;

#define SB_MAX_SPRITES 2048
#define SB_SORT_DEFERRED 0
#define SB_SORT_IMMEDIATE 1
#define SB_SORT_TEXTURE 2
#define SB_SORT_BACKTOFRONT 3
#define SB_SORT_FRONTTOBACK 4

static VitaVertex *sbVertices; /* SB_MAX_SPRITES * 4 */

/* The render thread's own vertex buffer for SpriteBatch flushes: batches are appended (NOOVERWRITE, a
 * mapped write vitaGL doesn't copy) and only a full buffer is discarded. Discarding SpriteBatch's own
 * 196 KB buffer for every batch made vitaGL allocate ~14 new buffers a frame, each held until the GPU was
 * done with it: megabytes in flight, and once its pools ran low (after switching camera distances,
 * which reallocates the cached pictures) every allocation slowed down and the same frame's SpriteBatch
 * replay took 4 times as long (22 ms instead of 6). 8192 sprites (768 KB): discarded every ~16 frames. */
#define SB_RING_SPRITES 8192
static FNA3D_Buffer *sbRing;
static int32_t sbRingUsed;
static uint32_t *sbKeys, *sbKeysTmp;
static int32_t *sbOrder, *sbOrderTmp;
static int32_t sbCapacity;

static const float sbCornerX[4] = { 0.0f, 1.0f, 0.0f, 1.0f };
static const float sbCornerY[4] = { 0.0f, 0.0f, 1.0f, 1.0f };

static void SB_Vertices(const VitaSpriteInfo *s, VitaVertex *v)
{
	float x0, y0, x1, y1;
	int i;
	if (s->rotationSin == 0.0f && s->rotationCos == 1.0f)
	{
		if (s->originX == 0.0f && s->originY == 0.0f)
		{
			x0 = s->destinationX;
			y0 = s->destinationY;
			x1 = s->destinationW + s->destinationX;
			y1 = s->destinationH + s->destinationY;
		}
		else
		{
			x0 = (-s->originX * s->destinationW) + s->destinationX;
			y0 = (-s->originY * s->destinationH) + s->destinationY;
			x1 = ((1.0f - s->originX) * s->destinationW) + s->destinationX;
			y1 = ((1.0f - s->originY) * s->destinationH) + s->destinationY;
		}
		v[0].x = x0; v[0].y = y0;
		v[1].x = x1; v[1].y = y0;
		v[2].x = x0; v[2].y = y1;
		v[3].x = x1; v[3].y = y1;
	}
	else
	{
		float sn = s->rotationSin, cs = s->rotationCos;
		float cx, cy;
		cx = -s->originX * s->destinationW;
		cy = -s->originY * s->destinationH;
		v[0].x = (-sn * cy) + (cs * cx) + s->destinationX;
		v[0].y = (cs * cy) + (sn * cx) + s->destinationY;
		cx = (1.0f - s->originX) * s->destinationW;
		cy = -s->originY * s->destinationH;
		v[1].x = (-sn * cy) + (cs * cx) + s->destinationX;
		v[1].y = (cs * cy) + (sn * cx) + s->destinationY;
		cx = -s->originX * s->destinationW;
		cy = (1.0f - s->originY) * s->destinationH;
		v[2].x = (-sn * cy) + (cs * cx) + s->destinationX;
		v[2].y = (cs * cy) + (sn * cx) + s->destinationY;
		cx = (1.0f - s->originX) * s->destinationW;
		cy = (1.0f - s->originY) * s->destinationH;
		v[3].x = (-sn * cy) + (cs * cx) + s->destinationX;
		v[3].y = (cs * cy) + (sn * cx) + s->destinationY;
	}
	if (s->effects == 0)
	{
		float u1 = s->sourceW + s->sourceX;
		float v1 = s->sourceH + s->sourceY;
		v[0].u = s->sourceX; v[0].v = s->sourceY;
		v[1].u = u1; v[1].v = s->sourceY;
		v[2].u = s->sourceX; v[2].v = v1;
		v[3].u = u1; v[3].v = v1;
	}
	else
	{
		for (i = 0; i < 4; i += 1)
		{
			v[i].u = (sbCornerX[i ^ s->effects] * s->sourceW) + s->sourceX;
			v[i].v = (sbCornerY[i ^ s->effects] * s->sourceH) + s->sourceY;
		}
	}
	for (i = 0; i < 4; i += 1)
	{
		v[i].z = s->depth;
		v[i].color = s->color;
	}
}

/* The order to draw the sprites in: SpriteBatch's stable radix sort on the same keys (NULL means
 * submission order). */
static const int32_t* SB_Sort(const VitaSpriteInfo *infos, int32_t n, int32_t mode)
{
	uint32_t counts[1024];
	uint32_t prevKey = 0, firstKey;
	int ascending = 1, first = 1, flipped = 0;
	int32_t i, shift;
	uint32_t *kSrc, *kDst, *tk;
	int32_t *oSrc, *oDst, *to;
	if (n > sbCapacity)
	{
		sbCapacity = n + 512;
		sbKeys = (uint32_t*) SDL_realloc(sbKeys, sizeof(uint32_t) * sbCapacity);
		sbKeysTmp = (uint32_t*) SDL_realloc(sbKeysTmp, sizeof(uint32_t) * sbCapacity);
		sbOrder = (int32_t*) SDL_realloc(sbOrder, sizeof(int32_t) * sbCapacity);
		sbOrderTmp = (int32_t*) SDL_realloc(sbOrderTmp, sizeof(int32_t) * sbCapacity);
	}
	SDL_memset(counts, 0, sizeof(counts));
	for (i = 0; i < n; i += 1)
	{
		uint32_t k;
		if (mode == SB_SORT_TEXTURE)
		{
			k = (uint32_t) infos[i].textureHash ^ 0x80000000u;
		}
		else
		{
			float d = infos[i].depth;
			if (d != d)
			{
				k = 0;
			}
			else
			{
				int32_t bits;
				if (d == 0.0f)
				{
					d = 0.0f;
				}
				SDL_memcpy(&bits, &d, 4);
				k = (uint32_t) (bits < 0 ? ~bits : (bits | (int32_t) 0x80000000));
			}
			if (mode == SB_SORT_BACKTOFRONT)
			{
				k = ~k;
			}
		}
		sbKeys[i] = k;
		if (k < prevKey)
		{
			ascending = 0;
		}
		prevKey = k;
		counts[k & 0xFF] += 1;
		counts[256 + ((k >> 8) & 0xFF)] += 1;
		counts[512 + ((k >> 16) & 0xFF)] += 1;
		counts[768 + (k >> 24)] += 1;
	}
	if (ascending)
	{
		return NULL;
	}
	firstKey = sbKeys[0];
	kSrc = sbKeys; kDst = sbKeysTmp;
	oSrc = sbOrder; oDst = sbOrderTmp;
	for (shift = 0; shift < 32; shift += 8)
	{
		uint32_t *c = counts + (shift << 5);
		uint32_t sum = 0;
		if (c[(firstKey >> shift) & 0xFF] == (uint32_t) n)
		{
			continue;
		}
		for (i = 0; i < 256; i += 1)
		{
			uint32_t cnt = c[i];
			c[i] = sum;
			sum += cnt;
		}
		for (i = 0; i < n; i += 1)
		{
			uint32_t k = kSrc[i];
			uint32_t dst = c[(k >> shift) & 0xFF]++;
			kDst[dst] = k;
			oDst[dst] = first ? i : oSrc[i];
		}
		first = 0;
		tk = kSrc; kSrc = kDst; kDst = tk;
		to = oSrc; oSrc = oDst; oDst = to;
		flipped = !flipped;
	}
	if (first)
	{
		return NULL;
	}
	return flipped ? sbOrderTmp : sbOrder;
}

static void SB_Draw(FNA3D_Device *d, FNA3D_Texture *texture, FNA3D_SamplerState *sampler, FNA3D_VertexBufferBinding *binding, uint8_t *updated, int32_t base, int32_t count, FNA3D_Buffer *indices)
{
	FNA3D_Renderer *r = d->driverData;
	statDraws += 1;
	d->VerifySampler(r, 0, texture, sampler);
	d->ApplyVertexBufferBindings(r, binding, 1, *updated, base * 4);
	*updated = 0;
	d->DrawIndexedPrimitives(r, FNA3D_PRIMITIVETYPE_TRIANGLELIST, base * 4, 0, count * 4, 0, count * 2, indices, FNA3D_INDEXELEMENTSIZE_16BIT);
}

static void ReplaySpriteBatch(uint8_t *p)
{
	FNA3D_Device *d = proxy->real;
	int32_t count = *(int32_t*) p;
	int32_t mode = *(int32_t*) (p + 4);
	uint8_t updated = p[8];
	FNA3D_Buffer *indices = *(FNA3D_Buffer**) (p + 12);
	FNA3D_SamplerState *sampler = (FNA3D_SamplerState*) (p + 16);
	FNA3D_VertexBufferBinding *binding = (FNA3D_VertexBufferBinding*) (p + 16 + Align8(sizeof(FNA3D_SamplerState)));
	FNA3D_Texture **textures = (FNA3D_Texture**) (p + 16 + Align8(sizeof(FNA3D_SamplerState)) + Align8(sizeof(FNA3D_VertexBufferBinding)));
	uint8_t *data = ((uint8_t*) textures) + Align8(sizeof(FNA3D_Texture*) * count);
	const int32_t *order = NULL;
	int32_t start;
	uint64_t began = SDL_GetTicksNS();
	FNA3D_VertexBufferBinding ring;
	statBatches += 1;
	statSprites += (uint32_t) count;
	if (sbVertices == NULL)
	{
		sbVertices = (VitaVertex*) SDL_malloc(sizeof(VitaVertex) * 4 * SB_MAX_SPRITES);
	}
	if (sbRing == NULL)
	{
		sbRing = d->GenVertexBuffer(d->driverData, 1, FNA3D_BUFFERUSAGE_WRITEONLY, SB_RING_SPRITES * 4 * (int32_t) sizeof(VitaVertex));
		sbRingUsed = SB_RING_SPRITES;
	}
	ring = *binding;
	if (sbRing != NULL)
	{
		ring.vertexBuffer = sbRing;
		binding = &ring;
		/* SpriteBatch's own buffer (or another one) may have been bound since */
		updated = 1;
	}
	if (mode != SB_SORT_DEFERRED)
	{
		order = SB_Sort((const VitaSpriteInfo*) data, count, mode);
	}
	for (start = 0; start < count; start += SB_MAX_SPRITES)
	{
		int32_t n = count - start;
		int32_t i, runStart = 0;
		FNA3D_Texture *current;
		VitaVertex *vertices;
		if (n > SB_MAX_SPRITES)
		{
			n = SB_MAX_SPRITES;
		}
		if (mode == SB_SORT_DEFERRED)
		{
			vertices = ((VitaVertex*) data) + start * 4;
		}
		else
		{
			const VitaSpriteInfo *infos = (const VitaSpriteInfo*) data;
			for (i = 0; i < n; i += 1)
			{
				int32_t s = order ? order[start + i] : start + i;
				SB_Vertices(&infos[s], &sbVertices[i * 4]);
			}
			vertices = sbVertices;
		}
		/* Appended after the batches still in flight; a full buffer is discarded (fresh storage) */
		if (sbRing != NULL)
		{
			int32_t at;
			if (sbRingUsed + n > SB_RING_SPRITES)
			{
				d->SetVertexBufferData(d->driverData, sbRing, 0, vertices, n * (int32_t) sizeof(VitaVertex) * 4, 1, 1, FNA3D_SETDATAOPTIONS_DISCARD);
				sbRingUsed = 0;
			}
			else
			{
				d->SetVertexBufferData(d->driverData, sbRing, sbRingUsed * (int32_t) sizeof(VitaVertex) * 4, vertices, n * (int32_t) sizeof(VitaVertex) * 4, 1, 1, FNA3D_SETDATAOPTIONS_NOOVERWRITE);
			}
			at = sbRingUsed;
			sbRingUsed += n;
			runStart = at;
			n += at;
			i = at;
		}
		else
		{
			/* No buffer of our own: a fresh SpriteBatch buffer for each piece */
			d->SetVertexBufferData(d->driverData, binding->vertexBuffer, 0, vertices, n * (int32_t) sizeof(VitaVertex) * 4, 1, 1, FNA3D_SETDATAOPTIONS_DISCARD);
			i = 0;
		}
		/* Sprite k of this piece is at vertex buffer slot runStart + k */
		{
			int32_t first = i, k;
			current = textures[order ? order[start] : start];
			for (k = 1; k < n - first; k += 1)
			{
				FNA3D_Texture *texture = textures[order ? order[start + k] : start + k];
				if (texture != current)
				{
					SB_Draw(d, current, sampler, binding, &updated, runStart, first + k - runStart, indices);
					current = texture;
					runStart = first + k;
				}
			}
			SB_Draw(d, current, sampler, binding, &updated, runStart, n - runStart, indices);
		}
	}
	statSpriteUs += (SDL_GetTicksNS() - began) / 1000;
}

/* A render target switch waits for the next command that needs it. Stardew Valley enters and leaves
 * its UI buffer about eight times a frame, and each switch that reaches vitaGL ends a GXM scene: the
 * next one loads and stores the whole target again. A switch followed by another switch with
 * nothing drawn in between is dropped. FNA follows every switch with its own viewport and scissor,
 * and those (and the rasterizer state, whose cull mode flips with the target) depend on what is
 * bound, so they wait with it and the last of each is applied after the switch. Anything else
 * applies the waiting switch first. */
static uint8_t pendingTargets[24 + 4 * sizeof(FNA3D_RenderTargetBinding)];
static uint8_t hasPendingTargets, hasPendingViewport, hasPendingScissor, hasPendingRasterizer;
static FNA3D_Viewport pendingViewport;
static FNA3D_Rect pendingScissor;
static FNA3D_RasterizerState pendingRasterizer;

static void ApplyRenderTargets(FNA3D_Device *d, FNA3D_Renderer *r, uint8_t *p)
{
	int32_t count = *(int32_t*) p;
	FNA3D_DepthFormat depthFormat = *(FNA3D_DepthFormat*) (p + 4);
	uint8_t preserve = p[8];
	FNA3D_Renderbuffer *depth = *(FNA3D_Renderbuffer**) (p + 16);
	statTargets += 1;
	d->SetRenderTargets(r, (count > 0) ? (FNA3D_RenderTargetBinding*) (p + 24) : NULL, count, depth, depthFormat, preserve);
}

static void FlushPendingTargets(FNA3D_Device *d, FNA3D_Renderer *r)
{
	if (!hasPendingTargets)
	{
		return;
	}
	hasPendingTargets = 0;
	ApplyRenderTargets(d, r, pendingTargets);
	if (hasPendingViewport)
	{
		d->SetViewport(r, &pendingViewport);
	}
	if (hasPendingScissor)
	{
		d->SetScissorRect(r, &pendingScissor);
	}
	if (hasPendingRasterizer)
	{
		d->ApplyRasterizerState(r, &pendingRasterizer);
	}
	hasPendingViewport = hasPendingScissor = hasPendingRasterizer = 0;
}

static void Replay(Cmd *cmd)
{
	FNA3D_Device *d = proxy->real;
	/* NULL while the first command (creating the real device) runs */
	FNA3D_Renderer *r = (d != NULL) ? d->driverData : NULL;
	uint8_t *p = (uint8_t*) (cmd + 1);
	if (hasPendingTargets)
	{
		switch (cmd->type)
		{
		case CMD_PAD:
			return;
		case CMD_RENDERTARGETS:
			statTargetsDropped += 1;
			break; /* replaced below */
		case CMD_VIEWPORT:
			SDL_memcpy(&pendingViewport, p, sizeof(pendingViewport));
			hasPendingViewport = 1;
			return;
		case CMD_SCISSOR:
			SDL_memcpy(&pendingScissor, p, sizeof(pendingScissor));
			hasPendingScissor = 1;
			return;
		case CMD_RASTERIZERSTATE:
			SDL_memcpy(&pendingRasterizer, p, sizeof(pendingRasterizer));
			hasPendingRasterizer = 1;
			return;
		default:
			FlushPendingTargets(d, r);
			break;
		}
	}
	switch (cmd->type)
	{
	case CMD_PAD:
		break;
	case CMD_SYNC:
	{
		SyncCall *call = *(SyncCall**) p;
		call->fn(call->args);
		__atomic_store_n(&call->done, 1, __ATOMIC_RELEASE);
		break;
	}
	case CMD_SWAP:
	{
		uint8_t hasSrc = p[0], hasDst = p[1];
		FNA3D_Rect *src = (FNA3D_Rect*) (p + 8);
		FNA3D_Rect *dst = (FNA3D_Rect*) (p + 8 + sizeof(FNA3D_Rect));
		void *window = *(void**) (p + 8 + 2 * sizeof(FNA3D_Rect));
		d->SwapBuffers(r, hasSrc ? src : NULL, hasDst ? dst : NULL, window);
		__atomic_sub_fetch(&proxy->swapsPending, 1, __ATOMIC_RELEASE);
		break;
	}
	case CMD_CLEAR:
		d->Clear(r, *(FNA3D_ClearOptions*) p, (FNA3D_Vec4*) (p + 8), *(float*) (p + 8 + sizeof(FNA3D_Vec4)), *(int32_t*) (p + 12 + sizeof(FNA3D_Vec4)));
		break;
	case CMD_DRAWINDEXED:
	{
		int32_t *a = (int32_t*) p;
		statDraws += 1;
		d->DrawIndexedPrimitives(r, (FNA3D_PrimitiveType) a[0], a[1], a[2], a[3], a[4], a[5], *(FNA3D_Buffer**) (p + 24), (FNA3D_IndexElementSize) a[8]);
		break;
	}
	case CMD_DRAWINSTANCED:
	{
		int32_t *a = (int32_t*) p;
		d->DrawInstancedPrimitives(r, (FNA3D_PrimitiveType) a[0], a[1], a[2], a[3], a[4], a[5], a[6], *(FNA3D_Buffer**) (p + 32), (FNA3D_IndexElementSize) a[10]);
		break;
	}
	case CMD_DRAW:
	{
		int32_t *a = (int32_t*) p;
		statDraws += 1;
		d->DrawPrimitives(r, (FNA3D_PrimitiveType) a[0], a[1], a[2]);
		break;
	}
	case CMD_VIEWPORT:
		d->SetViewport(r, (FNA3D_Viewport*) p);
		break;
	case CMD_SCISSOR:
		d->SetScissorRect(r, (FNA3D_Rect*) p);
		break;
	case CMD_BLENDFACTOR:
		d->SetBlendFactor(r, (FNA3D_Color*) p);
		break;
	case CMD_MULTISAMPLEMASK:
		d->SetMultiSampleMask(r, *(int32_t*) p);
		break;
	case CMD_REFERENCESTENCIL:
		d->SetReferenceStencil(r, *(int32_t*) p);
		break;
	case CMD_BLENDSTATE:
		d->SetBlendState(r, (FNA3D_BlendState*) p);
		break;
	case CMD_DEPTHSTENCILSTATE:
		d->SetDepthStencilState(r, (FNA3D_DepthStencilState*) p);
		break;
	case CMD_RASTERIZERSTATE:
		d->ApplyRasterizerState(r, (FNA3D_RasterizerState*) p);
		break;
	case CMD_VERIFYSAMPLER:
	case CMD_VERIFYVERTEXSAMPLER:
	{
		int32_t index = *(int32_t*) p;
		FNA3D_Texture *texture = *(FNA3D_Texture**) (p + 8);
		FNA3D_SamplerState *sampler = (FNA3D_SamplerState*) (p + 16);
		if (cmd->type == CMD_VERIFYSAMPLER)
		{
			d->VerifySampler(r, index, texture, sampler);
		}
		else
		{
			d->VerifyVertexSampler(r, index, texture, sampler);
		}
		break;
	}
	case CMD_VERTEXBINDINGS:
	{
		int32_t *a = (int32_t*) p;
		d->ApplyVertexBufferBindings(r, (FNA3D_VertexBufferBinding*) (p + 16), a[0], (uint8_t) a[1], a[2]);
		break;
	}
	case CMD_RENDERTARGETS:
	{
		int32_t count = *(int32_t*) p;
		uint32_t bytes = 24 + sizeof(FNA3D_RenderTargetBinding) * (uint32_t) count;
		if (count < 0 || bytes > sizeof(pendingTargets))
		{
			FlushPendingTargets(d, r);
			ApplyRenderTargets(d, r, p);
			break;
		}
		/* Held until something needs it (see above). A viewport, scissor or rasterizer state still
		 * waiting from a dropped switch stays, applied after this one unless a newer one comes. */
		SDL_memcpy(pendingTargets, p, bytes);
		hasPendingTargets = 1;
		break;
	}
	case CMD_RESOLVETARGET:
		d->ResolveTarget(r, (FNA3D_RenderTargetBinding*) p);
		break;
	case CMD_DISPOSETEXTURE:
		d->AddDisposeTexture(r, *(FNA3D_Texture**) p);
		break;
	case CMD_DISPOSERENDERBUFFER:
		d->AddDisposeRenderbuffer(r, *(FNA3D_Renderbuffer**) p);
		break;
	case CMD_DISPOSEVERTEXBUFFER:
		d->AddDisposeVertexBuffer(r, *(FNA3D_Buffer**) p);
		break;
	case CMD_DISPOSEINDEXBUFFER:
		d->AddDisposeIndexBuffer(r, *(FNA3D_Buffer**) p);
		break;
	case CMD_DISPOSEEFFECT:
		d->AddDisposeEffect(r, *(FNA3D_Effect**) p);
		break;
	case CMD_DISPOSEQUERY:
		d->AddDisposeQuery(r, *(FNA3D_Query**) p);
		break;
	case CMD_VERTEXDATA:
	{
		int32_t *a = (int32_t*) p;
		FNA3D_Buffer *buffer = *(FNA3D_Buffer**) (p + 24);
		d->SetVertexBufferData(r, buffer, a[0], p + 32, a[1], a[2], a[3], (FNA3D_SetDataOptions) a[4]);
		break;
	}
	case CMD_TECHNIQUE:
		d->SetEffectTechnique(r, *(FNA3D_Effect**) p, *(MOJOSHADER_effectTechnique**) (p + 8));
		break;
	case CMD_APPLYEFFECT:
		ReplayApplyEffect(p);
		break;
	case CMD_SPRITEBATCH:
		ReplaySpriteBatch(p);
		break;
	}
}

static int RenderThread(SceSize args, void *argp)
{
	renderThreadId = sceKernelGetThreadId();
	while (!__atomic_load_n(&proxy->quit, __ATOMIC_ACQUIRE))
	{
		uint32_t r = proxy->readPos;
		uint32_t w = __atomic_load_n(&proxy->writePos, __ATOMIC_ACQUIRE);
		if (r == w)
		{
			Wait();
			continue;
		}
		{
			SceUInt64 start = sceKernelGetProcessTimeWide();
			while (r != w)
			{
				Cmd *cmd = (Cmd*) (proxy->ring + (r & RING_MASK));
				uint32_t size = cmd->size;
				Replay(cmd);
				r += size;
				__atomic_store_n(&proxy->readPos, r, __ATOMIC_RELEASE);
			}
			statBusyUs += sceKernelGetProcessTimeWide() - start;
		}
	}
	return sceKernelExitDeleteThread(0);
}

/* Proxy functions: queued */

#define PROXY ((void) driverData)

static void P_SwapBuffers(FNA3D_Renderer *driverData, FNA3D_Rect *src, FNA3D_Rect *dst, void *window)
{
	uint32_t size;
	uint8_t *p;
	/* One frame in flight: the previous swap must have run */
	while (__atomic_load_n(&proxy->swapsPending, __ATOMIC_ACQUIRE) > 0)
	{
		MainWait();
	}
	__atomic_add_fetch(&proxy->swapsPending, 1, __ATOMIC_RELEASE);
	p = (uint8_t*) Begin(CMD_SWAP, 8 + 2 * sizeof(FNA3D_Rect) + sizeof(void*), &size);
	p[0] = src != NULL;
	p[1] = dst != NULL;
	if (src != NULL)
	{
		SDL_memcpy(p + 8, src, sizeof(FNA3D_Rect));
	}
	if (dst != NULL)
	{
		SDL_memcpy(p + 8 + sizeof(FNA3D_Rect), dst, sizeof(FNA3D_Rect));
	}
	*(void**) (p + 8 + 2 * sizeof(FNA3D_Rect)) = window;
	Commit(size);
	FlushLogs();
}

static void P_Clear(FNA3D_Renderer *driverData, FNA3D_ClearOptions options, FNA3D_Vec4 *color, float depth, int32_t stencil)
{
	uint32_t size;
	uint8_t *p = (uint8_t*) Begin(CMD_CLEAR, 16 + sizeof(FNA3D_Vec4), &size);
	*(FNA3D_ClearOptions*) p = options;
	SDL_memcpy(p + 8, color, sizeof(FNA3D_Vec4));
	*(float*) (p + 8 + sizeof(FNA3D_Vec4)) = depth;
	*(int32_t*) (p + 12 + sizeof(FNA3D_Vec4)) = stencil;
	Commit(size);
}

static void P_DrawIndexedPrimitives(FNA3D_Renderer *driverData, FNA3D_PrimitiveType primitiveType, int32_t baseVertex, int32_t minVertexIndex, int32_t numVertices, int32_t startIndex, int32_t primitiveCount, FNA3D_Buffer *indices, FNA3D_IndexElementSize indexElementSize)
{
	uint32_t size;
	int32_t *a = (int32_t*) Begin(CMD_DRAWINDEXED, 40, &size);
	a[0] = primitiveType;
	a[1] = baseVertex;
	a[2] = minVertexIndex;
	a[3] = numVertices;
	a[4] = startIndex;
	a[5] = primitiveCount;
	*(FNA3D_Buffer**) (a + 6) = indices;
	a[8] = indexElementSize;
	Commit(size);
}

static void P_DrawInstancedPrimitives(FNA3D_Renderer *driverData, FNA3D_PrimitiveType primitiveType, int32_t baseVertex, int32_t minVertexIndex, int32_t numVertices, int32_t startIndex, int32_t primitiveCount, int32_t instanceCount, FNA3D_Buffer *indices, FNA3D_IndexElementSize indexElementSize)
{
	uint32_t size;
	int32_t *a = (int32_t*) Begin(CMD_DRAWINSTANCED, 48, &size);
	a[0] = primitiveType;
	a[1] = baseVertex;
	a[2] = minVertexIndex;
	a[3] = numVertices;
	a[4] = startIndex;
	a[5] = primitiveCount;
	a[6] = instanceCount;
	*(FNA3D_Buffer**) (a + 8) = indices;
	a[10] = indexElementSize;
	Commit(size);
}

static void P_DrawPrimitives(FNA3D_Renderer *driverData, FNA3D_PrimitiveType primitiveType, int32_t vertexStart, int32_t primitiveCount)
{
	uint32_t size;
	int32_t *a = (int32_t*) Begin(CMD_DRAW, 12, &size);
	a[0] = primitiveType;
	a[1] = vertexStart;
	a[2] = primitiveCount;
	Commit(size);
}

#define QUEUE_STRUCT(name, type, cmdType, call) \
	static void name(FNA3D_Renderer *driverData, type *value) \
	{ \
		uint32_t size; \
		void *p = Begin(cmdType, sizeof(type), &size); \
		SDL_memcpy(p, value, sizeof(type)); \
		Commit(size); \
	}

QUEUE_STRUCT(P_SetViewport, FNA3D_Viewport, CMD_VIEWPORT, SetViewport)
QUEUE_STRUCT(P_SetScissorRect, FNA3D_Rect, CMD_SCISSOR, SetScissorRect)
QUEUE_STRUCT(P_SetBlendFactor, FNA3D_Color, CMD_BLENDFACTOR, SetBlendFactor)
QUEUE_STRUCT(P_SetBlendState, FNA3D_BlendState, CMD_BLENDSTATE, SetBlendState)
QUEUE_STRUCT(P_SetDepthStencilState, FNA3D_DepthStencilState, CMD_DEPTHSTENCILSTATE, SetDepthStencilState)
QUEUE_STRUCT(P_ApplyRasterizerState, FNA3D_RasterizerState, CMD_RASTERIZERSTATE, ApplyRasterizerState)

static void P_SetMultiSampleMask(FNA3D_Renderer *driverData, int32_t mask)
{
	uint32_t size;
	int32_t *p = (int32_t*) Begin(CMD_MULTISAMPLEMASK, 4, &size);
	*p = mask;
	Commit(size);
}

static void P_SetReferenceStencil(FNA3D_Renderer *driverData, int32_t ref)
{
	uint32_t size;
	int32_t *p = (int32_t*) Begin(CMD_REFERENCESTENCIL, 4, &size);
	*p = ref;
	Commit(size);
}

static void QueueSampler(uint32_t type, int32_t index, FNA3D_Texture *texture, FNA3D_SamplerState *sampler)
{
	uint32_t size;
	uint8_t *p = (uint8_t*) Begin(type, 16 + sizeof(FNA3D_SamplerState), &size);
	*(int32_t*) p = index;
	*(FNA3D_Texture**) (p + 8) = texture;
	SDL_memcpy(p + 16, sampler, sizeof(FNA3D_SamplerState));
	Commit(size);
}

static void P_VerifySampler(FNA3D_Renderer *driverData, int32_t index, FNA3D_Texture *texture, FNA3D_SamplerState *sampler)
{
	QueueSampler(CMD_VERIFYSAMPLER, index, texture, sampler);
}

static void P_VerifyVertexSampler(FNA3D_Renderer *driverData, int32_t index, FNA3D_Texture *texture, FNA3D_SamplerState *sampler)
{
	QueueSampler(CMD_VERIFYVERTEXSAMPLER, index, texture, sampler);
}

static void P_ApplyVertexBufferBindings(FNA3D_Renderer *driverData, FNA3D_VertexBufferBinding *bindings, int32_t numBindings, uint8_t bindingsUpdated, int32_t baseVertex)
{
	/* The bindings point at their declarations' pinned element arrays, which live as long as the
	 * declarations; the binding structs themselves are copied. */
	uint32_t size;
	uint8_t *p = (uint8_t*) Begin(CMD_VERTEXBINDINGS, 16 + sizeof(FNA3D_VertexBufferBinding) * numBindings, &size);
	int32_t *a = (int32_t*) p;
	a[0] = numBindings;
	a[1] = bindingsUpdated;
	a[2] = baseVertex;
	SDL_memcpy(p + 16, bindings, sizeof(FNA3D_VertexBufferBinding) * numBindings);
	Commit(size);
}

static void P_SetRenderTargets(FNA3D_Renderer *driverData, FNA3D_RenderTargetBinding *renderTargets, int32_t numRenderTargets, FNA3D_Renderbuffer *depthStencilBuffer, FNA3D_DepthFormat depthFormat, uint8_t preserveTargetContents)
{
	uint32_t size;
	int32_t count = (renderTargets != NULL) ? numRenderTargets : 0;
	uint8_t *p = (uint8_t*) Begin(CMD_RENDERTARGETS, 24 + sizeof(FNA3D_RenderTargetBinding) * count, &size);
	*(int32_t*) p = count;
	*(FNA3D_DepthFormat*) (p + 4) = depthFormat;
	p[8] = preserveTargetContents;
	*(FNA3D_Renderbuffer**) (p + 16) = depthStencilBuffer;
	if (count > 0)
	{
		SDL_memcpy(p + 24, renderTargets, sizeof(FNA3D_RenderTargetBinding) * count);
	}
	Commit(size);
}

static void P_ResolveTarget(FNA3D_Renderer *driverData, FNA3D_RenderTargetBinding *target)
{
	uint32_t size;
	void *p = Begin(CMD_RESOLVETARGET, sizeof(FNA3D_RenderTargetBinding), &size);
	SDL_memcpy(p, target, sizeof(FNA3D_RenderTargetBinding));
	Commit(size);
}

#define QUEUE_POINTER(name, type, cmdType) \
	static void name(FNA3D_Renderer *driverData, type *value) \
	{ \
		uint32_t size; \
		type **p = (type**) Begin(cmdType, sizeof(type*), &size); \
		*p = value; \
		Commit(size); \
	}

QUEUE_POINTER(P_AddDisposeTexture, FNA3D_Texture, CMD_DISPOSETEXTURE)
QUEUE_POINTER(P_AddDisposeRenderbuffer, FNA3D_Renderbuffer, CMD_DISPOSERENDERBUFFER)
QUEUE_POINTER(P_AddDisposeVertexBuffer, FNA3D_Buffer, CMD_DISPOSEVERTEXBUFFER)
QUEUE_POINTER(P_AddDisposeIndexBuffer, FNA3D_Buffer, CMD_DISPOSEINDEXBUFFER)
QUEUE_POINTER(P_AddDisposeQuery, FNA3D_Query, CMD_DISPOSEQUERY)

static void P_AddDisposeEffect(FNA3D_Renderer *driverData, FNA3D_Effect *effect)
{
	uint32_t size;
	FNA3D_Effect **p;
	RemoveEffect(effect);
	p = (FNA3D_Effect**) Begin(CMD_DISPOSEEFFECT, sizeof(FNA3D_Effect*), &size);
	*p = effect;
	Commit(size);
}

static void P_SetVertexBufferData(FNA3D_Renderer *driverData, FNA3D_Buffer *buffer, int32_t offsetInBytes, void *data, int32_t elementCount, int32_t elementSizeInBytes, int32_t vertexStride, FNA3D_SetDataOptions options)
{
	uint32_t size;
	uint32_t bytes = (uint32_t) (elementCount * elementSizeInBytes);
	uint8_t *p = (uint8_t*) Begin(CMD_VERTEXDATA, 32 + bytes, &size);
	int32_t *a = (int32_t*) p;
	a[0] = offsetInBytes;
	a[1] = elementCount;
	a[2] = elementSizeInBytes;
	a[3] = vertexStride;
	a[4] = options;
	*(FNA3D_Buffer**) (p + 24) = buffer;
	SDL_memcpy(p + 32, data, bytes);
	Commit(size);
}

static void P_SetEffectTechnique(FNA3D_Renderer *driverData, FNA3D_Effect *effect, MOJOSHADER_effectTechnique *technique)
{
	uint32_t size;
	uint8_t *p;
	EffectEntry *entry;
	SDL_LockMutex(proxy->lock);
	entry = FindEffect(effect);
	if (entry != NULL)
	{
		entry->technique = technique;
	}
	p = (uint8_t*) Begin(CMD_TECHNIQUE, 16, &size);
	*(FNA3D_Effect**) p = effect;
	*(MOJOSHADER_effectTechnique**) (p + 8) = technique;
	Commit(size);
	SDL_UnlockMutex(proxy->lock);
}

/* Sync calls */

typedef struct ApplyEffectArgs
{
	FNA3D_Effect *effect;
	uint32_t pass;
	MOJOSHADER_effectStateChanges *stateChanges;
} ApplyEffectArgs;

static void S_ApplyEffect(void *a)
{
	ApplyEffectArgs *args = (ApplyEffectArgs*) a;
	proxy->real->ApplyEffect(proxy->real->driverData, args->effect, args->pass, args->stateChanges);
}

static void P_ApplyEffect(FNA3D_Renderer *driverData, FNA3D_Effect *effect, uint32_t pass, MOJOSHADER_effectStateChanges *stateChanges)
{
	EffectEntry *entry;
	MOJOSHADER_effect *mojo;
	MOJOSHADER_effectTechnique *technique;
	MOJOSHADER_effectPass *curPass;
	MOJOSHADER_effectShader *rawVert = NULL, *rawPixl = NULL;
	uint32_t i, count, bytes, size;
	uint8_t *p, *values;
	int follow = 1;

	SDL_LockMutex(proxy->lock);
	entry = FindEffect(effect);
	if (entry == NULL || entry->technique == NULL || pass >= entry->technique->pass_count)
	{
		follow = 0;
	}
	else
	{
		mojo = entry->mojo;
		technique = entry->technique;
		curPass = &technique->passes[pass];
		for (i = 0; i < curPass->state_count; i += 1)
		{
			MOJOSHADER_effectState *state = &curPass->states[i];
			if (state->type == MOJOSHADER_RS_VERTEXSHADER)
			{
				rawVert = &mojo->objects[*state->value.valuesI].shader;
			}
			else if (state->type == MOJOSHADER_RS_PIXELSHADER)
			{
				rawPixl = &mojo->objects[*state->value.valuesI].shader;
			}
		}
		if (rawVert == NULL || rawPixl == NULL || rawVert->is_preshader || rawPixl->is_preshader)
		{
			follow = 0;
		}
	}
	if (!follow)
	{
		ApplyEffectArgs args;
		SDL_UnlockMutex(proxy->lock);
		args.effect = effect;
		args.pass = pass;
		args.stateChanges = stateChanges;
		RunSync(S_ApplyEffect, &args);
		return;
	}

	/* What MOJOSHADER_effectBeginPass reports for this pass */
	stateChanges->render_state_changes = curPass->states;
	stateChanges->render_state_change_count = curPass->state_count;
	stateChanges->vertex_sampler_state_changes = rawVert->samplers;
	stateChanges->vertex_sampler_state_change_count = rawVert->sampler_count;
	stateChanges->sampler_state_changes = rawPixl->samplers;
	stateChanges->sampler_state_change_count = rawPixl->sampler_count;

	count = mojo->param_count;
	bytes = 0;
	for (i = 0; i < count; i += 1)
	{
		bytes += Align8(mojo->params[i].value.value_count * 4);
	}
	p = (uint8_t*) Begin(CMD_APPLYEFFECT, 24 + bytes, &size);
	*(FNA3D_Effect**) p = effect;
	*(MOJOSHADER_effect**) (p + 8) = mojo;
	*(uint32_t*) (p + 16) = pass;
	*(uint32_t*) (p + 20) = count;
	values = p + 24;
	for (i = 0; i < count; i += 1)
	{
		uint32_t valueBytes = mojo->params[i].value.value_count * 4;
		SDL_memcpy(values, entry->values[i], valueBytes);
		values += Align8(valueBytes);
	}
	Commit(size);
	SDL_UnlockMutex(proxy->lock);
}

/* Everything else waits for the render thread. One args struct and trampoline per call. */

#define R proxy->real
#define RD proxy->real->driverData

typedef struct { MOJOSHADER_effectStateChanges *sc; FNA3D_Effect *e; } BPRArgs;
static void S_BeginPassRestore(void *a) { BPRArgs *x = (BPRArgs*) a; R->BeginPassRestore(RD, x->e, x->sc); }
static void P_BeginPassRestore(FNA3D_Renderer *driverData, FNA3D_Effect *effect, MOJOSHADER_effectStateChanges *stateChanges)
{
	BPRArgs x = { stateChanges, effect };
	RunSync(S_BeginPassRestore, &x);
}

static void S_EndPassRestore(void *a) { R->EndPassRestore(RD, *(FNA3D_Effect**) a); }
static void P_EndPassRestore(FNA3D_Renderer *driverData, FNA3D_Effect *effect)
{
	RunSync(S_EndPassRestore, &effect);
}

static void P_GetBlendFactor(FNA3D_Renderer *driverData, FNA3D_Color *blendFactor);
typedef struct { FNA3D_Color *c; } GBFArgs;
static void S_GetBlendFactor(void *a) { R->GetBlendFactor(RD, ((GBFArgs*) a)->c); }
static void P_GetBlendFactor(FNA3D_Renderer *driverData, FNA3D_Color *blendFactor)
{
	GBFArgs x = { blendFactor };
	RunSync(S_GetBlendFactor, &x);
}

typedef struct { int32_t ret; } IntRet;
static void S_GetMultiSampleMask(void *a) { ((IntRet*) a)->ret = R->GetMultiSampleMask(RD); }
static int32_t P_GetMultiSampleMask(FNA3D_Renderer *driverData) { IntRet x; RunSync(S_GetMultiSampleMask, &x); return x.ret; }
static void S_GetReferenceStencil(void *a) { ((IntRet*) a)->ret = R->GetReferenceStencil(RD); }
static int32_t P_GetReferenceStencil(FNA3D_Renderer *driverData) { IntRet x; RunSync(S_GetReferenceStencil, &x); return x.ret; }

static void CacheBackbuffer(void);

typedef struct { FNA3D_PresentationParameters *pp; } RBArgs;
static void S_ResetBackbuffer(void *a) { R->ResetBackbuffer(RD, ((RBArgs*) a)->pp); }
static void P_ResetBackbuffer(FNA3D_Renderer *driverData, FNA3D_PresentationParameters *presentationParameters)
{
	RBArgs x = { presentationParameters };
	RunSync(S_ResetBackbuffer, &x);
	CacheBackbuffer();
}

typedef struct { int32_t x, y, w, h; void *data; int32_t len; } RBBArgs;
static void S_ReadBackbuffer(void *a) { RBBArgs *x = (RBBArgs*) a; R->ReadBackbuffer(RD, x->x, x->y, x->w, x->h, x->data, x->len); }
static void P_ReadBackbuffer(FNA3D_Renderer *driverData, int32_t x, int32_t y, int32_t w, int32_t h, void *data, int32_t dataLength)
{
	RBBArgs a = { x, y, w, h, data, dataLength };
	RunSync(S_ReadBackbuffer, &a);
}

static void P_GetBackbufferSize(FNA3D_Renderer *driverData, int32_t *w, int32_t *h)
{
	*w = proxy->backbufferWidth;
	*h = proxy->backbufferHeight;
}
static FNA3D_SurfaceFormat P_GetBackbufferSurfaceFormat(FNA3D_Renderer *driverData) { return proxy->backbufferFormat; }
static FNA3D_DepthFormat P_GetBackbufferDepthFormat(FNA3D_Renderer *driverData) { return proxy->backbufferDepthFormat; }
static int32_t P_GetBackbufferMultiSampleCount(FNA3D_Renderer *driverData) { return proxy->backbufferMultiSampleCount; }

typedef struct { FNA3D_Texture *ret; FNA3D_SurfaceFormat f; int32_t w, h, d, l; uint8_t rt; } CTArgs;
static void S_CreateTexture2D(void *a) { CTArgs *x = (CTArgs*) a; x->ret = R->CreateTexture2D(RD, x->f, x->w, x->h, x->l, x->rt); }
static FNA3D_Texture* P_CreateTexture2D(FNA3D_Renderer *driverData, FNA3D_SurfaceFormat format, int32_t width, int32_t height, int32_t levelCount, uint8_t isRenderTarget)
{
	CTArgs x = { NULL, format, width, height, 0, levelCount, isRenderTarget };
	RunSync(S_CreateTexture2D, &x);
	return x.ret;
}
static void S_CreateTexture3D(void *a) { CTArgs *x = (CTArgs*) a; x->ret = R->CreateTexture3D(RD, x->f, x->w, x->h, x->d, x->l); }
static FNA3D_Texture* P_CreateTexture3D(FNA3D_Renderer *driverData, FNA3D_SurfaceFormat format, int32_t width, int32_t height, int32_t depth, int32_t levelCount)
{
	CTArgs x = { NULL, format, width, height, depth, levelCount, 0 };
	RunSync(S_CreateTexture3D, &x);
	return x.ret;
}
static void S_CreateTextureCube(void *a) { CTArgs *x = (CTArgs*) a; x->ret = R->CreateTextureCube(RD, x->f, x->w, x->l, x->rt); }
static FNA3D_Texture* P_CreateTextureCube(FNA3D_Renderer *driverData, FNA3D_SurfaceFormat format, int32_t size, int32_t levelCount, uint8_t isRenderTarget)
{
	CTArgs x = { NULL, format, size, 0, 0, levelCount, isRenderTarget };
	RunSync(S_CreateTextureCube, &x);
	return x.ret;
}

typedef struct { FNA3D_Texture *t; int32_t x, y, z, w, h, d, level; FNA3D_CubeMapFace face; void *data; int32_t len; } TDArgs;
static void S_SetTextureData2D(void *a) { TDArgs *x = (TDArgs*) a; R->SetTextureData2D(RD, x->t, x->x, x->y, x->w, x->h, x->level, x->data, x->len); }
static void P_SetTextureData2D(FNA3D_Renderer *driverData, FNA3D_Texture *texture, int32_t x, int32_t y, int32_t w, int32_t h, int32_t level, void *data, int32_t dataLength)
{
	TDArgs a = { texture, x, y, 0, w, h, 0, level, 0, data, dataLength };
	RunSync(S_SetTextureData2D, &a);
}
static void S_SetTextureData3D(void *a) { TDArgs *x = (TDArgs*) a; R->SetTextureData3D(RD, x->t, x->x, x->y, x->z, x->w, x->h, x->d, x->level, x->data, x->len); }
static void P_SetTextureData3D(FNA3D_Renderer *driverData, FNA3D_Texture *texture, int32_t x, int32_t y, int32_t z, int32_t w, int32_t h, int32_t d, int32_t level, void *data, int32_t dataLength)
{
	TDArgs a = { texture, x, y, z, w, h, d, level, 0, data, dataLength };
	RunSync(S_SetTextureData3D, &a);
}
static void S_SetTextureDataCube(void *a) { TDArgs *x = (TDArgs*) a; R->SetTextureDataCube(RD, x->t, x->x, x->y, x->w, x->h, x->face, x->level, x->data, x->len); }
static void P_SetTextureDataCube(FNA3D_Renderer *driverData, FNA3D_Texture *texture, int32_t x, int32_t y, int32_t w, int32_t h, FNA3D_CubeMapFace cubeMapFace, int32_t level, void *data, int32_t dataLength)
{
	TDArgs a = { texture, x, y, 0, w, h, 0, level, cubeMapFace, data, dataLength };
	RunSync(S_SetTextureDataCube, &a);
}
static void S_GetTextureData2D(void *a) { TDArgs *x = (TDArgs*) a; R->GetTextureData2D(RD, x->t, x->x, x->y, x->w, x->h, x->level, x->data, x->len); }
static void P_GetTextureData2D(FNA3D_Renderer *driverData, FNA3D_Texture *texture, int32_t x, int32_t y, int32_t w, int32_t h, int32_t level, void *data, int32_t dataLength)
{
	TDArgs a = { texture, x, y, 0, w, h, 0, level, 0, data, dataLength };
	RunSync(S_GetTextureData2D, &a);
}
static void S_GetTextureData3D(void *a) { TDArgs *x = (TDArgs*) a; R->GetTextureData3D(RD, x->t, x->x, x->y, x->z, x->w, x->h, x->d, x->level, x->data, x->len); }
static void P_GetTextureData3D(FNA3D_Renderer *driverData, FNA3D_Texture *texture, int32_t x, int32_t y, int32_t z, int32_t w, int32_t h, int32_t d, int32_t level, void *data, int32_t dataLength)
{
	TDArgs a = { texture, x, y, z, w, h, d, level, 0, data, dataLength };
	RunSync(S_GetTextureData3D, &a);
}
static void S_GetTextureDataCube(void *a) { TDArgs *x = (TDArgs*) a; R->GetTextureDataCube(RD, x->t, x->x, x->y, x->w, x->h, x->face, x->level, x->data, x->len); }
static void P_GetTextureDataCube(FNA3D_Renderer *driverData, FNA3D_Texture *texture, int32_t x, int32_t y, int32_t w, int32_t h, FNA3D_CubeMapFace cubeMapFace, int32_t level, void *data, int32_t dataLength)
{
	TDArgs a = { texture, x, y, 0, w, h, 0, level, cubeMapFace, data, dataLength };
	RunSync(S_GetTextureDataCube, &a);
}

typedef struct { FNA3D_Texture *y, *u, *v; int32_t yw, yh, uvw, uvh; void *data; int32_t len; } YUVArgs;
static void S_SetTextureDataYUV(void *a) { YUVArgs *x = (YUVArgs*) a; R->SetTextureDataYUV(RD, x->y, x->u, x->v, x->yw, x->yh, x->uvw, x->uvh, x->data, x->len); }
static void P_SetTextureDataYUV(FNA3D_Renderer *driverData, FNA3D_Texture *y, FNA3D_Texture *u, FNA3D_Texture *v, int32_t yWidth, int32_t yHeight, int32_t uvWidth, int32_t uvHeight, void *data, int32_t dataLength)
{
	YUVArgs a = { y, u, v, yWidth, yHeight, uvWidth, uvHeight, data, dataLength };
	RunSync(S_SetTextureDataYUV, &a);
}

typedef struct { FNA3D_Renderbuffer *ret; int32_t w, h, ms; FNA3D_SurfaceFormat f; FNA3D_DepthFormat df; FNA3D_Texture *t; } RBufArgs;
static void S_GenColorRenderbuffer(void *a) { RBufArgs *x = (RBufArgs*) a; x->ret = R->GenColorRenderbuffer(RD, x->w, x->h, x->f, x->ms, x->t); }
static FNA3D_Renderbuffer* P_GenColorRenderbuffer(FNA3D_Renderer *driverData, int32_t width, int32_t height, FNA3D_SurfaceFormat format, int32_t multiSampleCount, FNA3D_Texture *texture)
{
	RBufArgs x = { NULL, width, height, multiSampleCount, format, 0, texture };
	RunSync(S_GenColorRenderbuffer, &x);
	return x.ret;
}
static void S_GenDepthStencilRenderbuffer(void *a) { RBufArgs *x = (RBufArgs*) a; x->ret = R->GenDepthStencilRenderbuffer(RD, x->w, x->h, x->df, x->ms); }
static FNA3D_Renderbuffer* P_GenDepthStencilRenderbuffer(FNA3D_Renderer *driverData, int32_t width, int32_t height, FNA3D_DepthFormat format, int32_t multiSampleCount)
{
	RBufArgs x = { NULL, width, height, multiSampleCount, 0, format, NULL };
	RunSync(S_GenDepthStencilRenderbuffer, &x);
	return x.ret;
}

typedef struct { FNA3D_Buffer *ret; uint8_t dyn; FNA3D_BufferUsage usage; int32_t size; } GBArgs;
static void S_GenVertexBuffer(void *a) { GBArgs *x = (GBArgs*) a; x->ret = R->GenVertexBuffer(RD, x->dyn, x->usage, x->size); }
static FNA3D_Buffer* P_GenVertexBuffer(FNA3D_Renderer *driverData, uint8_t dynamic, FNA3D_BufferUsage usage, int32_t sizeInBytes)
{
	GBArgs x = { NULL, dynamic, usage, sizeInBytes };
	RunSync(S_GenVertexBuffer, &x);
	return x.ret;
}
static void S_GenIndexBuffer(void *a) { GBArgs *x = (GBArgs*) a; x->ret = R->GenIndexBuffer(RD, x->dyn, x->usage, x->size); }
static FNA3D_Buffer* P_GenIndexBuffer(FNA3D_Renderer *driverData, uint8_t dynamic, FNA3D_BufferUsage usage, int32_t sizeInBytes)
{
	GBArgs x = { NULL, dynamic, usage, sizeInBytes };
	RunSync(S_GenIndexBuffer, &x);
	return x.ret;
}

typedef struct { FNA3D_Buffer *b; int32_t off; void *data; int32_t count, elemSize, stride, len; FNA3D_SetDataOptions opt; } BDArgs;
static void S_GetVertexBufferData(void *a) { BDArgs *x = (BDArgs*) a; R->GetVertexBufferData(RD, x->b, x->off, x->data, x->count, x->elemSize, x->stride); }
static void P_GetVertexBufferData(FNA3D_Renderer *driverData, FNA3D_Buffer *buffer, int32_t offsetInBytes, void *data, int32_t elementCount, int32_t elementSizeInBytes, int32_t vertexStride)
{
	BDArgs x = { buffer, offsetInBytes, data, elementCount, elementSizeInBytes, vertexStride, 0, 0 };
	RunSync(S_GetVertexBufferData, &x);
}
static void S_SetIndexBufferData(void *a) { BDArgs *x = (BDArgs*) a; R->SetIndexBufferData(RD, x->b, x->off, x->data, x->len, x->opt); }
static void P_SetIndexBufferData(FNA3D_Renderer *driverData, FNA3D_Buffer *buffer, int32_t offsetInBytes, void *data, int32_t dataLength, FNA3D_SetDataOptions options)
{
	BDArgs x = { buffer, offsetInBytes, data, 0, 0, 0, dataLength, options };
	RunSync(S_SetIndexBufferData, &x);
}
static void S_GetIndexBufferData(void *a) { BDArgs *x = (BDArgs*) a; R->GetIndexBufferData(RD, x->b, x->off, x->data, x->len); }
static void P_GetIndexBufferData(FNA3D_Renderer *driverData, FNA3D_Buffer *buffer, int32_t offsetInBytes, void *data, int32_t dataLength)
{
	BDArgs x = { buffer, offsetInBytes, data, 0, 0, 0, dataLength, 0 };
	RunSync(S_GetIndexBufferData, &x);
}

typedef struct { uint8_t *code; uint32_t len; FNA3D_Effect *src; FNA3D_Effect **effect; MOJOSHADER_effect **result; } CEArgs;
static void S_CreateEffect(void *a) { CEArgs *x = (CEArgs*) a; R->CreateEffect(RD, x->code, x->len, x->effect, x->result); }
static void P_CreateEffect(FNA3D_Renderer *driverData, uint8_t *effectCode, uint32_t effectCodeLength, FNA3D_Effect **effect, MOJOSHADER_effect **result)
{
	CEArgs x = { effectCode, effectCodeLength, NULL, effect, result };
	RunSync(S_CreateEffect, &x);
	AddEffect(*effect, *result);
}
static void S_CloneEffect(void *a) { CEArgs *x = (CEArgs*) a; R->CloneEffect(RD, x->src, x->effect, x->result); }
static void P_CloneEffect(FNA3D_Renderer *driverData, FNA3D_Effect *cloneSource, FNA3D_Effect **effect, MOJOSHADER_effect **result)
{
	CEArgs x = { NULL, 0, cloneSource, effect, result };
	RunSync(S_CloneEffect, &x);
	AddEffect(*effect, *result);
}

typedef struct { FNA3D_Query *q; FNA3D_Query *ret; int32_t iret; uint8_t bret; } QArgs;
static void S_CreateQuery(void *a) { ((QArgs*) a)->ret = R->CreateQuery(RD); }
static FNA3D_Query* P_CreateQuery(FNA3D_Renderer *driverData) { QArgs x = { NULL, NULL, 0, 0 }; RunSync(S_CreateQuery, &x); return x.ret; }
static void S_QueryBegin(void *a) { R->QueryBegin(RD, ((QArgs*) a)->q); }
static void P_QueryBegin(FNA3D_Renderer *driverData, FNA3D_Query *query) { QArgs x = { query, NULL, 0, 0 }; RunSync(S_QueryBegin, &x); }
static void S_QueryEnd(void *a) { R->QueryEnd(RD, ((QArgs*) a)->q); }
static void P_QueryEnd(FNA3D_Renderer *driverData, FNA3D_Query *query) { QArgs x = { query, NULL, 0, 0 }; RunSync(S_QueryEnd, &x); }
static void S_QueryComplete(void *a) { QArgs *x = (QArgs*) a; x->bret = R->QueryComplete(RD, x->q); }
static uint8_t P_QueryComplete(FNA3D_Renderer *driverData, FNA3D_Query *query) { QArgs x = { query, NULL, 0, 0 }; RunSync(S_QueryComplete, &x); return x.bret; }
static void S_QueryPixelCount(void *a) { QArgs *x = (QArgs*) a; x->iret = R->QueryPixelCount(RD, x->q); }
static int32_t P_QueryPixelCount(FNA3D_Renderer *driverData, FNA3D_Query *query) { QArgs x = { query, NULL, 0, 0 }; RunSync(S_QueryPixelCount, &x); return x.iret; }

static uint8_t P_SupportsDXT1(FNA3D_Renderer *driverData) { return proxy->supportsDXT1; }
static uint8_t P_SupportsS3TC(FNA3D_Renderer *driverData) { return proxy->supportsS3TC; }
static uint8_t P_SupportsBC7(FNA3D_Renderer *driverData) { return proxy->supportsBC7; }
static uint8_t P_SupportsHardwareInstancing(FNA3D_Renderer *driverData) { return proxy->supportsInstancing; }
static uint8_t P_SupportsNoOverwrite(FNA3D_Renderer *driverData) { return proxy->supportsNoOverwrite; }
static uint8_t P_SupportsSRGBRenderTargets(FNA3D_Renderer *driverData) { return proxy->supportsSRGB; }
static void P_GetMaxTextureSlots(FNA3D_Renderer *driverData, int32_t *textures, int32_t *vertexTextures)
{
	*textures = proxy->maxTextures;
	*vertexTextures = proxy->maxVertexTextures;
}

typedef struct { FNA3D_SurfaceFormat f; int32_t ms; int32_t ret; } MSArgs;
static void S_GetMaxMultiSampleCount(void *a) { MSArgs *x = (MSArgs*) a; x->ret = R->GetMaxMultiSampleCount(RD, x->f, x->ms); }
static int32_t P_GetMaxMultiSampleCount(FNA3D_Renderer *driverData, FNA3D_SurfaceFormat format, int32_t multiSampleCount)
{
	MSArgs x = { format, multiSampleCount, 0 };
	RunSync(S_GetMaxMultiSampleCount, &x);
	return x.ret;
}

static void P_SetStringMarker(FNA3D_Renderer *driverData, const char *text)
{
	/* Debug markers aren't worth a wait */
}

typedef struct { FNA3D_Texture *t; const char *text; } TNArgs;
static void S_SetTextureName(void *a) { TNArgs *x = (TNArgs*) a; R->SetTextureName(RD, x->t, x->text); }
static void P_SetTextureName(FNA3D_Renderer *driverData, FNA3D_Texture *texture, const char *text)
{
	TNArgs x = { texture, text };
	RunSync(S_SetTextureName, &x);
}

typedef struct { FNA3D_SysRendererEXT *r; FNA3D_SysTextureEXT *t; FNA3D_Texture *ret; } SysArgs;
static void S_GetSysRenderer(void *a) { R->GetSysRenderer(RD, ((SysArgs*) a)->r); }
static void P_GetSysRenderer(FNA3D_Renderer *driverData, FNA3D_SysRendererEXT *renderer) { SysArgs x = { renderer, NULL, NULL }; RunSync(S_GetSysRenderer, &x); }
static void S_CreateSysTexture(void *a) { SysArgs *x = (SysArgs*) a; x->ret = R->CreateSysTexture(RD, x->t); }
static FNA3D_Texture* P_CreateSysTexture(FNA3D_Renderer *driverData, FNA3D_SysTextureEXT *info) { SysArgs x = { NULL, info, NULL }; RunSync(S_CreateSysTexture, &x); return x.ret; }

/* Cached answers, read on the render thread */

static void S_CacheBackbuffer(void *a)
{
	R->GetBackbufferSize(RD, &proxy->backbufferWidth, &proxy->backbufferHeight);
	proxy->backbufferFormat = R->GetBackbufferSurfaceFormat(RD);
	proxy->backbufferDepthFormat = R->GetBackbufferDepthFormat(RD);
	proxy->backbufferMultiSampleCount = R->GetBackbufferMultiSampleCount(RD);
}

static void CacheBackbuffer(void)
{
	RunSync(S_CacheBackbuffer, NULL);
}

static void S_CacheFeatures(void *a)
{
	proxy->supportsDXT1 = R->SupportsDXT1(RD);
	proxy->supportsS3TC = R->SupportsS3TC(RD);
	proxy->supportsBC7 = R->SupportsBC7(RD);
	proxy->supportsInstancing = R->SupportsHardwareInstancing(RD);
	proxy->supportsNoOverwrite = R->SupportsNoOverwrite(RD);
	proxy->supportsSRGB = R->SupportsSRGBRenderTargets(RD);
	R->GetMaxTextureSlots(RD, &proxy->maxTextures, &proxy->maxVertexTextures);
}

/* SpriteBatch flushes (FNA's SpriteBatch.FlushBatch on the Vita): 1 if device is the proxy */
FNA3DAPI uint8_t FNA3D_VitaSpriteBatchAvailable(FNA3D_Device *device)
{
	return proxy != NULL && device == &proxy->device;
}

/* Queues one SpriteBatch flush: count sprites, as FNA's SpriteInfo structs for the sorted modes or as
 * finished vertices (4 per sprite) for Deferred, with each sprite's texture. The caller has applied the
 * batch's render states and effect; the render thread sorts, builds the vertices, uploads them and
 * draws each run of one texture, as SpriteBatch.FlushBatch does. */
FNA3DAPI void FNA3D_VitaSpriteBatch(
	FNA3D_Device *device,
	void *sprites,
	FNA3D_Texture **textures,
	int32_t count,
	int32_t sortMode,
	FNA3D_SamplerState *sampler,
	FNA3D_VertexBufferBinding *binding,
	uint8_t bindingsUpdated,
	FNA3D_Buffer *indices
) {
	uint32_t size;
	uint32_t spriteBytes = (uint32_t) count * ((sortMode == SB_SORT_DEFERRED) ? (uint32_t) (sizeof(VitaVertex) * 4) : (uint32_t) sizeof(VitaSpriteInfo));
	uint32_t samplerBytes = Align8(sizeof(FNA3D_SamplerState));
	uint32_t bindingBytes = Align8(sizeof(FNA3D_VertexBufferBinding));
	uint32_t textureBytes = Align8(sizeof(FNA3D_Texture*) * count);
	uint8_t *p;
	if (count <= 0)
	{
		return;
	}
	p = (uint8_t*) Begin(CMD_SPRITEBATCH, 16 + samplerBytes + bindingBytes + textureBytes + spriteBytes, &size);
	*(int32_t*) p = count;
	*(int32_t*) (p + 4) = sortMode;
	p[8] = bindingsUpdated;
	*(FNA3D_Buffer**) (p + 12) = indices;
	SDL_memcpy(p + 16, sampler, sizeof(FNA3D_SamplerState));
	SDL_memcpy(p + 16 + samplerBytes, binding, sizeof(FNA3D_VertexBufferBinding));
	SDL_memcpy(p + 16 + samplerBytes + bindingBytes, textures, sizeof(FNA3D_Texture*) * count);
	SDL_memcpy(p + 16 + samplerBytes + bindingBytes + textureBytes, sprites, spriteBytes);
	Commit(size);
}

/* Device lifetime */

static void S_DestroyReal(void *a)
{
	R->DestroyDevice(R);
}

static void P_DestroyDevice(FNA3D_Device *device)
{
	RunSync(S_DestroyReal, NULL);
	__atomic_store_n(&proxy->quit, 1, __ATOMIC_RELEASE);
	while (renderThreadId >= 0 && proxy->thread >= 0)
	{
		SceUInt timeout = 100000;
		if (sceKernelWaitThreadEnd(proxy->thread, NULL, &timeout) >= 0)
		{
			break;
		}
	}
	renderThreadId = -1;
	SDL_DestroyMutex(proxy->lock);
	SDL_free(proxy->effects);
	SDL_free(proxy->ring);
	SDL_free(proxy);
	proxy = NULL;
}

typedef struct
{
	const FNA3D_Driver *driver;
	FNA3D_PresentationParameters *pp;
	uint8_t debugMode;
	FNA3D_Device *result;
} CreateArgs;

static void S_Create(void *a)
{
	CreateArgs *x = (CreateArgs*) a;
	x->result = x->driver->CreateDevice(x->pp, x->debugMode);
}

FNA3D_Device* FNA3D_VitaThread_CreateDevice(
	const FNA3D_Driver *driver,
	FNA3D_PresentationParameters *presentationParameters,
	uint8_t debugMode
) {
	SceIoStat stat;
	CreateArgs args;
	FNA3D_Device *d;

	if (	proxy != NULL ||
		sceIoGetstat("ux0:data/StardewValley/vita-gl-same-thread", &stat) >= 0	)
	{
		FNA3D_LogInfo("FNA3D: on the calling thread");
		return driver->CreateDevice(presentationParameters, debugMode);
	}

	proxy = (Proxy*) SDL_calloc(1, sizeof(Proxy));
	proxy->ring = (uint8_t*) SDL_malloc(RING_SIZE);
	proxy->lock = SDL_CreateMutex();
	if (logLock == NULL)
	{
		logLock = SDL_CreateMutex();
	}
	proxy->thread = sceKernelCreateThread(
		"FNA3D render",
		RenderThread,
		0x10000100,
		1024 * 1024,
		0,
		0x20000, /* SCE_KERNEL_CPU_MASK_USER_1: the core the game's main thread doesn't use */
		NULL
	);
	if (proxy->ring == NULL || proxy->lock == NULL || proxy->thread < 0 || sceKernelStartThread(proxy->thread, 0, NULL) < 0)
	{
		FNA3D_LogWarn("FNA3D: no render thread (%08x), on the calling thread", (unsigned) proxy->thread);
		if (proxy->lock != NULL)
		{
			SDL_DestroyMutex(proxy->lock);
		}
		SDL_free(proxy->ring);
		SDL_free(proxy);
		proxy = NULL;
		return driver->CreateDevice(presentationParameters, debugMode);
	}
	while (renderThreadId < 0)
	{
		Wait();
	}

	args.driver = driver;
	args.pp = presentationParameters;
	args.debugMode = debugMode;
	args.result = NULL;
	RunSync(S_Create, &args);
	if (args.result == NULL)
	{
		return NULL;
	}
	proxy->real = args.result;
	RunSync(S_CacheBackbuffer, NULL);
	RunSync(S_CacheFeatures, NULL);

	d = &proxy->device;
	d->driverData = (FNA3D_Renderer*) proxy;
	d->DestroyDevice = P_DestroyDevice;
	d->SwapBuffers = P_SwapBuffers;
	d->Clear = P_Clear;
	d->DrawIndexedPrimitives = P_DrawIndexedPrimitives;
	d->DrawInstancedPrimitives = P_DrawInstancedPrimitives;
	d->DrawPrimitives = P_DrawPrimitives;
	d->SetViewport = P_SetViewport;
	d->SetScissorRect = P_SetScissorRect;
	d->GetBlendFactor = P_GetBlendFactor;
	d->SetBlendFactor = P_SetBlendFactor;
	d->GetMultiSampleMask = P_GetMultiSampleMask;
	d->SetMultiSampleMask = P_SetMultiSampleMask;
	d->GetReferenceStencil = P_GetReferenceStencil;
	d->SetReferenceStencil = P_SetReferenceStencil;
	d->SetBlendState = P_SetBlendState;
	d->SetDepthStencilState = P_SetDepthStencilState;
	d->ApplyRasterizerState = P_ApplyRasterizerState;
	d->VerifySampler = P_VerifySampler;
	d->VerifyVertexSampler = P_VerifyVertexSampler;
	d->ApplyVertexBufferBindings = P_ApplyVertexBufferBindings;
	d->SetRenderTargets = P_SetRenderTargets;
	d->ResolveTarget = P_ResolveTarget;
	d->ResetBackbuffer = P_ResetBackbuffer;
	d->ReadBackbuffer = P_ReadBackbuffer;
	d->GetBackbufferSize = P_GetBackbufferSize;
	d->GetBackbufferSurfaceFormat = P_GetBackbufferSurfaceFormat;
	d->GetBackbufferDepthFormat = P_GetBackbufferDepthFormat;
	d->GetBackbufferMultiSampleCount = P_GetBackbufferMultiSampleCount;
	d->CreateTexture2D = P_CreateTexture2D;
	d->CreateTexture3D = P_CreateTexture3D;
	d->CreateTextureCube = P_CreateTextureCube;
	d->AddDisposeTexture = P_AddDisposeTexture;
	d->SetTextureData2D = P_SetTextureData2D;
	d->SetTextureData3D = P_SetTextureData3D;
	d->SetTextureDataCube = P_SetTextureDataCube;
	d->SetTextureDataYUV = P_SetTextureDataYUV;
	d->GetTextureData2D = P_GetTextureData2D;
	d->GetTextureData3D = P_GetTextureData3D;
	d->GetTextureDataCube = P_GetTextureDataCube;
	d->GenColorRenderbuffer = P_GenColorRenderbuffer;
	d->GenDepthStencilRenderbuffer = P_GenDepthStencilRenderbuffer;
	d->AddDisposeRenderbuffer = P_AddDisposeRenderbuffer;
	d->GenVertexBuffer = P_GenVertexBuffer;
	d->AddDisposeVertexBuffer = P_AddDisposeVertexBuffer;
	d->SetVertexBufferData = P_SetVertexBufferData;
	d->GetVertexBufferData = P_GetVertexBufferData;
	d->GenIndexBuffer = P_GenIndexBuffer;
	d->AddDisposeIndexBuffer = P_AddDisposeIndexBuffer;
	d->SetIndexBufferData = P_SetIndexBufferData;
	d->GetIndexBufferData = P_GetIndexBufferData;
	d->CreateEffect = P_CreateEffect;
	d->CloneEffect = P_CloneEffect;
	d->AddDisposeEffect = P_AddDisposeEffect;
	d->SetEffectTechnique = P_SetEffectTechnique;
	d->ApplyEffect = P_ApplyEffect;
	d->BeginPassRestore = P_BeginPassRestore;
	d->EndPassRestore = P_EndPassRestore;
	d->CreateQuery = P_CreateQuery;
	d->AddDisposeQuery = P_AddDisposeQuery;
	d->QueryBegin = P_QueryBegin;
	d->QueryEnd = P_QueryEnd;
	d->QueryComplete = P_QueryComplete;
	d->QueryPixelCount = P_QueryPixelCount;
	d->SupportsDXT1 = P_SupportsDXT1;
	d->SupportsS3TC = P_SupportsS3TC;
	d->SupportsBC7 = P_SupportsBC7;
	d->SupportsHardwareInstancing = P_SupportsHardwareInstancing;
	d->SupportsNoOverwrite = P_SupportsNoOverwrite;
	d->SupportsSRGBRenderTargets = P_SupportsSRGBRenderTargets;
	d->GetMaxTextureSlots = P_GetMaxTextureSlots;
	d->GetMaxMultiSampleCount = P_GetMaxMultiSampleCount;
	d->SetStringMarker = P_SetStringMarker;
	d->SetTextureName = P_SetTextureName;
	d->GetSysRenderer = P_GetSysRenderer;
	d->CreateSysTexture = P_CreateSysTexture;
	FNA3D_LogInfo("FNA3D: render thread on the second core");
	FlushLogs();
	return d;
}
