/* Vita GL glue for FNA3D's OpenGL driver: SDL_GL_* replacements on top of vitaGL.
 *
 * FNA3D runs its desktop OpenGL 2.1 path (MojoShader glsl120, compiled at runtime by vitaGL through
 * SceShaccCg), so glGetString reports desktop GL 2.1, and the legacy built-in varyings in MojoShader's
 * GLSL are rewritten to explicit ones (vita_glsl_rewrite.c). FNA3D only checks entry points for NULL, so the
 * few that vitaGL lacks get replacements here; optional extensions vitaGL lacks stay NULL, so FNA3D
 * treats them as unsupported.
 *
 * Memory: vitaGL puts textures, buffers and the display surfaces in its own pools, carved out at init.
 * Left alone it takes almost all free MAIN memory (about 211 MB), which the Mono runtime needs, so the
 * pool sizes are explicit and can be changed through SDL hints / environment variables:
 *   FNA3D_VITAGL_RAM_POOL_MB       MAIN (USER_RW) pool, default 32
 *   FNA3D_VITAGL_CDRAM_POOL_MB     CDRAM pool, default: all CDRAM left after the GXM parameter buffer
 *   FNA3D_VITAGL_PHYCONT_POOL_MB   PHYCONT pool, default 16 (clamped to what is free)
 *   FNA3D_VITAGL_CIRCULAR_POOL_MB  per-frame circular pool, default: vitaGL's own default
 *   FNA3D_VITAGL_SHADER_LOG        file to append shader sources and compile/link results to
 */

#include "vita_gl_glue.h"
#include "vita_glsl_rewrite.h"

#include <SDL3/SDL.h>
#include <vitaGL.h>
#include <psp2/gxm.h>
#include <psp2/kernel/processmgr.h>
#include <psp2/kernel/sysmem.h>
#include <stdarg.h>
#include <stdlib.h>
#include <string.h>

/* vitaGL internals: the render target CDRAM reserve (fna-vita/patch-vitagl.py) and its pool lookup. */
extern uint8_t vgl_vram_for_render_target;
extern uint32_t vgl_vram_reserve;
extern vglMemType vgl_mem_get_type_by_addr(void *addr);
/* vitaGL's vsync setting: refreshes each flip waits for, 0 for none (VitaGLGlue_SetVsyncInterval). */
extern uint32_t vsync_interval;
/* The refreshes per frame the game asked for (1: 60 fps, 2: 30 fps), kept while vsync is on. */
static uint32_t vsync_frames = 1;

#ifndef APIENTRY
#define APIENTRY
#endif

#define MB (1024 * 1024)
#define LEGACY_POOL_SIZE (1 * MB) /* immediate mode only; FNA3D never uses it */
/* vitaGL initializes GXM before it creates its pools, and GXM's parameter buffer takes this much
 * CDRAM (SCE_GXM_DEFAULT_PARAMETER_BUFFER_SIZE). A CDRAM pool sized to the CDRAM free before init
 * cannot be allocated, and vitaGL then runs with no CDRAM pool at all. */
#define GXM_PARAMETER_BUFFER_SIZE (16 * MB)
#define CDRAM_ALIGNMENT (256 * 1024)

static int attributes[SDL_GL_EGL_PLATFORM + 1];
static bool initialized;
static int context_token;

static int hint_mb(const char *name, int fallback)
{
	const char *value = SDL_GetHint(name);
	if (value == NULL || value[0] == '\0')
	{
		return fallback;
	}
	return SDL_atoi(value);
}

static void warn_once(bool *warned, const char *message)
{
	if (!*warned)
	{
		*warned = true;
		SDL_LogWarn(SDL_LOG_CATEGORY_RENDER, "VitaGLGlue: %s", message);
	}
}

/* vitaGL reports "OpenGL ES 2.0" / GLSL "1.00 ES", and MojoShader then refuses the glsl120 profile,
 * although vitaGL's GLSL translator accepts "#version 120" shaders. Report desktop GL 2.1 / GLSL 1.20
 * so FNA3D and MojoShader take the path this glue implements.
 */
