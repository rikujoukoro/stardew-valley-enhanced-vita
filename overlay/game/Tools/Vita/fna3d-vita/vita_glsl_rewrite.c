/* GLSL rewrite applied to MojoShader output before vitaGL compiles it (local port glue).
 * Plain C with no SDL/vitaGL dependency, so it can be checked on the host against real MojoShader output.
 */

#include "vita_glsl_rewrite.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct Text
{
	char *data;
	size_t length;
	size_t capacity;
} Text;

static int text_append(Text *text, const char *s, size_t n)
{
	if (text->length + n + 1 > text->capacity)
	{
		size_t capacity = text->capacity ? text->capacity : 1024;
		char *data;
		while (text->length + n + 1 > capacity)
		{
			capacity *= 2;
		}
		data = (char *) realloc(text->data, capacity);
		if (data == NULL)
		{
			return 0;
		}
		text->data = data;
		text->capacity = capacity;
	}
	memcpy(text->data + text->length, s, n);
	text->length += n;
	text->data[text->length] = '\0';
	return 1;
}

static int is_identifier_char(char c)
{
	return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
}

char *VitaGLSL_RewriteLegacyVaryings(const char *source)
{
	static const struct
	{
		const char *from;
		const char *to;
		unsigned colorBit;
	} colors[] = {
		{ "gl_FrontSecondaryColor", "vgl_Color1", 2 },
		{ "gl_SecondaryColor", "vgl_Color1", 2 },
		{ "gl_FrontColor", "vgl_Color0", 1 },
		{ "gl_Color", "vgl_Color0", 1 },
	};
	Text body = { NULL, 0, 0 };
	Text out = { NULL, 0, 0 };
	unsigned texcoordMask = 0, colorMask = 0;
	const char *p = source;
	const char *rest;
	char declaration[48];
	size_t i;
	int ok = 1;

	while (*p != '\0' && ok)
	{
		if (strncmp(p, "gl_", 3) == 0 && (p == source || !is_identifier_char(p[-1])))
		{
			/* MojoShader only emits constant, single-digit gl_TexCoord indices. */
			if (	strncmp(p, "gl_TexCoord[", 12) == 0 &&
				p[12] >= '0' && p[12] <= '9' &&
				p[13] == ']'	)
			{
				char name[16];
				int index = p[12] - '0';
				texcoordMask |= 1u << index;
				snprintf(name, sizeof(name), "vgl_TexCoord%d", index);
				ok = text_append(&body, name, strlen(name));
				p += 14;
				continue;
			}
			for (i = 0; i < sizeof(colors) / sizeof(colors[0]); i += 1)
			{
				size_t length = strlen(colors[i].from);
				if (strncmp(p, colors[i].from, length) == 0 && !is_identifier_char(p[length]))
				{
					colorMask |= colors[i].colorBit;
					ok = text_append(&body, colors[i].to, strlen(colors[i].to));
					p += length;
					break;
				}
			}
			if (i < sizeof(colors) / sizeof(colors[0]))
			{
				continue;
			}
		}
		ok = text_append(&body, p, 1);
		p += 1;
	}

	if (!ok || (texcoordMask == 0 && colorMask == 0))
	{
		free(body.data);
		return NULL;
	}

	/* Declarations go after "#version", which must stay the first line. */
	rest = body.data;
	if (strncmp(rest, "#version", 8) == 0)
	{
		const char *newline = strchr(rest, '\n');
		size_t length = newline ? (size_t) (newline - rest + 1) : strlen(rest);
		ok = text_append(&out, rest, length);
		rest += length;
	}
	for (i = 0; i < 10 && ok; i += 1)
	{
		if (texcoordMask & (1u << i))
		{
			snprintf(declaration, sizeof(declaration), "varying vec4 vgl_TexCoord%u;\n", (unsigned) i);
			ok = text_append(&out, declaration, strlen(declaration));
		}
	}
	if (ok && (colorMask & 1))
	{
		ok = text_append(&out, "varying vec4 vgl_Color0;\n", 25);
	}
	if (ok && (colorMask & 2))
	{
		ok = text_append(&out, "varying vec4 vgl_Color1;\n", 25);
	}
	if (ok)
	{
		ok = text_append(&out, rest, strlen(rest));
	}
	free(body.data);
	if (!ok)
	{
		free(out.data);
		return NULL;
	}
	return out.data;
}
