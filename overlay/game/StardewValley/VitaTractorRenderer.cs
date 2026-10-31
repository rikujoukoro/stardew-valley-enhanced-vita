using System;
using System.IO;
using System.IO.Compression;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley.Characters;

namespace StardewValley;

/// <summary>Official SMAPI Tractor Mod sprite renderer with authentic 32x32 vehicle dimensions and cabin seating.</summary>
public static class VitaTractorRenderer
{
	private const int FrameSize = 32;
	private const int Columns = 7;
	private static Texture2D _texture;
	private static GraphicsDevice _device;
	private static bool _loadFailed;

	// Compressed embedded SMAPI tractor texture bytes (224x128 RGBA, 1:1 horse layout)
	private const string TractorPngRawDeflate = "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";

	public static bool Draw(Horse horse, SpriteBatch batch)
	{
		if (horse == null || !VitaTractorMod.IsTractor(horse))
			return false;

		EnsureTexture();
		if (_texture == null)
			return false;

		int facing = horse.FacingDirection;
		// The sheet uses the vanilla Horse layout: south, east, north (not north first).
		// Reuse the horse's animation frame so the wheels and rider bob stay in sync.
		Rectangle source = GetFrameSource(facing, horse.isMoving() ? horse.Sprite.CurrentFrame % Columns : 0);

		Vector2 origin = new Vector2(16f, 24f);
		Vector2 screenPos = horse.getLocalPosition(Game1.viewport)
			+ new Vector2(horse.GetSpriteWidthForPositioning() * 4 / 2, horse.GetBoundingBox().Height / 2);

		SpriteEffects effects = facing == 3 ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
		float baseDepth = horse.rider != null ? horse.rider.getDrawLayer() : horse.StandingPixel.Y / 10000f;

		if (facing == 2 && horse.rider != null)
		{
			// Draw tractor body/cabin behind rider
			batch.Draw(_texture, screenPos, source, Color.White, 0f, origin, 4f, effects, baseDepth - 0.0001f);

			// Draw hood/steering wheel overlay in front of rider so farmer sits inside the cabin
			Rectangle hoodSource = new Rectangle(source.X, source.Y + 15, FrameSize, FrameSize - 15);
			Vector2 hoodOrigin = new Vector2(16f, 24f - 15f);
			batch.Draw(_texture, screenPos, hoodSource, Color.White, 0f, hoodOrigin, 4f, effects, baseDepth + 0.0016f);
		}
		else
		{
			batch.Draw(_texture, screenPos, source, Color.White, 0f, origin, 4f, effects, baseDepth - 0.0001f);
		}

		return true;
	}

	// Tractor kit ring, 16x16 in the game's ring style: a green band (tractor paint) set with a tractor tyre
	// (dark rubber, grey lugs, yellow hub) where a vanilla ring has its stone.
	private static readonly string[] KitRingRows = new string[16]
	{
		"......KKKK......",
		"....KKDTTDKK....",
		"...KDTDDDDTDK...",
		"...KTDYWYYDTK...",
		"...KDDYyyYDDK...",
		"...KTDYyyYDTK...",
		"...KDTDYYDTDK...",
		"....KKDTTDKK....",
		"...KGGKKKKGGK...",
		"..KGLK....KGgK..",
		"..KGK......KgK..",
		"..KGK......KgK..",
		"..KGGK....KGgK..",
		"...KGGKKKKGgK...",
		"....KKGGggKK....",
		"......KKKK......"
	};

	private static Texture2D _kitRing;

