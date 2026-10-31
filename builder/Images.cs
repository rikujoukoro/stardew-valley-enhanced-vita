// RGBA images for the LiveArea/manual art: read from the player's XNB textures, composed, written as PNG.
using System;
using System.IO;
using System.IO.Compression;
using System.Text;

sealed class Rgba
{
	public readonly int Width;

	public readonly int Height;

	public readonly byte[] Pixels;

	public Rgba(int width, int height, byte[] pixels = null)
	{
		Width = width;
		Height = height;
		Pixels = pixels ?? new byte[width * height * 4];
	}

	// The XNB textures are premultiplied; PNGs are straight alpha.
	public static Rgba FromXnb(byte[] xnb, string fnaPath)
	{
		var (format, width, height, level0) = Xnb.ReadTexture(xnb, fnaPath);
		if (format != 0)
		{
			throw new InvalidDataException("texture format " + format);
		}
		var image = new Rgba(width, height, level0);
		for (int i = 0; i < level0.Length; i += 4)
		{
			int a = level0[i + 3];
			if (a != 0 && a != 255)
			{
				for (int c = 0; c < 3; c++)
				{
					level0[i + c] = (byte)Math.Min(255, (level0[i + c] * 255 + a / 2) / a);
				}
			}
		}
		return image;
	}

	public static Rgba ReadPng(string path)
	{
		// Only what our own assets use: 8-bit RGBA, non-interlaced.
		byte[] f = File.ReadAllBytes(path);
		int pos = 8, w = 0, h = 0;
		var idat = new MemoryStream();
		while (pos < f.Length)
		{
			int len = (f[pos] << 24) | (f[pos + 1] << 16) | (f[pos + 2] << 8) | f[pos + 3];
			string type = Encoding.ASCII.GetString(f, pos + 4, 4);
			if (type == "IHDR")
			{
				w = (f[pos + 8] << 24) | (f[pos + 9] << 16) | (f[pos + 10] << 8) | f[pos + 11];
				h = (f[pos + 12] << 24) | (f[pos + 13] << 16) | (f[pos + 14] << 8) | f[pos + 15];
				if (f[pos + 16] != 8 || f[pos + 17] != 6 || f[pos + 20] != 0)
				{
					throw new InvalidDataException(path + ": not 8-bit RGBA");
				}
			}
			else if (type == "IDAT")
			{
				idat.Write(f, pos + 8, len);
			}
			pos += 12 + len;
		}
		idat.Position = 0;
		var raw = new MemoryStream();
		using (var z = new ZLibStream(idat, CompressionMode.Decompress))
		{
			z.CopyTo(raw);
		}
		byte[] d = raw.ToArray();
		var img = new Rgba(w, h);
		int stride = w * 4;
		var prev = new byte[stride];
		for (int y = 0; y < h; y++)
		{
			int filter = d[y * (stride + 1)];
			var line = new byte[stride];
			Buffer.BlockCopy(d, y * (stride + 1) + 1, line, 0, stride);
			for (int i = 0; i < stride; i++)
			{
				int a = i >= 4 ? line[i - 4] : 0, b = prev[i], c = i >= 4 ? prev[i - 4] : 0;
				int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
				int pred = filter switch
				{
					1 => a,
					2 => b,
					3 => (a + b) / 2,
					4 => pa <= pb && pa <= pc ? a : pb <= pc ? b : c,
					_ => 0
				};
				line[i] = (byte)(line[i] + pred);
			}
			Buffer.BlockCopy(line, 0, img.Pixels, y * stride, stride);
			prev = line;
		}
		return img;
	}

	public void Fill(byte r, byte g, byte b)
	{
		for (int i = 0; i < Pixels.Length; i += 4)
		{
			Pixels[i] = r;
			Pixels[i + 1] = g;
			Pixels[i + 2] = b;
			Pixels[i + 3] = 255;
		}
	}

	// Source rectangle of src drawn at (dx, dy) with nearest-neighbour scale sx/sy (destination pixels per source
	// pixel), alpha-blended like SpriteBatch with straight alpha, optionally faded by opacity.
	public void Draw(Rgba src, int x, int y, int w, int h, double dx, double dy, double sx, double sy, double opacity = 1)
	{
		int x0 = Math.Max(0, (int)Math.Floor(dx)), y0 = Math.Max(0, (int)Math.Floor(dy));
		int x1 = Math.Min(Width, (int)Math.Ceiling(dx + w * sx)), y1 = Math.Min(Height, (int)Math.Ceiling(dy + h * sy));
		for (int py = y0; py < y1; py++)
		{
			int v = (int)Math.Floor((py + 0.5 - dy) / sy);
			if (v < 0 || v >= h)
			{
				continue;
			}
			for (int px = x0; px < x1; px++)
			{
				int u = (int)Math.Floor((px + 0.5 - dx) / sx);
				if (u < 0 || u >= w)
				{
					continue;
				}
				int s = ((y + v) * src.Width + x + u) * 4;
				double a = src.Pixels[s + 3] / 255.0 * opacity;
				if (a <= 0)
				{
					continue;
				}
				int d = (py * Width + px) * 4;
				for (int c = 0; c < 3; c++)
				{
					Pixels[d + c] = (byte)Math.Round(src.Pixels[s + c] * a + Pixels[d + c] * (1 - a));
				}
				Pixels[d + 3] = (byte)Math.Round(255 * (a + Pixels[d + 3] / 255.0 * (1 - a)));
			}
		}
	}

