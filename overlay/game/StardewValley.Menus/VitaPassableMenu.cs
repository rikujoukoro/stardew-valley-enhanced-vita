using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace StardewValley.Menus;

/// <summary>Passable Crops settings, opened from the Mods menu: a large on/off row, then checkboxes and two growth stage sliders.</summary>
public class VitaPassableMenu : IClickableMenu
{
	private const int MenuWidth = 720;
	private const int MenuHeight = 640;
	private const int MasterRow = 0, TreeRow = 6, FruitTreeRow = 7;

	/// <summary>Row 1..9 → <see cref="VitaPassableCrops" /> option (-1: slider row).</summary>
	private static readonly int[] RowOption = { -1, VitaPassableCrops.Crops, VitaPassableCrops.Scarecrows, VitaPassableCrops.Sprinklers, VitaPassableCrops.Forage, VitaPassableCrops.TeaBushes, -1, -1, VitaPassableCrops.Weeds, VitaPassableCrops.SlowDown };
	private static readonly int[] RowText = { VitaPassableText.Mod, VitaPassableText.Crops, VitaPassableText.Scarecrows, VitaPassableText.Sprinklers, VitaPassableText.Forage, VitaPassableText.TeaBushes, VitaPassableText.Trees, VitaPassableText.FruitTrees, VitaPassableText.Weeds, VitaPassableText.SlowDown };

	private readonly ClickableComponent[] rows = new ClickableComponent[10];
	private readonly string[] labels = new string[10];
	private string title, footer, masterLabel;
	private LocalizedContentManager.LanguageCode cachedLanguage = (LocalizedContentManager.LanguageCode)(-1);
	private bool cachedEnabled;
	private int selected;
	private bool returnPending;

	public VitaPassableMenu()
		: base(0, 0, Math.Min(MenuWidth, Game1.uiViewport.Width - 32), Math.Min(MenuHeight, Game1.uiViewport.Height - 24))
	{
		xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
		yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;
		int top = yPositionOnScreen + 70;
		int masterHeight = 64;
		int rowHeight = Math.Max(32, Math.Min(44, (height - 70 - masterHeight - 16 - 44) / 9 - 4));
		allClickableComponents = new List<ClickableComponent>(rows.Length);
		for (int i = 0; i < rows.Length; i++)
		{
			Rectangle bounds = (i == MasterRow)
				? new Rectangle(xPositionOnScreen + 28, top, width - 56, masterHeight)
				: new Rectangle(xPositionOnScreen + 36, top + masterHeight + 16 + (i - 1) * (rowHeight + 4), width - 72, rowHeight);
			rows[i] = new ClickableComponent(bounds, "")
			{
				myID = i + 1,
				upNeighborID = i > 0 ? i : -1,
				downNeighborID = i < rows.Length - 1 ? i + 2 : -1,
				leftNeighborID = -1,
				rightNeighborID = -1
			};
			allClickableComponents.Add(rows[i]);
		}
		RefreshText();
		if (Game1.options.SnappyMenus) snapToDefaultClickableComponent();
	}

	// Same as VitaModsMenu: TitleMenu forwards pad events and Game1 turns Cross into a click.
	public override bool areGamePadControlsImplemented() => false;

	/// <summary>TitleMenu's Back handling would close the whole title submenu; Circle returns to the Mods menu instead.</summary>
	public override bool readyToClose() => false;

	public override void receiveGamePadButton(Buttons button)
	{
		if (button == Buttons.B || button == Buttons.Back) returnPending = true;
	}

	public override void update(GameTime time)
	{
		base.update(time);
		if (!returnPending) return;
		returnPending = false;
		Game1.playSound("bigDeSelect");
		TitleMenu.subMenu = new VitaModsMenu(VitaModsMenu.PassableRow);
	}

