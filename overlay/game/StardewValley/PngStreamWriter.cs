using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace StardewValley;

/// <summary>Writes an 8-bit RGB PNG row by row, so a large image never has to be in memory at once.</summary>
/// <remarks>
/// Port: replaces System.Drawing.Bitmap for map screenshots. The XNA build held the whole map in one
/// bitmap (a large map is well over 100 MB); here the caller passes horizontal bands of rows as they
/// are rendered. Rows use filter type 0 and are deflated into IDAT chunks.
/// </remarks>
public sealed class PngStreamWriter : IDisposable
{
	private static readonly uint[] CrcTable = CreateCrcTable();

	private readonly Stream _output;

	private readonly int _width;

	private readonly int _height;

	private readonly IdatStream _idat;

	private readonly DeflateStream _deflate;

	private readonly byte[] _rowBuffer;

	private uint _adlerA = 1u;

	private uint _adlerB;

	private int _rowsWritten;

	private bool _finished;

	public PngStreamWriter(Stream output, int width, int height)
	{
		if (width <= 0 || height <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(width), "PNG dimensions must be positive.");
		}
		_output = output;
		_width = width;
		_height = height;
		_output.Write(new byte[8] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
		byte[] header = new byte[13];
		WriteBigEndian(header, 0, (uint)width);
		WriteBigEndian(header, 4, (uint)height);
		header[8] = 8;
		header[9] = 2;
		WriteChunk(_output, "IHDR", header, header.Length);
		_idat = new IdatStream(_output);
		// zlib header: deflate, 32K window, default compression.
		_idat.Write(new byte[2] { 0x78, 0x9C }, 0, 2);
		_deflate = new DeflateStream(_idat, CompressionMode.Compress, leaveOpen: true);
		_rowBuffer = new byte[1 + width * 3];
	}

	/// <summary>Append rows of tightly packed RGB bytes (width * 3 per row).</summary>
	public void WriteRows(byte[] rgb, int rowCount)
	{
		for (int row = 0; row < rowCount; row++)
		{
			if (_rowsWritten >= _height)
			{
				throw new InvalidOperationException("More rows than the PNG height.");
			}
			_rowBuffer[0] = 0;
			Buffer.BlockCopy(rgb, row * _width * 3, _rowBuffer, 1, _width * 3);
			UpdateAdler(_rowBuffer, _rowBuffer.Length);
			_deflate.Write(_rowBuffer, 0, _rowBuffer.Length);
			_rowsWritten++;
		}
	}

	/// <summary>Finish the image: pads missing rows with black, then writes the trailer.</summary>
	public void Finish()
	{
		if (_finished)
		{
			return;
		}
		_finished = true;
		Array.Clear(_rowBuffer, 0, _rowBuffer.Length);
		while (_rowsWritten < _height)
		{
			UpdateAdler(_rowBuffer, _rowBuffer.Length);
			_deflate.Write(_rowBuffer, 0, _rowBuffer.Length);
			_rowsWritten++;
		}
		_deflate.Dispose();
		byte[] adler = new byte[4];
		WriteBigEndian(adler, 0, (_adlerB << 16) | _adlerA);
		_idat.Write(adler, 0, 4);
		_idat.FlushChunk();
		WriteChunk(_output, "IEND", new byte[0], 0);
		_output.Flush();
	}

	public void Dispose()
	{
		Finish();
	}

	private void UpdateAdler(byte[] data, int length)
	{
		const uint mod = 65521u;
		uint a = _adlerA;
		uint b = _adlerB;
		for (int i = 0; i < length; i++)
		{
			a = (a + data[i]) % mod;
			b = (b + a) % mod;
		}
		_adlerA = a;
		_adlerB = b;
	}

	private static void WriteChunk(Stream output, string type, byte[] data, int length)
	{
		byte[] lengthBytes = new byte[4];
		WriteBigEndian(lengthBytes, 0, (uint)length);
		output.Write(lengthBytes, 0, 4);
		byte[] typeBytes = Encoding.ASCII.GetBytes(type);
		output.Write(typeBytes, 0, 4);
		output.Write(data, 0, length);
		uint crc = UpdateCrc(0xFFFFFFFFu, typeBytes, 4);
		crc = UpdateCrc(crc, data, length) ^ 0xFFFFFFFFu;
		byte[] crcBytes = new byte[4];
		WriteBigEndian(crcBytes, 0, crc);
		output.Write(crcBytes, 0, 4);
	}

	private static void WriteBigEndian(byte[] buffer, int offset, uint value)
	{
		buffer[offset] = (byte)(value >> 24);
		buffer[offset + 1] = (byte)(value >> 16);
		buffer[offset + 2] = (byte)(value >> 8);
		buffer[offset + 3] = (byte)value;
	}

	private static uint UpdateCrc(uint crc, byte[] data, int length)
	{
		for (int i = 0; i < length; i++)
		{
			crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
		}
		return crc;
	}

	private static uint[] CreateCrcTable()
	{
		uint[] table = new uint[256];
		for (uint n = 0u; n < 256; n++)
		{
			uint c = n;
			for (int k = 0; k < 8; k++)
			{
				c = (((c & 1) != 0) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1));
			}
			table[n] = c;
		}
		return table;
	}

	/// <summary>Collects zlib output and writes it as IDAT chunks of up to 64 KB.</summary>
	private sealed class IdatStream : Stream
	{
		private readonly Stream _output;

		private readonly byte[] _buffer = new byte[65536];

		private int _count;

		public override bool CanRead => false;

		public override bool CanSeek => false;

		public override bool CanWrite => true;

		public override long Length => throw new NotSupportedException();

		public override long Position
		{
			get
			{
				throw new NotSupportedException();
			}
			set
			{
				throw new NotSupportedException();
			}
		}

		public IdatStream(Stream output)
		{
			_output = output;
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			while (count > 0)
			{
				int take = Math.Min(count, _buffer.Length - _count);
				Buffer.BlockCopy(buffer, offset, _buffer, _count, take);
				_count += take;
				offset += take;
				count -= take;
				if (_count == _buffer.Length)
				{
					FlushChunk();
				}
			}
		}

		public void FlushChunk()
		{
			if (_count > 0)
			{
				WriteChunk(_output, "IDAT", _buffer, _count);
				_count = 0;
			}
		}

		public override void Flush()
		{
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			throw new NotSupportedException();
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}
	}
}