	public void Draw(Rgba src, double dx, double dy, double scale = 1)
	{
		Draw(src, 0, 0, src.Width, src.Height, dx, dy, scale, scale);
	}

	// Smooth (box-filtered) resize, for downscaling photos such as the age label.
	public Rgba ResizeSmooth(int w, int h)
	{
		var r = new Rgba(w, h);
		double fx = (double)Width / w, fy = (double)Height / h;
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				double sa = 0, sr = 0, sg = 0, sb = 0, n = 0;
				for (int v = (int)(y * fy); v < Math.Min(Height, (int)Math.Ceiling((y + 1) * fy)); v++)
				{
					for (int u = (int)(x * fx); u < Math.Min(Width, (int)Math.Ceiling((x + 1) * fx)); u++)
					{
						int s = (v * Width + u) * 4;
						double a = Pixels[s + 3];
						sr += Pixels[s] * a;
						sg += Pixels[s + 1] * a;
						sb += Pixels[s + 2] * a;
						sa += a;
						n++;
					}
				}
				int d = (y * w + x) * 4;
				if (sa > 0)
				{
					r.Pixels[d] = (byte)Math.Round(sr / sa);
					r.Pixels[d + 1] = (byte)Math.Round(sg / sa);
					r.Pixels[d + 2] = (byte)Math.Round(sb / sa);
				}
				r.Pixels[d + 3] = (byte)Math.Round(sa / n);
			}
		}
		return r;
	}

	public Rgba Crop(int x, int y, int w, int h)
	{
		var r = new Rgba(w, h);
		for (int row = 0; row < h; row++)
		{
			Buffer.BlockCopy(Pixels, ((y + row) * Width + x) * 4, r.Pixels, row * w * 4, w * 4);
		}
		return r;
	}

	public void WritePng(string path)
	{
		File.WriteAllBytes(path, PngBytes());
	}

	public byte[] PngBytes()
	{
		var file = new MemoryStream();
		file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
		var ihdr = new MemoryStream();
		WriteBe(ihdr, Width);
		WriteBe(ihdr, Height);
		ihdr.Write(new byte[] { 8, 6, 0, 0, 0 });
		Chunk(file, "IHDR", ihdr.ToArray());
		var raw = new MemoryStream();
		using (var z = new ZLibStream(raw, CompressionLevel.SmallestSize, true))
		{
			for (int row = 0; row < Height; row++)
			{
				z.WriteByte(0);
				z.Write(Pixels, row * Width * 4, Width * 4);
			}
		}
		Chunk(file, "IDAT", raw.ToArray());
		Chunk(file, "IEND", Array.Empty<byte>());
		return file.ToArray();
	}

	// 8-bit palette PNG (LiveArea, icon and boot images must be indexed). Opaque images; more than 256 colors are
	// reduced by median cut, each pixel then taking its nearest palette color.
	public void WritePng8(string path)
	{
		var counts = new System.Collections.Generic.Dictionary<int, int>();
		for (int i = 0; i < Pixels.Length; i += 4)
		{
			int key = (Pixels[i] << 16) | (Pixels[i + 1] << 8) | Pixels[i + 2];
			counts[key] = counts.TryGetValue(key, out int c) ? c + 1 : 1;
		}
		var colors = new System.Collections.Generic.List<int>(counts.Keys);
		var palette = new System.Collections.Generic.List<int>();
		if (colors.Count <= 256)
		{
			palette.AddRange(colors);
		}
		else
		{
			var boxes = new System.Collections.Generic.List<System.Collections.Generic.List<int>> { colors };
			while (boxes.Count < 256)
			{
				int bi = -1, bestRange = -1, bestCh = 0;
				for (int b = 0; b < boxes.Count; b++)
				{
					if (boxes[b].Count < 2)
					{
						continue;
					}
					for (int ch = 0; ch < 3; ch++)
					{
						int sh = 16 - ch * 8, mn = 255, mx = 0;
						foreach (int c in boxes[b])
						{
							int v = (c >> sh) & 255;
							mn = Math.Min(mn, v);
							mx = Math.Max(mx, v);
						}
						if (mx - mn > bestRange)
						{
							bestRange = mx - mn;
							bi = b;
							bestCh = sh;
						}
					}
				}
				if (bi < 0)
				{
					break;
				}
				var box = boxes[bi];
				int shift = bestCh;
				box.Sort((p, q) => (((p >> shift) & 255) - ((q >> shift) & 255)) != 0 ? ((p >> shift) & 255) - ((q >> shift) & 255) : p - q);
				long total = 0;
				foreach (int c in box)
				{
					total += counts[c];
				}
				long acc = 0;
				int cut = 1;
				for (; cut < box.Count - 1; cut++)
				{
					acc += counts[box[cut - 1]];
					if (acc * 2 >= total)
					{
						break;
					}
				}
				boxes[bi] = box.GetRange(0, cut);
				boxes.Add(box.GetRange(cut, box.Count - cut));
			}
			foreach (var box in boxes)
			{
				long r = 0, g = 0, bl = 0, n = 0;
				foreach (int c in box)
				{
					int k = counts[c];
					r += ((c >> 16) & 255) * k;
					g += ((c >> 8) & 255) * k;
					bl += (c & 255) * k;
					n += k;
				}
				palette.Add((int)((r + n / 2) / n) << 16 | (int)((g + n / 2) / n) << 8 | (int)((bl + n / 2) / n));
			}
		}
		var index = new System.Collections.Generic.Dictionary<int, byte>();
		foreach (int c in colors)
		{
			int best = 0, bestD = int.MaxValue;
			for (int p = 0; p < palette.Count; p++)
			{
				int dr = ((c >> 16) & 255) - ((palette[p] >> 16) & 255), dg = ((c >> 8) & 255) - ((palette[p] >> 8) & 255), db = (c & 255) - (palette[p] & 255);
				int d = dr * dr + dg * dg + db * db;
				if (d < bestD)
				{
					bestD = d;
					best = p;
				}
			}
			index[c] = (byte)best;
		}
		using var file = File.Create(path);
		file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
		var ihdr = new MemoryStream();
		WriteBe(ihdr, Width);
		WriteBe(ihdr, Height);
		ihdr.Write(new byte[] { 8, 3, 0, 0, 0 });
		Chunk(file, "IHDR", ihdr.ToArray());
		var plte = new byte[palette.Count * 3];
		for (int p = 0; p < palette.Count; p++)
		{
			plte[p * 3] = (byte)(palette[p] >> 16);
			plte[p * 3 + 1] = (byte)(palette[p] >> 8);
			plte[p * 3 + 2] = (byte)palette[p];
		}
		Chunk(file, "PLTE", plte);
		var raw = new MemoryStream();
		using (var z = new ZLibStream(raw, CompressionLevel.SmallestSize, true))
		{
			for (int row = 0; row < Height; row++)
			{
				z.WriteByte(0);
				for (int x = 0; x < Width; x++)
				{
					int i = (row * Width + x) * 4;
					z.WriteByte(index[(Pixels[i] << 16) | (Pixels[i + 1] << 8) | Pixels[i + 2]]);
				}
			}
		}
		Chunk(file, "IDAT", raw.ToArray());
		Chunk(file, "IEND", Array.Empty<byte>());
	}

	static void WriteBe(Stream s, int v)
	{
		s.WriteByte((byte)(v >> 24));
		s.WriteByte((byte)(v >> 16));
		s.WriteByte((byte)(v >> 8));
		s.WriteByte((byte)v);
	}

	static void Chunk(Stream s, string type, byte[] data)
	{
		WriteBe(s, data.Length);
		byte[] t = Encoding.ASCII.GetBytes(type);
		s.Write(t);
		s.Write(data);
		uint crc = Crc(Crc(0xFFFFFFFF, t), data) ^ 0xFFFFFFFF;
		WriteBe(s, (int)crc);
	}

	static uint[] crcTable;

	static uint Crc(uint crc, byte[] data)
	{
		if (crcTable == null)
		{
			crcTable = new uint[256];
			for (uint n = 0; n < 256; n++)
			{
				uint c = n;
				for (int k = 0; k < 8; k++)
				{
					c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
				}
				crcTable[n] = c;
			}
		}
		foreach (byte b in data)
		{
			crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
		}
		return crc;
	}
}

