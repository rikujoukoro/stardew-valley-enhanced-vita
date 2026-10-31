using System;
using System.Globalization;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley.GameData.Characters;
using StardewValley.TokenizableStrings;

namespace StardewValley;

/// <summary>Read-only, bounded overlays. Never loads locations or modifies net fields.</summary>
internal static class VitaQoLOverlay
{
	private static Farmer farmer;
	private static GameLocation location;
	private static readonly int[] previousXp = new int[5];
	private static readonly FarmAnimal[] petMarkers = new FarmAnimal[32];
	private static readonly Rectangle[] rangeEdges = new Rectangle[512];
	private static int petCount, edgeCount, lastFlags = -1;
	private static Object rangeObject;
	private static Vector2 rangeTile;
	private static int rangeKind, rangeRadius;
	private static int cachedDay = int.MinValue;
	private static double cachedLuck = double.NaN;
	private static LocalizedContentManager.LanguageCode language = (LocalizedContentManager.LanguageCode)(-1);
	private static double nextInfoUpdate, xpUntil;
	private static double nextPetUpdate;
	private static object targetEntity;
	private static int targetState, targetKind = -1;
	private static LocalizedContentManager.LanguageCode targetLanguage;
	private struct PanelCache
	{
		public string Text, Prepared;
		public int Width, Height, Available;
		public float Scale, FontScale;
		public bool AlternateFont;
		public LocalizedContentManager.LanguageCode Language;
	}
	private static PanelCache dailyPanel, targetPanel, xpPanel;
	private static int xpSkill = -1;
	private static float xpFraction;
	private static string dailyText = "", targetText = "", xpText = "";
	private static readonly StringBuilder birthdayNames = new StringBuilder(96);

	private static bool CanShow => VitaPlatform.IsVita && Game1.game1 != null && Game1.gameMode == 3 && Game1.player != null
		&& Game1.currentLocation != null && Game1.activeClickableMenu == null && !Game1.eventUp
		&& !Game1.dialogueUp && !Game1.globalFade && !Game1.game1.takingMapScreenshot;

	public static void Reset()
	{
		farmer = null;
		location = null;
		rangeObject = null;
		Array.Clear(petMarkers, 0, petMarkers.Length);
		petCount = edgeCount = 0;
		lastFlags = -1;
		cachedDay = int.MinValue;
		cachedLuck = double.NaN;
		language = (LocalizedContentManager.LanguageCode)(-1);
		nextInfoUpdate = xpUntil = 0;
		nextPetUpdate = 0;
		targetEntity = null; targetKind = -1;
		dailyPanel = targetPanel = xpPanel = default;
		xpSkill = -1;
		dailyText = targetText = xpText = "";
		birthdayNames.Length = 0;
	}

	public static void Update(GameTime time)
	{
		if (!VitaPlatform.IsVita) return;
		int flags = VitaQoLSettings.OverlayFlags;
		if (flags == 0) { if (farmer != null) Reset(); return; }
		if (!CanShow) return;
		double now = time.TotalGameTime.TotalMilliseconds;
		Farmer player = Game1.player;
		bool playerChanged = farmer != player;
		if (playerChanged || lastFlags != flags)
		{
			for (int i = 0; i < previousXp.Length; i++) previousXp[i] = player.experiencePoints[i];
			farmer = player;
			lastFlags = flags;
			xpUntil = 0;
			nextInfoUpdate = 0;
			cachedDay = int.MinValue;
		}
		if (VitaQoLSettings.Enabled(1))
		{
			for (int skill = 0; skill < previousXp.Length; skill++)
			{
				int value = player.experiencePoints[skill];
				int gained = value - previousXp[skill];
				previousXp[skill] = value;
				if (gained <= 0) continue;
				int level = 0;
				while (level < 10 && value >= Farmer.getBaseExperienceForLevel(level + 1)) level++;
				if (level >= 10) continue;
				int low = level == 0 ? 0 : Farmer.getBaseExperienceForLevel(level);
				int high = level == 10 ? low : Farmer.getBaseExperienceForLevel(level + 1);
				xpFraction = high <= low ? 1f : MathHelper.Clamp((value - low) / (float)(high - low), 0f, 1f);
				xpSkill = skill;
				xpText = Farmer.getSkillDisplayNameFromIndex(skill) + "  +" + gained + " XP   " + Math.Min(value, high) + "/" + high;
				xpUntil = now + 4000;
			}
		}
		if (location != Game1.currentLocation)
		{
			location = Game1.currentLocation;
			Array.Clear(petMarkers, 0, petMarkers.Length);
			petCount = edgeCount = 0;
			rangeObject = null;
			nextInfoUpdate = 0;
			nextPetUpdate = 0;
			targetEntity = null; targetKind = -1; targetText = "";
		}
		if (now < nextInfoUpdate) return;
		nextInfoUpdate = now + 250;
		bool languageChanged = language != LocalizedContentManager.CurrentLanguageCode;
		language = LocalizedContentManager.CurrentLanguageCode;
		Vector2 tool = player.GetToolLocation(ignoreClick: true);
		Vector2 tile = new Vector2((int)Math.Floor(tool.X / 64f), (int)Math.Floor(tool.Y / 64f));
		if (VitaQoLSettings.Enabled(0))
		{
			// Game1.Date constructs a WorldDate/NetFields graph. Use scalar date fields.
			int day = Game1.year * 112 + (int)Game1.season * 28 + Game1.dayOfMonth;
			if (cachedDay != day || languageChanged || cachedLuck != farmer.DailyLuck)
			{
				cachedDay = day;
				cachedLuck = farmer.DailyLuck;
				RefreshDailyInfo();
			}
			RefreshTargetInfo(tile);
			if (now >= nextPetUpdate)
			{
				nextPetUpdate = now + 500;
				Array.Clear(petMarkers, 0, petMarkers.Length);
				petCount = 0;
				foreach (FarmAnimal animal in location.animals.Values)
				{
					if (animal.wasPet.Value || animal.wasAutoPet.Value || !Utility.isOnScreen(animal.Position, 96)) continue;
					petMarkers[petCount++] = animal;
					if (petCount == petMarkers.Length) break;
				}
			}
		}
		else { dailyText = targetText = ""; petCount = 0; }
		if (VitaQoLSettings.Enabled(2)) RefreshRange(tile);
		else { edgeCount = 0; rangeObject = null; }
	}