static const GLubyte *APIENTRY Glue_GetString(GLenum name)
{
	switch (name)
	{
	case GL_VERSION:
		return (const GLubyte *) "2.1 vitaGL";
	case GL_SHADING_LANGUAGE_VERSION:
		return (const GLubyte *) "1.20";
	default:
		return glGetString(name);
	}
}

/* Optional shader log (FNA3D_VITAGL_SHADER_LOG=<path>): every GLSL source MojoShader hands to vitaGL,
 * with compile and link results. vitaGL does not stop at a failed compile; it crashes later in
 * glLinkProgram, so this is how a shader the translator rejects is found.
 */

static const char *shader_log_path;

static void shader_log(const char *text, size_t length)
{
	SDL_IOStream *io;
	if (shader_log_path == NULL)
	{
		return;
	}
	io = SDL_IOFromFile(shader_log_path, "ab");
	if (io != NULL)
	{
		SDL_WriteIO(io, text, length);
		SDL_CloseIO(io);
	}
}

static void shader_logf(const char *fmt, ...)
{
	char line[2048];
	va_list args;
	int length;
	va_start(args, fmt);
	length = SDL_vsnprintf(line, sizeof(line), fmt, args);
	va_end(args);
	if (length > 0)
	{
		shader_log(line, SDL_min((size_t) length, sizeof(line) - 1));
	}
}

/* MojoShader's GLSL goes through VitaGLSL_RewriteLegacyVaryings before vitaGL sees it. */
static void APIENTRY Glue_ShaderSource(GLuint shader, GLsizei count, const GLchar *const *string, const GLint *length)
{
	size_t total = 0, offset = 0;
	char *source, *rewritten;
	const GLchar *final;
	GLsizei i;

	for (i = 0; i < count; i += 1)
	{
		total += (length != NULL && length[i] >= 0) ? (size_t) length[i] : SDL_strlen(string[i]);
	}
	source = (char *) SDL_malloc(total + 1);
	if (source == NULL)
	{
		glShaderSource(shader, count, string, length);
		return;
	}
	for (i = 0; i < count; i += 1)
	{
		size_t part = (length != NULL && length[i] >= 0) ? (size_t) length[i] : SDL_strlen(string[i]);
		SDL_memcpy(source + offset, string[i], part);
		offset += part;
	}
	source[total] = '\0';

	rewritten = VitaGLSL_RewriteLegacyVaryings(source);
	final = rewritten != NULL ? rewritten : source;
	glShaderSource(shader, 1, &final, NULL);
	if (shader_log_path != NULL)
	{
		shader_logf("=== shader %u source%s ===\n", shader, rewritten != NULL ? " (legacy varyings rewritten)" : "");
		shader_log(final, SDL_strlen(final));
		shader_log("\n", 1);
	}
	free(rewritten);
	SDL_free(source);
}

static void APIENTRY Glue_CompileShader(GLuint shader)
{
	GLint ok = 0;
	char info[1024];
	glCompileShader(shader);
	if (shader_log_path == NULL)
	{
		return;
	}
	info[0] = '\0';
	glGetShaderiv(shader, GL_COMPILE_STATUS, &ok);
	glGetShaderInfoLog(shader, sizeof(info) - 1, NULL, info);
	info[sizeof(info) - 1] = '\0';
	shader_logf("=== shader %u compile: %s ===\n%s\n", shader, ok ? "ok" : "FAILED", info);
}

static void APIENTRY Glue_LinkProgram(GLuint program)
{
	GLint ok = 0;
	char info[1024];
	shader_logf("=== program %u link start ===\n", program);
	glLinkProgram(program);
	if (shader_log_path == NULL)
	{
		return;
	}
	info[0] = '\0';
	glGetProgramiv(program, GL_LINK_STATUS, &ok);
	glGetProgramInfoLog(program, sizeof(info) - 1, NULL, info);
	info[sizeof(info) - 1] = '\0';
	shader_logf("=== program %u link: %s ===\n%s\n", program, ok ? "ok" : "FAILED", info);
}

