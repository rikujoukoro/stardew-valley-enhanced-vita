using System;

namespace StardewValley.Network.Compress;

public static class LZ4Codec
{
	public static int Compress(byte[] src, int srcOffset, int srcLength, byte[] dst, int dstOffset, int dstCapacity)
	{
		if (srcLength == 0)
		{
			return 0;
		}

		int[] hashTable = new int[4096];
		for (int i = 0; i < 4096; i++)
		{
			hashTable[i] = -1;
		}

		int anchor = srcOffset;
		int pos = srcOffset;
		int srcEnd = srcOffset + srcLength;
		int dstPtr = dstOffset;
		int dstEnd = dstOffset + dstCapacity;

		// LZ4 block format requires the final 5 bytes to be literals, and the
		// last match to start at least 12 bytes before the input end.
		while (pos <= srcEnd - 12)
		{
			uint seq = (uint)(src[pos] | (src[pos + 1] << 8) | (src[pos + 2] << 16) | (src[pos + 3] << 24));
			int h = (int)((seq * 2654435761u) >> 20) & 4095;
			int refPos = hashTable[h];
			hashTable[h] = pos;

			if (refPos >= srcOffset && (pos - refPos) <= 65535 && pos > refPos &&
				src[refPos] == src[pos] &&
				src[refPos + 1] == src[pos + 1] &&
				src[refPos + 2] == src[pos + 2] &&
				src[refPos + 3] == src[pos + 3])
			{
				int litLen = pos - anchor;
				int matchLen = 4;
				while (pos + matchLen < srcEnd - 5 && src[refPos + matchLen] == src[pos + matchLen])
				{
					matchLen++;
				}

				int offset = pos - refPos;
				int tokenLit = litLen < 15 ? litLen : 15;
				int tokenMatch = (matchLen - 4) < 15 ? (matchLen - 4) : 15;

				if (dstPtr >= dstEnd) return -1;
				dst[dstPtr++] = (byte)((tokenLit << 4) | tokenMatch);

				if (tokenLit == 15)
				{
					int rem = litLen - 15;
					while (rem >= 255)
					{
						if (dstPtr >= dstEnd) return -1;
						dst[dstPtr++] = 255;
						rem -= 255;
					}
					if (dstPtr >= dstEnd) return -1;
					dst[dstPtr++] = (byte)rem;
				}

				if (dstPtr + litLen > dstEnd) return -1;
				Buffer.BlockCopy(src, anchor, dst, dstPtr, litLen);
				dstPtr += litLen;

				if (dstPtr + 2 > dstEnd) return -1;
				dst[dstPtr++] = (byte)(offset & 0xFF);
				dst[dstPtr++] = (byte)((offset >> 8) & 0xFF);

				if (tokenMatch == 15)
				{
					int rem = (matchLen - 4) - 15;
					while (rem >= 255)
					{
						if (dstPtr >= dstEnd) return -1;
						dst[dstPtr++] = 255;
						rem -= 255;
					}
					if (dstPtr >= dstEnd) return -1;
					dst[dstPtr++] = (byte)rem;
				}

				pos += matchLen;
				anchor = pos;
			}
			else
			{
				pos++;
			}
		}

		int trailingLitLen = srcEnd - anchor;
		int finalTokenLit = trailingLitLen < 15 ? trailingLitLen : 15;
		if (dstPtr >= dstEnd) return -1;
		dst[dstPtr++] = (byte)(finalTokenLit << 4);

		if (finalTokenLit == 15)
		{
			int rem = trailingLitLen - 15;
			while (rem >= 255)
			{
				if (dstPtr >= dstEnd) return -1;
				dst[dstPtr++] = 255;
				rem -= 255;
			}
			if (dstPtr >= dstEnd) return -1;
			dst[dstPtr++] = (byte)rem;
		}

		if (dstPtr + trailingLitLen > dstEnd) return -1;
		Buffer.BlockCopy(src, anchor, dst, dstPtr, trailingLitLen);
		dstPtr += trailingLitLen;

		return dstPtr - dstOffset;
	}

	public static int Decompress(byte[] src, int srcOffset, int srcLength, byte[] dst, int dstOffset, int dstLength)
	{
		int srcPtr = srcOffset;
		int srcEnd = srcOffset + srcLength;
		int dstPtr = dstOffset;
		int dstEnd = dstOffset + dstLength;

		while (srcPtr < srcEnd)
		{
			int token = src[srcPtr++];
			int litLen = token >> 4;

			if (litLen == 15)
			{
				int b;
				while ((b = src[srcPtr++]) == 255)
				{
					litLen += 255;
				}
				litLen += b;
			}

			if (srcPtr + litLen > srcEnd || dstPtr + litLen > dstEnd)
			{
				throw new FormatException("Corrupt LZ4 literal stream");
			}

			Buffer.BlockCopy(src, srcPtr, dst, dstPtr, litLen);
			srcPtr += litLen;
			dstPtr += litLen;

			if (srcPtr >= srcEnd)
			{
				break;
			}

			if (srcPtr + 2 > srcEnd)
			{
				throw new FormatException("Corrupt LZ4 offset");
			}

			int offset = src[srcPtr++] | (src[srcPtr++] << 8);
			if (offset == 0)
			{
				throw new FormatException("Invalid LZ4 offset 0");
			}

			int matchLen = token & 0x0F;
			if (matchLen == 15)
			{
				int b;
				while ((b = src[srcPtr++]) == 255)
				{
					matchLen += 255;
				}
				matchLen += b;
			}
			matchLen += 4;

			if (dstPtr + matchLen > dstEnd)
			{
				throw new FormatException("Corrupt LZ4 match stream");
			}

			int matchSrc = dstPtr - offset;
			if (matchSrc < dstOffset)
			{
				throw new FormatException("LZ4 offset out of bounds");
			}

			for (int i = 0; i < matchLen; i++)
			{
				dst[dstPtr++] = dst[matchSrc++];
			}
		}

		return dstPtr - dstOffset;
	}
}
