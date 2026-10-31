using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley.BellsAndWhistles;

namespace StardewValley;

/// <summary>
/// Unified text measurement and rendering helper for PS Vita UI.
/// Supports both Latin fonts (dialogueFont/smallFont) and non-Latin CJK/Russian/Thai fonts (SpriteText).
/// </summary>
public static class VitaUiText
{
	public static bool UseSpriteText =>
		!LocalizedContentManager.CurrentLanguageLatin ||
		(LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.ru && Game1.options != null && Game1.options.useAlternateFont);

	private static void EnsureFontLoaded()
	{
		if (SpriteText.spriteTexture == null && Game1.content != null)
		{
			try
			{
				SpriteText.spriteTexture = Game1.content.Load<Texture2D>("LooseSprites\\font_bold");
			}
			catch
			{
			}
		}
		if (SpriteText.coloredTexture == null && Game1.content != null)
		{
			try
			{
				SpriteText.coloredTexture = Game1.content.Load<Texture2D>("LooseSprites\\font_colored");
			}
			catch
			{
			}
		}
		if (UseSpriteText && (SpriteText.characterMap == null || SpriteText.characterMap.Count == 0))
		{
			try
			{
				SpriteText.LoadFontData(LocalizedContentManager.CurrentLanguageCode);
			}
			catch
			{
			}
		}
	}

	public static Vector2 Measure(string text, bool small = false, int widthConstraint = 999999)
	{
		if (string.IsNullOrEmpty(text))
			return Vector2.Zero;

		if (!UseSpriteText)
		{
			SpriteFont font = small ? Game1.smallFont : Game1.dialogueFont;
			return font.MeasureString(text);
		}

		EnsureFontLoaded();
		string normalized = text.Replace("\r\n", "^").Replace('\n', '^');
		float oldZoom = SpriteText.fontPixelZoom;
		try
		{
			if (small)
			{
				SpriteText.fontPixelZoom = oldZoom * 0.75f;
			}
			int w = SpriteText.getWidthOfString(normalized, widthConstraint);
			int h = SpriteText.getHeightOfString(normalized, widthConstraint);
			return new Vector2(w, h);
		}
		finally
		{
			SpriteText.fontPixelZoom = oldZoom;
		}
	}

	public static void Draw(SpriteBatch b, string text, Vector2 position, Color color, float scale = 1f, bool small = false, int maxWidth = 99999)
	{
		if (string.IsNullOrEmpty(text))
			return;

		if (!UseSpriteText)
		{
			SpriteFont font = small ? Game1.smallFont : Game1.dialogueFont;
			Utility.drawTextWithShadow(b, text, font, position, color, scale);
			return;
		}

		EnsureFontLoaded();
		string normalized = text.Replace("\r\n", "^").Replace('\n', '^');
		float oldZoom = SpriteText.fontPixelZoom;
		try
		{
			float zoomMultiplier = small ? 0.75f : 1f;
			SpriteText.fontPixelZoom = oldZoom * scale * zoomMultiplier;
			SpriteText.VitaPlainPlus = true;
			SpriteText.drawString(b, normalized, (int)position.X, (int)position.Y, color: color, width: maxWidth);
		}
		finally
		{
			SpriteText.VitaPlainPlus = false;
			SpriteText.fontPixelZoom = oldZoom;
		}
	}

	/// <summary>
	/// Rows (font units, from the line top) that this language's letters occupy, from a fixed sample so every label of a
	/// menu gets the same size. Korean's BmFont line box is 49 px for ~34 px letters: fitting by the box halved the labels
	/// and centring by it pushed them to the top.
	/// </summary>
	private static bool LetterBand(out int top, out int bottom)
	{
		top = int.MaxValue;
		bottom = int.MinValue;
		if (SpriteText.characterMap == null)
			return false;
		foreach (char c in "AЯ漢国あ가한") // capitals and common full-height letters; no descenders, no tall outliers like 힣
		{
			if (!SpriteText.characterMap.TryGetValue(c, out BmFont.FontChar fc) || fc.Height <= 1)
				continue;
			top = Math.Min(top, fc.YOffset);
			bottom = Math.Max(bottom, fc.YOffset + fc.Height);
		}
		return bottom > top;
	}