/* Replacements for entry points vitaGL does not have */

static void APIENTRY Glue_BlendColor(GLfloat r, GLfloat g, GLfloat b, GLfloat a)
{
	static bool warned;
	/* vitaGL has no constant blend color; only the default (white) behaves correctly. */
	if (r != 1.0f || g != 1.0f || b != 1.0f || a != 1.0f)
	{
		warn_once(&warned, "glBlendColor unsupported, BlendState.BlendFactor is ignored");
	}
}

static void APIENTRY Glue_DrawBuffers(GLsizei n, const GLenum *bufs)
{
	static bool warned;
	(void) bufs;
	if (n > 1)
	{
		warn_once(&warned, "multiple render targets unsupported, only target 0 is drawn");
	}
}

static void APIENTRY Glue_TexImage3D(GLenum target, GLint level, GLint internalformat, GLsizei width, GLsizei height, GLsizei depth, GLint border, GLenum format, GLenum type, const GLvoid *pixels)
{
	static bool warned;
	warn_once(&warned, "3D textures unsupported");
}

static void APIENTRY Glue_TexSubImage3D(GLenum target, GLint level, GLint xoffset, GLint yoffset, GLint zoffset, GLsizei width, GLsizei height, GLsizei depth, GLenum format, GLenum type, const GLvoid *pixels)
{
	static bool warned;
	warn_once(&warned, "3D textures unsupported");
}

/* Compressed (DXT) textures: FNA3D allocates every level with glCompressedTexImage2D(NULL) and fills
 * it with glCompressedTexSubImage2D, which vitaGL lacks. A whole-level update is the same as
 * redefining the level, so it becomes glCompressedTexImage2D; that needs the level's size, and vitaGL
 * has no glGetTexLevelParameteriv either, so level 0's size is recorded per texture name here.
 */
typedef struct CompressedSize
{
	uint16_t width;
	uint16_t height;
} CompressedSize;

static CompressedSize *compressed_sizes;
static GLuint compressed_sizes_count;

static GLuint bound_texture_2d(void)
{
	GLint texture = 0;
	glGetIntegerv(GL_TEXTURE_BINDING_2D, &texture);
	return (GLuint) texture;
}

static void APIENTRY Glue_CompressedTexImage2D(GLenum target, GLint level, GLenum internalformat, GLsizei width, GLsizei height, GLint border, GLsizei imageSize, const GLvoid *data)
{
	GLuint texture = bound_texture_2d();
	if (target == GL_TEXTURE_2D && level == 0 && texture != 0)
	{
		if (texture >= compressed_sizes_count)
		{
			GLuint count = SDL_max(texture + 1, compressed_sizes_count * 2);
			CompressedSize *grown = (CompressedSize *) SDL_realloc(compressed_sizes, count * sizeof(CompressedSize));
			if (grown != NULL)
			{
				SDL_memset(grown + compressed_sizes_count, 0, (count - compressed_sizes_count) * sizeof(CompressedSize));
				compressed_sizes = grown;
				compressed_sizes_count = count;
			}
		}
		if (texture < compressed_sizes_count)
		{
			compressed_sizes[texture].width = (uint16_t) width;
			compressed_sizes[texture].height = (uint16_t) height;
		}
	}
	glCompressedTexImage2D(target, level, internalformat, width, height, border, imageSize, data);
}

static void APIENTRY Glue_CompressedTexSubImage2D(GLenum target, GLint level, GLint xoffset, GLint yoffset, GLsizei width, GLsizei height, GLenum format, GLsizei imageSize, const GLvoid *data)
{
	static bool warned;
	GLuint texture = bound_texture_2d();
	if (target == GL_TEXTURE_2D && xoffset == 0 && yoffset == 0 && texture < compressed_sizes_count)
	{
		const CompressedSize *size = &compressed_sizes[texture];
		GLsizei levelWidth = SDL_max(size->width >> level, 1);
		GLsizei levelHeight = SDL_max(size->height >> level, 1);
		if (size->width != 0 && width == levelWidth && height == levelHeight)
		{
			glCompressedTexImage2D(target, level, format, width, height, 0, imageSize, data);
			return;
		}
	}
	warn_once(&warned, "glCompressedTexSubImage2D: only whole-level updates are supported, partial update dropped");
}

