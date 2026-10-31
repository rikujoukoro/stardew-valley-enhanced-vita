using Microsoft.Xna.Framework;
using StardewValley.Locations;
using StardewValley.Monsters;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;

namespace StardewValley;

/// <summary>
/// Port: two optional helpers like the mobile version has, both off by default (options "Auto attack" and "Auto tool select",
/// <see cref="VitaPlatform.AutoAttack" /> / <see cref="VitaPlatform.AutoTool" />).
/// Auto attack: <see cref="Update" /> runs once per frame in play; when a living, vulnerable Monster is within 1.75 tiles of the player
/// (gap between the two bounding boxes) the player faces it along the dominant axis (a monster in front is preferred) and swings a
/// non-scythe MeleeWeapon through the game's own Farmer.BeginUsingTool, the same call Game1.pressUseToolButton makes (a normal swing,
/// never the club / dagger special move). The weapon is taken from the toolbar row, or the row holding it is rotated in with the game's
/// own Farmer.shiftToolbar. One second after the last monster the old slot (and row) is restored, but only if the player did not change the slot.
/// Auto tool select: <see cref="BeforeUseTool" /> runs at the top of Game1.pressUseToolButton; it looks at the very tile the tool will hit
/// (Farmer.GetToolLocation, as the game does for a gamepad; never the stale simulated mouse on the Vita) and selects the fitting tool.
/// Nothing allocates per frame, every access is null-checked, and nothing runs when the options are off.
/// </summary>
public static class VitaAutoActions
{
	/// <summary>Attack trigger range in pixels (0.75 tile, about the sword's real reach) between the player's and a monster's bounding boxes.</summary>
	private const int AttackRange = 48;

	/// <summary>Bonus (squared pixels) that makes a monster in the facing direction win against one slightly closer behind.</summary>
	private const int FacingBonus = 48 * 48;

	/// <summary>Ticks (60 a second) without a monster nearby before the old slot is restored.</summary>
	private const int RestoreDelayTicks = 30;
	private static int _lastSwingTick;
	private const int SwingCooldownTicks = 12;

	private const int ToolbarRowSize = 12;

	private const int KindPickaxe = 0;

	private const int KindAxe = 1;

	private const int KindHoe = 2;

	private const int KindCan = 3;

	private const int KindWeapon = 4;

	private const int KindScythe = 5;

	/// <summary>Port: weeds and grass: the scythe, else the best weapon.</summary>
	private const int KindScytheOrWeapon = 6;

	/// <summary>The slot held before auto attack switched to a weapon; -1 when nothing is to be restored.</summary>
	private static int _savedIndex = -1;

	/// <summary>The item held before auto attack switched (null for an empty slot).</summary>
	private static Item _savedItem;

	/// <summary>The weapon auto attack switched to; null when auto attack has not switched.</summary>
	private static Item _weaponItem;

	/// <summary>Net toolbar rotations (positive: to the right) done to fetch the weapon from another row.</summary>
	private static int _attackShift;

	private static int _lastMonsterTick;

	/// <summary>Whether the player is free to act: in play, no menu, event, minigame, festival, horse or swimming.</summary>
	private static bool InPlay(Farmer who)
	{
		if (who == null || Game1.currentLocation == null)
		{
			return false;
		}
		if (Game1.activeClickableMenu != null || Game1.eventUp || Game1.currentMinigame != null || Game1.dialogueUp || Game1.fadeToBlack || Game1.isFestival())
		{
			return false;
		}
		if (who.health <= 0 || who.isRidingHorse() || who.swimming.Value || who.onBridge.Value || who.IsSitting() || who.isEating)
		{
			return false;
		}
		return true;
	}

	private static void ClearAttackState()
	{
		_savedIndex = -1;
		_savedItem = null;
		_weaponItem = null;
		_attackShift = 0;
	}

