using System;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley.Locations;

namespace StardewValley;

/// <summary>Port: a test menu for the Vita, on only with a file named vita-debug in the game folder (<see cref="VitaPlatform.DebugMenu" />).</summary>
/// <remarks>
/// L + R + SELECT in play opens it: warps to the places worth measuring (the mines, the Skull Cavern down to floor 200,
/// the volcano, Ginger Island, the town), a few cheats for getting around them (invincible, full health and energy,
/// money, two hours later, next day) and an on-screen frame counter. Everything goes through the game's own debug
/// commands (<see cref="DebugCommands" />) and its question dialogue, so it behaves as those do on PC.
/// </remarks>
public static class VitaDebugMenu
{
	/// <summary>Whether the frame counter is drawn (<see cref="DrawOverlay" />).</summary>
	public static bool ShowFps;

	/// <summary>No damage at all while on (Farmer.takeDamage returns at once).</summary>
	public static bool Invincible;

	/// <summary>The cached pictures of the back layers, floors and soil off (drawn tile by tile as on PC), to tell whether a picture glitch comes from them.</summary>
	public static bool NoCachedPictures;

	private static readonly Stopwatch _clock = Stopwatch.StartNew();

	private static long _secondStart;

	private static long _lastFrame;

	private static int _frames;

	private static double _slowestMs;

	private static string _fpsText = "";

	private static bool Tr => VitaPlatform.IsTr;

	/// <summary>L + R + SELECT in play, where SELECT would open the journal (Game1's control input).</summary>
	public static void Open()
	{
		if (Game1.activeClickableMenu != null || Game1.eventUp || Game1.currentLocation == null || !Game1.player.CanMove || Game1.dialogueUp)
		{
			return;
		}
		ShowMain();
	}

	private static void Ask(string question, Response[] answers, Action<string> onAnswer)
	{
		Game1.currentLocation.createQuestionDialogue(question, answers, delegate(Farmer who, string answer)
		{
			// The dialogue box closes after this returns: the next one opens a moment later.
			DelayedAction.functionAfterDelay(delegate
			{
				onAnswer(answer);
			}, 50);
		});
	}

	private static Response R(string key, string tr, string en)
	{
		return new Response(key, Tr ? tr : en);
	}

	private static Response Back()
	{
		return R("back", "Geri", "Back");
	}

	private static void Run(params string[] command)
	{
		DebugCommands.TryHandle(command);
	}

	private static void ShowMain()
	{
		Ask(Tr ? "Test menüsü" : "Test menu", new Response[8]
		{
			R("mines", "Maden", "Mines"),
			R("skull", "Kafatası Mağarası", "Skull Cavern"),
			R("island", "Ginger Adası ve Volkan", "Ginger Island and volcano"),
			R("places", "Diğer yerler", "Other places"),
			R("cheats", "Hileler", "Cheats"),
			R("caches", NoCachedPictures ? "Önbellek resimlerini aç" : "Önbellek resimlerini kapat (test)", NoCachedPictures ? "Cached pictures on" : "Cached pictures off (test)"),
			R("fps", ShowFps ? "FPS göstergesini kapat" : "FPS göstergesini aç", ShowFps ? "Hide FPS counter" : "Show FPS counter"),
			R("close", "Kapat", "Close")
		}, delegate(string answer)
		{
			switch (answer)
			{
			case "mines":
				ShowMines();
				break;
			case "skull":
				ShowSkullCavern();
				break;
			case "island":
				ShowIsland();
				break;
			case "places":
				ShowPlaces();
				break;
			case "cheats":
				ShowCheats();
				break;
			case "fps":
				ShowFps = !ShowFps;
				break;
			case "caches":
				NoCachedPictures = !NoCachedPictures;
				break;
			}
		});
	}

	private static void ShowMines()
	{
		Ask(Tr ? "Maden katı" : "Mine floor", new Response[7]
		{
			R("1", "1. kat", "Floor 1"),
			R("40", "40. kat (buz)", "Floor 40 (ice)"),
			R("80", "80. kat (lav)", "Floor 80 (lava)"),
			R("100", "100. kat", "Floor 100"),
			R("110", "110. kat", "Floor 110"),
			R("120", "120. kat (dip)", "Floor 120 (bottom)"),
			Back()
		}, delegate(string answer)
		{
			if (answer == "back")
			{
				ShowMain();
			}
			else
			{
				Run("minelevel", answer);
			}
		});
	}

