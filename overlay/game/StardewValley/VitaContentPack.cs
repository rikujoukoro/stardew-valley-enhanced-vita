using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace StardewValley;

/// <summary>Port: the game's .xnb files packed into one file (Content.pak in the game folder, built by Tools/Vita/make-content-pack.py).</summary>
/// <remarks>
/// On the Vita's memory card, finding and opening a file cost ~100 ms: the Content folders hold thousands of
/// files (each asset in up to 11 languages). A 3 KB tree texture took 150 ms to load, a map's tile sheets
/// half a second when walking into it, and the game menu re-read a 13 KB texture (125 ms) every time it
/// opened. The pack is opened once; an asset is one seek and one read. It holds every language; an asset
/// it doesn't hold is read from the loose files as before. Main thread only, like content loading.
/// </remarks>
public static class VitaContentPack
{
	private const string PackFile = "Content.pak";

	private static Dictionary<string, long> _entries;

	private static FileStream _stream;

	private static bool _tried;

	/// <summary>The asset (a name under Content, without ".xnb") from the pack, or null if the pack doesn't hold it.</summary>
	public static Stream TryOpen(string assetName)
	{
		if (!VitaPlatform.IsVita || assetName == null)
		{
			return null;
		}
		if (!_tried)
		{
			_tried = true;
			Open();
		}
		if (_entries == null)
		{
			return null;
		}
		string key = assetName.Replace('\\', '/').ToLowerInvariant();
		if (!_entries.TryGetValue(key, out long packed))
		{
			return null;
		}
		long offset = packed >> 24;
		int length = (int)(packed & 0xFFFFFF);
		try
		{
			byte[] data = new byte[length];
			_stream.Seek(offset, SeekOrigin.Begin);
			int read = 0;
			while (read < length)
			{
				int n = _stream.Read(data, read, length - read);
				if (n <= 0)
				{
					return null;
				}
				read += n;
			}
			return new MemoryStream(data, 0, length, writable: false, publiclyVisible: true);
		}
		catch (Exception ex)
		{
			VitaPlatform.Trace("content pack read failed: " + ex.Message);
			return null;
		}
	}

	private static void Open()
	{
		try
		{
			if (!File.Exists(PackFile))
			{
				return;
			}
			FileStream stream = new FileStream(PackFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
			// Not disposed: that would close the stream the pack is read through.
			BinaryReader reader = new BinaryReader(stream, Encoding.UTF8);
			if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "SVPK1\0\0\0")
			{
				stream.Dispose();
				VitaPlatform.Trace("content pack ignored: not a pack");
				return;
			}
			int count = reader.ReadInt32();
			Dictionary<string, long> entries = new Dictionary<string, long>(count, StringComparer.Ordinal);
			for (int i = 0; i < count; i++)
			{
				int nameLength = reader.ReadUInt16();
				string name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
				long offset = reader.ReadInt64();
				uint length = reader.ReadUInt32();
				// Offset and length in one long: lengths stay under 16 MB, offsets under 512 GB.
				if (length < 0x1000000)
				{
					entries[name] = (offset << 24) | length;
				}
			}
			_stream = stream;
			_entries = entries;
			VitaPlatform.Trace($"content pack: {count} assets");
		}
		catch (Exception ex)
		{
			VitaPlatform.Trace("content pack not used: " + ex.Message);
			_entries = null;
		}
	}
}