	/// <summary>The y shift SpriteText.drawString itself applies to a non-Latin line at the current zoom.</summary>
	private static float DrawYShift()
	{
		float zoom = SpriteText.FontPixelZoom;
		LocalizedContentManager.LanguageCode code = LocalizedContentManager.CurrentLanguageCode;
		float shift = 0f;
		if (zoom < 4f && code != LocalizedContentManager.LanguageCode.ko && code != LocalizedContentManager.LanguageCode.zh)
			shift += (int)((4f - zoom) * 4f);
		if (code == LocalizedContentManager.LanguageCode.ko)
			shift -= 8f;
		if (code == LocalizedContentManager.LanguageCode.zh)
			shift += 4f;
		if (code == LocalizedContentManager.LanguageCode.ja || code == LocalizedContentManager.LanguageCode.zh || code == LocalizedContentManager.LanguageCode.th)
			shift -= (4f - zoom) * 4f;
		return shift;
	}

	/// <summary>
	/// Breaks text into '^'-separated lines no wider than maxWidth when drawn with Draw(scale, small). SpriteText's own
	/// Japanese/Chinese wrapping treats a whole kana/kanji run as one word and leaves one-character lines ("ス", "に"),
	/// and its measured height then disagrees with the drawn one; pre-broken lines are drawn as they are.
	/// CJK characters break anywhere (closing punctuation stays on its line), everything else at spaces.
	/// </summary>
	public static string WrapForSprite(string text, int maxWidth, float scale = 1f, bool small = false)
	{
		if (string.IsNullOrEmpty(text) || !UseSpriteText)
			return text;
		EnsureFontLoaded();
		string normalized = text.Replace("\r\n", "^").Replace('\n', '^');
		float oldZoom = SpriteText.fontPixelZoom;
		try
		{
			SpriteText.fontPixelZoom = oldZoom * scale * (small ? 0.75f : 1f);
			System.Text.StringBuilder result = new System.Text.StringBuilder(normalized.Length + 16);
			string[] paragraphs = normalized.Split('^');
			for (int p = 0; p < paragraphs.Length; p++)
			{
				if (p > 0) result.Append('^');
				string line = "";
				string paragraph = paragraphs[p];
				int i = 0;
				while (i < paragraph.Length)
				{
					// Next token: one CJK character (with any closing punctuation after it) or a space-delimited word with its space.
					int end = i;
					if (IsCjk(paragraph[i]) || IsClosing(paragraph[i]) || IsOpening(paragraph[i]))
					{
						// An opening bracket never ends a line: it travels with the character after it.
						while (end < paragraph.Length - 1 && IsOpening(paragraph[end])) end++;
						end++;
					}
					else
					{
						while (end < paragraph.Length && paragraph[end] != ' ' && !IsCjk(paragraph[end])) end++;
						while (end < paragraph.Length && paragraph[end] == ' ') end++;
					}
					while (end < paragraph.Length && IsClosing(paragraph[end])) end++;
					string token = paragraph.Substring(i, end - i);
					i = end;
					string candidate = line + token;
					if (line.Length == 0 || SpriteText.getWidthOfString(candidate.TrimEnd(' ')) <= maxWidth)
					{
						line = candidate;
						continue;
					}
					result.Append(line.TrimEnd(' ')).Append('^');
					line = token.TrimStart(' ');
				}
				result.Append(line.TrimEnd(' '));
			}
			return result.ToString();
		}
		finally
		{
			SpriteText.fontPixelZoom = oldZoom;
		}
	}

	private static bool IsCjk(char c) => (c >= '⺀' && c <= '鿿') || (c >= '豈' && c <= '﫿') || (c >= '＀' && c <= '￯');

	private static bool IsOpening(char c) => c == '（' || c == '「' || c == '『' || c == '《' || c == '“' || c == '‘';

	private static bool IsClosing(char c) => c == '、' || c == '。' || c == '，' || c == '．' || c == '：' || c == '；' || c == '！' || c == '？' || c == '）' || c == '」' || c == '』' || c == '》' || c == ')' || c == ',' || c == '.' || c == ':' || c == 'ー' || c == 'ッ' || c == 'っ' || c == 'ャ' || c == 'ュ' || c == 'ョ' || c == 'ゃ' || c == 'ゅ' || c == 'ょ' || c == 'ァ' || c == 'ィ' || c == 'ゥ' || c == 'ェ' || c == 'ォ';

	private static string _titleFontText;

	private static SpriteFont _titleFontChecked;

	private static bool _titleFontHasText;

