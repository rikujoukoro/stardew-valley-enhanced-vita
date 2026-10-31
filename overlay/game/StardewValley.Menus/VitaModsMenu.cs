using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace StardewValley.Menus;

/// <summary>Built-in features, using standard title-menu clicks. The last row opens the Passable Crops menu.</summary>
public class VitaModsMenu : IClickableMenu
{
	private const int MenuWidth = 760;
	private const int MenuHeight = 700;
	public const int ChestRow = 4, RingsRow = 5, PassableRow = 6;
	private readonly ClickableComponent[] toggles = new ClickableComponent[7];
	private LocalizedContentManager.LanguageCode cachedLanguage = (LocalizedContentManager.LanguageCode)(-1);
	private int cachedFlags = -1, selected;
	private string title, help;
	private float helpScale;
	private readonly int helpOffset;

	public VitaModsMenu(int selectedRow = 0)
		: base(0, 0, Math.Min(MenuWidth, Game1.uiViewport.Width - 32), Math.Min(MenuHeight, Game1.uiViewport.Height - 24))
	{
		xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
		yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;
		selected = Math.Max(0, Math.Min(toggles.Length - 1, selectedRow));
		int rowHeight = Math.Min(46, Math.Max(30, (height - 210) / toggles.Length));
		int rowStep = rowHeight + 8;
		helpOffset = 62 + toggles.Length * rowStep + 12;
		allClickableComponents = new List<ClickableComponent>(toggles.Length);
		for (int i = 0; i < toggles.Length; i++)
		{
			toggles[i] = new ClickableComponent(new Rectangle(xPositionOnScreen + 28, yPositionOnScreen + 62 + i * rowStep, width - 56, rowHeight), "")
			{
				myID = i + 1,
				upNeighborID = i > 0 ? i : -1,
				downNeighborID = i < toggles.Length - 1 ? i + 2 : -1,
				leftNeighborID = -1,
				rightNeighborID = -1
			};
			allClickableComponents.Add(toggles[i]);
		}
		RefreshText();
		if (Game1.options.SnappyMenus) snapToDefaultClickableComponent();
	}

