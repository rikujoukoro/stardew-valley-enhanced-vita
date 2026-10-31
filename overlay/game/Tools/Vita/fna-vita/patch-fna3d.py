#!/usr/bin/env python3
"""Applies the PS Vita port edits to a copy of FNA3D's sources (build-fna3d.sh's work tree).

Usage: patch-fna3d.py <fna3d-source-dir>

Each edit names the file, an anchor that must occur exactly once, and what replaces it. A missing or
ambiguous anchor stops the script; an edit already present is left alone.
"""
import sys
from pathlib import Path

EDITS = []


def edit(path, anchor, replacement, why):
    EDITS.append(dict(path=path, anchor=anchor, replacement=replacement, why=why))


# FNA3D's OpenGL driver renders to every render target through one framebuffer object, attaching
# the target's texture when it changes. vitaGL ties a GXM render target to the framebuffer's size:
# attaching a texture of another size throws the GXM render target away and creates a new one
# (sceGxmCreateRenderTarget allocates and maps driver memory) when the next scene begins. Stardew
# Valley switches between three targets of different sizes several times per frame (the world
# buffer, the UI buffer and the light map), so frames paid for several render target creations,
# and the discarded ones piled up until vitaGL's garbage collector freed them. A render target
# texture with no depth buffer now keeps a framebuffer of its own, attached once.

OPENGL = "src/FNA3D_Driver_OpenGL.c"

edit(OPENGL,
     why="per-texture framebuffer field",
     anchor="""	OpenGLTexture *next; /* linked list */
	uint8_t external;
};
""",
     replacement="""	OpenGLTexture *next; /* linked list */
	uint8_t external;
	GLuint vitaFramebuffer; /* Vita port: framebuffer of its own as a render target, 0 if none */
};
""")

edit(OPENGL,
     why="new textures have no framebuffer yet",
     anchor="""	result->next = NULL;
	result->external = 0;
""",
     replacement="""	result->next = NULL;
	result->external = 0;
	result->vitaFramebuffer = 0;
""")

edit(OPENGL,
     why="bind a single depthless 2D target through its own framebuffer",
     anchor="""	/* Bind the right framebuffer, if needed */
	if (numRenderTargets <= 0)
	{""",
     replacement="""	/* Vita port: a single 2D color target without depth/stencil is drawn through a framebuffer
	 * of its own, so its attachment (and vitaGL's GXM render target) never changes size.
	 * targetFramebuffer and its attachment bookkeeping are left alone for every other case.
	 */
	if (	numRenderTargets == 1 &&
		depthStencilBuffer == NULL &&
		renderTargets[0].colorBuffer == NULL &&
		renderTargets[0].type == FNA3D_RENDERTARGET_TYPE_2D	)
	{
		OpenGLTexture *vitaTarget = (OpenGLTexture*) renderTargets[0].texture;
		if (vitaTarget->vitaFramebuffer == 0)
		{
			renderer->glGenFramebuffers(1, &vitaTarget->vitaFramebuffer);
			BindFramebuffer(renderer, vitaTarget->vitaFramebuffer);
			renderer->glFramebufferTexture2D(
				GL_FRAMEBUFFER,
				GL_COLOR_ATTACHMENT0,
				GL_TEXTURE_2D,
				vitaTarget->handle,
				0
			);
		}
		else
		{
			BindFramebuffer(renderer, vitaTarget->vitaFramebuffer);
		}
		renderer->renderTargetBound = 1;
		ApplySRGBFlag(renderer, vitaTarget->format == FNA3D_SURFACEFORMAT_COLORSRGB_EXT);
		if (renderer->currentDrawBuffers != 1)
		{
			renderer->glDrawBuffers(1, renderer->drawBuffersArray);
			renderer->currentDrawBuffers = 1;
		}
		return;
	}

	/* Bind the right framebuffer, if needed */
	if (numRenderTargets <= 0)
	{""")

edit(OPENGL,
     why="delete a texture's own framebuffer with it",
     anchor="""	if (!texture->external)
	{
		renderer->glDeleteTextures(1, &texture->handle);
	}
	SDL_free(texture);
""",
     replacement="""	if (texture->vitaFramebuffer != 0)
	{
		/* Vita port: deleting a bound framebuffer leaves vitaGL drawing to the display, and the
		 * handle may be handed out again, so the bind cache must forget it.
		 */
		renderer->glDeleteFramebuffers(1, &texture->vitaFramebuffer);
		if (renderer->currentDrawFramebuffer == texture->vitaFramebuffer)
		{
			renderer->currentDrawFramebuffer = 0;
		}
		if (renderer->currentReadFramebuffer == texture->vitaFramebuffer)
		{
			renderer->currentReadFramebuffer = 0;
		}
		texture->vitaFramebuffer = 0;
	}
	if (!texture->external)
	{
		renderer->glDeleteTextures(1, &texture->handle);
	}
	SDL_free(texture);
""")