	/// <summary>Called every frame in play from Game1._update (right after the control input).</summary>
	public static void Update()
	{
		try
		{
			if (!VitaPlatform.AutoAttack)
			{
				if (_weaponItem != null || _savedIndex >= 0)
				{
					ClearAttackState();
				}
				return;
			}
			Farmer who = Game1.player;
			if (!InPlay(who) || who.UsingTool || !who.CanMove)
			{
				return;
			}
			// The player picked another slot (or shifted the toolbar) meanwhile: nothing to restore any more.
			if (_weaponItem != null && who.CurrentItem != _weaponItem)
			{
				ClearAttackState();
			}
			bool anyNear;
			Monster monster = FindMonster(who, facingOnly: false, out anyNear);
			if (anyNear)
			{
				_lastMonsterTick = Game1.ticks;
			}
			if (monster != null)
			{
				var thumb = Game1.input.GetGamePadState().ThumbSticks.Left;
				if (thumb.LengthSquared() > 0.15f)
				{
					int dir = DirectionTo(who.GetBoundingBox(), monster.GetBoundingBox());
					Vector2 monsterDirVec = (dir == 0) ? new Vector2(0f, 1f) : (dir == 1) ? new Vector2(1f, 0f) : (dir == 2) ? new Vector2(0f, -1f) : new Vector2(-1f, 0f);
					if (Vector2.Dot(thumb, monsterDirVec) < -0.2f)
					{
						return;
					}
				}
				if (Game1.ticks - _lastSwingTick >= SwingCooldownTicks)
				{
					_lastSwingTick = Game1.ticks;
					Swing(who, monster);
				}
			}
			else if (_weaponItem != null && Game1.ticks - _lastMonsterTick > RestoreDelayTicks)
			{
				RestoreSlot(who);
			}
		}
		catch (System.Exception)
		{
		}
	}

	/// <summary>Selects a weapon if needed, faces the monster and starts a normal swing.</summary>
	private static void Swing(Farmer who, Monster monster)
	{
		Item held = who.CurrentItem;
		if (!(held is MeleeWeapon { } current) || current.isScythe())
		{
			int shift;
			int slot = EnsureKind(who, KindWeapon, -1, out shift);
			if (slot < 0)
			{
				return;
			}
			if (_weaponItem == null)
			{
				_savedIndex = who.CurrentToolIndex;
				_savedItem = held;
				_attackShift = 0;
			}
			_attackShift += shift;
			SelectSlot(who, slot);
			_weaponItem = who.CurrentItem;
		}
		int oldDirection = who.FacingDirection;
		who.FacingDirection = DirectionTo(who.GetBoundingBox(), monster.GetBoundingBox());
		who.lastClick = who.GetToolLocation(ignoreClick: true);
		who.BeginUsingTool();
		if (!who.UsingTool)
		{
			who.FacingDirection = oldDirection;
		}
	}

	/// <summary>Puts the old row and slot back once the fight is over.</summary>
	private static void RestoreSlot(Farmer who)
	{
		int shifts = _attackShift;
		Item saved = _savedItem;
		int savedIndex = _savedIndex;
		ClearAttackState();
		if (shifts != 0)
		{
			bool right = shifts < 0;
			int count = System.Math.Abs(shifts);
			for (int i = 0; i < count; i++)
			{
				who.shiftToolbar(right);
			}
		}
		int target = -1;
		if (saved != null)
		{
			target = IndexInRow(who, saved);
		}
		else if (savedIndex >= 0 && savedIndex < who.Items.Count)
		{
			target = savedIndex;
		}
		if (target >= 0)
		{
			SelectSlot(who, target);
		}
	}

	/// <summary>Called at the start of Game1.pressUseToolButton: selects the tool that fits the tile the tool is about to hit.</summary>
	public static void BeforeUseTool()
	{
		try
		{
			if (!VitaPlatform.AutoTool)
			{
				return;
			}
			Farmer who = Game1.player;
			if (!InPlay(who) || who.UsingTool)
			{
				return;
			}
			Item current = who.CurrentItem;
			if (!IsSwitchable(current))
			{
				return;
			}
			GameLocation location = Game1.currentLocation;
			// Port: auto tool select never picks a sword; swords are only auto attack's business.
			int kind = ClassifyTile(who, location, current, TargetPosition(who));
			if (kind < 0)
			{
				return;
			}
			int alt = -1;
			if (kind == KindScytheOrWeapon)
			{
				kind = KindScythe;
			}
			int shift;
			int slot = EnsureKind(who, kind, alt, out shift);
			if (slot >= 0 && slot != who.CurrentToolIndex)
			{
				SelectSlot(who, slot);
			}
		}
		catch (System.Exception)
		{
		}
	}

