using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace StardewValley.SDKs;

/// <summary>Port: the game's achievements as Vita-style trophies (<see cref="T:StardewValley.SDKs.VitaSDKHelper" />).</summary>
/// <remarks>
/// <para>Every achievement the game reports to its platform SDK becomes a trophy: the numbered ones of Data/Achievements
/// (also kept per save in Farmer.achievements) and the ten Steam-only ones (Achievement_LocalLegend, ...) that the PC
/// game reports only to Steam. Unlocked trophies are kept for the console, as on a real Vita, in Trophies.txt next to
/// the saves; a newly unlocked one shows a toast in the top-left corner, one at a time.</para>
/// <para>Grades follow the official PS4/PS Vita Stardew Valley list (41 trophies, plus the 9-trophy "1.6 Update" pack on
/// PS4); see Tools/Vita/stardew/trophies/README.md for sources and the grades that are guesses. Perfection is shown as
/// the platinum here; in the real trophy pack (TROPHY.TRP) it is gold, because a Vita platinum is awarded by the
/// system when every other trophy is unlocked.</para>
/// <para>With the NoTrpDrm plugin the launcher also unlocks the matching real trophy (<see cref="F:StardewValley.SDKs.VitaTrophies.Trophy.TrophyId" />
/// is the id in TROPHY.TRP); without it nothing else happens.</para>
/// </remarks>
public static class VitaTrophies
{
	public enum Grade
	{
		Bronze,
		Silver,
		Gold,
		Platinum
	}

	public sealed class Trophy
	{
		/// <summary>What the game passes to SDKHelper.GetAchievement: the achievement number, or Achievement_* for the Steam-only ones.</summary>
		public readonly string Key;

		/// <summary>The trophy's id in TROPHY.TRP (0 is the platinum, awarded by the system).</summary>
		public readonly int TrophyId;

		public readonly Grade Grade;

		private readonly string _nameEn;

		private readonly string _nameTr;

		private readonly string _detailEn;

		private readonly string _detailTr;

		public Trophy(string key, int trophyId, Grade grade, string nameEn = null, string nameTr = null, string detailEn = null, string detailTr = null)
		{
			Key = key;
			TrophyId = trophyId;
			Grade = grade;
			_nameEn = nameEn;
			_nameTr = nameTr;
			_detailEn = detailEn;
			_detailTr = detailTr;
		}

		/// <summary>The achievement number, or -1 for a Steam-only trophy.</summary>
		public int AchievementId
		{
			get
			{
				if (!int.TryParse(Key, out var id))
				{
					return -1;
				}
				return id;
			}
		}

		/// <summary>The name in the current language: the game's own for numbered achievements, ours (English or Turkish) for the Steam-only ones.</summary>
		public string DisplayName => GameText(0) ?? (IsTurkish ? _nameTr : _nameEn) ?? Key;

		public string Description => GameText(1) ?? (IsTurkish ? _detailTr : _detailEn) ?? "";

		private string GameText(int field)
		{
			int id = AchievementId;
			if (id < 0 || Game1.achievements == null || !Game1.achievements.TryGetValue(id, out var raw))
			{
				return null;
			}
			string[] split = raw.Split('^');
			if (field >= split.Length)
			{
				return null;
			}
			return split[field];
		}
	}

