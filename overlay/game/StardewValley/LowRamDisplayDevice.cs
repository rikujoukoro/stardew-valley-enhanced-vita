using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using xTile.Dimensions;
using xTile.Display;
using xTile.Tiles;

namespace StardewValley;

/// <summary>Map display device whose tile sheet textures are reference counted and freed when unused.</summary>
/// <remarks>
/// Low-RAM port. xTile's XnaDisplayDevice loads every tile sheet through one shared content manager,
/// which never frees anything: tile sheets of every map ever loaded stay in memory, and a seasonal
/// switch (spring_town -> summer_town) loads the new sheet while the old one stays cached. Here each
/// texture lives in its own temporary content manager, counted per tile sheet that uses it, and is
/// unloaded when the last tile sheet using it is disposed. A texture the game itself already loaded
/// into Game1.content (e.g. Maps\springobjects) is shared instead of loaded a second time.
/// XnaDisplayDevice.LoadTileSheet/DisposeTileSheet aren't virtual, so they're re-implemented through
/// the interface, and DrawTile's lazy load is routed through ours.
/// </remarks>
public class LowRamDisplayDevice : XnaDisplayDevice, IDisplayDevice
{
	private sealed class SharedTexture
	{
		public Texture2D Texture;

		/// <summary>The manager that owns the texture, or null if it is borrowed from Game1.content.</summary>
		public LocalizedContentManager Owner;

		public int References;
	}

	private readonly Dictionary<string, SharedTexture> textures = new Dictionary<string, SharedTexture>(StringComparer.OrdinalIgnoreCase);

	/// <summary>The image source each loaded tile sheet was loaded with (it can change, e.g. seasonally).</summary>
	private readonly Dictionary<TileSheet, string> loadedSources = new Dictionary<TileSheet, string>();

	public int LoadedTextureCount => textures.Count;

	public LowRamDisplayDevice(ContentManager contentManager, GraphicsDevice graphicsDevice)
		: base(contentManager, graphicsDevice)
	{
	}

	public new void LoadTileSheet(TileSheet tileSheet)
	{
		string source = tileSheet.ImageSource;
		if (loadedSources.TryGetValue(tileSheet, out string previous))
		{
			if (string.Equals(previous, source, StringComparison.OrdinalIgnoreCase) && m_tileSheetTextures.ContainsKey(tileSheet))
			{
				return;
			}
			Release(previous);
			loadedSources.Remove(tileSheet);
			m_tileSheetTextures.Remove(tileSheet);
		}
		ForgetSheetDraw(tileSheet);
		xTile.Layers.Layer.VitaTileEdits++;
		Texture2D texture = Acquire(source);
		loadedSources[tileSheet] = source;
		m_tileSheetTextures[tileSheet] = texture;
	}

	public new void DisposeTileSheet(TileSheet tileSheet)
	{
		ForgetSheetDraw(tileSheet);
		xTile.Layers.Layer.VitaTileEdits++;
		m_tileSheetTextures.Remove(tileSheet);
		if (loadedSources.TryGetValue(tileSheet, out string source))
		{
			loadedSources.Remove(tileSheet);
			Release(source);
		}
	}

	/// <summary>Port: unloads every tile sheet texture owned by this display device when returning to the title screen.</summary>
	public void UnloadAll()
	{
		lastSheetDraw = null;
		sheetDraws.Clear();
		for (int i = 0; i < recentSheetDraws.Length; i++)
		{
			recentSheetDraws[i] = null;
		}
		m_tileSheetTextures.Clear();
		loadedSources.Clear();
		idleSources.Clear();
		idleBytes = 0;
		foreach (SharedTexture shared in textures.Values)
		{
			shared.Owner?.Unload();
		}
		textures.Clear();
	}

	/// <summary>Port: what a tile sheet needs to queue its tiles: the texture and, per tile index, the source rectangle already in texture coordinates.</summary>
	/// <remarks>
	/// Locating a tile on its sheet took an integer division and modulo (software helpers on the Vita's Cortex-A9) and the
	/// sprite batch then divided four more times by the texture size, per tile per frame. U/V are filled lazily the first time
	/// an index is drawn (-1 = not yet), with the same expressions the sprite batch used, so the values are bit-identical.
	/// At most <see cref="MaxTableTiles" /> entries (8 bytes each) per sheet; larger indexes take the generic path.
	/// </remarks>
	private sealed class SheetDraw
	{
		public readonly TileSheet Sheet;

		public readonly Texture2D Texture;

		/// <summary>The sheet's width in tiles when this was built; a change rebuilds it.</summary>
		public readonly int Columns;

		/// <summary>Source x / y of each tile index in texture coordinates, or -1 until first used.</summary>
		public readonly float[] U;

		public readonly float[] V;

