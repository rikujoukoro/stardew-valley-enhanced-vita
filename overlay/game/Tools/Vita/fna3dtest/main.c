// FNA3D smoke test (milestone 2, step 5): FNA3D's OpenGL driver on vitaGL, the way FNA will drive it.
//
// Creates an FNA3D device on an SDL3 window, loads FNA's SpriteEffect (the effect SpriteBatch uses for
// nearly everything Stardew draws), uploads a 64x64 texture and reads it back with GetTextureData2D,
// then draws a batch of 1024 sprites per frame from a dynamic vertex buffer for 20 seconds (or until
// Start). Logs FNA3D's own messages, free memory and fps to ux0:data/fna3dtest/log.txt.

#include <SDL3/SDL.h>
#include <FNA3D/FNA3D.h>
#include <FNA3D/mojoshader.h>
#include <psp2/ctrl.h>
#include <psp2/kernel/sysmem.h>
#include <psp2/kernel/processmgr.h>
#include <psp2/io/fcntl.h>
#include <psp2/io/stat.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

#define LOG_PATH "ux0:data/fna3dtest/log.txt"
#define WIDTH 960
#define HEIGHT 544
#define TEX 64
#define SPRITES 1024

static void log_line(const char *fmt, ...)
{
	char line[1024];
	va_list args;
	va_start(args, fmt);
	int length = vsnprintf(line, sizeof(line) - 1, fmt, args);
	va_end(args);
	if (length < 0) {
		return;
	}
	if (length > (int)sizeof(line) - 2) {
		length = (int)sizeof(line) - 2;
	}
	line[length++] = '\n';
	printf("%.*s", length, line);
	SceUID fd = sceIoOpen(LOG_PATH, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_APPEND, 0666);
	if (fd >= 0) {
		sceIoWrite(fd, line, (SceSize)length);
		sceIoClose(fd);
	}
}

static void log_free_memory(const char *when)
{
	SceKernelFreeMemorySizeInfo info;
	info.size = sizeof(info);
	if (sceKernelGetFreeMemorySize(&info) == 0) {
		log_line("[%s] free memory: user %d KB, cdram %d KB, phycont %d KB", when,
			info.size_user / 1024, info.size_cdram / 1024, info.size_phycont / 1024);
	}
}

static void FNA3DCALL log_info(const char *msg) { log_line("FNA3D info: %s", msg); }
static void FNA3DCALL log_warn(const char *msg) { log_line("FNA3D warn: %s", msg); }
static void FNA3DCALL log_error(const char *msg) { log_line("FNA3D ERROR: %s", msg); }

static void SDLCALL log_sdl(void *userdata, int category, SDL_LogPriority priority, const char *message)
{
	(void)userdata;
	log_line("SDL[%d/%d]: %s", category, (int)priority, message);
}

// XNA's VertexPositionColorTexture, as SpriteBatch submits it.
typedef struct Vertex {
	float x, y, z;
	uint8_t r, g, b, a;
	float u, v;
} Vertex;

static int set_matrix(MOJOSHADER_effect *effect, const char *name, const float *xna_row_major)
{
	for (int i = 0; i < effect->param_count; i++) {
		MOJOSHADER_effectValue *value = &effect->params[i].value;
		if (value->name != NULL && strcmp(value->name, name) == 0) {
			// Same element order as FNA's EffectParameter.SetValue(Matrix) for a 4x4 parameter.
			float *dst = value->valuesF;
			for (int row = 0; row < 4; row++) {
				for (int col = 0; col < 4; col++) {
					dst[col * 4 + row] = xna_row_major[row * 4 + col];
				}
			}
			return 1;
		}
	}
	return 0;
}