	/// <summary>Shown after the numbered achievements on the Collections page, with ids from <see cref="F:StardewValley.SDKs.VitaTrophies.CollectionsIdBase" />.</summary>
	public static readonly Trophy[] PlatformOnly = new Trophy[10]
	{
		new Trophy("Achievement_LocalLegend", 40, Grade.Bronze, "Local Legend", "Yerel Efsane", "Restore the Pelican Town Community Center.", "Pelican Kasabası Topluluk Merkezi'ni onar."),
		new Trophy("Achievement_Joja", 41, Grade.Bronze, "Joja Co. Member Of The Year", "Yılın Joja Co. Üyesi", "Become a Joja Co. member and purchase a community center development.", "Joja Co. üyesi ol ve topluluk merkezi için bir geliştirme satın al."),
		new Trophy("Achievement_PrairieKing", 42, Grade.Bronze, "Prairie King", "Çayırların Kralı", "Beat 'Journey Of The Prairie King'.", "'Journey Of The Prairie King' oyununu bitir."),
		new Trophy("Achievement_FectorsChallenge", 43, Grade.Silver, "Fector's Challenge", "Fector'un Meydan Okuması", "Beat 'Journey Of The Prairie King' without dying.", "'Journey Of The Prairie King' oyununu hiç ölmeden bitir."),
		new Trophy("Achievement_FullHouse", 44, Grade.Bronze, "Full House", "Kalabalık Aile", "Get married and have two kids.", "Evlen ve iki çocuk sahibi ol."),
		new Trophy("Achievement_TheBottom", 45, Grade.Bronze, "The Bottom", "En Dipte", "Reach the lowest level of the mines.", "Madenin en alt katına ulaş."),
		new Trophy("Achievement_KeeperOfTheMysticRings", 46, Grade.Silver, "Protector Of The Valley", "Vadinin Koruyucusu", "Complete all of the Adventure Guild Monster Slayer goals.", "Maceracılar Loncası'nın tüm canavar avı hedeflerini tamamla."),
		new Trophy("Achievement_SingularTalent", 47, Grade.Bronze, "Singular Talent", "Eşsiz Yetenek", "Reach level 10 in a skill.", "Bir beceride 10. seviyeye ulaş."),
		new Trophy("Achievement_MasterOfTheFiveWays", 48, Grade.Silver, "Master Of The Five Ways", "Beş Yolun Ustası", "Reach level 10 in every skill.", "Tüm becerilerde 10. seviyeye ulaş."),
		new Trophy("Achievement_Stardrop", 49, Grade.Gold, "Mystery Of The Stardrops", "Yıldız Damlalarının Gizemi", "Find every stardrop.", "Tüm yıldız damlalarını bul.")
	};

	/// <summary>The numbered achievements: key, TROPHY.TRP id, grade. Names and descriptions come from the game's data.</summary>
	private static readonly Trophy[] Numbered = new Trophy[39]
	{
		new Trophy("0", 1, Grade.Bronze),
		new Trophy("1", 2, Grade.Bronze),
		new Trophy("2", 3, Grade.Bronze),
		new Trophy("3", 4, Grade.Bronze),
		new Trophy("4", 5, Grade.Gold),
		new Trophy("5", 6, Grade.Silver),
		new Trophy("6", 7, Grade.Bronze),
		new Trophy("7", 8, Grade.Bronze),
		new Trophy("9", 9, Grade.Silver),
		new Trophy("11", 10, Grade.Bronze),
		new Trophy("12", 11, Grade.Bronze),
		new Trophy("13", 12, Grade.Bronze),
		new Trophy("15", 13, Grade.Bronze),
		new Trophy("16", 14, Grade.Silver),
		new Trophy("17", 15, Grade.Silver),
		new Trophy("18", 16, Grade.Bronze),
		new Trophy("19", 17, Grade.Bronze),
		new Trophy("20", 18, Grade.Bronze),
		new Trophy("21", 19, Grade.Silver),
		new Trophy("22", 20, Grade.Gold),
		new Trophy("24", 21, Grade.Bronze),
		new Trophy("25", 22, Grade.Bronze),
		new Trophy("26", 23, Grade.Silver),
		new Trophy("27", 24, Grade.Bronze),
		new Trophy("28", 25, Grade.Bronze),
		new Trophy("29", 26, Grade.Bronze),
		new Trophy("30", 27, Grade.Silver),
		new Trophy("31", 28, Grade.Bronze),
		new Trophy("32", 29, Grade.Bronze),
		new Trophy("34", 30, Grade.Gold),
		new Trophy("35", 31, Grade.Silver),
		new Trophy("36", 32, Grade.Bronze),
		new Trophy("37", 33, Grade.Bronze),
		new Trophy("38", 34, Grade.Bronze),
		new Trophy("39", 35, Grade.Bronze),
		new Trophy("40", 36, Grade.Bronze),
		new Trophy("41", 37, Grade.Silver),
		new Trophy("42", 38, Grade.Silver),
		new Trophy("44", 39, Grade.Platinum)
	};

	/// <summary>Collections page ids of <see cref="F:StardewValley.SDKs.VitaTrophies.PlatformOnly" />: this plus the index (above every Data/Achievements number).</summary>
	public const int CollectionsIdBase = 1000;

