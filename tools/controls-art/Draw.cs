using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

// Original controls diagram for the Vita port: everything drawn here from shapes, text in Tahoma (no game or
// official-release art). Drawn at 1x (320x170) without anti-aliasing, then scaled 2x nearest-neighbour.
public static class ControlsArt
{
	const int W = 320, H = 170;
	static readonly Color Ink = Color.FromArgb(86, 46, 18);
	static readonly Color Paper = Color.FromArgb(250, 228, 178);
	static readonly Color PaperDark = Color.FromArgb(232, 196, 136);
	static readonly Color Frame = Color.FromArgb(178, 106, 48);
	static readonly Color FrameDark = Color.FromArgb(120, 66, 26);
	static readonly Color Body = Color.FromArgb(52, 54, 62);
	static readonly Color BodyLight = Color.FromArgb(84, 88, 98);
	static readonly Color BodyEdge = Color.FromArgb(22, 22, 28);
	static readonly Color Screen = Color.FromArgb(18, 22, 30);
	static readonly Color Lead = Color.FromArgb(150, 92, 44);
	static Font font;
	static Graphics g;

	static Bitmap Diagram()
	{
		Bitmap bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
		g = Graphics.FromImage(bmp);
		g.SmoothingMode = SmoothingMode.None;
		g.PixelOffsetMode = PixelOffsetMode.Half;
		g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
		font = new Font("Tahoma", 11f, FontStyle.Regular, GraphicsUnit.Pixel);
		g.Clear(Color.Transparent);
		DrawPanel();
		DrawVita(152, 92);
		DrawLabels();
		g.Dispose();
		return bmp;
	}

	// LiveArea manual page (960x544): the diagram on a pixel sky with our own clouds, drawn at 480x272 and doubled.
	public static void RunManual(string outPath)
	{
		using (Bitmap diagram = Diagram())
		using (Bitmap page = new Bitmap(480, 272, PixelFormat.Format32bppArgb))
		{
			using (Graphics p = Graphics.FromImage(page))
			{
				p.SmoothingMode = SmoothingMode.None;
				p.PixelOffsetMode = PixelOffsetMode.Half;
				Color[] bands = { Color.FromArgb(58, 132, 214), Color.FromArgb(70, 148, 224), Color.FromArgb(86, 164, 232), Color.FromArgb(104, 180, 238), Color.FromArgb(124, 196, 244), Color.FromArgb(146, 210, 248) };
				for (int i = 0; i < bands.Length; i++)
					using (var b = new SolidBrush(bands[i])) p.FillRectangle(b, 0, i * 46, 480, 46);
				Random r = new Random(11);
				for (int c = 0; c < 9; c++)
				{
					int cx = r.Next(-20, 480), cy = r.Next(20, 260), cw = r.Next(30, 60);
					using (var shade = new SolidBrush(Color.FromArgb(214, 232, 248)))
					using (var white = new SolidBrush(Color.White))
					{
						p.FillEllipse(shade, cx, cy + 3, cw, 14);
						p.FillEllipse(white, cx, cy, cw, 12);
						p.FillEllipse(white, cx + cw / 4, cy - 7, cw / 2, 14);
					}
				}
				p.DrawImage(diagram, new Rectangle(80, 51, W, H), new Rectangle(0, 0, W, H), GraphicsUnit.Pixel);
			}
			using (Bitmap big = new Bitmap(960, 544, PixelFormat.Format24bppRgb))
			{
				using (Graphics g2 = Graphics.FromImage(big))
				{
					g2.InterpolationMode = InterpolationMode.NearestNeighbor;
					g2.PixelOffsetMode = PixelOffsetMode.Half;
					g2.DrawImage(page, new Rectangle(0, 0, 960, 544), new Rectangle(0, 0, 480, 272), GraphicsUnit.Pixel);
				}
				big.Save(outPath, ImageFormat.Png);
			}
		}
	}

