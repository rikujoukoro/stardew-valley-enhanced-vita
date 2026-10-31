// BC3 (DXT5) block encoder: a C# port of stb_dxt.h v1.12 (fabian "ryg" giesen, stb), STB_DXT_HIGHQUAL, no rounding
// bias, no dithering. Ported line for line, float/double where the C code uses them, so the output is the same as
// the C encoder the texture prebake was made with (checked against the resulting Content.pak's SHA-256).
//
// stb_dxt.h is available under either the MIT license or the public domain (unlicense.org):
// Copyright (c) 2017 Sean Barrett
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
// documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to the following conditions: The above copyright
// notice and this permission notice shall be included in all copies or substantial portions of the Software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
// WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
// COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
using System;

static partial class StbDxt
{
	// Encodes RGBA pixels (width and height multiples of 4, rows top to bottom) as BC3, 16 bytes per block.
	public static byte[] EncodeBc3(byte[] rgba, int width, int height)
	{
		var output = new byte[width / 4 * (height / 4) * 16];
		var block = new byte[64];
		var coded = new byte[16];
		int o = 0;
		for (int y = 0; y < height; y += 4)
		{
			for (int x = 0; x < width; x += 4)
			{
				for (int r = 0; r < 4; r++)
				{
					Buffer.BlockCopy(rgba, ((y + r) * width + x) * 4, block, r * 16, 16);
				}
				CompressDxtBlock(coded, block);
				Buffer.BlockCopy(coded, 0, output, o, 16);
				o += 16;
			}
		}
		return output;
	}

	static int Mul8Bit(int a, int b)
	{
		int t = a * b + 128;
		return (t + (t >> 8)) >> 8;
	}

	static void From16Bit(byte[] output, int at, ushort v)
	{
		int rv = (v & 0xf800) >> 11;
		int gv = (v & 0x07e0) >> 5;
		int bv = v & 0x001f;
		output[at + 0] = (byte)((rv * 33) >> 2);
		output[at + 1] = (byte)((gv * 65) >> 4);
		output[at + 2] = (byte)((bv * 33) >> 2);
		output[at + 3] = 0;
	}

	static ushort As16Bit(int r, int g, int b)
	{
		return (ushort)((Mul8Bit(r, 31) << 11) + (Mul8Bit(g, 63) << 5) + Mul8Bit(b, 31));
	}

	static int Lerp13(int a, int b)
	{
		return (2 * a + b) / 3;
	}

	static void Lerp13Rgb(byte[] c, int at, int p1, int p2)
	{
		c[at + 0] = (byte)Lerp13(c[p1 + 0], c[p2 + 0]);
		c[at + 1] = (byte)Lerp13(c[p1 + 1], c[p2 + 1]);
		c[at + 2] = (byte)Lerp13(c[p1 + 2], c[p2 + 2]);
	}

	static void EvalColors(byte[] color, ushort c0, ushort c1)
	{
		From16Bit(color, 0, c0);
		From16Bit(color, 4, c1);
		Lerp13Rgb(color, 8, 0, 4);
		Lerp13Rgb(color, 12, 4, 0);
	}

	static uint MatchColorsBlock(byte[] block, byte[] color)
	{
		uint mask = 0;
		int dirr = color[0] - color[4];
		int dirg = color[1] - color[5];
		int dirb = color[2] - color[6];
		var dots = new int[16];
		var stops = new int[4];
		for (int i = 0; i < 16; i++)
		{
			dots[i] = block[i * 4 + 0] * dirr + block[i * 4 + 1] * dirg + block[i * 4 + 2] * dirb;
		}
		for (int i = 0; i < 4; i++)
		{
			stops[i] = color[i * 4 + 0] * dirr + color[i * 4 + 1] * dirg + color[i * 4 + 2] * dirb;
		}
		int c0Point = stops[1] + stops[3];
		int halfPoint = stops[3] + stops[2];
		int c3Point = stops[2] + stops[0];
		for (int i = 15; i >= 0; i--)
		{
			int dot = dots[i] * 2;
			mask <<= 2;
			if (dot < halfPoint)
			{
				mask |= (dot < c0Point) ? 1u : 3u;
			}
			else
			{
				mask |= (dot < c3Point) ? 2u : 0u;
			}
		}
		return mask;
	}

