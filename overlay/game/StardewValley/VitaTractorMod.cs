using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewValley.Characters;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;

namespace StardewValley;

/// <summary>
/// Optional low-memory tractor conversion for vanilla Horse saves.
/// Operates on a 3x3 grid around the farmer when riding the horse and holding the Tool button (Square / Buttons.X).
/// Horse identity and modData stay vanilla; no custom network Item or NPC type is serialized.
/// </summary>
public static class VitaTractorMod
{
	public const string KitKey = "Vita/TractorKit";
	public const string HorseKey = "Vita/Tractor";
	public static bool IsKit(Item item) => item != null && item.modData.ContainsKey(KitKey);
	public static bool IsTractor(Horse horse) => Enabled && horse != null && horse.modData.ContainsKey(HorseKey);
	public static string KitName => VitaTractorText.Get(VitaTractorText.KitName);
	public static string KitDescription => VitaTractorText.Get(VitaTractorText.Description);
	public static void AddShopStock(StardewValley.Menus.ShopMenu menu)
	{
		if (!VitaPlatform.IsVita || !Enabled || menu.ShopId != "Carpenter") return;
		foreach (ISalable item in menu.forSale) if (item is Item i && IsKit(i)) return;
		// A ring (the plain band art): it can be worn in any ring slot, and works worn or in the backpack.
		Item kit = ItemRegistry.Create("(O)801");
		kit.modData[KitKey] = "1";
		menu.AddForSale(kit, new ItemStockInformation(25000, 1));
	}
	public const string EquippedKey = "Vita/TractorEquipped";

	public static bool UseKit(Item kit, Farmer farmer)
	{
		if (!IsKit(kit) || farmer == null) return false;
		if (!Enabled) { Game1.showRedMessage(VitaTractorText.Get(VitaTractorText.EnableFirst)); return false; }
		Horse horse = farmer.mount ?? Utility.findHorseForPlayer(farmer.UniqueMultiplayerID);
		if (horse == null || horse.currentLocation != farmer.currentLocation || Vector2.DistanceSquared(horse.Tile, farmer.Tile) > 9f || horse.ownerId.Value != farmer.UniqueMultiplayerID)
		{
			Game1.showRedMessage(VitaTractorText.Get(VitaTractorText.NearHorse)); return false;
		}
		farmer.modData[EquippedKey] = "1";
		if (horse.modData.ContainsKey(HorseKey))
		{
			horse.modData.Remove(HorseKey);
			Game1.playSound("horse_flute");
			Game1.addHUDMessage(new HUDMessage(VitaTractorText.Get(VitaTractorText.ToHorse), 2));
			return true;
		}
		horse.modData[HorseKey] = "1";
		Game1.playSound("wand");
		Game1.addHUDMessage(new HUDMessage(VitaTractorText.Get(VitaTractorText.ToTractor), 2));
		return true;
	}

	public static void ToggleTractorHotkey(Farmer player)
	{
		if (player == null || player.currentLocation == null) return;
		// The kit must be on the farmer (backpack or any ring slot); having used one once is not enough.
		bool hasKit = VitaExtraRings.WearsTractorKit(player);
		if (!hasKit)
		{
			foreach (Item item in player.Items)
			{
				if (IsKit(item)) { hasKit = true; break; }
			}
		}
		if (!hasKit)
		{
			Game1.showRedMessage(VitaTractorText.Get(VitaTractorText.MissingKit));
			return;
		}
		if (!Enabled)
		{
			Game1.showRedMessage(VitaTractorText.Get(VitaTractorText.EnableFirst));
			return;
		}
		Horse horse = player.mount ?? Utility.findHorseForPlayer(player.UniqueMultiplayerID);
		if (horse == null || horse.ownerId.Value != player.UniqueMultiplayerID || horse.currentLocation != player.currentLocation || Vector2.DistanceSquared(horse.Tile, player.Tile) > 9f)
		{
			Game1.showRedMessage(VitaTractorText.Get(VitaTractorText.NearHorse));
			return;
		}
		player.modData[EquippedKey] = "1";
		if (horse.modData.ContainsKey(HorseKey))
		{
			horse.modData.Remove(HorseKey);
			Game1.playSound("horse_flute");
			Game1.addHUDMessage(new HUDMessage(VitaTractorText.Get(VitaTractorText.ToHorse), 2));
		}
		else
		{
			horse.modData[HorseKey] = "1";
			Game1.playSound("wand");
			Game1.addHUDMessage(new HUDMessage(VitaTractorText.Get(VitaTractorText.ToTractor), 2));
		}
	}
	private static bool _enabled = false;
	private static bool _initialized = false;
	private static int _lastActionTick = 0;
	private static int _smokeTick = 0;
	private static int _emptyUntilTick;
	private static Vector2 _emptyCenter;
	private static GameLocation _emptyLocation;
	private static Item _emptyItem;
	internal const float EnergyPerPass = 0.1f;
	private const int ActionCooldownTicks = 10; // ~6 actions per second