	private static void ShowSkullCavern()
	{
		Ask(Tr ? "Kafatası Mağarası katı" : "Skull Cavern floor", new Response[8]
		{
			R("1", "1. kat", "Floor 1"),
			R("25", "25. kat", "Floor 25"),
			R("50", "50. kat", "Floor 50"),
			R("100", "100. kat", "Floor 100"),
			R("150", "150. kat", "Floor 150"),
			R("200", "200. kat", "Floor 200"),
			R("300", "300. kat", "Floor 300"),
			Back()
		}, delegate(string answer)
		{
			if (answer == "back")
			{
				ShowMain();
			}
			else
			{
				// The Skull Cavern's floor N is mine level 120 + N.
				Run("minelevel", (120 + int.Parse(answer)).ToString());
			}
		});
	}

	private static void ShowIsland()
	{
		Ask(Tr ? "Ginger Adası" : "Ginger Island", new Response[8]
		{
			R("IslandSouth", "Güney (iskele)", "South (dock)"),
			R("IslandNorth", "Kuzey", "North"),
			R("IslandWest", "Batı (çiftlik)", "West (farm)"),
			R("IslandEast", "Doğu (orman)", "East (jungle)"),
			R("v1", "Volkan 1. kat", "Volcano floor 1"),
			R("v5", "Volkan 5. kat", "Volcano floor 5"),
			R("v9", "Volkan 9. kat", "Volcano floor 9"),
			Back()
		}, delegate(string answer)
		{
			if (answer == "back")
			{
				ShowMain();
			}
			else if (answer.StartsWith("v"))
			{
				Run("volcano", answer.Substring(1));
			}
			else
			{
				Run("warp", answer);
			}
		});
	}

	private static void ShowPlaces()
	{
		Ask(Tr ? "Nereye?" : "Where to?", new Response[8]
		{
			R("Farm", "Çiftlik", "Farm"),
			R("Town", "Kasaba", "Town"),
			R("Beach", "Sahil", "Beach"),
			R("Forest", "Orman", "Forest"),
			R("Mountain", "Dağ", "Mountain"),
			R("Desert", "Çöl", "Desert"),
			R("Woods", "Gizli Orman", "Secret Woods"),
			Back()
		}, delegate(string answer)
		{
			if (answer == "back")
			{
				ShowMain();
			}
			else
			{
				Run("warp", answer);
			}
		});
	}

	private static void ShowCheats()
	{
		bool invincible = Invincible;
		Ask(Tr ? "Hileler" : "Cheats", new Response[6]
		{
			R("invincible", invincible ? "Ölümsüzlüğü kapat" : "Ölümsüz ol", invincible ? "Invincible off" : "Invincible on"),
			R("refill", "Can ve enerjiyi doldur", "Fill health and energy"),
			R("money", "100.000 altın", "100,000 gold"),
			R("time", "2 saat ileri", "Two hours later"),
			R("sleep", "Ertesi güne geç", "Next day"),
			Back()
		}, delegate(string answer)
		{
			switch (answer)
			{
			case "back":
				ShowMain();
				break;
			case "invincible":
				Invincible = !Invincible;
				Game1.playSound(Invincible ? "bigSelect" : "bigDeSelect");
				break;
			case "refill":
				Game1.player.health = Game1.player.maxHealth;
				Game1.player.Stamina = Game1.player.MaxStamina;
				Game1.playSound("bigSelect");
				break;
			case "money":
				Game1.player.Money += 100000;
				Game1.playSound("money");
				break;
			case "time":
				Run("time", Math.Min(2550, Game1.timeOfDay + 200).ToString());
				break;
			case "sleep":
				Run("sleep");
				break;
			}
		});
	}

	/// <summary>Called once per drawn frame, over everything: the frame counter when it is on.</summary>
	public static void DrawOverlay(SpriteBatch b)
	{
		long now = _clock.ElapsedTicks;
		if (_lastFrame != 0)
		{
			double frameMs = (now - _lastFrame) * 1000.0 / Stopwatch.Frequency;
			_slowestMs = Math.Max(_slowestMs, frameMs);
		}
		_lastFrame = now;
		_frames++;
		if (now - _secondStart >= Stopwatch.Frequency)
		{
			double seconds = (now - _secondStart) / (double)Stopwatch.Frequency;
			_fpsText = $"{_frames / seconds:F0} fps  {(Tr ? "en yavaş" : "slowest")} {_slowestMs:F0} ms";
			_secondStart = now;
			_frames = 0;
			_slowestMs = 0.0;
		}
		if (!ShowFps || Game1.smallFont == null || _fpsText.Length == 0)
		{
			return;
		}
		b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null);
		Vector2 size = Game1.smallFont.MeasureString(_fpsText);
		b.Draw(Game1.staminaRect, new Rectangle(4, 4, (int)size.X + 12, (int)size.Y + 4), Color.Black * 0.6f);
		b.DrawString(Game1.smallFont, _fpsText, new Vector2(10f, 6f), Color.Yellow);
		b.End();
	}
}