	/// <summary>
	/// The pixel position the tool will hit. Like Game1.pressUseToolButton, the mouse is only used when it is really visible, and never on the
	/// Vita or with gamepad controls (the simulated mouse position is stale there); otherwise the tile in front of the player, exactly as
	/// Farmer.GetToolLocation gives it to the tool animation.
	/// </summary>
	private static Vector2 TargetPosition(Farmer who)
	{
		if (!VitaPlatform.IsVita && !Game1.options.gamepadControls && Game1.wasMouseVisibleThisFrame)
		{
			return new Vector2(Game1.getOldMouseX() + Game1.viewport.X, Game1.getOldMouseY() + Game1.viewport.Y);
		}
		return who.GetToolLocation(ignoreClick: true);
	}

	/// <summary>Only tools (and an empty slot) are ever swapped away; seeds, fertilizer, objects, food, rod, slingshot stay.</summary>
	private static bool IsSwitchable(Item item)
	{
		return item == null || item is Pickaxe || item is Axe || item is Hoe || item is WateringCan || item is MeleeWeapon;
	}

	private static bool IsFarmTool(Item item)
	{
		return item == null || item is Pickaxe || item is Axe || item is Hoe;
	}

	/// <summary>Which tool the tile at the pixel position calls for (a Kind constant), or -1 to leave the current item alone.</summary>
	private static int ClassifyTile(Farmer who, GameLocation location, Item current, Vector2 position)
	{
		if (position.X < 0f || position.Y < 0f)
		{
			return -1;
		}
		int tileX = (int)(position.X / 64f);
		int tileY = (int)(position.Y / 64f);
		Vector2 tile = new Vector2(tileX, tileY);
		Object obj = FindObject(location, position, who.FacingDirection);
		if (obj != null)
		{
			if (obj.IsBreakableStone() || IsOreNode(obj))
			{
				return KindPickaxe;
			}
			if (obj.IsTwig())
			{
				return KindAxe;
			}
			if (obj.IsWeeds())
			{
				// A weapon in hand cuts weeds as well as the scythe does.
				return (current is MeleeWeapon) ? (-1) : KindScytheOrWeapon;
			}
			if (obj.QualifiedItemId == "(O)590" || obj.Name == "Artifact Spot" || obj.Name == "Seed Spot")
			{
				return KindHoe;
			}
			return -1;
		}
		// A rock crab hiding in its shell is hit with the pickaxe, not a sword.
		for (int c = location.characters.Count - 1; c >= 0; c--)
		{
			if (location.characters[c] is RockCrab crab && !crab.shellGone.Value && crab.Sprite.currentFrame % 4 == 0 && crab.Health > 0 && crab.GetBoundingBox().Contains((int)position.X, (int)position.Y))
			{
				return KindPickaxe;
			}
		}
		ResourceClump clump = FindClump(location, tile);
		if (clump != null)
		{
			if (clump is GiantCrop || clump.parentSheetIndex.Value == 600 || clump.parentSheetIndex.Value == 602)
			{
				return KindAxe;
			}
			return KindPickaxe;
		}
		if (location.terrainFeatures.TryGetValue(tile, out var feature) && feature != null)
		{
			if (feature is Tree tree)
			{
				// Grown trees and stumps only: saplings and tapped trees are kept, fruit trees are not touched at all.
				if (!tree.tapped.Value && (tree.stump.Value || tree.growthStage.Value >= 5))
				{
					return KindAxe;
				}
				return -1;
			}
			if (feature is Grass)
			{
				// Grass only swaps away from a farming tool; a weapon, watering can, rod or item in hand stays.
				return IsFarmTool(current) ? KindScytheOrWeapon : (-1);
			}
			if (feature is HoeDirt dirt)
			{
				// Dry crop or seeds: the watering can, only from a farming tool or an empty hand.
				if (dirt.crop != null && !dirt.crop.dead.Value && dirt.state.Value != 1 && dirt.needsWatering() && !dirt.readyForHarvest() && IsFarmTool(current))
				{
					return KindCan;
				}
			}
			return -1;
		}
		// Port: empty tillable ground never auto-selects hoe; only artifact spots and seed spots do.
		return -1;
	}

	/// <summary>The object on the tile at the position; a stone or boulder within the pickaxe's 8 pixel side tolerance also counts.</summary>
	private static Object FindObject(GameLocation location, Vector2 position, int facing)
	{
		int x = (int)position.X;
		int y = (int)position.Y;
		Vector2 tile = new Vector2(x / 64, y / 64);
		if (location.Objects.TryGetValue(tile, out var obj) && obj != null)
		{
			return obj;
		}
		for (int side = 0; side < 2; side++)
		{
			int offset = ((side == 0) ? 8 : (-8));
			Vector2 other = ((facing == 0 || facing == 2) ? new Vector2((x + offset) / 64, y / 64) : new Vector2(x / 64, (y + offset) / 64));
			if (other != tile && location.Objects.TryGetValue(other, out var near) && near != null && (near.IsBreakableStone() || IsOreNode(near)))
			{
				return near;
			}
		}
		return null;
	}

