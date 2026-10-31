using System;
using System.IO;

// Rewrites an XACT3 wave bank (.xwb, version 46) between in-memory and streaming types.
//
//   WaveBankTool info <bank.xwb>
//   WaveBankTool streamify <in.xwb> <out.xwb>
//
// An in-memory bank is read fully into RAM by WaveBank(engine, path). A streaming bank is opened
// with WaveBank(engine, path, offset, packetSize) and only small per-cue packet buffers are kept.
// The XACT runtime refuses to stream an in-memory bank, so the type flag must be flipped and every
// wave's data must start on a 2048-byte (DVD sector) boundary. Wave data itself is copied
// unchanged; only the header, entry offsets and padding change.
internal static class Program
{
	private const uint Signature = 0x444E4257; // "WBND"
	private const uint TypeStreaming = 0x00000001;
	private const uint TypeMask = 0x00000001;
	private const uint FlagCompact = 0x00020000;
	private const int StreamingAlignment = 2048;
	private const int SegmentCount = 5;
	private const int SegBankData = 0;
	private const int SegEntryMetaData = 1;
	private const int SegWaveData = 4;

	private static int Main(string[] args)
	{
		if (args.Length == 2 && args[0] == "info")
		{
			Info(File.ReadAllBytes(args[1]));
			return 0;
		}
		if (args.Length == 3 && args[0] == "streamify")
		{
			byte[] output = Streamify(File.ReadAllBytes(args[1]));
			File.WriteAllBytes(args[2], output);
			Console.WriteLine($"wrote {args[2]} ({output.Length:N0} B)");
			return 0;
		}
		Console.Error.WriteLine("usage: WaveBankTool info <bank.xwb> | streamify <in.xwb> <out.xwb>");
		return 2;
	}

	private sealed class Bank
	{
		public uint Version;
		public uint HeaderVersion;
		public uint[] SegOffset = new uint[SegmentCount];
		public uint[] SegLength = new uint[SegmentCount];
		public uint Flags;
		public uint EntryCount;
		public uint MetaDataElementSize;
		public uint Alignment;
		public int FlagsPosition;
		public int AlignmentPosition;
	}

	private static Bank ReadHeader(byte[] data)
	{
		if (BitConverter.ToUInt32(data, 0) != Signature)
		{
			throw new InvalidDataException("not a little-endian XACT wave bank (missing WBND)");
		}
		var bank = new Bank
		{
			Version = BitConverter.ToUInt32(data, 4),
			HeaderVersion = BitConverter.ToUInt32(data, 8)
		};
		if (bank.Version != 46)
		{
			throw new InvalidDataException($"unsupported wave bank version {bank.Version} (expected 46)");
		}
		for (int i = 0; i < SegmentCount; i++)
		{
			bank.SegOffset[i] = BitConverter.ToUInt32(data, 12 + i * 8);
			bank.SegLength[i] = BitConverter.ToUInt32(data, 16 + i * 8);
		}
		int b = (int)bank.SegOffset[SegBankData];
		bank.FlagsPosition = b;
		bank.Flags = BitConverter.ToUInt32(data, b);
		bank.EntryCount = BitConverter.ToUInt32(data, b + 4);
		// dwFlags, dwEntryCount, szBankName[64], dwEntryMetaDataElementSize, dwEntryNameElementSize, dwAlignment
		bank.MetaDataElementSize = BitConverter.ToUInt32(data, b + 72);
		bank.AlignmentPosition = b + 80;
		bank.Alignment = BitConverter.ToUInt32(data, bank.AlignmentPosition);
		if ((bank.Flags & FlagCompact) != 0)
		{
			throw new InvalidDataException("compact wave banks are not supported");
		}
		if (bank.MetaDataElementSize < 16)
		{
			throw new InvalidDataException($"unexpected entry metadata size {bank.MetaDataElementSize}");
		}
		return bank;
	}

	private static void Info(byte[] data)
	{
		Bank bank = ReadHeader(data);
		bool streaming = (bank.Flags & TypeMask) == TypeStreaming;
		Console.WriteLine($"type={(streaming ? "streaming" : "in-memory")} flags=0x{bank.Flags:X} entries={bank.EntryCount} alignment={bank.Alignment} wavedata={bank.SegLength[SegWaveData]:N0} B");
		int misaligned = 0;
		for (int i = 0; i < bank.EntryCount; i++)
		{
			uint offset = BitConverter.ToUInt32(data, (int)(bank.SegOffset[SegEntryMetaData] + i * bank.MetaDataElementSize) + 8);
			if (offset % StreamingAlignment != 0)
			{
				misaligned++;
			}
		}
		Console.WriteLine($"entries not on a {StreamingAlignment}-byte boundary: {misaligned}");
	}

	private static uint Align(uint value, uint alignment) => (value + alignment - 1) / alignment * alignment;

	private static byte[] Streamify(byte[] input)
	{
		Bank bank = ReadHeader(input);
		uint metaStart = bank.SegOffset[SegEntryMetaData];
		uint waveStart = bank.SegOffset[SegWaveData];

		// Everything before the wave data segment (header, bank data, metadata, seek tables, names)
		// is kept; the wave data segment is rebuilt with each entry on a sector boundary.
		uint newWaveStart = Align(waveStart, StreamingAlignment);
		uint newWaveLength = 0;
		var newOffsets = new uint[bank.EntryCount];
		for (int i = 0; i < bank.EntryCount; i++)
		{
			int entry = (int)(metaStart + i * bank.MetaDataElementSize);
			uint length = BitConverter.ToUInt32(input, entry + 12);
			newOffsets[i] = Align(newWaveLength, StreamingAlignment);
			newWaveLength = newOffsets[i] + length;
		}
		newWaveLength = Align(newWaveLength, StreamingAlignment);

		var output = new byte[newWaveStart + newWaveLength];
		Buffer.BlockCopy(input, 0, output, 0, (int)waveStart);
		for (int i = 0; i < bank.EntryCount; i++)
		{
			int entry = (int)(metaStart + i * bank.MetaDataElementSize);
			uint offset = BitConverter.ToUInt32(input, entry + 8);
			uint length = BitConverter.ToUInt32(input, entry + 12);
			Buffer.BlockCopy(input, (int)(waveStart + offset), output, (int)(newWaveStart + newOffsets[i]), (int)length);
			WriteUInt32(output, entry + 8, newOffsets[i]);
		}
		WriteUInt32(output, 12 + SegWaveData * 8, newWaveStart);
		WriteUInt32(output, 16 + SegWaveData * 8, newWaveLength);
		WriteUInt32(output, bank.FlagsPosition, (bank.Flags & ~TypeMask) | TypeStreaming);
		WriteUInt32(output, bank.AlignmentPosition, StreamingAlignment);
		return output;
	}

	private static void WriteUInt32(byte[] data, int position, uint value)
	{
		byte[] bytes = BitConverter.GetBytes(value);
		Buffer.BlockCopy(bytes, 0, data, position, 4);
	}
}
