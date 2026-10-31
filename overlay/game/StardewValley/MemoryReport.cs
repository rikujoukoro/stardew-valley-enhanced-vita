using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using xTile;
using xTile.Layers;

namespace StardewValley;

/// <summary>Development memory report for the low-RAM port.</summary>
/// <remarks>
/// Enabled with the environment variable SV_MEMREPORT=1. Every 30 s and on every location change
/// it appends one summary line to %APPDATA%/StardewValley/memreport.log, and every 30 s it rewrites
/// memreport.txt with a breakdown: process memory, GC heap, assets per content manager (textures
/// sized from their dimensions and format), loaded maps and the game's own render targets.
/// When disabled it does nothing and keeps no references.
/// </remarks>
public static class MemoryReport
{
	public static readonly bool Enabled = Environment.GetEnvironmentVariable("SV_MEMREPORT") == "1";

	private sealed class ManagerInfo
	{
		public WeakReference Manager;

		public string CreatedBy;

		public readonly Dictionary<string, WeakReference> Assets = new Dictionary<string, WeakReference>(StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>A report line and the size it is sorted by.</summary>
	private sealed class Row
	{
		public readonly string Text;

		public readonly long Bytes;

		public Row(string text, long bytes)
		{
			Text = text;
			Bytes = bytes;
		}
	}

	private static readonly List<ManagerInfo> managers = new List<ManagerInfo>();

	private static readonly Stopwatch timer = Stopwatch.StartNew();

	private static long nextReportMs = 30000;

	private static string lastLocation;

	public static void RegisterContentManager(LocalizedContentManager manager)
	{
		if (!Enabled)
		{
			return;
		}
		StackFrame caller = new StackTrace(skipFrames: 2, fNeedFileInfo: false).GetFrames()?.FirstOrDefault((StackFrame f) => f.GetMethod()?.DeclaringType != typeof(LocalizedContentManager));
		lock (managers)
		{
			managers.Add(new ManagerInfo
			{
				Manager = new WeakReference(manager),
				CreatedBy = (caller?.GetMethod() is { } m) ? (m.DeclaringType?.Name + "." + m.Name) : "?"
			});
		}
	}

	public static void TrackAsset(LocalizedContentManager manager, string assetName, object asset)
	{
		if (!Enabled || asset == null)
		{
			return;
		}
		ManagerInfo info = Find(manager);
		if (info != null)
		{
			lock (info.Assets)
			{
				info.Assets[assetName] = new WeakReference(asset);
			}
		}
	}

	public static void ForgetAssets(LocalizedContentManager manager)
	{
		if (!Enabled)
		{
			return;
		}
		ManagerInfo info = Find(manager);
		if (info != null)
		{
			lock (info.Assets)
			{
				info.Assets.Clear();
			}
		}
	}

	private static ManagerInfo Find(LocalizedContentManager manager)
	{
		lock (managers)
		{
			foreach (ManagerInfo info in managers)
			{
				if (info.Manager.Target == manager)
				{
					return info;
				}
			}
		}
		return null;
	}

	public static void Update()
	{
		if (!Enabled)
		{
			return;
		}
		string location = Game1.currentLocation?.NameOrUniqueName;
		bool locationChanged = location != lastLocation;
		bool due = timer.ElapsedMilliseconds >= nextReportMs;
		if (!locationChanged && !due)
		{
			return;
		}
		lastLocation = location;
		try
		{
			string folder = Program.GetAppDataFolder();
			File.AppendAllText(Path.Combine(folder, "memreport.log"), SummaryLine(locationChanged ? "warp" : "tick") + Environment.NewLine);
			if (due)
			{
				nextReportMs = timer.ElapsedMilliseconds + 30000;
				File.WriteAllText(Path.Combine(folder, "memreport.txt"), BuildReport());
			}
		}
		catch (Exception ex)
		{
			Game1.log.Warn("Memory report failed: " + ex.Message);
		}
	}

	private static string SummaryLine(string reason)
	{
		Process process = Process.GetCurrentProcess();
		process.Refresh();
		// Peak values are the OS's own high-water marks, so short spikes between reports are included.
		return $"{DateTime.Now:HH:mm:ss} {reason,-4} loc={lastLocation ?? "-"} private={process.PrivateMemorySize64 / 1048576} MB (peak {process.PeakPagedMemorySize64 / 1048576}) ws={process.WorkingSet64 / 1048576} MB (peak {process.PeakWorkingSet64 / 1048576}) gc={GC.GetTotalMemory(forceFullCollection: false) / 1048576} MB cheats={(ChatCommands.AllowCheats ? "on" : "off")}";
	}

	private static string BuildReport()
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendLine(SummaryLine("full"));
		sb.AppendLine($"GC heap after full collection: {GC.GetTotalMemory(forceFullCollection: true) / 1048576.0:F1} MB");
		sb.AppendLine();

		var textureSeen = new HashSet<Texture2D>();
		long contentTextureBytes = 0;
		sb.AppendLine("== Content managers (live assets only)");
		List<Row> managerLines = new List<Row>();
		List<Row> textures = new List<Row>();
		lock (managers)
		{
			managers.RemoveAll((ManagerInfo m) => !m.Manager.IsAlive);
			for (int i = 0; i < managers.Count; i++)
			{
				ManagerInfo info = managers[i];
				int count = 0;
				int textureCount = 0;
				long bytes = 0;
				lock (info.Assets)
				{
					foreach (KeyValuePair<string, WeakReference> pair in info.Assets)
					{
						object asset = pair.Value.Target;
						if (asset == null)
						{
							continue;
						}
						count++;
						if (asset is Texture2D texture && !texture.IsDisposed)
						{
							textureCount++;
							long size = TextureBytes(texture);
							if (textureSeen.Add(texture))
							{
								bytes += size;
								textures.Add(new Row($"  {size / 1048576.0,6:F2} MB  #{i,-3} {pair.Key}", size));
							}
						}
					}
				}
				contentTextureBytes += bytes;
				managerLines.Add(new Row($"  #{i,-3} {info.CreatedBy,-48} assets={count,4} textures={textureCount,4} {bytes / 1048576.0,7:F1} MB", bytes));
			}
		}
		foreach (Row row in managerLines.OrderByDescending((Row x) => x.Bytes))
		{
			sb.AppendLine(row.Text);
		}
		sb.AppendLine($"  total texture memory via content managers: {contentTextureBytes / 1048576.0:F1} MB ({textureSeen.Count} textures)");
		sb.AppendLine();
		sb.AppendLine("== Largest content textures");
		foreach (Row row in textures.OrderByDescending((Row x) => x.Bytes).Take(40))
		{
			sb.AppendLine(row.Text);
		}
		sb.AppendLine();

		sb.AppendLine("== Render targets owned by Game1");
		long rtBytes = 0;
		void AddTarget(string name, Texture2D target)
		{
			if (target != null && !target.IsDisposed)
			{
				long size = TextureBytes(target);
				rtBytes += size;
				sb.AppendLine($"  {size / 1048576.0,6:F2} MB  {name} {target.Width}x{target.Height} {target.Format}");
			}
		}
		AddTarget("screen", Game1.game1?.screen);
		AddTarget("uiScreen", Game1.game1?.uiScreen);
		AddTarget("lightmap", Game1.lightmap);
		AddTarget("nonUIRenderTarget", Game1.nonUIRenderTarget);
		sb.AppendLine($"  total: {rtBytes / 1048576.0:F1} MB");
		sb.AppendLine();

		sb.AppendLine("== Locations with a loaded map");
		int locations = 0;
		int mapped = 0;
		long cells = 0;
		long tiles = 0;
		int idleMaps = 0;
		long idleCells = 0;
		long idleTiles = 0;
		List<Row> maps = new List<Row>();
		Utility.ForEachLocation(delegate(GameLocation location)
		{
			locations++;
			Map map = location.PeekMap;
			if (map != null)
			{
				mapped++;
				long cellCount = 0;
				long tileCount = 0;
				foreach (Layer layer in map.Layers)
				{
					cellCount += (long)layer.LayerWidth * layer.LayerHeight;
					for (int x = 0; x < layer.LayerWidth; x++)
					{
						for (int y = 0; y < layer.LayerHeight; y++)
						{
							if (layer.Tiles[x, y] != null)
							{
								tileCount++;
							}
						}
					}
				}
				cells += cellCount;
				tiles += tileCount;
				double idleSeconds = (Game1.ticks - location.MapLastAccessTick) / 60.0;
				if (idleSeconds >= 60.0)
				{
					idleMaps++;
					idleCells += cellCount;
					idleTiles += tileCount;
				}
				maps.Add(new Row($"  {cellCount,9:N0} cells {tileCount,8:N0} tiles  idle {idleSeconds,7:F0} s  npcs={location.characters.Count,2}  {location.NameOrUniqueName}", cellCount));
			}
			return true;
		}, includeInteriors: true, includeGenerated: true);
		sb.AppendLine($"  {mapped} of {locations} locations have a map loaded; {cells:N0} cells, {tiles:N0} non-empty tiles");
		sb.AppendLine($"  map unloading {(MapMemoryManager.Enabled ? "on" : "off (SV_KEEP_MAPS=1)")}: {MapMemoryManager.Unloads} unloads, {MapMemoryManager.Reloads} reloads ({MapMemoryManager.QuickReloads} quick, each pins that map for the day), {MapMemoryManager.TileSheetReleases} tile sheet releases");
		if (Game1.mapDisplayDevice is LowRamDisplayDevice device)
		{
			sb.AppendLine($"  tile sheet textures held by the display device: {device.LoadedTextureCount}");
		}
		sb.AppendLine($"  not read for >= 60 s: {idleMaps} maps, {idleCells:N0} cells, {idleTiles:N0} tiles");
		foreach (Row row in maps.OrderByDescending((Row x) => x.Bytes))
		{
			sb.AppendLine(row.Text);
		}
		return sb.ToString();
	}

