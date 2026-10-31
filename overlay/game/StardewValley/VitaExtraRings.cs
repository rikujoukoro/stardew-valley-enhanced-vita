using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley.Inventories;
using StardewValley.Monsters;
using StardewValley.Objects;

namespace StardewValley;

/// <summary>
/// Mods menu option: three more ring slots on the inventory page. The rings live in a team global inventory named after
/// the farmer, so they are written to the save like any chest and stay there while the option is off (then they simply
/// give no effect); turning it back on brings them back.
/// </summary>
internal static class VitaExtraRings
{
	public const int Count = 3;

	private static readonly Dictionary<long, string> keys = new Dictionary<long, string>();

	public static bool Enabled => VitaQoLSettings.Enabled(4);

	private static Inventory Get(Farmer farmer, bool create = false)
	{
		FarmerTeam team = farmer?.team;
		if (farmer == null || team == null) return null;
		if (!keys.TryGetValue(farmer.UniqueMultiplayerID, out string key))
		{
			key = "Vita/ExtraRings/" + farmer.UniqueMultiplayerID;
			keys[farmer.UniqueMultiplayerID] = key;
		}
		// Effect queries run many times per frame, including for remote farmers.
		// Reading an empty slot must not create/dirty a shared net inventory.
		if (!team.globalInventories.TryGetValue(key, out Inventory rings))
		{
			if (!create) return null;
			rings = team.GetOrCreateGlobalInventory(key);
		}
		if (rings == null) return null; // A network reference may not have arrived yet.
		if (create) while (rings.Count < Count) rings.Add(null);
		return rings;
	}

	private static Inventory Equipped(Farmer farmer) => Enabled ? Get(farmer) : null;

	internal static Inventory GetEquippedInventory(Farmer farmer) => Equipped(farmer);

	/// <summary>Reconcile live effects when the local option changes; stored items are never moved.</summary>
	internal static void OnEnabledChanged()
	{
		Farmer farmer = Game1.player;
		if (farmer?.currentLocation == null) return;
		Inventory rings = Get(farmer);
		if (rings == null) return;
		bool enabled = Enabled;
		for (int i = 0; i < Count && i < rings.Count; i++)
		{
			if (!(rings[i] is Ring ring)) continue;
			if (enabled) ring.onEquip(farmer);
			else ring.onUnequip(farmer);
		}
		farmer.buffs.Dirty = true;
	}

	/// <summary>The ring in an extra slot, or null (also while the option is off).</summary>
	public static Ring At(Farmer farmer, int slot)
	{
		if (slot < 0 || slot >= Count) return null;
		Inventory rings = Equipped(farmer);
		return rings != null && slot < rings.Count ? rings[slot] as Ring : null;
	}

	/// <summary>Puts a ring (or nothing) into an extra slot and returns the ring that was there.</summary>
	public static Ring Swap(Farmer farmer, int slot, Ring ring)
	{
		if (!Enabled || farmer != Game1.player || slot < 0 || slot >= Count) return ring;
		Inventory rings = Get(farmer, create: ring != null);
		if (rings == null || slot >= rings.Count) return ring;
		Ring old = rings[slot] as Ring;
		old?.onUnequip(farmer);
		rings[slot] = ring;
		ring?.onEquip(farmer);
		farmer.buffs.Dirty = true;
		return old;
	}

	public static void AddEquipped(Farmer farmer, List<Item> items)
	{
		Inventory rings = Equipped(farmer);
		if (rings == null) return;
		for (int i = 0; i < Count && i < rings.Count; i++)
		{
			Ring ring = rings[i] as Ring;
			if (ring != null) items.Add(ring);
		}
	}

	public static int EffectMultiplier(Farmer farmer, string ringId)
	{
		int count = 0;
		Inventory rings = Equipped(farmer);
		if (rings == null) return 0;
		for (int i = 0; i < Count && i < rings.Count; i++) count += (rings[i] as Ring)?.GetEffectsOfRingMultiplier(ringId) ?? 0;
		return count;
	}

	public static int CountWearing(Farmer farmer, string ringId)
	{
		int count = 0;
		Inventory rings = Equipped(farmer);
		if (rings == null) return 0;
		for (int i = 0; i < Count && i < rings.Count; i++) if ((rings[i] as Ring)?.GetsEffectOfRing(ringId) == true) count++;
		return count;
	}

	public static void Update(GameTime time, GameLocation location, Farmer farmer)
	{
		Inventory rings = Equipped(farmer);
		if (rings == null) return;
		for (int i = 0; i < Count && i < rings.Count; i++) (rings[i] as Ring)?.update(time, location, farmer);
	}

	public static void OnNewLocation(Farmer farmer, GameLocation location)
	{
		Inventory rings = Equipped(farmer);
		if (rings == null) return;
		for (int i = 0; i < Count && i < rings.Count; i++) (rings[i] as Ring)?.onNewLocation(farmer, location);
	}

	public static void OnLeaveLocation(Farmer farmer, GameLocation location)
	{
		Inventory rings = Equipped(farmer);
		if (rings == null) return;
		for (int i = 0; i < Count && i < rings.Count; i++) (rings[i] as Ring)?.onLeaveLocation(farmer, location);
	}

	public static void OnMonsterSlay(Monster monster, GameLocation location, Farmer farmer)
	{
		Inventory rings = Equipped(farmer);
		if (rings == null) return;
		for (int i = 0; i < Count && i < rings.Count; i++) (rings[i] as Ring)?.onMonsterSlay(monster, location, farmer);
	}

	/// <summary>Whether the farmer wears a tractor kit ring in any ring slot (vanilla or extra, the extra ones even while off).</summary>
	public static bool WearsTractorKit(Farmer farmer)
	{
		if (farmer == null) return false;
		if (VitaTractorMod.IsKit(farmer.leftRing.Value) || VitaTractorMod.IsKit(farmer.rightRing.Value)) return true;
		Inventory rings = Get(farmer);
		if (rings == null) return false;
		for (int i = 0; i < Count && i < rings.Count; i++) if (VitaTractorMod.IsKit(rings[i])) return true;
		return false;
	}

	public static void Clear() => keys.Clear();
}