		/// <summary>A tile's source width / height in texture coordinates (16 px).</summary>
		public readonly float SourceW;

		public readonly float SourceH;

		private readonly float textureWidth;

		private readonly float textureHeight;

		public SheetDraw(TileSheet sheet, Texture2D texture, int columns)
		{
			Sheet = sheet;
			Texture = texture;
			Columns = columns;
			textureWidth = texture.Width;
			textureHeight = texture.Height;
			// Port: inset by 1/32 texel on each side. At 88% the screen showed one-pixel rows of the neighbouring tile
			// in the sheet along tile edges (a sample landing just outside the tile); the inset keeps every sample inside
			// it, and no pixel centre of a 64-pixel tile changes texel.
			SourceW = (16f - 2f * TileInset) / textureWidth;
			SourceH = (16f - 2f * TileInset) / textureHeight;
			int count = columns > 0 ? Math.Min(sheet.TileCount, MaxTableTiles) : 0;
			if (count < 0)
			{
				count = 0;
			}
			U = new float[count];
			V = new float[count];
			for (int i = 0; i < count; i++)
			{
				U[i] = -1f;
			}
		}

		public void Fill(int index)
		{
			U[index] = (16 * (index % Columns) + TileInset) / textureWidth;
			V[index] = (16 * (index / Columns) + TileInset) / textureHeight;
		}
	}

	private const int MaxTableTiles = 4096;

	private const float TileInset = 1f / 32f;

	private readonly Dictionary<TileSheet, SheetDraw> sheetDraws = new Dictionary<TileSheet, SheetDraw>();

	/// <summary>The last few sheets drawn, so maps that alternate between a handful of sheets skip the dictionary.</summary>
	private readonly SheetDraw[] recentSheetDraws = new SheetDraw[4];

	private int recentSheetDrawNext;

	/// <summary>Port: the sheet <see cref="DrawTile" /> drew last; cleared whenever a sheet's texture changes.</summary>
	private SheetDraw lastSheetDraw;

	private void ForgetSheetDraw(TileSheet tileSheet)
	{
		lastSheetDraw = null;
		sheetDraws.Remove(tileSheet);
		for (int i = 0; i < recentSheetDraws.Length; i++)
		{
			if (recentSheetDraws[i] != null && recentSheetDraws[i].Sheet == tileSheet)
			{
				recentSheetDraws[i] = null;
			}
		}
	}

	private SheetDraw FindSheetDraw(TileSheet sheet)
	{
		int columns = sheet.SheetWidth;
		for (int i = 0; i < recentSheetDraws.Length; i++)
		{
			SheetDraw recent = recentSheetDraws[i];
			if (recent != null && recent.Sheet == sheet && recent.Columns == columns)
			{
				lastSheetDraw = recent;
				return recent;
			}
		}
		if (!sheetDraws.TryGetValue(sheet, out SheetDraw info) || info.Columns != columns)
		{
			if (!m_tileSheetTextures.TryGetValue(sheet, out Texture2D texture))
			{
				LoadTileSheet(sheet);
				texture = m_tileSheetTextures[sheet];
			}
			info = new SheetDraw(sheet, texture, columns);
			sheetDraws[sheet] = info;
		}
		recentSheetDraws[recentSheetDrawNext] = info;
		recentSheetDrawNext = (recentSheetDrawNext + 1) & 3;
		lastSheetDraw = info;
		return info;
	}

	/// <summary>Port: while set, DrawTile keeps in <see cref="VitaNextAnimationChange" /> the earliest map time at which an animated tile it drew shows another frame (long.MinValue for a tile it can't tell about), for Game1's cached back layers.</summary>
	public static bool VitaRecordingAnimation;

	public static long VitaNextAnimationChange;