/* Base vertex: FNA3D enables ARB_draw_elements_base_vertex only when both entry points exist, and
 * vitaGL has glDrawRangeElementsBaseVertex but not the instanced one. Without it FNA3D adds
 * baseVertex * stride to the attribute pointers instead, which vitaGL ignores for an interleaved
 * buffer: every draw read the buffer's first vertices, so once SpriteBatch appended to its buffer
 * (NoOverwrite) most of each frame came out black, flickering or garbled. Hardware instancing is
 * never used by the game; the instanced call only has to exist.
 */
static void APIENTRY Glue_DrawElementsInstancedBaseVertex(GLenum mode, GLsizei count, GLenum type, const GLvoid *indices, GLsizei instancecount, GLint basevertex)
{
	static bool warned;
	if (basevertex != 0)
	{
		warn_once(&warned, "glDrawElementsInstancedBaseVertex: base vertex ignored");
	}
	glDrawElementsInstanced(mode, count, type, indices, instancecount);
}

/* VertexBuffer/IndexBuffer.GetData: read through a read-only mapping. */
static void APIENTRY Glue_GetBufferSubData(GLenum target, GLintptr offset, GLsizeiptr size, GLvoid *data)
{
	static bool warned;
	const uint8_t *mapped = (const uint8_t *) glMapBuffer(target, GL_READ_ONLY);
	if (mapped == NULL)
	{
		warn_once(&warned, "glGetBufferSubData: glMapBuffer failed, returning zeroes");
		SDL_memset(data, 0, (size_t) size);
		return;
	}
	SDL_memcpy(data, mapped + offset, (size_t) size);
	glUnmapBuffer(target);
}

/* Texture2D.GetData: attach the bound texture to a temporary framebuffer and read it back. Rows come
 * back in upload order, as glGetTexImage would return them.
 */
static void APIENTRY Glue_GetTexImage(GLenum target, GLint level, GLenum format, GLenum type, GLvoid *pixels)
{
	static bool warned_target, warned_texture;
	GLint texture = 0, drawFramebuffer = 0, readFramebuffer = 0;
	GLuint framebuffer;
	SceGxmTexture *gxmTexture;

	if (target != GL_TEXTURE_2D || level != 0)
	{
		warn_once(&warned_target, "glGetTexImage: only level 0 of 2D textures is supported");
		return;
	}
	gxmTexture = vglGetGxmTexture(GL_TEXTURE_2D);
	glGetIntegerv(GL_TEXTURE_BINDING_2D, &texture);
	if (gxmTexture == NULL || texture == 0)
	{
		warn_once(&warned_texture, "glGetTexImage: no texture bound");
		return;
	}

	glGetIntegerv(GL_FRAMEBUFFER_BINDING, &drawFramebuffer);
	glGetIntegerv(GL_READ_FRAMEBUFFER_BINDING, &readFramebuffer);
	glGenFramebuffers(1, &framebuffer);
	glBindFramebuffer(GL_FRAMEBUFFER, framebuffer);
	glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, (GLuint) texture, 0);
	glReadPixels(
		0,
		0,
		(GLsizei) sceGxmTextureGetWidth(gxmTexture),
		(GLsizei) sceGxmTextureGetHeight(gxmTexture),
		format,
		type,
		pixels
	);
	glBindFramebuffer(GL_FRAMEBUFFER, (GLuint) drawFramebuffer);
	glBindFramebuffer(GL_READ_FRAMEBUFFER, (GLuint) readFramebuffer);
	glDeleteFramebuffers(1, &framebuffer);
}