edit(OPENGL,
     why="a backbuffer reset rebinds the bound target's framebuffer, not targetFramebuffer",
     anchor="""			if (renderer->renderTargetBound)
			{
				renderer->glBindFramebuffer(
					GL_FRAMEBUFFER,
					renderer->targetFramebuffer
				);
			}
""",
     replacement="""			if (renderer->renderTargetBound)
			{
				/* Vita port: the bound target may use its own framebuffer */
				renderer->glBindFramebuffer(
					GL_FRAMEBUFFER,
					renderer->currentDrawFramebuffer
				);
			}
""")


# FNA3D on a render thread of its own (fna3d-vita/FNA3D_VitaThread.c, see there): CreateDevice goes
# through the proxy, and log messages written on the render thread wait for the main thread, since
# FNA's log hooks are managed callbacks.

FNA3DC = "src/FNA3D.c"

edit(FNA3DC,
     why="render thread: log hooks callable from the proxy",
     anchor="""#undef MAX_MESSAGE_SIZE
""",
     replacement="""#undef MAX_MESSAGE_SIZE

/* Vita port: the render thread's messages, passed on by the main thread (FNA3D_VitaThread.c) */
void FNA3D_VitaThread_CallLog(int level, const char *msg)
{
	if (level == 0)
	{
		FNA3D_LogInfoFunc(msg);
	}
	else if (level == 1)
	{
		FNA3D_LogWarnFunc(msg);
	}
	else
	{
		FNA3D_LogErrorFunc(msg);
	}
}
""")

for level, name in ((0, "Info"), (1, "Warn"), (2, "Error")):
    edit(FNA3DC,
         why=f"render thread: Log{name} defers on the render thread",
         anchor=f"""	va_end(ap);
	FNA3D_Log{name}Func(msg);
}}""",
         replacement=f"""	va_end(ap);
	if (!FNA3D_VitaThread_DeferLog({level}, msg))
	{{
		FNA3D_Log{name}Func(msg);
	}}
}}""")

edit(FNA3DC,
     why="render thread: declarations",
     anchor="""static FNA3D_LogFunc FNA3D_LogInfoFunc = FNA3D_Default_LogInfo;""",
     replacement="""extern int FNA3D_VitaThread_DeferLog(int level, const char *msg);
extern FNA3D_Device* FNA3D_VitaThread_CreateDevice(
	const FNA3D_Driver *driver,
	FNA3D_PresentationParameters *presentationParameters,
	uint8_t debugMode
);

static FNA3D_LogFunc FNA3D_LogInfoFunc = FNA3D_Default_LogInfo;""")

edit(FNA3DC,
     why="render thread: CreateDevice through the proxy",
     anchor="""	return drivers[selectedDriver]->CreateDevice(
		presentationParameters,
		debugMode
	);""",
     replacement="""	return FNA3D_VitaThread_CreateDevice(
		drivers[selectedDriver],
		presentationParameters,
		debugMode
	);""")

edit("CMakeLists.txt",
     why="render thread: proxy source",
     anchor="""	src/FNA3D.c
""",
     replacement="""	src/FNA3D.c
	src/FNA3D_VitaThread.c
""")

# Render targets in CDRAM: vitaGL (patch-vitagl.py) keeps a CDRAM reserve for allocations made while
# vgl_vram_for_render_target is set. Each render target's memory is logged (a target in main RAM made
# the frames that use it much slower).
edit(OPENGL,
     why="render targets allocated from the CDRAM reserve",
     anchor="""		glType = XNAToGL_TextureDataType[format];
		for (i = 0; i < levelCount; i += 1)
		{
			renderer->glTexImage2D(
				GL_TEXTURE_2D,
				i,
				glInternalFormat,
				SDL_max(width >> i, 1),
				SDL_max(height >> i, 1),
				0,
				glFormat,
				glType,
				NULL
			);
		}
	}

	return (FNA3D_Texture*) result;
}

static FNA3D_Texture* OPENGL_CreateTexture3D(""",
     replacement="""		extern void VitaGLGlue_RenderTargetAlloc(int on);
		extern void VitaGLGlue_LogRenderTarget(int width, int height);
		glType = XNAToGL_TextureDataType[format];
		VitaGLGlue_RenderTargetAlloc(isRenderTarget);
		for (i = 0; i < levelCount; i += 1)
		{
			renderer->glTexImage2D(
				GL_TEXTURE_2D,
				i,
				glInternalFormat,
				SDL_max(width >> i, 1),
				SDL_max(height >> i, 1),
				0,
				glFormat,
				glType,
				NULL
			);
		}
		VitaGLGlue_RenderTargetAlloc(0);
		if (isRenderTarget)
		{
			VitaGLGlue_LogRenderTarget(width, height);
		}
	}

	return (FNA3D_Texture*) result;
}

static FNA3D_Texture* OPENGL_CreateTexture3D(""")


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