	public override void DrawTile(Tile tile, Location location, float layerDepth)
	{
		if (tile == null)
		{
			return;
		}
		// Port: XnaDisplayDevice.DrawTile -> DrawImpl -> SpriteBatch.Draw(...) reduced to the work that differs per tile.
		// The tile is resolved to its static frame once (no virtual property calls, one animation lookup), the sheet is
		// found without a dictionary lookup while consecutive tiles share it (nearly always), the source rectangle comes
		// from the sheet's per-index table, and SpriteBatch.DrawTileEXT queues the sprite without the Rectangle?/Vector2
		// temporaries. Same texture, position, source rectangle, scale, colour and depth as before.
		StaticTile frame = tile as StaticTile;
		if (frame == null)
		{
			AnimatedTile animated = tile as AnimatedTile;
			if (animated != null)
			{
				frame = animated.CurrentFrame;
				if (VitaRecordingAnimation)
				{
					// The frame index changes at each multiple of the frame interval on the map's clock.
					long interval = Math.Max(animated.FrameInterval, 1L);
					long next = (animated.Layer.Map.ElapsedTime / interval + 1) * interval;
					if (next < VitaNextAnimationChange)
					{
						VitaNextAnimationChange = next;
					}
				}
			}
		}
		if (frame == null)
		{
			VitaNextAnimationChange = long.MinValue;
			DrawTileGeneric(tile, location, layerDepth);
			return;
		}
		TileSheet sheet = frame.RawTileSheet;
		SheetDraw info = lastSheetDraw;
		if (info == null || info.Sheet != sheet || info.Columns != sheet.SheetWidth)
		{
			info = FindSheetDraw(sheet);
		}
		Texture2D texture = info.Texture;
		if (texture.IsDisposed)
		{
			return;
		}
		int index = frame.RawTileIndex;
		float[] us = info.U;
		if ((uint)index >= (uint)us.Length)
		{
			DrawTileGeneric(tile, location, layerDepth);
			return;
		}
		float u = us[index];
		if (u < 0f)
		{
			info.Fill(index);
			u = us[index];
		}
		float size = xTile.Layers.Layer.zoom * 16f;
		m_spriteBatchAlpha.DrawTileEXT(texture, location.X, location.Y, u, info.V[index], info.SourceW, info.SourceH, size, size, m_modulationColour, layerDepth);
	}

	/// <summary>Port: the previous DrawTile (one texture lookup), for tiles that are neither static nor animated or whose index is beyond the sheet's table.</summary>
	private void DrawTileGeneric(Tile tile, Location location, float layerDepth)
	{
		TileSheet sheet = tile.TileSheet;
		if (!m_tileSheetTextures.TryGetValue(sheet, out Texture2D texture))
		{
			LoadTileSheet(sheet);
			texture = m_tileSheetTextures[sheet];
		}
		if (!texture.IsDisposed)
		{
			xTile.Dimensions.Rectangle bounds = sheet.GetTileImageBounds(tile.TileIndex);
			m_tilePosition.X = location.X;
			m_tilePosition.Y = location.Y;
			m_sourceRectangle.X = bounds.X;
			m_sourceRectangle.Y = bounds.Y;
			m_sourceRectangle.Width = bounds.Width;
			m_sourceRectangle.Height = bounds.Height;
			DrawImpl(tile, location, layerDepth, texture);
		}
	}

	/// <summary>How much memory textures no tile sheet uses any more may keep: the farmhouse, shops and town share sheets that took up to a second to read back from the Vita's memory card.</summary>
	public const int IdleTextureBudget = 10 * 1024 * 1024;

	/// <summary>Textures nothing references, least recently released first; unloaded beyond <see cref="IdleTextureBudget" />.</summary>
	private readonly List<string> idleSources = new List<string>();

	private int idleBytes;

	private static int TextureBytes(Texture2D texture)
	{
		int pixels = texture.Width * texture.Height;
		return texture.Format == SurfaceFormat.Color ? pixels * 4 : pixels;
	}

	private Texture2D Acquire(string source)
	{
		if (textures.TryGetValue(source, out SharedTexture idle) && idle.References == 0)
		{
			idleSources.Remove(source);
			idleBytes -= TextureBytes(idle.Texture);
		}
		if (!textures.TryGetValue(source, out SharedTexture shared))
		{
			shared = new SharedTexture();
			if (Game1.content.IsLoaded(source))
			{
				shared.Texture = Game1.content.Load<Texture2D>(source);
			}
			else
			{
				shared.Owner = Game1.content.CreateTemporary();
				try
				{
					shared.Texture = shared.Owner.Load<Texture2D>(source);
				}
				catch
				{
					shared.Owner.Unload();
					throw;
				}
			}
			textures[source] = shared;
		}
		shared.References++;
		return shared.Texture;
	}

	private void Release(string source)
	{
		if (source != null && textures.TryGetValue(source, out SharedTexture shared))
		{
			shared.References--;
			if (shared.References > 0)
			{
				return;
			}
			if (shared.Owner == null || shared.Texture == null || shared.Texture.IsDisposed)
			{
				// Borrowed from Game1.content: keeping it costs nothing, dropping it neither.
				textures.Remove(source);
				shared.Owner?.Unload();
				return;
			}
			idleSources.Add(source);
			idleBytes += TextureBytes(shared.Texture);
			while (idleBytes > IdleTextureBudget && idleSources.Count > 0)
			{
				string oldest = idleSources[0];
				idleSources.RemoveAt(0);
				if (textures.TryGetValue(oldest, out SharedTexture evicted))
				{
					idleBytes -= TextureBytes(evicted.Texture);
					textures.Remove(oldest);
					evicted.Owner.Unload();
				}
			}
		}
	}
}
