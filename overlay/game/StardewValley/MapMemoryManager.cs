using System;
using StardewValley.Menus;

namespace StardewValley;

/// <summary>Releases map memory of locations the player isn't in (low-RAM port).</summary>
/// <remarks>
/// The original game keeps the map of every location loaded for the whole session (93 maps, about
/// 22 MB of managed heap on a mid-game save) plus every tile sheet texture it ever drew (about 30 MB).
/// Two levels, both undone transparently on the next use:
/// <list type="number">
///   <item>Tile sheet textures of a location that hasn't been current for <see cref="TextureIdleTicks" />
///   are released; the map stays. Only the current location is drawn, and drawing reloads them.</item>
///   <item>A map nothing has read for <see cref="MapIdleTicks" /> is unloaded
///   (<see cref="GameLocation.UnloadMapForMemory" />); any read reloads it. Maps of unoccupied locations
///   are still read regularly (NPCs walking off-screen every frame, outdoor maps in the 10-minute
///   update every ~7 s), so those stay loaded. A map reloaded soon after unloading is pinned for the
///   rest of the day so it can't thrash.</item>
/// </list>
/// Disable with the environment variable SV_KEEP_MAPS=1.
/// </remarks>
public static class MapMemoryManager
{
	public static readonly bool Enabled = Environment.GetEnvironmentVariable("SV_KEEP_MAPS") != "1";

	/// <summary>Whether level 2 (unloading maps) runs; level 1 (tile sheet textures) always does when <see cref="Enabled" />.</summary>
	/// <remarks>
	/// Off on the Vita: interiors are entered by NPCs throughout the day, and reading such a map back
	/// from the memory card took 20-460 ms, a visible stall each time (the off-screen NPC update or
	/// the 10-minute update). All maps together cost ~22 MB of heap, which the Vita's heap has room for.
	/// </remarks>
	public static readonly bool UnloadMaps = !VitaPlatform.IsVita;

	/// <summary>How long after the player leaves a location its tile sheet textures are released (10 s at 60 ticks/s).</summary>
	public const int TextureIdleTicks = 600;

	/// <summary>How long a map must go unread before it's unloaded (60 s).</summary>
	public const int MapIdleTicks = 3600;

	/// <summary>How often the checks run (1 s).</summary>
	private const int CheckIntervalTicks = 60;

	private static int nextCheckTick;

	/// <summary>The current location and the ones before it, most recent first; their tile sheets are kept.</summary>
	/// <remarks>Going back and forth (farm, town, a shop) reloaded the town's tile sheets every time: up to a second from the memory card on the Vita. Three locations' tile sheets fit the GPU pools.</remarks>
	private static readonly GameLocation[] recentLocations = new GameLocation[3];

	public static int Unloads { get; private set; }

	public static int Reloads { get; private set; }

	/// <summary>Reloads within <see cref="MapIdleTicks" /> of the unload; each one pins that map for the day.</summary>
	public static int QuickReloads { get; private set; }

	public static int TileSheetReleases { get; private set; }

	public static void Update()
	{
		if (!Enabled || Game1.ticks < nextCheckTick)
		{
			return;
		}
		nextCheckTick = Game1.ticks + CheckIntervalTicks;
		if (Game1.currentLocation != null && Game1.currentLocation != recentLocations[0])
		{
			for (int i = recentLocations.Length - 1; i > 0; i--)
			{
				recentLocations[i] = recentLocations[i - 1];
			}
			recentLocations[0] = Game1.currentLocation;
		}
		if (Game1.currentLocation != null)
		{
			Game1.currentLocation.LastCurrentTick = Game1.ticks;
			Game1.currentLocation.TileSheetsReleasedForMemory = false;
		}
		if (!IsSafeToRelease())
		{
			return;
		}
		Utility.ForEachLocation(delegate(GameLocation location)
		{
			if (UnloadMaps && Game1.ticks - location.MapLastAccessTick >= MapIdleTicks && location.CanUnloadMapForMemory())
			{
				location.UnloadMapForMemory();
			}
			else if (Game1.ticks - location.LastCurrentTick >= TextureIdleTicks && Array.IndexOf(recentLocations, location) < 0 && location.CanReleaseTileSheetsForMemory())
			{
				location.ReleaseTileSheetsForMemory();
				TileSheetReleases++;
			}
			return true;
		});
	}

	/// <summary>Only release during normal play: not while warping, in events, saving or starting a day.</summary>
	private static bool IsSafeToRelease()
	{
		if (Game1.gameMode != 3 || Game1.currentLocation == null || Game1.player == null)
		{
			return false;
		}
		if (Game1.isWarping || Game1.locationRequest != null || Game1.eventUp || Game1.CurrentEvent != null || Game1.newDay || Game1.fadeToBlack)
		{
			return false;
		}
		if (Game1.game1.IsSaving || Game1.activeClickableMenu is SaveGameMenu || Game1.activeClickableMenu is ShippingMenu)
		{
			return false;
		}
		return true;
	}

	public static void OnUnload(GameLocation location)
	{
		Unloads++;
	}

	/// <summary>Count a reload; returns whether it came soon enough after the unload to pin the map.</summary>
	public static bool OnReload(GameLocation location)
	{
		Reloads++;
		if (Game1.ticks - location.MapUnloadedTick < MapIdleTicks)
		{
			QuickReloads++;
			return true;
		}
		return false;
	}
}