	private static void RefreshDailyInfo()
	{
		birthdayNames.Length = 0;
		// Read the already-loaded character data instead of visiting other maps/interiors.
		foreach (var entry in Game1.characterData)
		{
			CharacterData data = entry.Value;
			if (!data.BirthSeason.HasValue || Utility.getSeasonKey(data.BirthSeason.Value) != Game1.currentSeason || data.BirthDay != Game1.dayOfMonth) continue;
			if (data.Calendar == CalendarBehavior.HiddenAlways || (data.Calendar == CalendarBehavior.HiddenUntilMet && !farmer.friendshipData.ContainsKey(entry.Key))) continue;
			if (birthdayNames.Length > 0) birthdayNames.Append(", ");
			birthdayNames.Append(TokenParser.ParseText(data.DisplayName));
		}
		dailyText = VitaQoLText.Get(6) + ": " + farmer.DailyLuck.ToString("+0.0%;-0.0%;0.0%", CultureInfo.CurrentCulture);
		if (birthdayNames.Length > 0) dailyText += "\n" + VitaQoLText.Get(7) + ": " + birthdayNames;
	}

	private static void RefreshTargetInfo(Vector2 tile)
	{
		Crop crop = location.GetHoeDirtAtTile(tile)?.crop;
		if (crop != null && !crop.forageCrop.Value)
		{
			int days = HarvestDays(crop);
			if (!TargetChanged(crop, days | (crop.dead.Value ? 1 << 28 : 0), 1)) return;
			string name = ItemRegistry.GetDataOrErrorItem(crop.indexOfHarvest.Value).DisplayName;
			targetText = name + "\n" + (crop.dead.Value ? VitaQoLText.Get(10) : days == 0 ? VitaQoLText.Get(9) : string.Format(VitaQoLText.Get(8), days));
			return;
		}
		if (location.objects.TryGetValue(tile, out Object machine) && machine.heldObject.Value != null)
		{
			if (!TargetChanged(machine, machine.readyForHarvest.Value ? -1 : machine.MinutesUntilReady, 2)) return;
			targetText = "";
			if (machine.readyForHarvest.Value) targetText = machine.DisplayName + " — " + VitaQoLText.Get(9);
			else if (machine.MinutesUntilReady > 0)
			{
				int minutes = machine.MinutesUntilReady;
				targetText = machine.DisplayName + "\n" + (minutes / 60) + ":" + (minutes % 60).ToString("00", CultureInfo.InvariantCulture);
			}
			return;
		}
		if (TargetChanged(null, 0, 0)) targetText = "";
	}

	private static bool TargetChanged(object entity, int state, int kind)
	{
		var code = LocalizedContentManager.CurrentLanguageCode;
		if (ReferenceEquals(targetEntity, entity) && targetState == state && targetKind == kind && targetLanguage == code) return false;
		targetEntity = entity; targetState = state; targetKind = kind; targetLanguage = code;
		return true;
	}

