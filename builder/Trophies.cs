// The launcher's trophy pack (sce_sys/trophy/STDV00001_00/TROPHY.TRP), as Tools/Vita/stardew/trophies/make-trophies.py
// builds it, so the VPK stage needs no Python: the same list, cups and file format; the title picture is the boot
// picture (pic0) scaled to 320x176. Keep the list in step with make-trophies.py and StardewValley.SDKs/VitaTrophies.cs.
//
// The pack is unsigned (placeholder signature lines): it only works with the NoTrpDrm plugin. Format: a 0x40-byte
// big-endian header (magic DCA24D00, version 2, file size, entry count, entry size 0x40, flags, SHA-1 of the whole
// file with this field zeroed), 0x40-byte entries (name, offset, size), then the files at 16-byte alignment.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

static class Trophies
{
	const string CommId = "STDV00001_00";
	const string SetVersion = "01.00";
	const string Title = "Stardew Valley Enhanced";
	const string TitleDetail = "Achievements of Stardew Valley 1.6 as trophies (unofficial homebrew port).";

	// id, grade (P/G/S/B), name, detail. Id 0 is the platinum; 1-39 are Data/Achievements in VitaTrophies.cs order,
	// 40-49 the Steam-only achievements.
	static readonly (int Id, char Grade, string Name, string Detail)[] List =
	{
		(0, 'P', "Stardew Valley Enhanced", "Unlock every other trophy."),
		(1, 'B', "Greenhorn", "Earn 15,000g"),
		(2, 'B', "Cowpoke", "Earn 50,000g"),
		(3, 'B', "Homesteader", "Earn 250,000g"),
		(4, 'B', "Millionaire", "Earn 1,000,000g"),
		(5, 'G', "Legend", "Earn 10,000,000g"),
		(6, 'S', "A Complete Collection", "Complete the museum collection."),
		(7, 'B', "A New Friend", "Reach a 5-heart friend level with someone."),
		(8, 'B', "Best Friends", "Reach a 10-heart friend level with someone."),
		(9, 'S', "The Beloved Farmer", "Reach a 10-heart friend level with 8 people."),
		(10, 'B', "Cliques", "Reach a 5-heart friend level with 4 people."),
		(11, 'B', "Networking", "Reach a 5-heart friend level with 10 people."),
		(12, 'B', "Popular", "Reach a 5-heart friend level with 20 people."),
		(13, 'B', "Cook", "Cook 10 different recipes."),
		(14, 'S', "Sous Chef", "Cook 25 different recipes."),
		(15, 'S', "Gourmet Chef", "Cook every recipe."),
		(16, 'B', "Moving Up", "Upgrade your house."),
		(17, 'B', "Living Large", "Upgrade your house to the maximum size."),
		(18, 'B', "D.I.Y.", "Craft 15 different items."),
		(19, 'S', "Artisan", "Craft 30 different items."),
		(20, 'G', "Craft Master", "Craft every item."),
		(21, 'B', "Fisherman", "Catch 10 different fish."),
		(22, 'B', "Ol' Mariner", "Catch 24 different fish."),
		(23, 'S', "Master Angler", "Catch every fish."),
		(24, 'B', "Mother Catch", "Catch 100 fish."),
		(25, 'B', "Treasure Trove", "Donate 40 different items to the museum."),
		(26, 'B', "Gofer", "Complete 10 'Help Wanted' requests."),
		(27, 'S', "A Big Help", "Complete 40 'Help Wanted' requests."),
		(28, 'B', "Polyculture", "Ship 15 of each crop."),
		(29, 'B', "Monoculture", "Ship 300 of one crop."),
		(30, 'G', "Full Shipment", "Ship every item."),
		(31, 'S', "Well-Read", "Read every book."),
		(32, 'B', "Two Thumbs Up", "See a movie."),
		(33, 'B', "Blue Ribbon", "Get 1st place in the Stardew Valley Fair competition."),
		(34, 'B', "An Unforgettable Soup", "Delight the Governor."),
		(35, 'B', "Good Neighbors", "Help your forest neighbors grow their family."),
		(36, 'B', "A Distant Shore", "Reach Ginger Island."),
		(37, 'S', "Danger In The Deep", "Reach the bottom of the 'dangerous' mines."),
		(38, 'S', "Infinite Power", "Obtain the most powerful weapon."),
		(39, 'G', "Perfection", "Reach 100% game completion."),
		(40, 'B', "Local Legend", "Restore the Pelican Town Community Center."),
		(41, 'B', "Joja Co. Member Of The Year", "Become a Joja Co. member and purchase a community center development."),
		(42, 'B', "Prairie King", "Beat 'Journey Of The Prairie King'."),
		(43, 'S', "Fector's Challenge", "Beat 'Journey Of The Prairie King' without dying."),
		(44, 'B', "Full House", "Get married and have two kids."),
		(45, 'B', "The Bottom", "Reach the lowest level of the mines."),
		(46, 'S', "Protector Of The Valley", "Complete all of the Adventure Guild Monster Slayer goals."),
		(47, 'B', "Singular Talent", "Reach level 10 in a skill."),
		(48, 'S', "Master Of The Five Ways", "Reach level 10 in every skill."),
		(49, 'G', "Mystery Of The Stardrops", "Find every stardrop."),
	};