	/// <summary>The tractor kit ring's 16x16 menu icon (1 KB), built once from <see cref="KitRingRows" />.</summary>
	internal static Texture2D KitRingTexture
	{
		get
		{
			GraphicsDevice device = Game1.graphics?.GraphicsDevice;
			if (device == null)
				return null;
			if (_kitRing != null && !_kitRing.IsDisposed && _kitRing.GraphicsDevice == device)
				return _kitRing;
			Color[] pixels = new Color[16 * 16];
			for (int y = 0; y < 16; y++)
			{
				for (int x = 0; x < 16; x++)
				{
					switch (KitRingRows[y][x])
					{
					case 'K': pixels[y * 16 + x] = new Color(43, 26, 14); break;
					case 'D': pixels[y * 16 + x] = new Color(58, 56, 54); break;
					case 'T': pixels[y * 16 + x] = new Color(122, 118, 112); break;
					case 'Y': pixels[y * 16 + x] = new Color(242, 194, 48); break;
					case 'y': pixels[y * 16 + x] = new Color(184, 134, 28); break;
					case 'W': pixels[y * 16 + x] = new Color(255, 247, 200); break;
					case 'G': pixels[y * 16 + x] = new Color(63, 143, 47); break;
					case 'g': pixels[y * 16 + x] = new Color(38, 94, 30); break;
					case 'L': pixels[y * 16 + x] = new Color(124, 196, 74); break;
					default: pixels[y * 16 + x] = Color.Transparent; break;
					}
				}
			}
			_kitRing = new Texture2D(device, 16, 16);
			_kitRing.SetData(pixels);
			return _kitRing;
		}
	}

	/// <summary>The tractor sheet for the title screen's Mods sign; loaded once and kept (224x128, ~112 KB).</summary>
	internal static Texture2D IconTexture
	{
		get
		{
			EnsureTexture();
			return _texture;
		}
	}

	internal static Rectangle GetFrameSource(int facing, int animation)
	{
		int row = facing == 0 ? 2 : (facing == 1 || facing == 3) ? 1 : 0;
		return new Rectangle(Math.Max(0, Math.Min(Columns - 1, animation)) * FrameSize, row * FrameSize, FrameSize, FrameSize);
	}

	internal static Vector2 GetExhaustWorldPosition(Horse horse)
	{
		Vector2 pipe = horse.FacingDirection == 1 ? new Vector2(20, 12)
			: horse.FacingDirection == 3 ? new Vector2(12, 12) : new Vector2(23, 11);
		return horse.Position + horse.drawOffset + new Vector2(horse.GetSpriteWidthForPositioning() * 2 - 64,
			horse.GetBoundingBox().Height / 2f - 96 + horse.yJumpOffset) + pipe * 4f;
	}

	public static void Reset()
	{
		try
		{
			_texture?.Dispose();
		}
		catch
		{
		}
		_texture = null;
		_device = null;
		_loadFailed = false;
	}

	internal static void EnsureTexture()
	{
		GraphicsDevice device = Game1.graphics?.GraphicsDevice;
		if (device == null)
			return;

		if (_texture != null && _device == device && !_texture.IsDisposed)
			return;
		if (_loadFailed && _device == device) return;

		Reset();
		_device = device;

		// 1. Try loading from game Content folder
		try
		{
			string path = Path.Combine(Game1.content.RootDirectory, "Animals", "tractor.png");
			if (File.Exists(path))
			{
				using (FileStream fs = File.OpenRead(path))
				{
					_texture = LoadTexture(device, fs);
					return;
				}
			}
		}
		catch
		{
		}

		// 2. Fallback to embedded SMAPI tractor asset
		try
		{
			byte[] compressed = Convert.FromBase64String(TractorPngRawDeflate);
			using (MemoryStream msComp = new MemoryStream(compressed))
			using (DeflateStream ds = new DeflateStream(msComp, CompressionMode.Decompress))
			using (MemoryStream msOut = new MemoryStream())
			{
				byte[] buf = new byte[4096];
				int read;
				while ((read = ds.Read(buf, 0, buf.Length)) > 0)
				{
					msOut.Write(buf, 0, read);
				}
				msOut.Position = 0;
				_texture = LoadTexture(device, msOut);
			}
		}
		catch (Exception ex)
		{
			_loadFailed = true;
			Game1.log?.Error("Failed to load tractor texture.", ex);
		}
	}

	private static Texture2D LoadTexture(GraphicsDevice device, Stream stream)
	{
		Texture2D texture = Texture2D.FromStream(device, stream);
		if (texture.Width == Columns * FrameSize && texture.Height >= 3 * FrameSize) return texture;
		texture.Dispose();
		throw new InvalidDataException("Tractor texture must contain seven 32px frames per row and at least three rows.");
	}
}
