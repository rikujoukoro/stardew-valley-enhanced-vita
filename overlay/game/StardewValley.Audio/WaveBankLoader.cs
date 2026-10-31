using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Xna.Framework.Audio;

namespace StardewValley.Audio;

/// <summary>Opens XACT wave banks in the mode their file was built for.</summary>
/// <remarks>
/// The shipped banks are in-memory banks: WaveBank(engine, path) reads all ~475 MB into RAM.
/// A bank converted to the streaming type (Tools/WaveBankTool streamify) is opened with the
/// streaming constructor instead, which keeps only small per-cue packet buffers. The XACT
/// runtime refuses to stream an in-memory bank, so the file's own type flag decides.
/// </remarks>
public static class WaveBankLoader
{
	/// <summary>Stream packet size in 2048-byte sectors (16 = 32 KB per buffer).</summary>
	public const short StreamPacketSize = 16;

	private const uint Signature = 0x444E4257;

	private const uint TypeStreaming = 0x00000001;

	public static WaveBank Open(AudioEngine engine, string path)
	{
		if (IsStreamingBank(path))
		{
			return new WaveBank(engine, path, 0, StreamPacketSize);
		}
		return new WaveBank(engine, path);
	}

	public static bool IsStreamingBank(string path)
	{
		using FileStream stream = File.OpenRead(path);
		using BinaryReader reader = new BinaryReader(stream);
		if (reader.ReadUInt32() != Signature)
		{
			return false;
		}
		reader.ReadUInt32();
		reader.ReadUInt32();
		// The first segment descriptor points at the bank data, whose first field is dwFlags.
		uint bankDataOffset = reader.ReadUInt32();
		stream.Position = bankDataOffset;
		return (reader.ReadUInt32() & TypeStreaming) != 0;
	}

	/// <summary>Streaming banks become playable only after a few engine updates; playing a cue before that throws.</summary>
	public static bool WaitUntilPrepared(AudioEngine engine, int timeoutMs, params WaveBank[] banks)
	{
		Stopwatch timer = Stopwatch.StartNew();
		while (true)
		{
			bool prepared = true;
			foreach (WaveBank bank in banks)
			{
				prepared &= bank.IsPrepared;
			}
			if (prepared)
			{
				return true;
			}
			if (timer.ElapsedMilliseconds > timeoutMs)
			{
				return false;
			}
			engine.Update();
			Thread.Sleep(1);
		}
	}
}