	// The same 16x16 cup as VitaTrophies.CreateIcons: o outline, # body, h highlight, d shade.
	static readonly string[] Cup =
	{
		"................",
		"...oooooooooo...",
		".oooh######dooo.",
		".o.oh######do.o.",
		".o.oh######do.o.",
		"..ooh######doo..",
		"....oh####do....",
		".....oh##do.....",
		"......o##o......",
		"......o##o......",
		".....oo##oo.....",
		"....o######o....",
		"....oooooooo....",
		"................",
		"................",
		"................",
	};

	static readonly Dictionary<char, byte[][]> Palettes = new()
	{
		['B'] = new[] { new byte[] { 70, 40, 15 }, new byte[] { 205, 127, 50 }, new byte[] { 240, 180, 110 }, new byte[] { 140, 80, 30 } },
		['S'] = new[] { new byte[] { 60, 60, 70 }, new byte[] { 192, 192, 200 }, new byte[] { 245, 245, 250 }, new byte[] { 130, 130, 140 } },
		['G'] = new[] { new byte[] { 90, 60, 10 }, new byte[] { 230, 180, 40 }, new byte[] { 255, 235, 130 }, new byte[] { 170, 120, 20 } },
		['P'] = new[] { new byte[] { 40, 55, 80 }, new byte[] { 170, 200, 230 }, new byte[] { 240, 250, 255 }, new byte[] { 110, 140, 170 } },
	};

	static readonly string Signature = "<!--Sce-Np-Trophy-Signature: " + new string('x', 320) + "-->\n";

	public static void Write(Rgba pic0, string sceSys)
	{
		var icons = "BSGP".ToDictionary(g => g, TrophyIcon);
		var files = new List<(string Name, byte[] Content)>
		{
			("TROPCONF.SFM", Sfm(false)),
			("TROP.SFM", Sfm(true)),
			("ICON0.PNG", TitleIcon(pic0)),
		};
		foreach (var t in List)
		{
			files.Add(($"TROP{t.Id:D3}.PNG", icons[t.Grade]));
		}
		string dir = Path.Combine(sceSys, "trophy", CommId);
		Directory.CreateDirectory(dir);
		File.WriteAllBytes(Path.Combine(dir, "TROPHY.TRP"), Pack(files));
	}

	static Rgba CupImage(char grade, int scale)
	{
		var img = new Rgba(16 * scale, 16 * scale);
		for (int y = 0; y < 16; y++)
		{
			for (int x = 0; x < 16; x++)
			{
				int i = "o#hd".IndexOf(Cup[y][x]);
				if (i < 0)
				{
					continue;
				}
				byte[] c = Palettes[grade][i];
				for (int v = 0; v < scale; v++)
				{
					for (int u = 0; u < scale; u++)
					{
						int d = (((y * scale + v) * img.Width) + x * scale + u) * 4;
						img.Pixels[d] = c[0];
						img.Pixels[d + 1] = c[1];
						img.Pixels[d + 2] = c[2];
						img.Pixels[d + 3] = 255;
					}
				}
			}
		}
		return img;
	}