	public static void DrawTitleLabel(SpriteBatch b, string text, Rectangle bounds, Color color)
	{
		if (UseSpriteText)
		{
			// Chinese/Japanese/Korean: the title appears before anything else needs the SpriteText font, and its first use
			// loads all its pages (Chinese 4 x 1024x1024), so the Mods sign came seconds after the other three. The
			// dialogue SpriteFont is already loaded; use it when it has every letter of the label.
			SpriteFont font = Game1.dialogueFont;
			if (font != null && (!ReferenceEquals(_titleFontChecked, font) || _titleFontText != text))
			{
				_titleFontChecked = font;
				_titleFontText = text;
				_titleFontHasText = true;
				foreach (char c in text)
				{
					if (c != ' ' && !font.Characters.Contains(c)) { _titleFontHasText = false; break; }
				}
			}
			if (font != null && _titleFontHasText)
			{
				Vector2 size = font.MeasureString(text);
				// The Chinese SpriteFont's line is much taller than its letters: fit those by three quarters of it.
				float lineShare = (LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh) ? 0.75f : 1f;
				float fit = Math.Min(bounds.Width / Math.Max(1f, size.X), bounds.Height / Math.Max(1f, size.Y * lineShare));
				Vector2 position = new Vector2(bounds.Center.X, bounds.Center.Y) - size * fit / 2f;
				Utility.drawTextWithShadow(b, text, font, position, color, fit);
				return;
			}
			DrawFittedLabel(b, text, bounds, color);
			return;
		}
		EnsureFontLoaded();
		// The title signs use thick pixel lettering, not the thin dialogue SpriteFont.
		int w = SpriteText.getWidthOfString(text), h = SpriteText.getHeightOfString(text);
		float oldZoom = SpriteText.fontPixelZoom;
		try
		{
			float fit = Math.Min(bounds.Width / (float)Math.Max(1, w), bounds.Height / (float)Math.Max(1, h));
			SpriteText.fontPixelZoom = oldZoom * fit;
			int width = SpriteText.getWidthOfString(text), height = SpriteText.getHeightOfString(text);
			int correction = SpriteText.FontPixelZoom < 4f ? (int)((4f - SpriteText.FontPixelZoom) * 4f) : 0;
			SpriteText.drawString(b, text, bounds.Center.X - width / 2, bounds.Center.Y - height / 2 - correction, width: width + 8, color: color, layerDepth: 1f);
		}
		finally { SpriteText.fontPixelZoom = oldZoom; }
	}

	/// <summary>Draws text as large as fits in bounds (never above its normal size), centred or left-aligned, centred vertically.</summary>
	public static void DrawFittedLabel(SpriteBatch b, string text, Rectangle bounds, Color? color = null, bool left = false, bool small = false)
	{
		if (string.IsNullOrEmpty(text))
			return;

		Color textColor = color ?? Game1.textColor;

		if (!UseSpriteText)
		{
			Vector2 size = Game1.dialogueFont.MeasureString(text);
			float scale = Math.Min(1f, Math.Min((float)bounds.Width / Math.Max(1f, size.X), (float)bounds.Height / Math.Max(1f, size.Y)));
			Vector2 pos = new Vector2(left ? bounds.X : bounds.Center.X - size.X * scale / 2f, bounds.Center.Y - size.Y * scale / 2f);
			Utility.drawTextWithShadow(b, text, Game1.dialogueFont, pos, textColor, scale);
			return;
		}

		EnsureFontLoaded();
		string normalized = text.Replace("\r\n", "^").Replace('\n', '^');
		float oldZoom = SpriteText.fontPixelZoom;
		try
		{
			if (small)
				SpriteText.fontPixelZoom = oldZoom * 0.75f;
			float baseZoom = SpriteText.fontPixelZoom;
			int w = SpriteText.getWidthOfString(normalized);
			int h = SpriteText.getHeightOfString(normalized);
			int top = 0, bottom = 0;
			bool band = normalized.IndexOf('^') < 0 && LetterBand(out top, out bottom);
			float fitH = band ? (bottom - top) * SpriteText.FontPixelZoom + 8f : h;
			float scaleFactor = Math.Min(1f, Math.Min((float)bounds.Width / Math.Max(1f, w), (float)bounds.Height / Math.Max(1f, fitH)));
			SpriteText.fontPixelZoom = baseZoom * scaleFactor;
			int scaledW = SpriteText.getWidthOfString(normalized);
			int posX = left ? bounds.X : bounds.Center.X - scaledW / 2;
			int posY = band
				? (int)(bounds.Center.Y - (top + bottom) / 2f * SpriteText.FontPixelZoom - DrawYShift())
				: bounds.Center.Y - SpriteText.getHeightOfString(normalized) / 2;
			SpriteText.VitaPlainPlus = true;
			// Width to the box's right edge: with less room SpriteText wraps the trailing '>' icon onto a new line.
			SpriteText.drawString(b, normalized, posX, posY, width: Math.Max(scaledW, bounds.Right - posX) + 8, color: textColor);
		}
		finally
		{
			SpriteText.VitaPlainPlus = false;
			SpriteText.fontPixelZoom = oldZoom;
		}
	}
}