static const struct
{
	const char *name;
	SDL_FunctionPointer function;
} replacements[] = {
	{ "glGetString", (SDL_FunctionPointer) Glue_GetString },
	{ "glShaderSource", (SDL_FunctionPointer) Glue_ShaderSource },
	{ "glCompileShader", (SDL_FunctionPointer) Glue_CompileShader },
	{ "glLinkProgram", (SDL_FunctionPointer) Glue_LinkProgram },
	{ "glBlendColor", (SDL_FunctionPointer) Glue_BlendColor },
	{ "glDrawBuffers", (SDL_FunctionPointer) Glue_DrawBuffers },
	{ "glTexImage3D", (SDL_FunctionPointer) Glue_TexImage3D },
	{ "glTexSubImage3D", (SDL_FunctionPointer) Glue_TexSubImage3D },
	{ "glDrawElementsInstancedBaseVertex", (SDL_FunctionPointer) Glue_DrawElementsInstancedBaseVertex },
	{ "glDrawRangeElementsBaseVertex", (SDL_FunctionPointer) glDrawRangeElementsBaseVertex },
	{ "glCompressedTexImage2D", (SDL_FunctionPointer) Glue_CompressedTexImage2D },
	{ "glCompressedTexSubImage2D", (SDL_FunctionPointer) Glue_CompressedTexSubImage2D },
	{ "glGetBufferSubData", (SDL_FunctionPointer) Glue_GetBufferSubData },
	{ "glGetTexImage", (SDL_FunctionPointer) Glue_GetTexImage },
};

/* SDL_GL_* replacements */

bool VitaGLGlue_LoadLibrary(const char *path)
{
	(void) path;
	return true;
}

bool VitaGLGlue_SetAttribute(SDL_GLAttr attr, int value)
{
	if ((int) attr < 0 || (int) attr >= (int) SDL_arraysize(attributes))
	{
		return SDL_SetError("VitaGLGlue: unknown GL attribute %d", (int) attr);
	}
	attributes[attr] = value;
	return true;
}

bool VitaGLGlue_GetAttribute(SDL_GLAttr attr, int *value)
{
	switch (attr)
	{
	case SDL_GL_CONTEXT_PROFILE_MASK:
		/* Desktop GL 2.1 path: glsl120 shaders, glGetTexImage-based GetData. */
		*value = SDL_GL_CONTEXT_PROFILE_COMPATIBILITY;
		return true;
	case SDL_GL_CONTEXT_FLAGS:
	case SDL_GL_MULTISAMPLEBUFFERS:
	case SDL_GL_MULTISAMPLESAMPLES:
		*value = 0;
		return true;
	default:
		if ((int) attr < 0 || (int) attr >= (int) SDL_arraysize(attributes))
		{
			return SDL_SetError("VitaGLGlue: unknown GL attribute %d", (int) attr);
		}
		*value = attributes[attr];
		return true;
	}
}