	static void OptimizeColorsBlock(byte[] block, out ushort max16, out ushort min16)
	{
		const int nIterPower = 4;
		var mu = new int[3];
		var min = new int[3];
		var max = new int[3];
		for (int ch = 0; ch < 3; ch++)
		{
			int muv, minv, maxv;
			muv = minv = maxv = block[ch];
			for (int i = 4; i < 64; i += 4)
			{
				int v = block[i + ch];
				muv += v;
				if (v < minv)
				{
					minv = v;
				}
				else if (v > maxv)
				{
					maxv = v;
				}
			}
			mu[ch] = (muv + 8) >> 4;
			min[ch] = minv;
			max[ch] = maxv;
		}
		var cov = new int[6];
		for (int i = 0; i < 16; i++)
		{
			int r = block[i * 4 + 0] - mu[0];
			int g = block[i * 4 + 1] - mu[1];
			int b = block[i * 4 + 2] - mu[2];
			cov[0] += r * r;
			cov[1] += r * g;
			cov[2] += r * b;
			cov[3] += g * g;
			cov[4] += g * b;
			cov[5] += b * b;
		}
		var covf = new float[6];
		for (int i = 0; i < 6; i++)
		{
			covf[i] = cov[i] / 255.0f;
		}
		float vfr = max[0] - min[0];
		float vfg = max[1] - min[1];
		float vfb = max[2] - min[2];
		for (int iter = 0; iter < nIterPower; iter++)
		{
			float r = vfr * covf[0] + vfg * covf[1] + vfb * covf[2];
			float g = vfr * covf[1] + vfg * covf[3] + vfb * covf[4];
			float b = vfr * covf[2] + vfg * covf[4] + vfb * covf[5];
			vfr = r;
			vfg = g;
			vfb = b;
		}
		double magn = Math.Abs((double)vfr);
		if (Math.Abs((double)vfg) > magn)
		{
			magn = Math.Abs((double)vfg);
		}
		if (Math.Abs((double)vfb) > magn)
		{
			magn = Math.Abs((double)vfb);
		}
		int vr, vg, vb;
		if (magn < 4.0f)
		{
			vr = 299;
			vg = 587;
			vb = 114;
		}
		else
		{
			magn = 512.0 / magn;
			vr = (int)(vfr * magn);
			vg = (int)(vfg * magn);
			vb = (int)(vfb * magn);
		}
		int minp = 0, maxp = 0;
		int mind, maxd;
		mind = maxd = block[0] * vr + block[1] * vg + block[2] * vb;
		for (int i = 1; i < 16; i++)
		{
			int dot = block[i * 4 + 0] * vr + block[i * 4 + 1] * vg + block[i * 4 + 2] * vb;
			if (dot < mind)
			{
				mind = dot;
				minp = i * 4;
			}
			if (dot > maxd)
			{
				maxd = dot;
				maxp = i * 4;
			}
		}
		max16 = As16Bit(block[maxp], block[maxp + 1], block[maxp + 2]);
		min16 = As16Bit(block[minp], block[minp + 1], block[minp + 2]);
	}

	static ushort Quantize5(float x)
	{
		x = x < 0 ? 0 : x > 1 ? 1 : x;
		ushort q = (ushort)(x * 31);
		if (x > Midpoints5[q])
		{
			q++;
		}
		return q;
	}

	static ushort Quantize6(float x)
	{
		x = x < 0 ? 0 : x > 1 ? 1 : x;
		ushort q = (ushort)(x * 63);
		if (x > Midpoints6[q])
		{
			q++;
		}
		return q;
	}

	static readonly int[] W1Tab = { 3, 0, 2, 1 };

	static readonly int[] Prods = { 0x090000, 0x000900, 0x040102, 0x010402 };

	static bool RefineBlock(byte[] block, ref ushort pmax16, ref ushort pmin16, uint mask)
	{
		ushort oldMin = pmin16;
		ushort oldMax = pmax16;
		ushort min16, max16;
		if ((mask ^ (mask << 2)) < 4)
		{
			int r = 8, g = 8, b = 8;
			for (int i = 0; i < 16; i++)
			{
				r += block[i * 4 + 0];
				g += block[i * 4 + 1];
				b += block[i * 4 + 2];
			}
			r >>= 4;
			g >>= 4;
			b >>= 4;
			max16 = (ushort)((OMatch5[r * 2] << 11) | (OMatch6[g * 2] << 5) | OMatch5[b * 2]);
			min16 = (ushort)((OMatch5[r * 2 + 1] << 11) | (OMatch6[g * 2 + 1] << 5) | OMatch5[b * 2 + 1]);
		}
		else
		{
			int akku = 0;
			int at1R = 0, at1G = 0, at1B = 0;
			int at2R = 0, at2G = 0, at2B = 0;
			uint cm = mask;
			for (int i = 0; i < 16; i++, cm >>= 2)
			{
				int step = (int)(cm & 3);
				int w1 = W1Tab[step];
				int r = block[i * 4 + 0];
				int g = block[i * 4 + 1];
				int b = block[i * 4 + 2];
				akku += Prods[step];
				at1R += w1 * r;
				at1G += w1 * g;
				at1B += w1 * b;
				at2R += r;
				at2G += g;
				at2B += b;
			}
			at2R = 3 * at2R - at1R;
			at2G = 3 * at2G - at1G;
			at2B = 3 * at2B - at1B;
			int xx = akku >> 16;
			int yy = (akku >> 8) & 0xff;
			int xy = akku & 0xff;
			float f = 3.0f / 255.0f / (xx * yy - xy * xy);
			max16 = (ushort)(Quantize5((at1R * yy - at2R * xy) * f) << 11);
			max16 |= (ushort)(Quantize6((at1G * yy - at2G * xy) * f) << 5);
			max16 |= Quantize5((at1B * yy - at2B * xy) * f);
			min16 = (ushort)(Quantize5((at2R * xx - at1R * xy) * f) << 11);
			min16 |= (ushort)(Quantize6((at2G * xx - at1G * xy) * f) << 5);
			min16 |= Quantize5((at2B * xx - at1B * xy) * f);
		}
		pmin16 = min16;
		pmax16 = max16;
		return oldMin != min16 || oldMax != max16;
	}