static class Xnb
{
	// Format, size and first mip of a Texture2D XNB with no shared resources.
	public static (int Format, int Width, int Height, byte[] Level0) ReadTexture(byte[] xnb, string fnaPath)
	{
		var (body, texStart) = TextureBody(xnb, fnaPath);
		using var r = new BinaryReader(new MemoryStream(body, texStart, body.Length - texStart));
		int format = r.ReadInt32(), width = r.ReadInt32(), height = r.ReadInt32();
		r.ReadInt32();
		return (format, width, height, r.ReadBytes(r.ReadInt32()));
	}

	public static (byte[] Body, int TextureStart) TextureBody(byte[] xnb, string fnaPath)
	{
		byte[] body = Program.XnbBody(xnb, fnaPath);
		using var ms = new MemoryStream(body);
		using var r = new BinaryReader(ms);
		int readers = Program.Read7BitInt(r);
		var names = new string[readers];
		for (int i = 0; i < readers; i++)
		{
			names[i] = Encoding.UTF8.GetString(r.ReadBytes(Program.Read7BitInt(r)));
			r.ReadInt32();
		}
		int shared = Program.Read7BitInt(r);
		int root = Program.Read7BitInt(r);
		if (shared != 0 || root < 1 || root > readers || !names[root - 1].Contains("Texture2DReader"))
		{
			throw new InvalidDataException("not a Texture2D");
		}
		return (body, (int)ms.Position);
	}
}