SDL_GLContext VitaGLGlue_CreateContext(SDL_Window *window)
{
	SceKernelFreeMemorySizeInfo info;
	int width = 960, height = 544;
	int ramPool, cdramPool, cdramLeft, phycontPool, circularPool, fboScenes;

	if (initialized)
	{
		return (SDL_GLContext) &context_token;
	}
	if (window != NULL)
	{
		SDL_GetWindowSizeInPixels(window, &width, &height);
	}

	SDL_zero(info);
	info.size = sizeof(info);
	sceKernelGetFreeMemorySize(&info);
	ramPool = hint_mb("FNA3D_VITAGL_RAM_POOL_MB", 32) * MB;
	cdramLeft = SDL_max(info.size_cdram - GXM_PARAMETER_BUFFER_SIZE, 0) & ~(CDRAM_ALIGNMENT - 1);
	cdramPool = SDL_min(hint_mb("FNA3D_VITAGL_CDRAM_POOL_MB", cdramLeft / MB) * MB, cdramLeft);
	phycontPool = SDL_min(hint_mb("FNA3D_VITAGL_PHYCONT_POOL_MB", 16) * MB, info.size_phycont);
	circularPool = hint_mb("FNA3D_VITAGL_CIRCULAR_POOL_MB", 0) * MB;
	if (circularPool > 0)
	{
		vglSetCircularPoolSize((uint32_t) circularPool);
	}
	/* Each render target texture has a framebuffer (and GXM render target) of its own, and a
	 * frame comes back to the same one a few times: Stardew Valley's world and UI buffers get a
	 * new scene every time the game switches between UI and world drawing. A GXM render target
	 * made for one scene per frame (vitaGL's default) makes a second scene wait for the GPU to
	 * finish the first. The display keeps one scene per frame. */
	fboScenes = SDL_clamp(hint_mb("FNA3D_VITAGL_FBO_SCENES", 4), 1, 8);
	vglSetupRenderTargetScenesNum(1, (uint8_t) fboScenes);

	/* The return value only reports a resolution fallback, not failure. */
	if (vglInitWithCustomSizes(
		LEGACY_POOL_SIZE,
		width,
		height,
		ramPool,
		cdramPool,
		phycontPool,
		0,
		SCE_GXM_MULTISAMPLE_NONE
	)) {
		SDL_LogWarn(SDL_LOG_CATEGORY_RENDER, "VitaGLGlue: %dx%d too large, display size used instead", width, height);
	}
	/* FNA3D's dynamic buffers (GL_STREAM_DRAW) would otherwise live in vitaGL's per-frame scratch
	 * pool, valid for the frame that reserved it. SpriteBatch keeps appending to its vertex buffer
	 * across frames (NoOverwrite, through glMapBufferRange when FNA3D_OPENGL_ALLOW_MAP_UNSYNCHRONIZED
	 * is set), so its storage must persist: regular pool memory, replaced only on Discard. Without
	 * that hint every SpriteBatch flush discards the buffer, and scratch memory suits that best. */
	if (SDL_GetHintBoolean("FNA3D_OPENGL_ALLOW_MAP_UNSYNCHRONIZED", false))
	{
		vglSetupScratchMemory(GL_FALSE, GL_FALSE);
	}
	/* CDRAM kept free for render targets (fna-vita/patch-vitagl.py): the UI buffer and the cached
	 * pictures are recreated on camera changes, and one placed in main RAM made frames 2-3x slower
	 * on the GPU. Other textures take CDRAM above this and go to main RAM below it. */
	vgl_vram_reserve = (uint32_t) hint_mb("FNA3D_VITAGL_RT_RESERVE_MB", 16) * MB;
	initialized = true;

	shader_log_path = SDL_GetHint("FNA3D_VITAGL_SHADER_LOG");
	if (shader_log_path != NULL && shader_log_path[0] == '\0')
	{
		shader_log_path = NULL;
	}
	if (SDL_GetHint("FNA3D_MOJOSHADER_PROFILE") == NULL)
	{
		SDL_SetHint("FNA3D_MOJOSHADER_PROFILE", "glsl120");
	}
	SDL_LogInfo(
		SDL_LOG_CATEGORY_RENDER,
		"VitaGLGlue: %dx%d, pools RAM %u KB, CDRAM %u KB, PHYCONT %u KB, %d scenes per frame for render targets (free before init: user %d KB, cdram %d KB, phycont %d KB)",
		width,
		height,
		(unsigned) (vglMemTotal(VGL_MEM_RAM) / 1024),
		(unsigned) (vglMemTotal(VGL_MEM_VRAM) / 1024),
		(unsigned) (vglMemTotal(VGL_MEM_PHYCONT) / 1024),
		fboScenes,
		info.size_user / 1024,
		info.size_cdram / 1024,
		info.size_phycont / 1024
	);
	return (SDL_GLContext) &context_token;
}

/* How many display refreshes each flip waits for while vsync is on. The game's 30 fps cap sets 2, so
 * every frame stays up for exactly two refreshes; vitaGL's display queue callback reads it at each
 * flip. Called from the game's main thread (vita_port_set_vsync_interval). */