	/// <summary>Ore, gem and geode nodes are "Stone" objects in most cases; some carry a "Stone"/"Node" suffix in their name.</summary>
	private static bool IsOreNode(Object obj)
	{
		if (obj.Category != -999 || obj.Name == null)
		{
			return false;
		}
		return obj.Name.EndsWith("Stone") || obj.Name.EndsWith("Node") || obj.Name.EndsWith("Boulder");
	}

	/// <summary>The resource clump covering the tile, or null.</summary>
	private static ResourceClump FindClump(GameLocation location, Vector2 tile)
	{
		if (location.resourceClumps == null)
		{
			return null;
		}
		for (int i = location.resourceClumps.Count - 1; i >= 0; i--)
		{
			ResourceClump clump = location.resourceClumps[i];
			if (clump != null && tile.X >= clump.netTile.Value.X && tile.X < clump.netTile.Value.X + (float)clump.width.Value && tile.Y >= clump.netTile.Value.Y && tile.Y < clump.netTile.Value.Y + (float)clump.height.Value)
			{
				return clump;
			}
		}
		return null;
	}

	/// <summary>Selects a slot the way the toolbar does.</summary>
	private static void SelectSlot(Farmer who, int index)
	{
		who.CurrentToolIndex = index;
		who.CurrentItem?.actionWhenBeingHeld(who);
	}

	private static bool IsKind(Item item, int kind)
	{
		switch (kind)
		{
		case KindPickaxe:
			return item is Pickaxe;
		case KindAxe:
			return item is Axe;
		case KindHoe:
			return item is Hoe;
		case KindCan:
			return item is WateringCan { WaterLeft: >0 };
		case KindWeapon:
			return item is MeleeWeapon weapon && !weapon.isScythe();
		case KindScythe:
			return item is MeleeWeapon scythe && scythe.isScythe();
		default:
			return false;
		}
	}

	/// <summary>The slot in the given toolbar row (0 = the visible one) of the item of that kind (the strongest weapon for KindWeapon); -1 when none.</summary>
	private static int FindInRow(Farmer who, int row, int kind)
	{
		int start = row * ToolbarRowSize;
		int end = System.Math.Min(start + ToolbarRowSize, who.Items.Count);
		int found = -1;
		int bestDamage = -1;
		for (int i = start; i < end; i++)
		{
			Item item = who.Items[i];
			if (item == null || !IsKind(item, kind))
			{
				continue;
			}
			if (kind != KindWeapon)
			{
				return i;
			}
			int damage = ((MeleeWeapon)item).maxDamage.Value;
			if (damage > bestDamage)
			{
				bestDamage = damage;
				found = i;
			}
		}
		return found;
	}

	/// <summary>The slot of the item in the visible toolbar row, or -1.</summary>
	private static int IndexInRow(Farmer who, Item item)
	{
		int end = System.Math.Min(ToolbarRowSize, who.Items.Count);
		for (int i = 0; i < end; i++)
		{
			if (who.Items[i] == item)
			{
				return i;
			}
		}
		return -1;
	}

	/// <summary>
	/// The slot of the visible row holding a tool of the kind (or the alternative kind, -1 for none); the current one if it already fits.
	/// A tool in another toolbar row is brought into the visible row with the game's own Farmer.shiftToolbar (fewest rotations);
	/// shift is the net rotation done (positive: to the right). -1 when there is no such tool.
	/// </summary>
	private static int EnsureKind(Farmer who, int kind, int alt, out int shift)
	{
		shift = 0;
		Item current = who.CurrentItem;
		if (current != null && (IsKind(current, kind) || (alt >= 0 && IsKind(current, alt))))
		{
			return who.CurrentToolIndex;
		}
		int total = who.Items.Count;
		int rows = (total + ToolbarRowSize - 1) / ToolbarRowSize;
		for (int row = 0; row < rows; row++)
		{
			int found = FindInRow(who, row, kind);
			if (found < 0 && alt >= 0)
			{
				found = FindInRow(who, row, alt);
			}
			if (found < 0)
			{
				continue;
			}
			if (row == 0)
			{
				return found;
			}
			// Port: only whole 12-slot rows can rotate (the game's own shiftToolbar).
			if (total % ToolbarRowSize != 0)
			{
				return -1;
			}
			Item target = who.Items[found];
			bool right = row <= rows - row;
			int count = (right ? row : (rows - row));
			for (int i = 0; i < count; i++)
			{
				who.shiftToolbar(right);
			}
			int index = IndexInRow(who, target);
			if (index >= 0)
			{
				shift = (right ? count : (-count));
			}
			return index;
		}
		return -1;
	}