	public static void Run(string outPath, string previewPath)
	{
		using (Bitmap bmp = Diagram())
		{
			using (Bitmap big = new Bitmap(W * 2, H * 2, PixelFormat.Format32bppArgb))
			{
				using (Graphics g2 = Graphics.FromImage(big))
				{
					g2.InterpolationMode = InterpolationMode.NearestNeighbor;
					g2.PixelOffsetMode = PixelOffsetMode.Half;
					g2.DrawImage(bmp, new Rectangle(0, 0, W * 2, H * 2), new Rectangle(0, 0, W, H), GraphicsUnit.Pixel);
				}
				big.Save(outPath, ImageFormat.Png);
				if (!string.IsNullOrEmpty(previewPath)) // PowerShell passes $null as ""
				{
					using (Bitmap prev = new Bitmap(W * 4, H * 4, PixelFormat.Format32bppArgb))
					using (Graphics g3 = Graphics.FromImage(prev))
					{
						g3.Clear(Color.FromArgb(40, 60, 110));
						g3.InterpolationMode = InterpolationMode.NearestNeighbor;
						g3.PixelOffsetMode = PixelOffsetMode.Half;
						g3.DrawImage(bmp, new Rectangle(0, 0, W * 4, H * 4), new Rectangle(0, 0, W, H), GraphicsUnit.Pixel);
						prev.Save(previewPath, ImageFormat.Png);
					}
				}
			}
		}
	}

	static void Fill(Color c, int x, int y, int w, int h) { using (var b = new SolidBrush(c)) g.FillRectangle(b, x, y, w, h); }
	static void Px(Color c, int x, int y) { Fill(c, x, y, 1, 1); }

	// Parchment card with a two-tone wooden frame and notched corners.
	static void DrawPanel()
	{
		Fill(FrameDark, 2, 0, W - 4, H);
		Fill(FrameDark, 0, 2, W, H - 4);
		Fill(Frame, 3, 1, W - 6, H - 2);
		Fill(Frame, 1, 3, W - 2, H - 6);
		Fill(PaperDark, 4, 4, W - 8, H - 8);
		Fill(Paper, 5, 5, W - 10, H - 10);
		// faint paper grain
		Random r = new Random(7);
		for (int i = 0; i < 260; i++) Px(Color.FromArgb(242, 216, 162), 6 + r.Next(W - 12), 6 + r.Next(H - 12));
	}

	static void Ellipse(Color c, int x, int y, int w, int h) { using (var b = new SolidBrush(c)) g.FillEllipse(b, x, y, w, h); }
	static void Circle(Color c, int cx, int cy, int r) { Ellipse(c, cx - r, cy - r, r * 2 + 1, r * 2 + 1); }

	static void RoundRect(Color c, int x, int y, int w, int h, int r)
	{
		using (var b = new SolidBrush(c))
		using (var p = new GraphicsPath())
		{
			p.AddArc(x, y, r * 2, r * 2, 180, 90);
			p.AddArc(x + w - r * 2 - 1, y, r * 2, r * 2, 270, 90);
			p.AddArc(x + w - r * 2 - 1, y + h - r * 2 - 1, r * 2, r * 2, 0, 90);
			p.AddArc(x, y + h - r * 2 - 1, r * 2, r * 2, 90, 90);
			p.CloseFigure();
			g.FillPath(b, p);
		}
	}

	// Key positions, filled in by DrawVita for the leader lines.
	static Point lShoulder, rShoulder, dpad, lStick, rStick, btnTri, btnCir, btnCro, btnSq, selectBtn, startBtn, rearL, rearR;