	// TitleMenu forwards pad events and Game1 generates a click for A/Cross.
	// Handling A here as well would immediately toggle the setting back again.
	public override bool areGamePadControlsImplemented() => false;
	public override void receiveGamePadButton(Buttons button)
	{
		if (button == Buttons.B || button == Buttons.Back) Close();
	}
	private static void Close()
	{
		TitleMenu.subMenu = null;
		if (Game1.activeClickableMenu is TitleMenu titleMenu && Game1.options.SnappyMenus)
		{
			titleMenu.setCurrentlySnappedComponentTo(TitleMenu.region_modsButton);
			titleMenu.snapCursorToCurrentSnappedComponent();
		}
	}
	public override void receiveKeyPress(Keys key)
	{
		if (key == Keys.Escape) { Close(); return; }
		if (key == Keys.Enter || key == Keys.Space)
		{
			ClickableComponent row = currentlySnappedComponent ?? toggles[selected];
			receiveLeftClick(row.bounds.Center.X, row.bounds.Center.Y);
			return;
		}
		base.receiveKeyPress(key);
		SetSelectedFromFocus();
	}
	public override void snapToDefaultClickableComponent()
	{
		currentlySnappedComponent = toggles[selected];
		snapCursorToCurrentSnappedComponent();
	}
	private void SetSelectedFromFocus()
	{
		if (currentlySnappedComponent == null) return;
		int index = currentlySnappedComponent.myID - 1;
		if (index < 0 || index >= toggles.Length || selected == index) return;
		selected = index;
		RefreshHelp();
	}
	public override void performHoverAction(int x, int y)
	{
		for (int i = 0; i < toggles.Length; i++)
		{
			if (!toggles[i].containsPoint(x, y) || selected == i) continue;
			selected = i;
			currentlySnappedComponent = toggles[i];
			RefreshHelp();
			break;
		}
	}
	public override void receiveLeftClick(int x, int y, bool playSound = true)
	{
		// Ignore standard click repeats from holding A/Cross longer than 650ms.
		if (Game1.options.gamepadControls && Game1.input.GetGamePadState().IsButtonDown(Buttons.A) && Game1.oldPadState.IsButtonDown(Buttons.A)) return;
		for (int i = 0; i < toggles.Length; i++)
		{
			if (!toggles[i].containsPoint(x, y)) continue;
			selected = i;
			currentlySnappedComponent = toggles[i];
			if (i == PassableRow)
			{
				if (playSound) Game1.playSound("bigSelect");
				TitleMenu.subMenu = new VitaPassableMenu();
				return;
			}
			if (i == 0) VitaTractorMod.Enabled = !VitaTractorMod.Enabled;
			else if (i == RingsRow) VitaQoLSettings.Toggle(4);
			else VitaQoLSettings.Toggle(i - 1); // the chest row is QoL bit 3
			RefreshText();
			RefreshHelp();
			if (playSound) Game1.playSound("drumkit6");
			return;
		}
	}
	private void RefreshText()
	{
		int flags = VitaQoLSettings.Flags | (VitaTractorMod.Enabled ? 32 : 0) | (VitaPassableCrops.Enabled ? 64 : 0);
		if (cachedFlags == flags && cachedLanguage == LocalizedContentManager.CurrentLanguageCode) return;
		cachedFlags = flags;
		cachedLanguage = LocalizedContentManager.CurrentLanguageCode;
		title = VitaModsText.Title;
		toggles[0].label = VitaModsText.TractorLabel;
		for (int i = 1; i < ChestRow; i++)
			toggles[i].label = VitaQoLText.Get(i - 1) + ": " + VitaModsText.Status(VitaQoLSettings.Enabled(i - 1));
		toggles[ChestRow].label = VitaChestText.Get(VitaChestText.Label) + ": " + VitaModsText.Status(VitaChestMod.Enabled);
		toggles[RingsRow].label = VitaRingsText.Get(VitaRingsText.Label) + ": " + VitaModsText.Status(VitaExtraRings.Enabled);
		toggles[PassableRow].label = VitaPassableText.Get(VitaPassableText.Title) + ": " + VitaModsText.Status(VitaPassableCrops.Enabled) + " >";
		RefreshHelp();
	}
	private void RefreshHelp()
	{
		string rawHelp = selected == 0 ? VitaModsText.Help : selected == ChestRow ? VitaChestText.Get(VitaChestText.Help) : selected == RingsRow ? VitaRingsText.Get(VitaRingsText.Help) : selected == PassableRow ? VitaPassableText.Get(VitaPassableText.RowHelp) : VitaQoLText.Get(selected + 2);
		int maxW = width - 56;
		int availableH = height - helpOffset - 16;
		if (VitaUiText.UseSpriteText)
		{
			// Row-label size first, then smaller steps until the pre-broken lines fit (Draw then uses small: false).
			for (helpScale = 1f; ; helpScale -= 0.05f)
			{
				help = VitaUiText.WrapForSprite(rawHelp, maxW, helpScale);
				if (helpScale <= 0.5f || VitaUiText.Measure(help, widthConstraint: maxW * 2).Y * helpScale <= availableH) break;
			}
		}
		else
		{
			help = Game1.parseText(rawHelp, Game1.smallFont, maxW);
			Vector2 size = Game1.smallFont.MeasureString(help);
			helpScale = Math.Min(1f, (float)availableH / Math.Max(1f, size.Y));
		}
	}
	public override void draw(SpriteBatch b)
	{
		RefreshText();
		SetSelectedFromFocus();
		drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9), xPositionOnScreen, yPositionOnScreen, width, height, Color.White, 4f);
		DrawFittedLabel(b, title, new Rectangle(xPositionOnScreen + 28, yPositionOnScreen + 14, width - 56, 42));
		for (int i = 0; i < toggles.Length; i++)
		{
			Rectangle row = toggles[i].bounds;
			drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9), row.X, row.Y, row.Width, row.Height, selected == i ? Color.Wheat : Color.White, 4f);
			DrawFittedLabel(b, toggles[i].label, new Rectangle(row.X + 16, row.Y + 4, row.Width - 32, row.Height - 8));
		}
		if (VitaUiText.UseSpriteText)
			VitaUiText.Draw(b, help, new Vector2(xPositionOnScreen + 28, yPositionOnScreen + helpOffset), Game1.textColor, helpScale, maxWidth: width - 48);
		else
			VitaUiText.Draw(b, help, new Vector2(xPositionOnScreen + 28, yPositionOnScreen + helpOffset), Game1.textColor, helpScale, small: true, maxWidth: width - 56);
		base.draw(b);
	}
	private static void DrawFittedLabel(SpriteBatch b, string text, Rectangle bounds)
	{
		VitaUiText.DrawFittedLabel(b, text, bounds);
	}
}