	// A dark tile with the grade's cup at 13x and a strip in the grade's color.
	static byte[] TrophyIcon(char grade)
	{
		var img = new Rgba(240, 240);
		img.Fill(28, 30, 38);
		byte[] c = Palettes[grade][1];
		var strip = new Rgba(240, 12);
		strip.Fill(c[0], c[1], c[2]);
		img.Draw(strip, 0, 228);
		img.Draw(CupImage(grade, 13), 16, 22);
		return img.PngBytes();
	}

	// The boot picture scaled to the width and cropped top and bottom to 320x176.
	static byte[] TitleIcon(Rgba pic0)
	{
		double scale = Math.Max(320.0 / pic0.Width, 176.0 / pic0.Height);
		int w = (int)Math.Round(pic0.Width * scale), h = (int)Math.Round(pic0.Height * scale);
		return pic0.ResizeSmooth(w, h).Crop((w - 320) / 2, (h - 176) / 2, 320, 176).PngBytes();
	}

	static string Escape(string s)
	{
		return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
	}

	static byte[] Sfm(bool withNames)
	{
		var lines = new List<string>
		{
			"<trophyconf version=\"1.1\" platform=\"psp2\" policy=\"small\">",
			$" <npcommid>{CommId}</npcommid>",
			$" <trophyset-version>{SetVersion}</trophyset-version>",
			" <parental-level license-area=\"default\">0</parental-level>",
		};
		if (withNames)
		{
			lines.Add($" <title-name>{Escape(Title)}</title-name>");
			lines.Add($" <title-detail>{Escape(TitleDetail)}</title-detail>");
		}
		foreach (var t in List)
		{
			string pid = t.Grade != 'P' ? "000" : "-1";
			string head = $" <trophy id=\"{t.Id:D3}\" hidden=\"no\" ttype=\"{t.Grade}\" pid=\"{pid}\"";
			if (withNames)
			{
				lines.Add(head + ">");
				lines.Add($"  <name>{Escape(t.Name)}</name>");
				lines.Add($"  <detail>{Escape(t.Detail)}</detail>");
				lines.Add(" </trophy>");
			}
			else
			{
				lines.Add(head + "/>");
			}
		}
		lines.Add("</trophyconf>");
		return new UTF8Encoding(false).GetBytes(Signature + string.Join("\n", lines) + "\n");
	}

	static byte[] Pack(List<(string Name, byte[] Content)> files)
	{
		const int HeaderSize = 0x40, EntrySize = 0x40;
		long offset = HeaderSize + EntrySize * files.Count;
		var entries = new MemoryStream();
		var data = new MemoryStream();
		foreach (var (name, content) in files)
		{
			int pad = (int)((16 - offset % 16) % 16);
			data.Write(new byte[pad]);
			offset += pad;
			var e = new byte[EntrySize];
			Encoding.ASCII.GetBytes(name).CopyTo(e, 0);
			Be64(e, 32, offset);
			Be64(e, 40, content.Length);
			entries.Write(e);
			data.Write(content);
			offset += content.Length;
		}
		var trp = new byte[HeaderSize + entries.Length + data.Length];
		Be32(trp, 0, 0xDCA24D00);
		Be32(trp, 4, 2);
		Be64(trp, 8, trp.Length);
		Be32(trp, 16, (uint)files.Count);
		Be32(trp, 20, EntrySize);
		entries.ToArray().CopyTo(trp, HeaderSize);
		data.ToArray().CopyTo(trp, HeaderSize + entries.Length);
		SHA1.HashData(trp).CopyTo(trp, 0x1C);
		return trp;
	}

	static void Be32(byte[] b, int at, uint v)
	{
		for (int i = 0; i < 4; i++)
		{
			b[at + i] = (byte)(v >> (24 - 8 * i));
		}
	}

	static void Be64(byte[] b, int at, long v)
	{
		for (int i = 0; i < 8; i++)
		{
			b[at + i] = (byte)(v >> (56 - 8 * i));
		}
	}
}