	public override void receiveKeyPress(Keys key)
	{
		if (key == Keys.Escape) { returnPending = true; return; }
		if (key == Keys.Enter || key == Keys.Space)
		{
			ClickableComponent row = currentlySnappedComponent ?? rows[selected];
			receiveLeftClick(row.bounds.Center.X, row.bounds.Center.Y);
			return;
		}
		if (selected == TreeRow || selected == FruitTreeRow)
		{
			int step = Game1.options.doesInputListContain(Game1.options.moveLeftButton, key) ? -1 : (Game1.options.doesInputListContain(Game1.options.moveRightButton, key) ? 1 : 0);
			if (step != 0)
			{
				ChangeStage(selected, step, wrap: false);
				return;
			}
		}
		base.receiveKeyPress(key);
		SetSelectedFromFocus();
	}

	public override void snapToDefaultClickableComponent()
	{
		currentlySnappedComponent = rows[selected];
		snapCursorToCurrentSnappedComponent();
	}

	private void SetSelectedFromFocus()
	{
		if (currentlySnappedComponent == null) return;
		int index = currentlySnappedComponent.myID - 1;
		if (index >= 0 && index < rows.Length) selected = index;
	}

	public override void performHoverAction(int x, int y)
	{
		for (int i = 0; i < rows.Length; i++)
		{
			if (!rows[i].containsPoint(x, y)) continue;
			selected = i;
			currentlySnappedComponent = rows[i];
			break;
		}
	}

	public override void receiveLeftClick(int x, int y, bool playSound = true)
	{
		// Ignore standard click repeats from holding A/Cross longer than 650ms.
		if (Game1.options.gamepadControls && Game1.input.GetGamePadState().IsButtonDown(Buttons.A) && Game1.oldPadState.IsButtonDown(Buttons.A)) return;
		for (int i = 0; i < rows.Length; i++)
		{
			if (!rows[i].containsPoint(x, y)) continue;
			selected = i;
			currentlySnappedComponent = rows[i];
			if (i == MasterRow)
			{
				VitaPassableCrops.Enabled = !VitaPassableCrops.Enabled;
			}
			else if (RowOption[i] >= 0)
			{
				VitaPassableCrops.ToggleOption(RowOption[i]);
			}
			else
			{
				Rectangle bar = SliderBar(rows[i]);
				if (x >= bar.X - 8 && x <= bar.Right + 8)
				{
					int max = (i == TreeRow) ? VitaPassableCrops.MaxTreeStage : VitaPassableCrops.MaxFruitTreeStage;
					int value = (int)Math.Round((float)(x - bar.X) / Math.Max(1, bar.Width) * max);
					SetStage(i, value);
				}
				else
				{
					ChangeStage(i, 1, wrap: true);
					return;
				}
			}
			RefreshText();
			if (playSound) Game1.playSound("drumkit6");
			return;
		}
	}

	private static int Stage(int row) => (row == TreeRow) ? VitaPassableCrops.TreeStage : VitaPassableCrops.FruitTreeStage;

	private static void SetStage(int row, int value)
	{
		if (row == TreeRow) VitaPassableCrops.TreeStage = value;
		else VitaPassableCrops.FruitTreeStage = value;
	}

	private void ChangeStage(int row, int step, bool wrap)
	{
		int max = (row == TreeRow) ? VitaPassableCrops.MaxTreeStage : VitaPassableCrops.MaxFruitTreeStage;
		int value = Stage(row) + step;
		if (wrap) value = (value > max) ? 0 : value;
		else if (value < 0 || value > max) return;
		SetStage(row, value);
		Game1.playSound("shiny4");
	}

	private Rectangle SliderBar(ClickableComponent row) => new Rectangle(row.bounds.Right - 236, row.bounds.Center.Y - 12, 180, 24);

	private void RefreshText()
	{
		bool enabled = VitaPassableCrops.Enabled;
		if (cachedLanguage == LocalizedContentManager.CurrentLanguageCode && cachedEnabled == enabled && title != null) return;
		cachedLanguage = LocalizedContentManager.CurrentLanguageCode;
		cachedEnabled = enabled;
		title = VitaPassableText.Get(VitaPassableText.Title);
		footer = VitaPassableText.Get(VitaPassableText.Footer);
		masterLabel = VitaPassableText.Get(VitaPassableText.Mod) + ": " + VitaModsText.Status(enabled);
		for (int i = 1; i < rows.Length; i++) labels[i] = VitaPassableText.Get(RowText[i]);
	}

