using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Xna.Framework;
using StardewValley.Inventories;
using StardewValley.Menus;
using StardewValley.Objects;
using StardewValley.Tools;

namespace StardewValley;

/// <summary>
/// Chest helper from the Mods menu. L + R + up in an open chest moves backpack items into it when it already holds the
/// same item; Triangle in a chest searches it. Uses the vanilla chest lock and Chest.addItem, like opening the chest and
/// moving the items by hand, so saves and PC players see ordinary chest contents.
/// </summary>
internal static class VitaChestMod
{
	/// <summary>The whole backpack, toolbar row included (tools never move, see Eligible).</summary>
	private const int FirstSlot = 0;

	public static bool Enabled => VitaQoLSettings.Enabled(3);

	/// <summary>
	/// L + R + up with a chest open: backpack items that exactly match a stack already in that chest go into it.
	/// The open chest menu already holds the chest's lock, so the items move as if dragged by hand.
	/// </summary>
	public static void StackIntoOpenChest(Farmer player)
	{
		if (!(Game1.activeClickableMenu is ItemGrabMenu menu) || menu.source != ItemGrabMenu.source_chest || !(menu.sourceItem is Chest chest) || player == null)
		{
			Game1.playSound("cancel");
			Game1.VitaShowShortcutMessage(VitaChestText.Get(VitaChestText.OpenChestFirst));
			return;
		}
		int moved = MoveInto(chest, player);
		if (moved <= 0)
		{
			Game1.playSound("cancel");
			Game1.VitaShowShortcutMessage(VitaChestText.Get(VitaChestText.NothingToStack));
			return;
		}
		Game1.playSound("Ship");
		Game1.VitaShowShortcutMessage(string.Format(VitaChestText.Get(VitaChestText.Stacked), moved));
	}

	private static bool Eligible(Item item) => item != null && !(item is Tool) && item.maximumStackSize() > 1;

	private static int MoveInto(Chest chest, Farmer player)
	{
		if (player != Game1.player) return 0;
		IInventory chestItems = chest.GetItemsForPlayer();
		int moved = 0;
		for (int i = FirstSlot; i < player.Items.Count; i++)
		{
			Item item = player.Items[i];
			if (!Eligible(item)) continue;
			bool match = false;
			for (int j = 0; j < chestItems.Count && !match; j++)
			{
				match = chestItems[j] != null && chestItems[j].canStackWith(item);
			}
			if (!match) continue;
			int before = item.Stack;
			Item left = chest.addItem(item);
			player.Items[i] = left;
			moved += before - (left?.Stack ?? 0);
		}
		return moved;
	}

	/// <summary>Whether an item's name contains the search text (any case); empty text matches everything.</summary>
	public static bool Matches(Item item, string query)
	{
		if (string.IsNullOrEmpty(query)) return true;
		if (item == null) return false;
		string name = item.DisplayName;
		return name != null && CultureInfo.InvariantCulture.CompareInfo.IndexOf(name, query, CompareOptions.IgnoreCase) >= 0;
	}
}