	internal static int HarvestDays(Crop crop)
	{
		if (crop.fullyGrown.Value) return Math.Max(0, crop.dayOfCurrentPhase.Value);
		int days = 0;
		for (int i = Math.Max(0, crop.currentPhase.Value); i < crop.phaseDays.Count - 1; i++)
			days += Math.Max(0, crop.phaseDays[i] - (i == crop.currentPhase.Value ? crop.dayOfCurrentPhase.Value : 0));
		return days;
	}

	private static int RangeKind(Object item)
	{
		if (item == null) return 0;
		if (item.IsSprinkler()) return 1;
		if (item.IsScarecrow()) return 2;
		return item.QualifiedItemId == "(BC)10" ? 3 : 0;
	}

	private static void RefreshRange(Vector2 facingTile)
	{
		Object selected = null;
		int index = farmer.CurrentToolIndex;
		if (index >= 0 && index < farmer.Items.Count) selected = farmer.Items[index] as Object;
		int kind = RangeKind(selected);
		Vector2 tile = facingTile;
		if (kind == 0)
		{
			location.objects.TryGetValue(facingTile, out selected);
			kind = RangeKind(selected);
			if (selected != null) tile = selected.TileLocation;
		}
		int radius = kind == 1 ? selected.GetModifiedRadiusForSprinkler() : kind == 2 ? selected.GetRadiusForScarecrow() : kind == 3 ? 5 : -1;
		if (radius < 0 || radius > 17) { edgeCount = 0; rangeObject = null; return; }
		if (rangeObject == selected && rangeTile == tile && rangeKind == kind && rangeRadius == radius) return;
		rangeObject = selected; rangeTile = tile; rangeKind = kind; rangeRadius = radius;
		edgeCount = 0;
		int extent = Math.Max(1, radius);
		for (int y = -extent; y <= extent; y++) for (int x = -extent; x <= extent; x++)
		{
			if (!InsideRange(kind, radius, x, y)) continue;
			int px = ((int)tile.X + x) * 64, py = ((int)tile.Y + y) * 64;
			if (!InsideRange(kind, radius, x - 1, y)) AddEdge(new Rectangle(px, py, 3, 64));
			if (!InsideRange(kind, radius, x + 1, y)) AddEdge(new Rectangle(px + 61, py, 3, 64));
			if (!InsideRange(kind, radius, x, y - 1)) AddEdge(new Rectangle(px, py, 64, 3));
			if (!InsideRange(kind, radius, x, y + 1)) AddEdge(new Rectangle(px, py + 61, 64, 3));
		}
	}

	internal static bool InsideRange(int kind, int radius, int x, int y)
	{
		if (kind == 1) return radius == 0 ? Math.Abs(x) + Math.Abs(y) == 1 : Math.Abs(x) <= radius && Math.Abs(y) <= radius;
		if (kind == 2) return x * x + y * y < radius * radius;
		return kind == 3 && Math.Abs(x) + Math.Abs(y) <= radius;
	}
	private static void AddEdge(Rectangle edge)
	{
		// Merge straight boundary runs once when coverage changes, instead of drawing
		// one rectangle per tile on every frame. A square sprinkler becomes four draws.
		for (int i = 0; i < edgeCount; i++)
		{
			Rectangle old = rangeEdges[i];
			if (old.X == edge.X && old.Width == edge.Width && (old.Bottom == edge.Top || edge.Bottom == old.Top))
			{
				int top = Math.Min(old.Top, edge.Top);
				rangeEdges[i] = new Rectangle(old.X, top, old.Width, old.Height + edge.Height); return;
			}
			if (old.Y == edge.Y && old.Height == edge.Height && (old.Right == edge.Left || edge.Right == old.Left))
			{
				int left = Math.Min(old.Left, edge.Left);
				rangeEdges[i] = new Rectangle(left, old.Y, old.Width + edge.Width, old.Height); return;
			}
		}
		if (edgeCount < rangeEdges.Length) rangeEdges[edgeCount++] = edge;
	}