/* What vitaGL's flips wait for right now (0: no vsync), for the game's frames line. */
int VitaGLGlue_GetVsyncInterval(void)
{
	return (int) vsync_interval;
}

void VitaGLGlue_SetVsyncInterval(int interval)
{
	vsync_frames = (uint32_t) SDL_clamp(interval, 1, 4);
	if (vsync_interval != 0)
	{
		vsync_interval = vsync_frames;
	}
}

/* Render target textures (patch-fna3d.py, OPENGL_CreateTexture2D): allocated from the CDRAM
 * reserve while on is set, and where each one went is logged. */
void VitaGLGlue_RenderTargetAlloc(int on)
{
	vgl_vram_for_render_target = on ? GL_TRUE : GL_FALSE;
}

void VitaGLGlue_LogRenderTarget(int width, int height)
{
	static const char *pools[] = { "CDRAM", "RAM", "PHYCONT", "BUDGET", "NEWLIB" };
	int pool = vgl_mem_get_type_by_addr(vglGetTexDataPointer(GL_TEXTURE_2D)) & 0xFF; /* a 1-byte enum in vitaGL */
	SDL_LogInfo(
		SDL_LOG_CATEGORY_RENDER,
		"render target %dx%d in %s (vitaGL free: cdram %u KB, ram %u KB)",
		width,
		height,
		pool < 5 ? pools[pool] : "?",
		(unsigned) (vglMemFree(VGL_MEM_VRAM) / 1024),
		(unsigned) (vglMemFree(VGL_MEM_RAM) / 1024)
	);
}

/* For launchers: vitaGL's memory queries are only valid once the context exists. */
int VitaGLGlue_IsInitialized(void)
{
	return initialized;
}

bool VitaGLGlue_DestroyContext(SDL_GLContext context)
{
	/* vitaGL has no teardown; it lives until the process exits. */
	(void) context;
	return true;
}

SDL_FunctionPointer VitaGLGlue_GetProcAddress(const char *proc)
{
	size_t i;
	for (i = 0; i < SDL_arraysize(replacements); i += 1)
	{
		if (SDL_strcmp(proc, replacements[i].name) == 0)
		{
			return replacements[i].function;
		}
	}
	return (SDL_FunctionPointer) vglGetProcAddress(proc);
}

static uint64_t swap_us_total;
static uint32_t swap_count;

/* For launchers: time spent in vglSwapBuffers (waiting for the GPU and vblank) since the last call. */
void VitaGLGlue_TakeSwapStats(uint64_t *total_us, uint32_t *count)
{
	*total_us = swap_us_total;
	*count = swap_count;
	swap_us_total = 0;
	swap_count = 0;
}

/* The launcher's trophy thread (stardew/main.c): whether its setup dialog is open. Weak, since the test
 * launchers that also link this glue have no trophies. */
extern int vita_port_common_dialog_active(void) __attribute__((weak));

bool VitaGLGlue_SwapWindow(SDL_Window *window)
{
	uint64_t start = sceKernelGetProcessTimeWide();
	/* While text input is active SDL shows the system keyboard (sceImeDialog), a common dialog that
	 * only appears if the app updates it with every frame; vitaGL does that with GL_TRUE. Undrawn,
	 * it still takes the buttons, and the game sees only the sticks. The trophy setup dialog (first
	 * start with the NoTrpDrm plugin) is one too, and never finishes unless it is updated. */
	int dialog = (window != NULL && SDL_TextInputActive(window)) ||
		(vita_port_common_dialog_active != NULL && vita_port_common_dialog_active());
	vglSwapBuffers(dialog ? GL_TRUE : GL_FALSE);
	swap_us_total += sceKernelGetProcessTimeWide() - start;
	swap_count++;
	return true;
}

bool VitaGLGlue_SetSwapInterval(int interval)
{
	/* FNA turns vsync on again on every ApplyChanges (camera changes too): keep the game's frame count. */
	vsync_interval = (interval != 0) ? vsync_frames : 0;
	return true;
}