int main(void)
{
	sceIoMkdir("ux0:data/fna3dtest", 0777);
	sceIoRemove(LOG_PATH);
	log_line("fna3dtest start");
	log_free_memory("start");

	SDL_SetLogOutputFunction(log_sdl, NULL);
	SDL_SetHint("FNA3D_FORCE_DRIVER", "OpenGL");
	sceIoRemove("ux0:data/fna3dtest/shaders.txt");
	SDL_SetHint("FNA3D_VITAGL_SHADER_LOG", "ux0:data/fna3dtest/shaders.txt");
	if (!SDL_Init(SDL_INIT_VIDEO)) {
		log_line("SDL_Init failed: %s", SDL_GetError());
		return 1;
	}
	FNA3D_HookLogFunctions(log_info, log_warn, log_error);
	log_line("FNA3D linked version %u", FNA3D_LinkedVersion());

	uint32_t window_flags = FNA3D_PrepareWindowAttributes();
	log_line("window flags from FNA3D: 0x%08X", window_flags);
	SDL_Window *window = SDL_CreateWindow("fna3dtest", WIDTH, HEIGHT, (SDL_WindowFlags)window_flags);
	if (window == NULL) {
		log_line("SDL_CreateWindow failed: %s", SDL_GetError());
		return 1;
	}

	FNA3D_PresentationParameters params;
	memset(&params, 0, sizeof(params));
	params.backBufferWidth = WIDTH;
	params.backBufferHeight = HEIGHT;
	params.backBufferFormat = FNA3D_SURFACEFORMAT_COLOR;
	params.deviceWindowHandle = window;
	params.isFullScreen = 1;
	params.depthStencilFormat = FNA3D_DEPTHFORMAT_NONE;
	params.presentationInterval = FNA3D_PRESENTINTERVAL_ONE;
	params.displayOrientation = FNA3D_DISPLAYORIENTATION_DEFAULT;
	params.renderTargetUsage = FNA3D_RENDERTARGETUSAGE_DISCARDCONTENTS;
	FNA3D_Device *device = FNA3D_CreateDevice(&params, 0);
	if (device == NULL) {
		log_line("FNA3D_CreateDevice failed");
		return 1;
	}
	log_free_memory("after device");

	// SpriteEffect, exactly as FNA ships it.
	size_t fxb_length = 0;
	uint8_t *fxb = SDL_LoadFile("app0:SpriteEffect.fxb", &fxb_length);
	if (fxb == NULL) {
		log_line("SpriteEffect.fxb not found: %s", SDL_GetError());
		return 1;
	}
	FNA3D_Effect *effect = NULL;
	MOJOSHADER_effect *effect_data = NULL;
	FNA3D_CreateEffect(device, fxb, (uint32_t)fxb_length, &effect, &effect_data);
	SDL_free(fxb);
	if (effect == NULL || effect_data == NULL) {
		log_line("FNA3D_CreateEffect failed");
		return 1;
	}
	log_line("SpriteEffect: %d errors, %d params, %d techniques", effect_data->error_count,
		effect_data->param_count, effect_data->technique_count);
	for (int i = 0; i < effect_data->error_count; i++) {
		log_line("  effect error: %s", effect_data->errors[i].error);
	}
	FNA3D_SetEffectTechnique(device, effect, &effect_data->techniques[0]);
	// Pixel-space orthographic projection, same as SpriteBatch with an identity transform.
	const float projection[16] = {
		2.0f / WIDTH, 0.0f, 0.0f, 0.0f,
		0.0f, -2.0f / HEIGHT, 0.0f, 0.0f,
		0.0f, 0.0f, 1.0f, 0.0f,
		-1.0f, 1.0f, 0.0f, 1.0f,
	};
	log_line("MatrixTransform %s", set_matrix(effect_data, "MatrixTransform", projection) ? "set" : "NOT FOUND");
	log_free_memory("after effect");

	// Texture upload and GetData round trip (GetData goes through the glue's glGetTexImage).
	uint8_t *pixels = malloc(TEX * TEX * 4);
	for (int y = 0; y < TEX; y++) {
		for (int x = 0; x < TEX; x++) {
			uint8_t *p = pixels + (y * TEX + x) * 4;
			int checker = ((x / 8) + (y / 8)) & 1;
			p[0] = checker ? 255 : (uint8_t)(x * 4);
			p[1] = checker ? 255 : (uint8_t)(y * 4);
			p[2] = checker ? 255 : 128;
			p[3] = 255;
		}
	}
	FNA3D_Texture *texture = FNA3D_CreateTexture2D(device, FNA3D_SURFACEFORMAT_COLOR, TEX, TEX, 1, 0);
	FNA3D_SetTextureData2D(device, texture, 0, 0, TEX, TEX, 0, pixels, TEX * TEX * 4);
	uint8_t *readback = calloc(TEX * TEX, 4);
	FNA3D_GetTextureData2D(device, texture, 0, 0, TEX, TEX, 0, readback, TEX * TEX * 4);
	int mismatches = 0, first_mismatch = -1;
	for (int i = 0; i < TEX * TEX * 4; i++) {
		if (readback[i] != pixels[i]) {
			if (first_mismatch < 0) {
				first_mismatch = i;
			}
			mismatches++;
		}
	}
	log_line("GetTextureData2D full: %d of %d bytes differ%s", mismatches, TEX * TEX * 4,
		mismatches ? "" : " (exact)");
	if (first_mismatch >= 0) {
		int px = first_mismatch / 4;
		log_line("  first difference at pixel (%d,%d): wrote %02X%02X%02X%02X, read %02X%02X%02X%02X",
			px % TEX, px / TEX,
			pixels[px * 4], pixels[px * 4 + 1], pixels[px * 4 + 2], pixels[px * 4 + 3],
			readback[px * 4], readback[px * 4 + 1], readback[px * 4 + 2], readback[px * 4 + 3]);
		log_line("  row 0 vs last row read: %02X%02X%02X / %02X%02X%02X (flipped if these match the opposite rows)",
			readback[0], readback[1], readback[2],
			readback[(TEX - 1) * TEX * 4], readback[(TEX - 1) * TEX * 4 + 1], readback[(TEX - 1) * TEX * 4 + 2]);
	}
	// A sub-rectangle read, as Stardew does for single sprites.
	uint8_t sub[8 * 8 * 4];
	FNA3D_GetTextureData2D(device, texture, 8, 16, 8, 8, 0, sub, sizeof(sub));
	int sub_mismatches = 0;
	for (int y = 0; y < 8; y++) {
		sub_mismatches += memcmp(sub + y * 8 * 4, pixels + ((16 + y) * TEX + 8) * 4, 8 * 4) != 0;
	}
	log_line("GetTextureData2D 8x8 at (8,16): %d of 8 rows differ", sub_mismatches);
	free(readback);
	free(pixels);

	// Sprite batch buffers: SPRITES quads, 16-bit indices like SpriteBatch.
	FNA3D_Buffer *vertex_buffer = FNA3D_GenVertexBuffer(device, 1, FNA3D_BUFFERUSAGE_WRITEONLY, SPRITES * 4 * sizeof(Vertex));
	FNA3D_Buffer *index_buffer = FNA3D_GenIndexBuffer(device, 0, FNA3D_BUFFERUSAGE_WRITEONLY, SPRITES * 6 * sizeof(uint16_t));
	uint16_t *indices = malloc(SPRITES * 6 * sizeof(uint16_t));
	for (int i = 0; i < SPRITES; i++) {
		uint16_t base = (uint16_t)(i * 4);
		uint16_t *q = indices + i * 6;
		q[0] = base; q[1] = base + 1; q[2] = base + 2;
		q[3] = base + 1; q[4] = base + 3; q[5] = base + 2;
	}
	FNA3D_SetIndexBufferData(device, index_buffer, 0, indices, SPRITES * 6 * sizeof(uint16_t), FNA3D_SETDATAOPTIONS_NONE);
	free(indices);
	Vertex *vertices = malloc(SPRITES * 4 * sizeof(Vertex));

	FNA3D_VertexElement elements[3] = {
		{ 0, FNA3D_VERTEXELEMENTFORMAT_VECTOR3, FNA3D_VERTEXELEMENTUSAGE_POSITION, 0 },
		{ 12, FNA3D_VERTEXELEMENTFORMAT_COLOR, FNA3D_VERTEXELEMENTUSAGE_COLOR, 0 },
		{ 16, FNA3D_VERTEXELEMENTFORMAT_VECTOR2, FNA3D_VERTEXELEMENTUSAGE_TEXTURECOORDINATE, 0 },
	};
	FNA3D_VertexBufferBinding binding;
	memset(&binding, 0, sizeof(binding));
	binding.vertexBuffer = vertex_buffer;
	binding.vertexDeclaration.vertexStride = sizeof(Vertex);
	binding.vertexDeclaration.elementCount = 3;
	binding.vertexDeclaration.elements = elements;

	// BlendState.AlphaBlend, DepthStencilState.None, RasterizerState.CullCounterClockwise, SamplerState.PointClamp.
	FNA3D_BlendState blend;
	memset(&blend, 0, sizeof(blend));
	blend.colorSourceBlend = FNA3D_BLEND_ONE;
	blend.colorDestinationBlend = FNA3D_BLEND_INVERSESOURCEALPHA;
	blend.colorBlendFunction = FNA3D_BLENDFUNCTION_ADD;
	blend.alphaSourceBlend = FNA3D_BLEND_ONE;
	blend.alphaDestinationBlend = FNA3D_BLEND_INVERSESOURCEALPHA;
	blend.alphaBlendFunction = FNA3D_BLENDFUNCTION_ADD;
	blend.colorWriteEnable = blend.colorWriteEnable1 = blend.colorWriteEnable2 = blend.colorWriteEnable3 = FNA3D_COLORWRITECHANNELS_ALL;
	blend.blendFactor.r = blend.blendFactor.g = blend.blendFactor.b = blend.blendFactor.a = 255;
	blend.multiSampleMask = -1;
	FNA3D_DepthStencilState depth_stencil;
	memset(&depth_stencil, 0, sizeof(depth_stencil));
	depth_stencil.depthBufferFunction = FNA3D_COMPAREFUNCTION_LESSEQUAL;
	depth_stencil.stencilMask = depth_stencil.stencilWriteMask = -1;
	depth_stencil.stencilFunction = depth_stencil.ccwStencilFunction = FNA3D_COMPAREFUNCTION_ALWAYS;
	FNA3D_RasterizerState rasterizer;
	memset(&rasterizer, 0, sizeof(rasterizer));
	rasterizer.cullMode = FNA3D_CULLMODE_CULLCOUNTERCLOCKWISEFACE;
	FNA3D_SamplerState sampler;
	memset(&sampler, 0, sizeof(sampler));
	sampler.filter = FNA3D_TEXTUREFILTER_POINT;
	sampler.addressU = sampler.addressV = sampler.addressW = FNA3D_TEXTUREADDRESSMODE_CLAMP;
	sampler.maxAnisotropy = 4;
	FNA3D_Viewport viewport = { 0, 0, WIDTH, HEIGHT, 0.0f, 1.0f };
	FNA3D_Vec4 clear_color = { 0.39f, 0.58f, 0.93f, 1.0f }; // CornflowerBlue
	MOJOSHADER_effectStateChanges state_changes;
	memset(&state_changes, 0, sizeof(state_changes));

	log_free_memory("before loop");
	SceCtrlData pad;
	SceUInt64 start = sceKernelGetProcessTimeWide();
	SceUInt64 last_progress = start;
	unsigned frames = 0;
	for (;;) {
		SceUInt64 now = sceKernelGetProcessTimeWide();
		sceCtrlPeekBufferPositive(0, &pad, 1);
		if ((pad.buttons & SCE_CTRL_START) || now - start > 20000000ULL) {
			break;
		}
		int trace = frames < 2;
		float t = (float)(now - start) / 1000000.0f;

		// 32x32 grid of 24x24 sprites, each orbiting a little, tinted by position.
		for (int i = 0; i < SPRITES; i++) {
			int gx = i % 32, gy = i / 32;
			float x = 20.0f + gx * 29.0f + 6.0f * sinf(t * 2.0f + i * 0.37f);
			float y = 8.0f + gy * 16.5f + 4.0f * cosf(t * 1.5f + i * 0.21f);
			uint8_t r = (uint8_t)(128 + gx * 4), g = (uint8_t)(128 + gy * 4), b = 255;
			Vertex *v = vertices + i * 4;
			Vertex corners[4] = {
				{ x, y, 0, r, g, b, 255, 0.0f, 0.0f },
				{ x + 24, y, 0, r, g, b, 255, 1.0f, 0.0f },
				{ x, y + 24, 0, r, g, b, 255, 0.0f, 1.0f },
				{ x + 24, y + 24, 0, r, g, b, 255, 1.0f, 1.0f },
			};
			memcpy(v, corners, sizeof(corners));
		}

		if (trace) log_line("frame %u: clear", frames);
		FNA3D_SetViewport(device, &viewport);
		FNA3D_Clear(device, FNA3D_CLEAROPTIONS_TARGET, &clear_color, 1.0f, 0);
		if (trace) log_line("frame %u: draw", frames);
		FNA3D_SetBlendState(device, &blend);
		FNA3D_SetDepthStencilState(device, &depth_stencil);
		FNA3D_ApplyRasterizerState(device, &rasterizer);
		FNA3D_SetVertexBufferData(device, vertex_buffer, 0, vertices, SPRITES * 4, sizeof(Vertex), sizeof(Vertex), FNA3D_SETDATAOPTIONS_DISCARD);
		FNA3D_ApplyEffect(device, effect, 0, &state_changes);
		FNA3D_VerifySampler(device, 0, texture, &sampler);
		FNA3D_ApplyVertexBufferBindings(device, &binding, 1, 1, 0);
		FNA3D_DrawIndexedPrimitives(device, FNA3D_PRIMITIVETYPE_TRIANGLELIST, 0, 0, SPRITES * 4, 0, SPRITES * 2,
			index_buffer, FNA3D_INDEXELEMENTSIZE_16BIT);
		if (trace) log_line("frame %u: swap", frames);
		FNA3D_SwapBuffers(device, NULL, NULL, window);
		if (trace) log_line("frame %u: done", frames);
		frames++;
		if (now - last_progress >= 5000000ULL) {
			last_progress = now;
			log_line("alive at %llu ms, %u frames", (now - start) / 1000ULL, frames);
		}
	}
	SceUInt64 elapsed = sceKernelGetProcessTimeWide() - start;
	log_line("frames: %u in %llu ms (%.1f fps, %d sprites per frame)", frames, elapsed / 1000ULL,
		elapsed ? frames * 1000000.0 / elapsed : 0.0, SPRITES);
	log_free_memory("before quit");
	log_line("fna3dtest end");
	sceKernelExitProcess(0);
	return 0;
}