	static void DrawVita(int cx, int cy)
	{
		int bw = 156, bh = 76;
		int x = cx - bw / 2, y = cy - bh / 2;
		// shoulders
		RoundRect(BodyEdge, x + 12, y - 4, 40, 10, 4);
		RoundRect(BodyLight, x + 13, y - 3, 38, 8, 3);
		RoundRect(BodyEdge, x + bw - 52, y - 4, 40, 10, 4);
		RoundRect(BodyLight, x + bw - 51, y - 3, 38, 8, 3);
		lShoulder = new Point(x + 26, y - 3); rShoulder = new Point(x + bw - 26, y - 3);
		// body: a capsule
		RoundRect(BodyEdge, x, y, bw, bh, 36);
		RoundRect(Body, x + 1, y + 1, bw - 2, bh - 2, 35);
		// screen
		int sx = cx - 38, sy = y + 8, sw = 76, sh = 56;
		Fill(BodyEdge, sx - 1, sy - 1, sw + 2, sh + 2);
		Fill(Screen, sx, sy, sw, sh);
		Fill(Color.FromArgb(34, 42, 58), sx + 2, sy + 2, sw - 4, 2);
		// rear touch halves, shown as dashed outlines on the screen (they are on the back)
		DashRect(sx + 4, sy + 29, 30, 26);
		DashRect(sx + sw - 34, sy + 29, 30, 26);
		rearL = new Point(sx + 21, sy + 52); rearR = new Point(sx + sw - 21, sy + 52);
		// d-pad
		int dx = x + 24, dy = y + 22;
		Fill(BodyEdge, dx - 9, dy - 3, 19, 7); Fill(BodyEdge, dx - 3, dy - 9, 7, 19);
		Fill(Color.FromArgb(150, 154, 164), dx - 8, dy - 2, 17, 5); Fill(Color.FromArgb(150, 154, 164), dx - 2, dy - 8, 5, 17);
		Px(BodyEdge, dx, dy);
		dpad = new Point(dx, dy);
		// sticks
		lStick = new Point(x + 34, y + 50); rStick = new Point(x + bw - 34, y + 50);
		foreach (Point s in new[] { lStick, rStick })
		{
			Circle(BodyEdge, s.X, s.Y, 9);
			Circle(Color.FromArgb(30, 30, 36), s.X, s.Y, 8);
			Circle(Color.FromArgb(70, 72, 82), s.X, s.Y - 1, 5);
		}
		// face buttons
		int fx = x + bw - 25, fy = y + 22;
		btnTri = new Point(fx, fy - 8); btnCir = new Point(fx + 8, fy); btnCro = new Point(fx, fy + 8); btnSq = new Point(fx - 8, fy);
		FaceButton(btnTri, 't'); FaceButton(btnCir, 'o'); FaceButton(btnCro, 'x'); FaceButton(btnSq, 's');
		// select / start
		selectBtn = new Point(x + bw - 46, y + 70); startBtn = new Point(x + bw - 34, y + 70);
		RoundRect(BodyEdge, selectBtn.X - 4, selectBtn.Y - 2, 8, 5, 2); RoundRect(Color.FromArgb(120, 124, 134), selectBtn.X - 3, selectBtn.Y - 1, 6, 3, 1);
		RoundRect(BodyEdge, startBtn.X - 4, startBtn.Y - 2, 8, 5, 2); RoundRect(Color.FromArgb(120, 124, 134), startBtn.X - 3, startBtn.Y - 1, 6, 3, 1);
	}

	static void DashRect(int x, int y, int w, int h)
	{
		Color c = Color.FromArgb(190, 196, 210);
		for (int i = 0; i < w; i++) if ((i / 2) % 2 == 0) { Px(c, x + i, y); Px(c, x + i, y + h - 1); }
		for (int j = 0; j < h; j++) if ((j / 2) % 2 == 0) { Px(c, x, y + j); Px(c, x + w - 1, y + j); }
	}

	static void FaceButton(Point p, char kind)
	{
		Circle(BodyEdge, p.X, p.Y, 4);
		Circle(Color.FromArgb(34, 34, 40), p.X, p.Y, 3);
		Symbol(p.X, p.Y, kind);
	}

	// PlayStation symbols, 5x5, in their usual colours.
	static void Symbol(int cx, int cy, char kind)
	{
		switch (kind)
		{
		case 't': { Color c = Color.FromArgb(64, 214, 160); Px(c, cx, cy - 2); Px(c, cx - 1, cy - 1); Px(c, cx + 1, cy - 1); Px(c, cx - 1, cy); Px(c, cx + 1, cy); Fill(c, cx - 2, cy + 1, 5, 1); break; }
		case 'o': { Color c = Color.FromArgb(255, 102, 102); Fill(c, cx - 1, cy - 2, 3, 1); Fill(c, cx - 1, cy + 2, 3, 1); Fill(c, cx - 2, cy - 1, 1, 3); Fill(c, cx + 2, cy - 1, 1, 3); break; }
		case 'x': { Color c = Color.FromArgb(124, 178, 232); for (int i = -2; i <= 2; i++) { Px(c, cx + i, cy + i); Px(c, cx + i, cy - i); } break; }
		case 's': { Color c = Color.FromArgb(240, 140, 210); Fill(c, cx - 2, cy - 2, 5, 1); Fill(c, cx - 2, cy + 2, 5, 1); Fill(c, cx - 2, cy - 1, 1, 3); Fill(c, cx + 2, cy - 1, 1, 3); break; }
		}
	}

