using System;
using System.Globalization;
using System.IO;
using Microsoft.Xna.Framework;
using StardewValley.TerrainFeatures;

namespace StardewValley;

/// <summary>
/// Built-in take on the "Passable Crops" idea: the local player walks through chosen crops and objects.
/// Local preference only; it changes no object, save field or network message, so other players (PC ones
/// included) keep vanilla collision. Read from the collision test of the local farmer only.
/// </summary>
internal static class VitaPassableCrops
{
	public const int Crops = 0, Scarecrows = 1, Sprinklers = 2, Forage = 3, TeaBushes = 4, Weeds = 5, SlowDown = 6;
	public const int OptionCount = 7;
	public const int MaxTreeStage = 5, MaxFruitTreeStage = 4;
	private const int DefaultFlags = (1 << OptionCount) - 1;

	private static bool loaded, enabled;
	private static int flags = DefaultFlags, treeStage = 4, fruitTreeStage = 1;

	private static string ConfigPath => Path.Combine(Program.GetAppDataFolder(), "vita-passable.txt");

	/// <summary>Plain field for the per-step collision test; no file access after the first read.</summary>
	public static bool Active;

	public static bool Enabled { get { Load(); return enabled; } set { Load(); enabled = value; Active = value; Save(); } }
	public static int TreeStage { get { Load(); return treeStage; } set { Load(); treeStage = Math.Max(0, Math.Min(MaxTreeStage, value)); Save(); } }
	public static int FruitTreeStage { get { Load(); return fruitTreeStage; } set { Load(); fruitTreeStage = Math.Max(0, Math.Min(MaxFruitTreeStage, value)); Save(); } }
	public static bool Option(int option) { Load(); return (flags & (1 << option)) != 0; }
	public static void ToggleOption(int option) { Load(); flags ^= 1 << option; Save(); }

	/// <summary>Reads the preferences once at startup so <see cref="Active" /> is right before the first step.</summary>
	public static void Load()
	{
		if (loaded) return;
		loaded = true;
		try
		{
			if (File.Exists(ConfigPath))
			{
				string[] parts = File.ReadAllText(ConfigPath).Trim().Split(' ');
				if (parts.Length >= 4)
				{
					enabled = parts[0] == "1";
					if (int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) flags = value & DefaultFlags;
					if (int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) treeStage = Math.Max(0, Math.Min(MaxTreeStage, value));
					if (int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) fruitTreeStage = Math.Max(0, Math.Min(MaxFruitTreeStage, value));
				}
			}
		}
		catch (Exception e) { Console.WriteLine("[Vita Passable] Preferences not read: " + e.Message); }
		Active = enabled;
	}

	private static void Save()
	{
		try
		{
			string path = ConfigPath;
			string directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
			File.WriteAllText(path, string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3}", enabled ? 1 : 0, flags, treeStage, fruitTreeStage));
		}
		catch (Exception e) { Console.WriteLine("[Vita Passable] Preferences not saved: " + e.Message); }
	}

	/// <summary>Whether the local farmer walks through this object; shakes it and slows them when they do.</summary>
	public static bool ObjectPasses(Object obj, Farmer farmer, Rectangle position, bool effects)
	{
		if (!farmer.IsLocalPlayer) return false;
		bool passes;
		if (obj.IsScarecrow()) passes = (flags & (1 << Scarecrows)) != 0;
		else if (obj.IsSprinkler()) passes = (flags & (1 << Sprinklers)) != 0;
		else if (obj.bigCraftable.Value) return false;
		else if (obj.IsWeeds()) passes = (flags & (1 << Weeds)) != 0;
		else if (obj.IsSpawnedObject && !obj.IsBreakableStone() && !obj.IsTwig()) passes = (flags & (1 << Forage)) != 0;
		else return false;
		if (passes && effects && obj.GetBoundingBox().Intersects(position))
		{
			if (obj.shakeTimer <= 0)
			{
				obj.shakeTimer = 100;
				obj.Location?.QueueFrameUpdate(obj);
			}
			Slow(farmer);
		}
		return passes;
	}

	/// <summary>Whether the local farmer walks through this terrain feature (vanilla already lets everyone through plain crops).</summary>
	public static bool FeaturePasses(TerrainFeature feature, Farmer farmer, Rectangle position, bool effects)
	{
		if (!farmer.IsLocalPlayer) return false;
		switch (feature)
		{
		case HoeDirt dirt:
			// Trellis crops; HoeDirt's own collision action already shakes and slows for crops.
			return dirt.crop != null && (flags & (1 << Crops)) != 0;
		case Tree tree:
			if (tree.stump.Value || tree.growthStage.Value > treeStage || treeStage == 0) return false;
			if (effects && VitaPlatform.ShakeEnabled && tree.maxShake == 0f && tree.getBoundingBox().Intersects(position))
			{
				tree.shakeLeft.Value = position.Center.X > tree.Tile.X * 64f + 32f;
				tree.maxShake = (float)Math.PI / 64f;
				tree.NeedsUpdate = true;
				Grass.PlayGrassSound();
			}
			if (effects) Slow(farmer);
			return true;
		case FruitTree fruitTree:
			if (fruitTree.stump.Value || fruitTree.growthStage.Value > fruitTreeStage || fruitTreeStage == 0) return false;
			if (effects && VitaPlatform.ShakeEnabled && fruitTree.maxShake == 0f && fruitTree.getBoundingBox().Intersects(position))
			{
				fruitTree.shakeLeft.Value = position.Center.X > fruitTree.Tile.X * 64f + 32f;
				fruitTree.maxShake = (float)Math.PI / 64f;
				fruitTree.NeedsUpdate = true;
				Grass.PlayGrassSound();
			}
			if (effects) Slow(farmer);
			return true;
		case Bush bush:
			if (bush.size.Value != Bush.greenTeaBush || (flags & (1 << TeaBushes)) == 0) return false;
			if (effects && bush.getBoundingBox().Intersects(position))
			{
				bush.VitaBrushShake(position.Center.X > bush.Tile.X * 64f + 32f);
			}
			if (effects) Slow(farmer);
			return true;
		default:
			return false;
		}
	}

	private static void Slow(Farmer farmer)
	{
		if ((flags & (1 << SlowDown)) != 0 && !farmer.isRidingHorse())
		{
			farmer.temporarySpeedBuff = (farmer.stats.Get("Book_Grass") != 0) ? -0.33f : -1f;
		}
	}
}