	static void CompressColorBlock(byte[] dest, int at, byte[] block)
	{
		const int refinecount = 2;
		uint mask;
		ushort max16, min16;
		var color = new byte[16];
		int i;
		for (i = 1; i < 16; i++)
		{
			if (BitConverter.ToUInt32(block, i * 4) != BitConverter.ToUInt32(block, 0))
			{
				break;
			}
		}
		if (i == 16)
		{
			int r = block[0], g = block[1], b = block[2];
			mask = 0xaaaaaaaa;
			max16 = (ushort)((OMatch5[r * 2] << 11) | (OMatch6[g * 2] << 5) | OMatch5[b * 2]);
			min16 = (ushort)((OMatch5[r * 2 + 1] << 11) | (OMatch6[g * 2 + 1] << 5) | OMatch5[b * 2 + 1]);
		}
		else
		{
			OptimizeColorsBlock(block, out max16, out min16);
			if (max16 != min16)
			{
				EvalColors(color, max16, min16);
				mask = MatchColorsBlock(block, color);
			}
			else
			{
				mask = 0;
			}
			for (i = 0; i < refinecount; i++)
			{
				uint lastmask = mask;
				if (RefineBlock(block, ref max16, ref min16, mask))
				{
					if (max16 != min16)
					{
						EvalColors(color, max16, min16);
						mask = MatchColorsBlock(block, color);
					}
					else
					{
						mask = 0;
						break;
					}
				}
				if (mask == lastmask)
				{
					break;
				}
			}
		}
		if (max16 < min16)
		{
			(max16, min16) = (min16, max16);
			mask ^= 0x55555555;
		}
		dest[at + 0] = (byte)max16;
		dest[at + 1] = (byte)(max16 >> 8);
		dest[at + 2] = (byte)min16;
		dest[at + 3] = (byte)(min16 >> 8);
		dest[at + 4] = (byte)mask;
		dest[at + 5] = (byte)(mask >> 8);
		dest[at + 6] = (byte)(mask >> 16);
		dest[at + 7] = (byte)(mask >> 24);
	}

	static void CompressAlphaBlock(byte[] dest, byte[] src, int offset, int stride)
	{
		int mn, mx;
		mn = mx = src[offset];
		for (int i = 1; i < 16; i++)
		{
			int v = src[offset + i * stride];
			if (v < mn)
			{
				mn = v;
			}
			else if (v > mx)
			{
				mx = v;
			}
		}
		dest[0] = (byte)mx;
		dest[1] = (byte)mn;
		int d = 2;
		int dist = mx - mn;
		int dist4 = dist * 4;
		int dist2 = dist * 2;
		int bias = (dist < 8) ? (dist - 1) : (dist / 2 + 2);
		bias -= mn * 7;
		int bits = 0, mask = 0;
		for (int i = 0; i < 16; i++)
		{
			int a = src[offset + i * stride] * 7 + bias;
			int ind, t;
			t = (a >= dist4) ? -1 : 0;
			ind = t & 4;
			a -= dist4 & t;
			t = (a >= dist2) ? -1 : 0;
			ind += t & 2;
			a -= dist2 & t;
			ind += (a >= dist) ? 1 : 0;
			ind = -ind & 7;
			ind ^= (2 > ind) ? 1 : 0;
			mask |= ind << bits;
			if ((bits += 3) >= 8)
			{
				dest[d++] = (byte)mask;
				mask >>= 8;
				bits -= 8;
			}
		}
	}

	static void CompressDxtBlock(byte[] dest, byte[] src)
	{
		CompressAlphaBlock(dest, src, 3, 4);
		var data = (byte[])src.Clone();
		for (int i = 0; i < 16; i++)
		{
			data[i * 4 + 3] = 255;
		}
		CompressColorBlock(dest, 8, data);
	}
}