	/// <summary>The direction (0 up, 1 right, 2 down, 3 left) from one bounding box to the other along the dominant axis.</summary>
	private static int DirectionTo(Microsoft.Xna.Framework.Rectangle from, Microsoft.Xna.Framework.Rectangle to)
	{
		int dx = to.Center.X - from.Center.X;
		int dy = to.Center.Y - from.Center.Y;
		if (System.Math.Abs(dx) > System.Math.Abs(dy))
		{
			return (dx > 0) ? 1 : 3;
		}
		return (dy > 0) ? 2 : 0;
	}

	/// <summary>
	/// The best living, visible, vulnerable Monster within 1.75 tiles (gap between bounding boxes) of the player, preferring the one in front;
	/// with facingOnly just a monster in the facing direction. anyNear is true when any living monster is in range, even one that cannot be hit
	/// right now (invincibility frames), so the weapon stays out while a fight goes on.
	/// </summary>
	private static Monster FindMonster(Farmer who, bool facingOnly, out bool anyNear)
	{
		anyNear = false;
		GameLocation location = Game1.currentLocation;
		// The slimes in a slime hutch are livestock, not enemies.
		if (location == null || location is SlimeHutch || location.characters == null)
		{
			return null;
		}
		Microsoft.Xna.Framework.Rectangle playerBox = who.GetBoundingBox();
		Monster best = null;
		int bestScore = int.MaxValue;
		for (int i = location.characters.Count - 1; i >= 0; i--)
		{
			if (!(location.characters[i] is Monster monster) || monster.Health <= 0 || monster.IsInvisible)
			{
				continue;
			}
			// A crumbled mummy waits to revive; only a bomb finishes it.
			if (monster is Mummy mummy && mummy.reviveTimer.Value > 0)
			{
				continue;
			}
			// A sword can't hurt these: a rock crab in its shell, an armored bug, a spiker.
			if (monster is Spiker || (monster is Bug bug && bug.isArmoredBug.Value) || (monster is RockCrab crab && !crab.shellGone.Value && crab.Sprite.currentFrame % 4 == 0))
			{
				continue;
			}
			Microsoft.Xna.Framework.Rectangle box = monster.GetBoundingBox();
			int gapX = System.Math.Max(0, System.Math.Max(playerBox.Left - box.Right, box.Left - playerBox.Right));
			int gapY = System.Math.Max(0, System.Math.Max(playerBox.Top - box.Bottom, box.Top - playerBox.Bottom));
			int distanceSquared = gapX * gapX + gapY * gapY;
			if (distanceSquared > AttackRange * AttackRange)
			{
				continue;
			}
			bool inFront = DirectionTo(playerBox, box) == who.FacingDirection;
			if (!facingOnly || inFront)
			{
				anyNear = true;
			}
			// Check if an obstacle or stone blocks the attack between player and monster
			int mdir = DirectionTo(playerBox, box);
			Vector2 interveningTile = who.Tile;
			switch (mdir)
			{
				case 0: interveningTile.Y -= 1f; break;
				case 1: interveningTile.X += 1f; break;
				case 2: interveningTile.Y += 1f; break;
				case 3: interveningTile.X -= 1f; break;
			}
			if (location.Objects.TryGetValue(interveningTile, out var blk) && blk != null && (blk.IsBreakableStone() || IsOreNode(blk) || !blk.isPassable()))
			{
				continue;
			}
			if (FindClump(location, interveningTile) != null)
			{
				continue;
			}
			if (monster.isInvincible() || (facingOnly && !inFront))
			{
				continue;
			}
			int score = distanceSquared - (inFront ? FacingBonus : 0);
			if (score < bestScore)
			{
				bestScore = score;
				best = monster;
			}
		}
		return best;
	}
}