	// Text with inline tokens: [L] [R] shoulder keys, [<] [>] [^] [v] d-pad arrows, [t][o][x][s] face buttons.
	static int Measure(string s)
	{
		int w = 0;
		for (int i = 0; i < s.Length; i++)
		{
			if (s[i] == '[') { int j = s.IndexOf(']', i); w += TokenWidth(s.Substring(i + 1, j - i - 1)) + 1; i = j; continue; }
			int k = i; while (k < s.Length && s[k] != '[') k++;
			w += TextWidth(s.Substring(i, k - i)); i = k - 1;
		}
		return w;
	}

	static int TextWidth(string t)
	{
		if (t.Length == 0) return 0;
		var fmt = StringFormat.GenericTypographic; fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
		return (int)Math.Ceiling(g.MeasureString(t, font, 1000, fmt).Width);
	}

	static int TokenWidth(string tok) { return (tok == "L" || tok == "R" || tok == "SEL" || tok == "START") ? KeyCapWidth(tok) : 11; }

	static int KeyCapWidth(string tok) { return TextWidth(tok == "SEL" ? "Select" : tok == "START" ? "Start" : tok) + 6; }

	static void Text(string s, int x, int y, bool right = false, bool center = false)
	{
		int w = Measure(s);
		if (right) x -= w; else if (center) x -= w / 2;
		var fmt = StringFormat.GenericTypographic;
		using (var b = new SolidBrush(Ink))
		{
			for (int i = 0; i < s.Length; i++)
			{
				if (s[i] == '[')
				{
					int j = s.IndexOf(']', i);
					string tok = s.Substring(i + 1, j - i - 1);
					DrawToken(tok, x, y);
					x += TokenWidth(tok) + 1;
					i = j;
					continue;
				}
				int k = i; while (k < s.Length && s[k] != '[') k++;
				string part = s.Substring(i, k - i);
				g.DrawString(part, font, b, x, y - 1, fmt);
				x += TextWidth(part);
				i = k - 1;
			}
		}
	}

	static void DrawToken(string tok, int x, int y)
	{
		int cy = y + 6;
		if (tok == "L" || tok == "R" || tok == "SEL" || tok == "START")
		{
			string label = tok == "SEL" ? "Select" : tok == "START" ? "Start" : tok;
			int w = KeyCapWidth(tok);
			RoundRect(BodyEdge, x, y, w, 12, 3);
			RoundRect(Body, x + 1, y + 1, w - 2, 10, 2);
			using (var b = new SolidBrush(Color.FromArgb(236, 238, 244)))
				g.DrawString(label, font, b, x + 3, y - 1, StringFormat.GenericTypographic);
			return;
		}
		if (tok.Length == 1 && "toxs".IndexOf(tok[0]) >= 0)
		{
			Circle(BodyEdge, x + 3, cy, 4);
			Circle(Color.FromArgb(34, 34, 40), x + 3, cy, 3);
			Symbol(x + 3, cy, tok[0]);
			return;
		}
		// d-pad arrow in a key cap like L/R
		RoundRect(BodyEdge, x, y, 11, 12, 3);
		RoundRect(Body, x + 1, y + 1, 9, 10, 2);
		Color a = Color.FromArgb(236, 238, 244);
		int ax = x + 5, ay = y + 6;
		switch (tok)
		{
		case ">": Fill(a, ax - 3, ay - 1, 4, 3); Fill(a, ax + 1, ay - 3, 1, 7); Fill(a, ax + 2, ay - 2, 1, 5); Fill(a, ax + 3, ay - 1, 1, 3); break;
		case "<": Fill(a, ax, ay - 1, 4, 3); Fill(a, ax - 1, ay - 3, 1, 7); Fill(a, ax - 2, ay - 2, 1, 5); Fill(a, ax - 3, ay - 1, 1, 3); break;
		case "^": Fill(a, ax - 1, ay, 3, 4); Fill(a, ax - 3, ay - 1, 7, 1); Fill(a, ax - 2, ay - 2, 5, 1); Fill(a, ax - 1, ay - 3, 3, 1); break;
		case "v": Fill(a, ax - 1, ay - 4, 3, 4); Fill(a, ax - 3, ay, 7, 1); Fill(a, ax - 2, ay + 1, 5, 1); Fill(a, ax - 1, ay + 2, 3, 1); break;
		}
	}