	public override void draw(SpriteBatch b)
	{
		RefreshText();
		SetSelectedFromFocus();
		bool enabled = VitaPassableCrops.Enabled;
		drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9), xPositionOnScreen, yPositionOnScreen, width, height, Color.White, 4f);
		VitaUiText.DrawFittedLabel(b, title, new Rectangle(xPositionOnScreen + 28, yPositionOnScreen + 16, width - 56, 44));
		Rectangle master = rows[MasterRow].bounds;
		Color masterColor = enabled ? new Color(170, 230, 140) : new Color(235, 160, 140);
		drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9), master.X, master.Y, master.Width, master.Height, (selected == MasterRow) ? Color.Lerp(masterColor, Color.Wheat, 0.35f) : masterColor, 4f);
		VitaUiText.DrawFittedLabel(b, masterLabel, new Rectangle(master.X + 16, master.Y + 6, master.Width - 32, master.Height - 12));
		float dim = enabled ? 1f : 0.45f;
		for (int i = 1; i < rows.Length; i++)
		{
			Rectangle row = rows[i].bounds;
			if (selected == i)
			{
				drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9), row.X - 8, row.Y - 2, row.Width + 16, row.Height + 4, Color.Wheat, 4f, drawShadow: false);
			}
			int labelRight = (RowOption[i] >= 0) ? row.Right - 60 : SliderBar(rows[i]).X - 12;
			DrawLeftLabel(b, labels[i], new Rectangle(row.X + 12, row.Y, labelRight - row.X - 20, row.Height), Game1.textColor * dim);
			if (RowOption[i] >= 0)
			{
				bool on = VitaPassableCrops.Option(RowOption[i]);
				b.Draw(Game1.mouseCursors, new Vector2(row.Right - 48, row.Center.Y - 18), on ? OptionsCheckbox.sourceRectChecked : OptionsCheckbox.sourceRectUnchecked, Color.White * dim, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.4f);
			}
			else
			{
				Rectangle bar = SliderBar(rows[i]);
				int max = (i == TreeRow) ? VitaPassableCrops.MaxTreeStage : VitaPassableCrops.MaxFruitTreeStage;
				int value = Stage(i);
				drawTextureBox(b, Game1.mouseCursors, OptionsSlider.sliderBGSource, bar.X, bar.Y, bar.Width, bar.Height, Color.White * dim, 4f, drawShadow: false);
				float knobX = bar.X + (bar.Width - 40) * (value / (float)max);
				b.Draw(Game1.mouseCursors, new Vector2(knobX, bar.Y), OptionsSlider.sliderButtonRect, Color.White * dim, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.9f);
				Utility.drawTextWithShadow(b, value.ToString(), Game1.dialogueFont, new Vector2(bar.Right + 18, row.Center.Y - Game1.dialogueFont.LineSpacing / 2 + 4), Game1.textColor * dim);
			}
		}
		Rectangle last = rows[rows.Length - 1].bounds;
		DrawLeftLabel(b, footer, new Rectangle(xPositionOnScreen + 36, last.Bottom + 8, width - 72, Math.Max(24, yPositionOnScreen + height - last.Bottom - 24)), Game1.textColor * 0.8f, small: true);
		base.draw(b);
	}

	private static void DrawLeftLabel(SpriteBatch b, string text, Rectangle bounds, Color color, bool small = false)
	{
		if (string.IsNullOrEmpty(text) || bounds.Width <= 0) return;
		if (VitaUiText.UseSpriteText)
		{
			VitaUiText.DrawFittedLabel(b, text, bounds, color, left: true, small: small);
			return;
		}
		Microsoft.Xna.Framework.Graphics.SpriteFont font = small ? Game1.smallFont : Game1.dialogueFont;
		Vector2 measured = font.MeasureString(text);
		float fit = Math.Min(small ? 1f : 0.9f, Math.Min(bounds.Width / Math.Max(1f, measured.X), bounds.Height / Math.Max(1f, measured.Y)));
		Utility.drawTextWithShadow(b, text, font, new Vector2(bounds.X, bounds.Center.Y - measured.Y * fit / 2f), color, fit);
	}
}