	private const string StoreFile = "Trophies.txt";

	private static readonly Dictionary<string, Trophy> ByKey = BuildIndex();

	private static readonly HashSet<string> Unlocked = new HashSet<string>();

	private static readonly object SaveLock = new object();

	private static bool _loaded;

	private static readonly Queue<TrophyToast> Pending = new Queue<TrophyToast>();

	private static TrophyToast _current;

	private static Texture2D _icons;

	private static bool _nativeMissing;

	private static bool IsTurkish => LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.tr;

	private static Dictionary<string, Trophy> BuildIndex()
	{
		Dictionary<string, Trophy> index = new Dictionary<string, Trophy>();
		foreach (Trophy trophy in Numbered)
		{
			index[trophy.Key] = trophy;
		}
		foreach (Trophy trophy2 in PlatformOnly)
		{
			index[trophy2.Key] = trophy2;
		}
		return index;
	}

	/// <summary>The trophy grade of a Collections page achievement id: a Data/Achievements number, or <see cref="F:StardewValley.SDKs.VitaTrophies.CollectionsIdBase" /> and up for <see cref="F:StardewValley.SDKs.VitaTrophies.PlatformOnly" />.</summary>
	public static bool TryGetGrade(int collectionsId, out Grade grade)
	{
		int platformIndex = collectionsId - CollectionsIdBase;
		if (platformIndex >= 0 && platformIndex < PlatformOnly.Length)
		{
			grade = PlatformOnly[platformIndex].Grade;
			return true;
		}
		if (ByKey.TryGetValue(collectionsId.ToString(), out var trophy))
		{
			grade = trophy.Grade;
			return true;
		}
		grade = Grade.Bronze;
		return false;
	}

	/// <summary>Whether this console has the trophy (any save, like a Vita trophy).</summary>
	public static bool IsUnlocked(Trophy trophy)
	{
		Load();
		lock (Unlocked)
		{
			return Unlocked.Contains(trophy.Key);
		}
	}

	public static string GradeName(Grade grade)
	{
		bool tr = IsTurkish;
		return grade switch
		{
			Grade.Bronze => tr ? "Bronz Kupa" : "Bronze Trophy",
			Grade.Silver => tr ? "Gümüş Kupa" : "Silver Trophy",
			Grade.Gold => tr ? "Altın Kupa" : "Gold Trophy",
			_ => tr ? "Platin Kupa" : "Platinum Trophy",
		};
	}

	/// <summary>Reads Trophies.txt and hands every unlocked trophy to the launcher, which unlocks the real ones that are still locked (NoTrpDrm).</summary>
	public static void Initialize()
	{
		Load();
		NativeStart();
		List<int> ids = new List<int>();
		lock (Unlocked)
		{
			foreach (string key in Unlocked)
			{
				if (ByKey.TryGetValue(key, out var trophy))
				{
					ids.Add(trophy.TrophyId);
				}
			}
		}
		foreach (int id in ids)
		{
			NativeUnlock(id);
		}
	}

	/// <summary>SDKHelper.GetAchievement: records the trophy; a new one gets a toast unless a save is loading.</summary>
	/// <remarks>While a save loads (before its first day has started) the game reports every achievement it already has (Game1.OnDayStartedImpl) and
	/// rechecks the Steam-only ones (Stats.checkForAchievements): those are recorded without a toast.</remarks>
	public static void Unlock(string key)
	{
		if (key == null)
		{
			return;
		}
		if (key == "a0")
		{
			key = "0";
		}
		if (!ByKey.TryGetValue(key, out var trophy))
		{
			return;
		}
		Load();
		bool isNew;
		lock (Unlocked)
		{
			isNew = Unlocked.Add(key);
		}
		NativeUnlock(trophy.TrophyId);
		if (!isNew)
		{
			return;
		}
		SaveAsync();
		if (Game1.hasStartedDay && Game1.gameMode == 3)
		{
			Pending.Enqueue(new TrophyToast(trophy));
		}
	}

	public static void Reset()
	{
		Load();
		lock (Unlocked)
		{
			Unlocked.Clear();
		}
		SaveAsync();
	}