	/// <summary>Bytes for one copy of the texture, including mip levels.</summary>
	private static long TextureBytes(Texture2D texture)
	{
		long bytes = 0;
		int width = texture.Width;
		int height = texture.Height;
		for (int level = 0; level < texture.LevelCount; level++)
		{
			bytes += LevelBytes(texture.Format, width, height);
			width = Math.Max(1, width / 2);
			height = Math.Max(1, height / 2);
		}
		return bytes;
	}

	private static long LevelBytes(SurfaceFormat format, int width, int height)
	{
		switch (format)
		{
		case SurfaceFormat.Dxt1:
			return (long)Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8;
		case SurfaceFormat.Dxt3:
		case SurfaceFormat.Dxt5:
			return (long)Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16;
		case SurfaceFormat.Alpha8:
			return (long)width * height;
		case SurfaceFormat.Bgr565:
		case SurfaceFormat.Bgra5551:
		case SurfaceFormat.Bgra4444:
		case SurfaceFormat.HalfSingle:
			return (long)width * height * 2;
		case SurfaceFormat.Vector4:
			return (long)width * height * 16;
		case SurfaceFormat.Vector2:
		case SurfaceFormat.Rgba64:
		case SurfaceFormat.HalfVector4:
			return (long)width * height * 8;
		default:
			return (long)width * height * 4;
		}
	}
}
