using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace StardewValley.Menus;

/// <summary>Compact Vita prompt shown after changing the game language.</summary>
internal sealed class VitaLanguageRestartDialog : IClickableMenu
{
	private readonly bool _isTurkish;
	private readonly ClickableTextureComponent _restartButton;
	private readonly ClickableTextureComponent _cancelButton;
	private bool _restartFailed;
	private bool _restartPending;

	public VitaLanguageRestartDialog()
		: base(0, 0, 0, 0)
	{
		_isTurkish = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.tr;
		UpdateLayout();
		int buttonY = yPositionOnScreen + height - 108;
		_restartButton = new ClickableTextureComponent("restart", new Rectangle(xPositionOnScreen + width / 2 - 176, buttonY, 64, 64), null, null, Game1.mouseCursors, Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 46), 1f)
		{
			myID = 101,
			rightNeighborID = 102
		};
		_cancelButton = new ClickableTextureComponent("cancel", new Rectangle(xPositionOnScreen + width / 2 + 112, buttonY, 64, 64), null, null, Game1.mouseCursors, Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 47), 1f)
		{
			myID = 102,
			leftNeighborID = 101
		};
		if (Game1.options.SnappyMenus)
		{
			allClickableComponents = new System.Collections.Generic.List<ClickableComponent> { _restartButton, _cancelButton };
			currentlySnappedComponent = _cancelButton;
			snapCursorToCurrentSnappedComponent();
		}
	}

	private void UpdateLayout()
	{
		Rectangle safe = new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height);
		width = System.Math.Min(800, safe.Width - 32);
		height = System.Math.Min(360, safe.Height - 32);
		xPositionOnScreen = safe.X + (safe.Width - width) / 2;
		yPositionOnScreen = safe.Y + (safe.Height - height) / 2;
	}

	private string GetMessage()
	{
		if (_isTurkish)
		{
			return _restartFailed
				? "Yeniden başlatılamadı. Tekrar deneyin veya oyunu daha sonra yeniden açın."
				: "Dil değişti. Yeni dilin uygulanması için oyun yeniden başlatılsın mı?";
		}
		return _restartFailed
			? "The game could not restart. Try again or restart it later."
			: "Language changed. Restart now for memory stability?";
	}

	private void ConfirmRestart()
	{
		if (_restartPending) return;
		try
		{
			_restartPending = VitaPlatform.TryRestartGame();
			if (!_restartPending)
			{
				_restartFailed = true;
			}
		}
		catch
		{
			_restartFailed = true;
		}
	}

	private void Cancel()
	{
		if (Game1.activeClickableMenu is TitleMenu) TitleMenu.subMenu = null;
		else exitThisMenu();
	}

	public override void receiveLeftClick(int x, int y, bool playSound = true)
	{
		if (_restartButton.containsPoint(x, y))
		{
			ConfirmRestart();
		}
		else if (_cancelButton.containsPoint(x, y))
		{
			Cancel();
		}
	}

	public override void receiveKeyPress(Keys key)
	{
		base.receiveKeyPress(key);
		if (key == Keys.Enter || key == Keys.Space)
		{
			if (currentlySnappedComponent?.myID == 101)
			{
				ConfirmRestart();
			}
			else
			{
				Cancel();
			}
		}
		else if (key == Keys.Escape)
		{
			Cancel();
		}
	}

	public override bool areGamePadControlsImplemented()
	{
		return true;
	}

	public override void receiveGamePadButton(Buttons button)
	{
		if (button == Buttons.A)
		{
			if (currentlySnappedComponent?.myID == 101)
			{
				ConfirmRestart();
			}
			else
			{
				Cancel();
			}
		}
		else if (button == Buttons.B)
		{
			Cancel();
		}
		else if (button == Buttons.LeftThumbstickLeft || button == Buttons.DPadLeft)
		{
			currentlySnappedComponent = _restartButton;
		}
		else if (button == Buttons.LeftThumbstickRight || button == Buttons.DPadRight)
		{
			currentlySnappedComponent = _cancelButton;
		}
		if (currentlySnappedComponent != null) snapCursorToCurrentSnappedComponent();
	}

	public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
	{
		base.gameWindowSizeChanged(oldBounds, newBounds);
		UpdateLayout();
		int buttonY = yPositionOnScreen + height - 108;
		_restartButton.setPosition(xPositionOnScreen + width / 2 - 176, buttonY);
		_cancelButton.setPosition(xPositionOnScreen + width / 2 + 112, buttonY);
	}

	public override void performHoverAction(int x, int y)
	{
		_restartButton.tryHover(x, y);
		_cancelButton.tryHover(x, y);
	}

	public override void draw(SpriteBatch b)
	{
		b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.55f);
		IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9), xPositionOnScreen, yPositionOnScreen, width, height, Color.White, 4f);
		string message = Game1.parseText(GetMessage(), Game1.dialogueFont, width - IClickableMenu.borderWidth * 2 - 48);
		Vector2 messageSize = Game1.dialogueFont.MeasureString(message);
		Vector2 messagePosition = new Vector2(xPositionOnScreen + (width - messageSize.X) / 2f, yPositionOnScreen + 48);
		b.DrawString(Game1.dialogueFont, message, messagePosition, Game1.textColor);
		_restartButton.draw(b, currentlySnappedComponent == _restartButton ? Color.LightGreen : Color.White, 1f);
		_cancelButton.draw(b, currentlySnappedComponent == _cancelButton ? Color.LightGreen : Color.White, 1f);
		string restartLabel = _isTurkish ? "Yeniden başlat" : "Restart";
		string cancelLabel = _isTurkish ? "Şimdi değil" : "Later";
		Vector2 restartSize = Game1.smallFont.MeasureString(restartLabel);
		Vector2 cancelSize = Game1.smallFont.MeasureString(cancelLabel);
		b.DrawString(Game1.smallFont, restartLabel, new Vector2(_restartButton.bounds.Center.X - restartSize.X / 2f, _restartButton.bounds.Bottom + 4), Game1.textColor);
		b.DrawString(Game1.smallFont, cancelLabel, new Vector2(_cancelButton.bounds.Center.X - cancelSize.X / 2f, _cancelButton.bounds.Bottom + 4), Game1.textColor);
		drawMouse(b);
	}
}