	public static bool Enabled
	{
		get
		{
			EnsureLoaded();
			return _enabled;
		}
		set
		{
			_initialized = true;
			_enabled = value;
			SaveConfig();
		}
	}

	private static string ConfigPath => Path.Combine(Program.GetAppDataFolder(), "vita-tractor.txt");

	private static void EnsureLoaded()
	{
		if (_initialized)
			return;
		_initialized = true;
		try
		{
			string path = ConfigPath;
			if (File.Exists(path))
			{
				string text = File.ReadAllText(path).Trim();
				_enabled = (text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase));
			}
		}
		catch
		{
			_enabled = false;
		}
	}

	private static void SaveConfig()
	{
		try
		{
			string path = ConfigPath;
			string dir = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
			{
				Directory.CreateDirectory(dir);
			}
			File.WriteAllText(path, _enabled ? "1" : "0");
		}
		catch
		{
		}
	}


	public static void Update()
	{
		if (!Enabled)
			return;

		Farmer player = Game1.player;
		if (player == null || !IsTractor(player.mount) || player.currentLocation == null || Game1.activeClickableMenu != null || Game1.eventUp || Game1.IsChatting)
			return;

		GameLocation loc = player.currentLocation;
		if (!CanWorkInLocation(player, loc)) return;
		if (_emptyLocation != null && _emptyLocation != loc) { _emptyLocation = null; _emptyItem = null; _emptyUntilTick = 0; }

		// Subtle exhaust smoke puff while moving
		if (player.isMoving())
		{
			_smokeTick++;
			if (_smokeTick >= 15)
			{
				_smokeTick = 0;
				Vector2 puffPos = VitaTractorRenderer.GetExhaustWorldPosition(player.mount);
				loc.TemporarySprites.Add(new TemporaryAnimatedSprite("LooseSprites\\Cursors", new Rectangle(372, 1956, 10, 10), puffPos, flipped: false, 0.02f, Color.Gray * 0.6f)
				{
					scale = 2f,
					layerDepth = player.mount.StandingPixel.Y / 10000f + 0.0001f,
					motion = new Vector2(-0.2f, -0.7f),
					alphaFade = 0.02f
				});
			}
		}

		// Check if Tool button is held (Square / Buttons.X on gamepad or LeftButton)
		GamePadState pad = Game1.input.GetGamePadState();
		MouseState mouse = Game1.input.GetMouseState();
		bool toolHeld = pad.IsButtonDown(Buttons.X) || mouse.LeftButton == ButtonState.Pressed;

		if (!toolHeld)
		{
			_emptyUntilTick = 0;
			_emptyLocation = null; _emptyItem = null;
			return;
		}
		if (player.Stamina < EnergyPerPass) return;

		if (Game1.ticks - _lastActionTick < ActionCooldownTicks && Game1.ticks >= _lastActionTick)
			return;

		_lastActionTick = Game1.ticks;

		Vector2 center = player.Tile;
		int centerX = (int)center.X;
		int centerY = (int)center.Y;

		// Riding stows CurrentItem, so read the selected inventory slot instead.
		int slot = player.CurrentToolIndex;
		Item currentItem = slot >= 0 && slot < player.Items.Count ? player.Items[slot] : null;
		Tool currentTool = currentItem as Tool;
		if (currentTool != null) currentTool.lastUser = player;
		if (_emptyLocation == loc && _emptyItem == currentItem && _emptyCenter == center && Game1.ticks < _emptyUntilTick) return;

		bool playedCutSound = false;
		bool worked = false;
		// The plant rules are the same for every tile of one pass (same location and seed): ask them once.
		GameLocation.VitaCachePlantRules = true;
		try
		{
			for (int dy = -1; dy <= 1; dy++)
			{
				for (int dx = -1; dx <= 1; dx++)
				{
					if (currentItem is Object consumable && consumable.Stack <= 0) break;
					Vector2 tile = new Vector2(centerX + dx, centerY + dy);
					worked |= ApplyTractorAction(player, loc, tile, currentItem, currentTool, ref playedCutSound);
				}
			}
		}
		finally
		{
			GameLocation.VitaCachePlantRules = false;
			GameLocation.VitaClearPlantRuleCache();
		}
		if (worked)
		{
			player.Stamina = Math.Max(0f, player.Stamina - EnergyPerPass);
			_emptyUntilTick = 0;
			_emptyLocation = null; _emptyItem = null;
		}
		else
		{
			_emptyCenter = center; _emptyItem = currentItem; _emptyLocation = loc;
			_emptyUntilTick = Game1.ticks + 30;
		}
	}

	public static void Reset()
	{
		_lastActionTick = _smokeTick = _emptyUntilTick = 0;
		_emptyItem = null; _emptyLocation = null;
	}

	private static bool ApplyTractorAction(Farmer player, GameLocation loc, Vector2 tile, Item currentItem, Tool tool, ref bool playedSound)
	{
		if (!CanWorkInLocation(player, loc)) return false;
		int tileX = (int)tile.X, tileY = (int)tile.Y;
		loc.Objects.TryGetValue(tile, out Object obj);
		loc.terrainFeatures.TryGetValue(tile, out TerrainFeature terrain);
		if (tool is Hoe hoe)
		{
			if (obj != null && obj.QualifiedItemId == "(O)590")
			{
				if (!obj.performToolAction(hoe)) return false;
				obj.performRemoveAction(); loc.Objects.Remove(tile); return true;
			}
			if (terrain != null || obj != null || loc.doesTileHaveProperty(tileX, tileY, "Diggable", "Back") == null) return false;
			if (!loc.makeHoeDirt(tile, ignoreChecks: false)) return false;
			PlayOnce(loc, "hoeHit", ref playedSound); return true;
		}
		if (tool is WateringCan)
		{
			HoeDirt dirt = terrain as HoeDirt;
			if (dirt == null || dirt.state.Value == 1) return false;
			dirt.state.Value = 1;
			PlayOnce(loc, "wateringCan", ref playedSound); return true;
		}
		if (tool is MeleeWeapon weapon && weapon.isScythe())
		{
			if (terrain is Grass grass)
			{
				int cut = weapon.ItemId == "66" ? 4 : weapon.ItemId == "53" ? 2 : 1;
				if (grass.grassType.Value == 6 && Game1.random.Next(2) == 0) return false;
				// Same blade count as the vanilla scythe; retain hay/drop logic, omit cosmetic bursts.
				grass.numberOfWeeds.Value -= cut;
				if (grass.numberOfWeeds.Value <= 0)
				{
					grass.TryDropItemsOnCut(weapon, addAnimation: false);
					loc.terrainFeatures.Remove(tile);
				}
				PlayOnce(loc, "daggerswipe", ref playedSound); return true;
			}
			if (terrain is HoeDirt soil)
			{
				if (!Harvest(soil, tileX, tileY)) return false;
				PlayOnce(loc, "daggerswipe", ref playedSound); return true;
			}
			if (terrain is Bush bush && bush.isDestroyable())
			{
				float before = bush.health;
				bool remove = bush.performToolAction(weapon, 0, tile);
				if (remove) loc.terrainFeatures.Remove(tile);
				return remove || bush.health != before;
			}
			return RemoveToolObject(obj, weapon, loc, tile);
		}
		if (tool is Pickaxe pick)
		{
			if (obj != null)
			{
				if (!obj.IsBreakableStone()) return obj.IsWeeds() && RemoveToolObject(obj, pick, loc, tile);
				if (obj.MinutesUntilReady > 0)
				{
					obj.MinutesUntilReady -= Math.Max(1, pick.UpgradeLevel + 1) + pick.additionalPower.Value;
					obj.shakeTimer = 200;
					if (obj.MinutesUntilReady > 0) { PlayOnce(loc, "hammer", ref playedSound); return true; }
				}
				loc.OnStoneDestroyed(obj.ItemId, tileX, tileY, player);
				obj.performRemoveAction(); loc.Objects.Remove(tile);
				Game1.stats.RocksCrushed++;
				// Debris here was decorative; OnStoneDestroyed already creates real loot.
				PlayOnce(loc, "stoneCrack", ref playedSound); return true;
			}
			if (terrain is HoeDirt soil && soil.crop == null)
			{
				loc.terrainFeatures.Remove(tile); PlayOnce(loc, "hammer", ref playedSound); return true;
			}
			return false;
		}
		if (tool is Axe axe)
		{
			if (obj != null) return (obj.IsTwig() || obj.IsWeeds()) && RemoveToolObject(obj, axe, loc, tile);
			float before;
			if (terrain is Tree tree)
			{
				if (tree.falling.Value || tree.health.Value <= -99f) return false;
				before = tree.health.Value;
				bool remove = tree.performToolAction(axe, 0, tile);
				if (remove) loc.terrainFeatures.Remove(tile);
				return remove || tree.health.Value != before;
			}
			if (terrain is FruitTree fruitTree)
			{
				if (fruitTree.falling.Value || fruitTree.health.Value <= -99f) return false;
				before = fruitTree.health.Value;
				bool remove = fruitTree.performToolAction(axe, 0, tile);
				if (remove) loc.terrainFeatures.Remove(tile);
				return remove || fruitTree.health.Value != before;
			}
			if (terrain is Bush bush && bush.isDestroyable())
			{
				before = bush.health;
				bool remove = bush.performToolAction(axe, 0, tile);
				if (remove) loc.terrainFeatures.Remove(tile);
				return remove || bush.health != before;
			}
			return false;
		}
		if (currentItem == null) return terrain is HoeDirt soil && Harvest(soil, tileX, tileY);
		if (!(currentItem is Object item) || !(terrain is HoeDirt dirtToPlant)) return false;
		bool fertilizer = item.Category == Object.fertilizerCategory;
		if (fertilizer)
		{
			if (dirtToPlant.fertilizer.Value != null) return false;
		}
		else if (item.Category != Object.SeedsCategory || dirtToPlant.crop != null || !dirtToPlant.canPlantThisSeedHere(item.ItemId, isFertilizer: false)) return false;
		bool planted;
		GameLocation.VitaMuteSounds = true;
		try { planted = dirtToPlant.plant(item.ItemId, player, isFertilizer: fertilizer); }
		finally { GameLocation.VitaMuteSounds = false; }
		if (!planted) return false;
		ConsumeSelected(player, item); PlayOnce(loc, "dirtyHit", ref playedSound); return true;
	}

	internal static bool CanWorkInLocation(Farmer player, GameLocation location)
	{
		return player != null && ReferenceEquals(player, Game1.player) && location != null
			&& location == player.currentLocation && location == Game1.currentLocation
			&& (!Game1.IsClient || location.Root != null);
	}

	private static bool Harvest(HoeDirt soil, int x, int y)
	{
		Crop crop = soil.crop;
		if (crop == null) return false;
		if (crop.dead.Value) { soil.crop = null; return true; }
		if (crop.currentPhase.Value < crop.phaseDays.Count - 1 || (crop.fullyGrown.Value && crop.dayOfCurrentPhase.Value > 0)) return false;
		if (crop.harvest(x, y, soil, null, isForcedScytheHarvest: true)) soil.crop = null;
		return true;
	}

	private static bool RemoveToolObject(Object obj, Tool tool, GameLocation location, Vector2 tile)
	{
		if (obj == null || (!obj.IsWeeds() && !obj.IsTwig()) || !obj.performToolAction(tool)) return false;
		obj.performRemoveAction(); location.Objects.Remove(tile); return true;
	}

	private static void PlayOnce(GameLocation location, string sound, ref bool played)
	{
		if (played) return;
		played = true; location.playSound(sound);
	}

	private static void ConsumeSelected(Farmer farmer, Item item)
	{
		// CurrentItem is null while mounted. Consume the actual selected stack exactly once.
		if (item.Stack > 1) item.Stack--;
		else
		{
			item.Stack = 0;
			int slot = farmer.CurrentToolIndex;
			if (slot >= 0 && slot < farmer.Items.Count && ReferenceEquals(farmer.Items[slot], item)) farmer.Items[slot] = null;
		}
	}
}
