using System;
using System.IO;
using System.Text;

namespace FastStartup.BundleCache
{
    internal enum BundleCompression
    {
        /// <summary>Not a UnityFS archive or not parseable: never touch the load.</summary>
        Unknown,

        /// <summary>At least one data block is LZMA (or the block table itself is LZMA): recompressing to LZ4 pays.</summary>
        Lzma,

        /// <summary>All data blocks are stored uncompressed or LZ4/LZ4HC: already fast to load, nothing to cache.</summary>
        NotLzma,
    }

    /// <summary>
    /// Reads the UnityFS header and block table to find the data-block compression. The header's own flags only
    /// describe the block table (every bundle here, LZMA or LZ4, has an LZ4HC table: flags 0x43/0x243), so the table
    /// is decompressed and each block's flags are checked. Layout (big-endian): "UnityFS\0", u32 format version,
    /// two C strings (player version, engine revision), i64 file size, u32 compressed / uncompressed table size,
    /// u32 flags (bits 0-5 table compression: 0 none, 1 LZMA, 2 LZ4, 3 LZ4HC; 0x80 table at the end of the file);
    /// format 7+ aligns the table start to 16 bytes. Table: 16-byte hash, i32 block count, per block u32
    /// uncompressed size, u32 compressed size, u16 flags (bits 0-5 = compression).
    /// </summary>
    internal static class BundleHeader
    {
        private const int CompressionMask = 0x3F;
        private const int BlocksInfoAtTheEnd = 0x80;
        private const int MaxTableSize = 64 * 1024 * 1024;

        /// <summary>Classifies the bundle at the stream's current position; restores the position afterwards.</summary>
        public static BundleCompression Classify(Stream stream)
        {
            long origin = stream.Position;
            try
            {
                return Classify(stream, origin);
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is IndexOutOfRangeException ||
                                      e is ArgumentException || e is EndOfStreamException)
            {
                return BundleCompression.Unknown;
            }
            finally
            {
                stream.Position = origin;
            }
        }

        public static BundleCompression Classify(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                return Classify(file);
            }
        }

        private static BundleCompression Classify(Stream stream, long origin)
        {
            var reader = new BigEndianReader(stream);
            if (reader.CString() != "UnityFS")
            {
                return BundleCompression.Unknown;
            }
            uint version = reader.UInt32();
            reader.CString();
            reader.CString();
            reader.Int64();
            uint compressedSize = reader.UInt32();
            uint uncompressedSize = reader.UInt32();
            uint flags = reader.UInt32();
            if (compressedSize > MaxTableSize || uncompressedSize > MaxTableSize || uncompressedSize < 20)
            {
                return BundleCompression.Unknown;
            }

            if ((flags & BlocksInfoAtTheEnd) != 0)
            {
                stream.Position = stream.Length - compressedSize;
            }
            else if (version >= 7)
            {
                long relative = stream.Position - origin;
                stream.Position = origin + ((relative + 15) & ~15L);
            }
            byte[] packed = reader.Bytes((int)compressedSize);

            byte[] table;
            switch (flags & CompressionMask)
            {
                case 0:
                    table = packed;
                    break;
                case 1:
                    // An LZMA table cannot be read without an LZMA decoder; only LZMA bundles are written this way.
                    return BundleCompression.Lzma;
                case 2:
                case 3:
                    table = Lz4Block.Decode(packed, (int)uncompressedSize);
                    break;
                default:
                    return BundleCompression.Unknown;
            }

            int count = ReadInt32(table, 16);
            if (count < 0 || 20 + (long)count * 10 > table.Length)
            {
                return BundleCompression.Unknown;
            }
            for (int i = 0; i < count; i++)
            {
                int blockFlags = (table[20 + i * 10 + 8] << 8) | table[20 + i * 10 + 9];
                if ((blockFlags & CompressionMask) == 1)
                {
                    return BundleCompression.Lzma;
                }
            }
            return BundleCompression.NotLzma;
        }

        private static int ReadInt32(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

        private sealed class BigEndianReader
        {
            private readonly Stream _stream;

            public BigEndianReader(Stream stream) => _stream = stream;

            public byte[] Bytes(int count)
            {
                var buffer = new byte[count];
                int read = 0;
                while (read < count)
                {
                    int n = _stream.Read(buffer, read, count - read);
                    if (n <= 0)
                    {
                        throw new EndOfStreamException();
                    }
                    read += n;
                }
                return buffer;
            }

            public uint UInt32()
            {
                byte[] b = Bytes(4);
                return (uint)ReadInt32(b, 0);
            }

            public long Int64()
            {
                byte[] b = Bytes(8);
                return ((long)(uint)ReadInt32(b, 0) << 32) | (uint)ReadInt32(b, 4);
            }

            /// <summary>Null-terminated ASCII, at most 256 bytes (header strings are short version numbers).</summary>
            public string CString()
            {
                var sb = new StringBuilder();
                for (int i = 0; i < 256; i++)
                {
                    int c = _stream.ReadByte();
                    if (c < 0)
                    {
                        throw new EndOfStreamException();
                    }
                    if (c == 0)
                    {
                        return sb.ToString();
                    }
                    sb.Append((char)c);
                }
                throw new InvalidDataException("header string too long");
            }
        }
    }

    /// <summary>LZ4 block format decoder (no frame header), enough for UnityFS block tables.</summary>
    internal static class Lz4Block
    {
        public static byte[] Decode(byte[] src, int outputSize)
        {
            var dst = new byte[outputSize];
            int s = 0;
            int d = 0;
            while (s < src.Length)
            {
                int token = src[s++];
                int literals = token >> 4;
                if (literals == 15)
                {
                    int b;
                    do
                    {
                        b = src[s++];
                        literals += b;
                    }
                    while (b == 255);
                }
                Buffer.BlockCopy(src, s, dst, d, literals);
                s += literals;
                d += literals;
                if (s >= src.Length)
                {
                    break; // the last sequence carries literals only
                }

                int offset = src[s] | (src[s + 1] << 8);
                s += 2;
                int match = token & 15;
                if (match == 15)
                {
                    int b;
                    do
                    {
                        b = src[s++];
                        match += b;
                    }
                    while (b == 255);
                }
                match += 4;
                int from = d - offset;
                if (offset == 0 || from < 0 || d + match > dst.Length)
                {
                    throw new InvalidDataException("corrupt LZ4 block");
                }
                for (int i = 0; i < match; i++)
                {
                    dst[d++] = dst[from + i]; // byte by byte: source and target may overlap
                }
            }
            if (d != outputSize)
            {
                throw new InvalidDataException("LZ4 block size mismatch");
            }
            return dst;
        }
    }
}