	public static void DrawWorld(SpriteBatch batch)
	{
		if (!CanShow || location != Game1.currentLocation || VitaQoLSettings.OverlayFlags == 0) return;
		if (VitaQoLSettings.Enabled(2))
		{
			Color color = rangeKind == 1 ? Color.CornflowerBlue : rangeKind == 2 ? Color.Gold : Color.Orange;
			for (int i = 0; i < edgeCount; i++)
			{
				Rectangle edge = rangeEdges[i];
				edge.Offset(-Game1.viewport.X, -Game1.viewport.Y);
				if (edge.Right <= 0 || edge.Bottom <= 0 || edge.Left >= Game1.viewport.Width || edge.Top >= Game1.viewport.Height) continue;
				batch.Draw(Game1.staminaRect, edge, color * 0.75f);
			}
		}
		if (!VitaQoLSettings.Enabled(0)) return;
		for (int i = 0; i < petCount; i++)
		{
			FarmAnimal animal = petMarkers[i];
			if (animal == null || animal.wasPet.Value || animal.wasAutoPet.Value || !Utility.isOnScreen(animal.Position, 96)) continue;
			// Centred over the sprite, just above its top. FarmAnimal.draw puts the sprite's top-left at Position - (0, 24),
			// SpriteWidth * 4 wide; a fixed offset sat left of centre on 32 px cows and pigs.
			int spriteWidth = (animal.Sprite?.SpriteWidth ?? 16) * 4;
			Vector2 position = Game1.GlobalToLocal(Game1.viewport, animal.Position + new Vector2(spriteWidth / 2 - 10, -24 - 16));
			batch.Draw(Game1.mouseCursors, position + new Vector2(1, 1), new Rectangle(211, 428, 7, 6), Color.Black * 0.6f, 0f, Vector2.Zero, 3f, SpriteEffects.None, 0.99f);
			batch.Draw(Game1.mouseCursors, position, new Rectangle(211, 428, 7, 6), Color.White, 0f, Vector2.Zero, 3f, SpriteEffects.None, 1f);
		}
	}

	public static void DrawHud(SpriteBatch batch, GameTime time)
	{
		if (!CanShow || farmer != Game1.player || VitaQoLSettings.OverlayFlags == 0) return;
		if (VitaQoLSettings.Enabled(0))
		{
			int y = 16;
			y += DrawPanel(batch, ref dailyPanel, dailyText, 16, y, 400);
			if (targetText.Length > 0) DrawPanel(batch, ref targetPanel, targetText, 16, y + 6, 400);
		}
		if (VitaQoLSettings.Enabled(1) && xpSkill >= 0 && time.TotalGameTime.TotalMilliseconds < xpUntil)
		{
			int width = Math.Min(380, Game1.uiViewport.Width - 112);
			// The bar spans exactly the text panel above it, both centred (the panel's width is cached from its text).
			int panelWidth = (xpPanel.Text == xpText && xpPanel.Width > 0) ? xpPanel.Width : width;
			int x = (Game1.uiViewport.Width - panelWidth) / 2;
			int y = 14;
			int h = DrawPanel(batch, ref xpPanel, xpText, x, y, width, bgAlpha: 0.55f, textAlpha: 0.85f);
			panelWidth = xpPanel.Width;
			batch.Draw(Game1.staminaRect, new Rectangle(x, y + h + 2, panelWidth, 7), Color.Black * 0.55f);
			batch.Draw(Game1.staminaRect, new Rectangle(x + 1, y + h + 3, (int)((panelWidth - 2) * xpFraction), 5), Color.LimeGreen * 0.85f);
		}
	}

	private static int DrawPanel(SpriteBatch batch, ref PanelCache cache, string text, int x, int y, int maxWidth, float bgAlpha = 0.75f, float textAlpha = 1f)
	{
		if (string.IsNullOrEmpty(text)) return 0;
		int available = Math.Max(64, Math.Min(maxWidth, Game1.uiViewport.Width - x - 40));
		bool small = !VitaUiText.UseSpriteText;
		var code = LocalizedContentManager.CurrentLanguageCode;
		float fontScale = Game1.options.dialogueFontScale;
		bool alternate = Game1.options.useAlternateFont;
		if (cache.Text != text || cache.Available != available || cache.Language != code || cache.FontScale != fontScale || cache.AlternateFont != alternate)
		{
			cache.Text = text; cache.Available = available; cache.Language = code; cache.FontScale = fontScale; cache.AlternateFont = alternate;
			// Non-Latin: break the lines here (VitaUiText.WrapForSprite); SpriteText's own CJK wrapping is not what Measure
			// counts, so a third line fell below the black box. The drawn width then never wraps again (see Draw below).
			cache.Prepared = small ? Game1.parseText(text, Game1.smallFont, available - 20) : VitaUiText.WrapForSprite(text, available - 20);
			Vector2 size = VitaUiText.Measure(cache.Prepared, small: small, widthConstraint: small ? (available - 20) : 99999);
			cache.Scale = Math.Min(1f, (available - 20f) / Math.Max(1f, size.X));
			cache.Width = Math.Min(available, (int)(size.X * cache.Scale) + 20);
			cache.Height = (int)(size.Y * cache.Scale) + 14;
		}
		batch.Draw(Game1.staminaRect, new Rectangle(x, y, cache.Width, cache.Height), Color.Black * bgAlpha);
		VitaUiText.Draw(batch, cache.Prepared, new Vector2(x + 10, y + 7), Color.White * textAlpha, cache.Scale, small: small, maxWidth: small ? (available - 20) : (available - 12));
		return cache.Height;
	}
}