	/// <summary>Each update: puts the next toast on the HUD once the one before has finished.</summary>
	public static void Update()
	{
		if (Game1.gameMode != 3 || Game1.hudMessages == null || Game1.player == null)
		{
			return;
		}
		if (_current != null && _current.Finished)
		{
			_current = null;
		}
		if (_current == null)
		{
			if (Pending.Count == 0)
			{
				return;
			}
			_current = Pending.Dequeue();
		}
		// Day changes and warps clear the HUD messages: a toast that was cleared before it finished shows again.
		if (!Game1.hudMessages.Contains(_current))
		{
			Game1.hudMessages.Add(_current);
		}
	}

	private static void Load()
	{
		if (_loaded)
		{
			return;
		}
		_loaded = true;
		try
		{
			string path = Path.Combine(Program.GetLocalAppDataFolder(), StoreFile);
			if (!File.Exists(path))
			{
				return;
			}
			lock (Unlocked)
			{
				foreach (string line in File.ReadAllLines(path))
				{
					string key = line.Trim();
					if (key.Length > 0)
					{
						Unlocked.Add(key);
					}
				}
			}
		}
		catch (Exception e)
		{
			Game1.log?.Warn("Trophies: could not read " + StoreFile + ": " + e.Message);
		}
	}

	/// <summary>Writes Trophies.txt on a pool thread: a memory card write on the main thread can take 50-130 ms on the Vita.</summary>
	private static void SaveAsync()
	{
		ThreadPool.QueueUserWorkItem(delegate
		{
			lock (SaveLock)
			{
				try
				{
					string text;
					lock (Unlocked)
					{
						text = string.Join("\n", new List<string>(Unlocked).ToArray()) + "\n";
					}
					File.WriteAllText(Path.Combine(Program.GetLocalAppDataFolder(), StoreFile), text);
				}
				catch (Exception e)
				{
					Game1.log?.Warn("Trophies: could not write " + StoreFile + ": " + e.Message);
				}
			}
		});
	}

	[DllImport("VitaPort", CallingConvention = CallingConvention.Cdecl)]
	private static extern void vita_port_trophy_start();

	[DllImport("VitaPort", CallingConvention = CallingConvention.Cdecl)]
	private static extern void vita_port_trophy_unlock(int id);

	/// <summary>Starts the launcher's trophy thread at game start, so its setup dialog runs on the title screen.</summary>
	private static void NativeStart()
	{
		if (!VitaPlatform.IsVita || _nativeMissing)
		{
			return;
		}
		try
		{
			vita_port_trophy_start();
		}
		catch (Exception e) when (e is EntryPointNotFoundException || e is DllNotFoundException)
		{
			_nativeMissing = true;
		}
	}

	/// <summary>Queues the real trophy in the launcher (Tools/Vita/stardew/main.c); returns at once, and does nothing there without NoTrpDrm.</summary>
	private static void NativeUnlock(int id)
	{
		if (!VitaPlatform.IsVita || _nativeMissing)
		{
			return;
		}
		try
		{
			vita_port_trophy_unlock(id);
		}
		catch (Exception e) when (e is EntryPointNotFoundException || e is DllNotFoundException)
		{
			// An older launcher without trophy support.
			_nativeMissing = true;
		}
	}

	/// <summary>16x16 cups, one per grade (bronze, silver, gold, platinum), drawn here: nothing of the game's art, 4 KB.</summary>
	public static Texture2D Icons
	{
		get
		{
			if (_icons == null || _icons.IsDisposed)
			{
				_icons = CreateIcons();
			}
			return _icons;
		}
	}

	public static Rectangle IconSource(Grade grade)
	{
		return new Rectangle((int)grade * 16, 0, 16, 16);
	}

