// The VPK's sce_sys art (boot picture, icon, LiveArea, manual), drawn from the player's own Content like the title
// screen draws itself (TitleMenu.draw: sky strip, night sky, clouds, two mountain layers, bushes, logo), plus our own
// parts from assets/sce_sys-parts (ENHANCED label, "Ported by", manual pages) and the PEGI 12 label (downloaded).
using System;
using System.IO;

static class LiveArea
{
	sealed class Textures
	{
		public Rgba Cursors, Clouds, TitleButtons;
	}

	// TitleMenu.draw's background at zoom z (pixels per texel) and scroll viewportY (negative: looking up at the
	// night sky, 0: the day view the menu settles on). Cloud positions are fixed (the game randomizes them).
	// skyViewportY scrolls the sky layers alone (the boot picture: night sky over the day view's mountains).
	static Rgba Scene(Textures t, int w, int h, int z, double viewportY, double? skyViewportY = null, bool clouds = true)
	{
		var img = new Rgba(w, h);
		img.Fill(64, 136, 248);
		double sky = skyViewportY ?? viewportY;
		double stripTop = -300 * z - sky * 0.66;
		if (stripTop > 0)
		{
			// Scrolled down past its top (the boot picture): its first row fills the rest, instead of the blue backdrop.
			img.Draw(t.Cursors, 703, 1912, 1, 1, 0, 0, w, Math.Ceiling(stripTop) + 1);
		}
		img.Draw(t.Cursors, 703, 1912, 1, 264, 0, stripTop, w, (300 * z + h - 120 * z) / 264.0);
		for (int x = -10; x < w; x += 638)
		{
			img.Draw(t.Cursors, 0, 1453, 638, 195, x * z, -360 * z - sky * 0.66, z, z);
		}
		if (clouds)
		{
			img.Draw(t.Clouds, 0, 0, 512, 337, w * 0.55, h - 250 * z - viewportY * 0.5, z, z);
		}
		img.Draw(t.Cursors, 0, 886, 639, 148, -30 * z, h - 158 * z - viewportY * 0.66, z, z);
		img.Draw(t.Cursors, 0, 886, 640, 148, -30 * z + 639 * z, h - 158 * z - viewportY * 0.66, z, z);
		var small = new (int X, int Y, int W, int H)[] { (152, 447, 123, 55), (0, 471, 149, 66), (410, 467, 63, 37) };
		double[] smallX = { w * 0.08, w * 0.62, w * 0.35 };
		for (int i = 0; i < (clouds ? 3 : 0); i++)
		{
			img.Draw(t.Clouds, small[i].X, small[i].Y, small[i].W, small[i].H, smallX[i], h - 300 * z - i * 12 * z - viewportY * 0.5, z, z);
		}
		img.Draw(t.Cursors, 0, 737, 639, 148, -80 * z, h - 148 * z - viewportY, z, z);
		img.Draw(t.Cursors, 0, 737, 640, 148, -80 * z + 639 * z, h - 148 * z - viewportY, z, z);
		img.Draw(t.Clouds, 0, 554, 165, 142, 0, h - 142 * z - viewportY * 2, z, z);
		img.Draw(t.Clouds, 390, 543, 122, 153, w - 122 * z, h - 153 * z - viewportY * 2, z, z);
		return img;
	}

	static void Logo(Rgba img, Textures t, double centerX, double top, double scale)
	{
		img.Draw(t.TitleButtons, 0, 0, 400, 187, centerX - 200 * scale, top, scale, scale);
	}