	static void Line(int x1, int y1, int x2, int y2)
	{
		using (var p = new Pen(Lead, 1f)) g.DrawLine(p, x1, y1, x2, y2);
	}

	static void Dot(Point p) { Fill(Lead, p.X - 1, p.Y - 1, 3, 3); }

	// A label at the left edge with a leader from its end: right, then to the key.
	static void LeftLabel(string text, int y, Point key, int elbowX)
	{
		Text(text, 10, y);
		int sx = 10 + Measure(text) + 2, ly = y + 6;
		Line(sx, ly, elbowX, ly); Line(elbowX, ly, key.X, key.Y); Dot(key);
	}

	static void RightLabel(string text, int y, Point key, int elbowX)
	{
		Text(text, W - 10, y, right: true);
		int sx = W - 10 - Measure(text) - 3, ly = y + 6;
		Line(sx, ly, elbowX, ly); Line(elbowX, ly, key.X, key.Y); Dot(key);
	}

	static void LightText(string s, int cx, int y)
	{
		int w = TextWidth(s);
		using (var b = new SolidBrush(Color.FromArgb(206, 212, 226)))
			g.DrawString(s, font, b, cx - w / 2, y - 1, StringFormat.GenericTypographic);
	}

	static void DrawLabels()
	{
		// combos across the top
		Text("[L]+[R]+[>] Camera far/close", 10, 8);
		Text("[L]+[R]+[<] FPS 30/60/Auto", W - 10, 8, right: true);
		Text("[L]+[SEL] Chat", 10, 22);
		Text("[R]+[SEL] Emote", W - 10, 22, right: true);
		// left side
		LeftLabel("Swap tool", 40, new Point(lShoulder.X, lShoulder.Y + 1), lShoulder.X - 6);
		LeftLabel("Move", 62, new Point(dpad.X - 9, dpad.Y), dpad.X - 16);
		Text("Menu cursor", 10, 110);
		LeftLabel("Move", 98, new Point(lStick.X - 9, lStick.Y), lStick.X - 16);
		// right side
		RightLabel("Swap tool", 40, new Point(rShoulder.X, rShoulder.Y + 1), rShoulder.X + 6);
		RightLabel("[t] Crafting", 54, new Point(btnTri.X + 5, btnTri.Y), btnTri.X + 5);
		RightLabel("[o] Cancel/Menu", 66, new Point(btnCir.X + 5, btnCir.Y), btnCir.X + 5);
		RightLabel("[x] Check", 80, new Point(btnCro.X + 5, btnCro.Y), btnCro.X + 5);
		RightLabel("[s] Use tool", 92, new Point(btnSq.X, btnSq.Y + 5), btnSq.X);
		Text("Scroll in menus", W - 10, 122, right: true);
		RightLabel("Menu cursor", 110, new Point(rStick.X + 9, rStick.Y), rStick.X + 16);
		// rear touch, written on the screen between its two outlines
		LightText("Rear touch:", 152, 66);
		LightText("toolbar row", 152, 78);
		// select / start: straight down from each key, then outwards to its label
		Line(selectBtn.X, selectBtn.Y + 3, selectBtn.X, 142); Line(selectBtn.X, 142, selectBtn.X - 30, 142); Dot(new Point(selectBtn.X, selectBtn.Y + 3));
		Text("[SEL] Journal", selectBtn.X - 32, 136, right: true);
		Line(startBtn.X, startBtn.Y + 3, startBtn.X, 142); Line(startBtn.X, 142, startBtn.X + 30, 142); Dot(new Point(startBtn.X, startBtn.Y + 3));
		Text("[START] Menu", startBtn.X + 32, 136);
		Text("[L]+[R]+[v] Tractor", 10, 152);
		Text("[L]+[R]+[^] Chest (in a chest)", W - 10, 152, right: true);
	}
}