	private static Texture2D CreateIcons()
	{
		// o outline, # body, h highlight, d shade (the same cup as the pack's icons, Tools/Vita/stardew/trophies/make-trophies.py).
		string[] cup = new string[16]
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
			"................"
		};
		Color[][] palettes = new Color[4][]
		{
			new Color[4]
			{
				new Color(70, 40, 15),
				new Color(205, 127, 50),
				new Color(240, 180, 110),
				new Color(140, 80, 30)
			},
			new Color[4]
			{
				new Color(60, 60, 70),
				new Color(192, 192, 200),
				new Color(245, 245, 250),
				new Color(130, 130, 140)
			},
			new Color[4]
			{
				new Color(90, 60, 10),
				new Color(230, 180, 40),
				new Color(255, 235, 130),
				new Color(170, 120, 20)
			},
			new Color[4]
			{
				new Color(40, 55, 80),
				new Color(170, 200, 230),
				new Color(240, 250, 255),
				new Color(110, 140, 170)
			}
		};
		Color[] pixels = new Color[64 * 16];
		for (int grade = 0; grade < 4; grade++)
		{
			for (int y = 0; y < 16; y++)
			{
				for (int x = 0; x < 16; x++)
				{
					int shade = "o#hd".IndexOf(cup[y][x]);
					pixels[y * 64 + grade * 16 + x] = ((shade < 0) ? Color.Transparent : palettes[grade][shade]);
				}
			}
		}
		Texture2D texture = new Texture2D(Game1.graphics.GraphicsDevice, 64, 16);
		texture.SetData(pixels);
		return texture;
	}

	/// <summary>The toast: a dark box in the top-left corner with the grade's cup, "You have earned a trophy." and the trophy's name.</summary>
	/// <remarks>It times itself by the frames it is drawn in, so a toast queued while the world is hidden (the end-of-night menus) is seen in full later.</remarks>
	private sealed class TrophyToast : HUDMessage
	{
		private const float SlideMs = 250f;

		private const float HoldMs = 4000f;

		private const float FadeMs = 400f;

		private readonly Trophy _trophy;

		private readonly int _createdTicks = Environment.TickCount;

		private float _shownMs = -1f;

		public bool Finished;

		public TrophyToast(Trophy trophy)
			: base(trophy.DisplayName, float.MaxValue)
		{
			_trophy = trophy;
		}

		public override bool update(GameTime time)
		{
			return Finished;
		}

		public override void draw(SpriteBatch b, int i, ref int heightUsed)
		{
			if (Finished)
			{
				return;
			}
			if (_shownMs < 0f)
			{
				_shownMs = 0f;
				// Numbered achievements already played the game's "achievement" sound when they unlocked.
				if (_trophy.AchievementId < 0 || Environment.TickCount - _createdTicks > 1000)
				{
					Game1.playSound("achievement");
				}
			}
			else
			{
				_shownMs += (float)(Game1.currentGameTime?.ElapsedGameTime.TotalMilliseconds ?? 16.0);
			}
			if (_shownMs >= SlideMs + HoldMs + FadeMs)
			{
				Finished = true;
				return;
			}
			string heading = (LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.tr) ? "Kupa kazandınız!" : "You have earned a trophy.";
			string name = _trophy.DisplayName;
			Vector2 headingSize = Game1.smallFont.MeasureString(heading);
			Vector2 nameSize = Game1.smallFont.MeasureString(name);
			int width = 16 + 64 + 16 + (int)Math.Max(headingSize.X * 0.8f, nameSize.X) + 20;
			int height = 96;
			float alpha = 1f;
			int y = 16;
			if (_shownMs < SlideMs)
			{
				y = (int)(-height + (height + 16) * (_shownMs / SlideMs));
			}
			else if (_shownMs > SlideMs + HoldMs)
			{
				alpha = 1f - (_shownMs - SlideMs - HoldMs) / FadeMs;
			}
			int x = 16;
			b.Draw(Game1.staminaRect, new Rectangle(x, y, width, height), Color.Black * (0.8f * alpha));
			b.Draw(Game1.staminaRect, new Rectangle(x, y + height - 4, width, 4), GradeColor(_trophy.Grade) * alpha);
			b.Draw(Icons, new Vector2(x + 16, y + 14), IconSource(_trophy.Grade), Color.White * alpha, 0f, Vector2.Zero, 4f, SpriteEffects.None, 1f);
			b.DrawString(Game1.smallFont, heading, new Vector2(x + 96, y + 12), Color.LightGray * alpha, 0f, Vector2.Zero, 0.8f, SpriteEffects.None, 1f);
			b.DrawString(Game1.smallFont, name, new Vector2(x + 96, y + 46), Color.White * alpha);
		}

		private static Color GradeColor(Grade grade)
		{
			return grade switch
			{
				Grade.Bronze => new Color(205, 127, 50),
				Grade.Silver => new Color(192, 192, 200),
				Grade.Gold => new Color(230, 180, 40),
				_ => new Color(170, 200, 230),
			};
		}
	}
}