	public static void Write(string content, string fnaPath, string repo, string pegiPng, string outDir)
	{
		var t = new Textures
		{
			Cursors = Rgba.FromXnb(File.ReadAllBytes(Path.Combine(content, "LooseSprites", "Cursors.xnb")), fnaPath),
			Clouds = Rgba.FromXnb(File.ReadAllBytes(Path.Combine(content, "Minigames", "Clouds.xnb")), fnaPath),
			TitleButtons = Rgba.FromXnb(File.ReadAllBytes(Path.Combine(content, "Minigames", "TitleButtons.xnb")), fnaPath),
		};
		string parts = Path.Combine(repo, "assets", "sce_sys-parts");
		Rgba enhanced = Rgba.ReadPng(Path.Combine(parts, "enhanced.png"));
		Rgba portedBy = Rgba.ReadPng(Path.Combine(parts, "ported-by.png"));
		string manual = Path.Combine(outDir, "manual");
		string live = Path.Combine(outDir, "livearea", "contents");
		Directory.CreateDirectory(manual);
		Directory.CreateDirectory(live);

		// Boot picture: the title screen at night, as it opens.
		Rgba pic0 = Scene(t, 960, 544, 2, 0, -920, clouds: false);
		Logo(pic0, t, 480, 60, 1.25);
		pic0.Draw(enhanced, 480 - enhanced.Width / 2, 300);
		pic0.Draw(portedBy, 960 - 14 - portedBy.Width, 544 - 15 - portedBy.Height);
		pic0.WritePng8(Path.Combine(outDir, "pic0.png"));
		Trophies.Write(pic0, outDir);

		// Manual cover and the text pages over the day view.
		Rgba day = Scene(t, 960, 544, 2, 0);
		Rgba cover = Copy(day);
		Logo(cover, t, 480, 80, 1.25);
		cover.Draw(enhanced, 480 - enhanced.Width / 2, 320);
		cover.WritePng(Path.Combine(manual, "001.png"));
		File.Copy(Path.Combine(repo, "assets", "manual-002.png"), Path.Combine(manual, "002.png"), true);
		foreach (string page in new[] { "003", "004" })
		{
			Rgba p = Copy(day);
			p.Draw(Rgba.ReadPng(Path.Combine(parts, "manual-" + page + "-panel.png")), 80, 32);
			p.WritePng(Path.Combine(manual, page + ".png"));
		}

		// LiveArea background: day view, logo, label, credit, age label, developer badge.
		Rgba bg = Scene(t, 840, 500, 2, 0);
		Logo(bg, t, 420, 50, 1.0);
		bg.Draw(enhanced, 420 - enhanced.Width / 2, 245);
		bg.Draw(portedBy, 840 - 14 - portedBy.Width, 380);
		Rgba pegi = Rgba.ReadPng(pegiPng).ResizeSmooth(62, 76);
		bg.Draw(pegi, 14, 410);
		bg.Draw(t.TitleButtons, 1, 300, 83, 75, 630, 418, 1, 1);
		bg.Draw(t.TitleButtons, 173, 300, 107, 75, 718, 418, 1, 1);
		bg.WritePng8(Path.Combine(live, "bg0.png"));

		// LOAD gate: the title menu's Load button at 2x (148x116).
		Rgba gate = new Rgba(148, 116);
		gate.Draw(t.TitleButtons, 74, 187, 74, 58, 0, 0, 2, 2);
		gate.WritePng8(Path.Combine(live, "startgame.png"));
		File.Copy(Path.Combine(repo, "assets", "sce_sys-parts", "template.xml"), Path.Combine(live, "template.xml"), true);

		// Bubble icon: logo over the day sky, drawn at 4x and scaled down smoothly (at 128 px the logo at 0.3 came out
		// as single dropped pixels).
		const int k = 4;
		Rgba big = Scene(t, 128 * k, 128 * k, k, -110 * k);
		Logo(big, t, 64 * k, 14 * k, 0.3 * k);
		big.Draw(enhanced, 64 * k - enhanced.Width * k / 4.0, 76 * k, k / 2.0);
		big.ResizeSmooth(128, 128).WritePng8(Path.Combine(outDir, "icon0.png"));
	}

	static Rgba Copy(Rgba src)
	{
		return new Rgba(src.Width, src.Height, (byte[])src.Pixels.Clone());
	}
}
