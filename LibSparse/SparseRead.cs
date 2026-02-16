using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace AndroidSparseTools.LibSparse;

internal enum SparseReadMode
{
    Normal = 0,
    Sparse = 1,
    Hole = 2,
}

internal static class SparseRead
{
    private const int CopyBufSize = 1024 * 1024;

    private static byte[]? s_copyBuf;

    public static SparseFile? SparseFileImport(Stream stream, bool verbose, bool crc)
    {
        SparseFileSource source = new SparseFileStreamSource(stream);
        return SparseFileImportSource(source, verbose, crc);
    }

    public static SparseFile? SparseFileImportBuf(byte[] buf, int len, bool verbose, bool crc)
    {
        SparseFileSource source = new SparseFileBufSource(buf, len);
        return SparseFileImportSource(source, verbose, crc);
    }

    public static SparseFile? SparseFileImportAuto(Stream stream, bool crc, bool verbose)
    {
        SparseFile? s = SparseFileImport(stream, false, crc);
        if (s is not null)
        {
            return s;
        }

        if (!stream.CanSeek)
        {
            return null;
        }

        long len;
        try
        {
            len = stream.Seek(0, SeekOrigin.End);
            stream.Seek(0, SeekOrigin.Begin);
        }
        catch
        {
            return null;
        }

        s = SparseFile.New(4096, len);
        if (s is null)
        {
            return null;
        }

        if (verbose)
        {
            s.Verbose = true;
        }

        int ret = SparseFileReadNormal(s, stream);
        if (ret < 0)
        {
            return null;
        }

        return s;
    }

    public static int SparseFileRead(SparseFile s, Stream stream, SparseReadMode mode, bool crc)
    {
        if (crc && mode != SparseReadMode.Sparse)
        {
            return Errno.Neg(Errno.EINVAL);
        }

        return mode switch
        {
            SparseReadMode.Sparse => SparseFileReadSparse(s, new SparseFileStreamSource(stream), crc),
            SparseReadMode.Normal => SparseFileReadNormal(s, stream),
            SparseReadMode.Hole => SparseFileReadHole(s, stream),
            _ => Errno.Neg(Errno.EINVAL),
        };
    }

    private static SparseFile? SparseFileImportSource(SparseFileSource source, bool verbose, bool crc)
    {
        byte[] sparseHeaderBytes = new byte[SparseFormat.SPARSE_HEADER_LEN];
        int ret = source.ReadValue(sparseHeaderBytes);
        if (ret < 0)
        {
            VerboseError(verbose, ret, "header");
            return null;
        }

        SparseHeader sparseHeader = SparseHeader.Read(sparseHeaderBytes);

        if (sparseHeader.Magic != SparseFormat.SPARSE_HEADER_MAGIC)
        {
            VerboseError(verbose, Errno.Neg(Errno.EINVAL), "header magic");
            return null;
        }

        if (sparseHeader.MajorVersion != SparseFormat.SPARSE_HEADER_MAJOR_VER)
        {
            VerboseError(verbose, Errno.Neg(Errno.EINVAL), "header major version");
            return null;
        }

        if (sparseHeader.FileHeaderSize < SparseFormat.SPARSE_HEADER_LEN)
        {
            return null;
        }

        if (sparseHeader.ChunkHeaderSize < SparseFormat.CHUNK_HEADER_LEN)
        {
            return null;
        }

        if (sparseHeader.BlockSize == 0 || sparseHeader.BlockSize % 4 != 0)
        {
            return null;
        }

        if (sparseHeader.TotalBlocks == 0)
        {
            return null;
        }

        long len = (long)sparseHeader.TotalBlocks * sparseHeader.BlockSize;
        SparseFile? s = SparseFile.New(sparseHeader.BlockSize, len);
        if (s is null)
        {
            VerboseError(verbose, Errno.Neg(Errno.EINVAL), null);
            return null;
        }

        ret = source.Rewind();
        if (ret < 0)
        {
            VerboseError(verbose, ret, "seeking");
            return null;
        }

        s.Verbose = verbose;

        ret = SparseFileReadSparse(s, source, crc);
        if (ret < 0)
        {
            return null;
        }

        return s;
    }

    private static int SparseFileReadSparse(SparseFile s, SparseFileSource source, bool crc)
    {
        if (s_copyBuf is null)
        {
            try
            {
                s_copyBuf = new byte[CopyBufSize];
            }
            catch
            {
                return Errno.Neg(Errno.ENOMEM);
            }
        }

        uint? crcValue = crc ? 0u : null;

        byte[] sparseHeaderBytes = new byte[SparseFormat.SPARSE_HEADER_LEN];
        int ret = source.ReadValue(sparseHeaderBytes);
        if (ret < 0)
        {
            return ret;
        }

        SparseHeader sparseHeader = SparseHeader.Read(sparseHeaderBytes);

        if (sparseHeader.Magic != SparseFormat.SPARSE_HEADER_MAGIC)
        {
            return Errno.Neg(Errno.EINVAL);
        }

        if (sparseHeader.MajorVersion != SparseFormat.SPARSE_HEADER_MAJOR_VER)
        {
            return Errno.Neg(Errno.EINVAL);
        }

        if (sparseHeader.FileHeaderSize < SparseFormat.SPARSE_HEADER_LEN)
        {
            return Errno.Neg(Errno.EINVAL);
        }

        if (sparseHeader.ChunkHeaderSize < SparseFormat.CHUNK_HEADER_LEN)
        {
            return Errno.Neg(Errno.EINVAL);
        }

        if (sparseHeader.FileHeaderSize > SparseFormat.SPARSE_HEADER_LEN)
        {
            ret = source.Seek(sparseHeader.FileHeaderSize - SparseFormat.SPARSE_HEADER_LEN);
            if (ret < 0)
            {
                return ret;
            }
        }

        uint curBlock = 0;
        for (uint i = 0; i < sparseHeader.TotalChunks; i++)
        {
            byte[] chunkHeaderBytes = new byte[SparseFormat.CHUNK_HEADER_LEN];
            ret = source.ReadValue(chunkHeaderBytes);
            if (ret < 0)
            {
                return ret;
            }

            ChunkHeader chunkHeader = ChunkHeader.Read(chunkHeaderBytes);

            if (sparseHeader.ChunkHeaderSize > SparseFormat.CHUNK_HEADER_LEN)
            {
                ret = source.Seek(sparseHeader.ChunkHeaderSize - SparseFormat.CHUNK_HEADER_LEN);
                if (ret < 0)
                {
                    return ret;
                }
            }

            ret = ProcessChunk(s, source, sparseHeader.ChunkHeaderSize, chunkHeader, curBlock, ref crcValue);
            if (ret < 0)
            {
                return ret;
            }

            curBlock += (uint)ret;
        }

        if (sparseHeader.TotalBlocks != curBlock)
        {
            return Errno.Neg(Errno.EINVAL);
        }

        return 0;
    }

    private static int ProcessChunk(
        SparseFile s,
        SparseFileSource source,
        uint chunkHeaderSize,
        ChunkHeader chunkHeader,
        uint curBlock,
        ref uint? crc)
    {
        long offset = source.GetOffset();
        uint chunkDataSize = unchecked(chunkHeader.TotalSize - chunkHeaderSize);

        switch (chunkHeader.ChunkType)
        {
            case SparseFormat.CHUNK_TYPE_RAW:
                {
                    int ret = ProcessRawChunk(s, chunkDataSize, source, chunkHeader.ChunkSize, curBlock, ref crc);
                    if (ret < 0)
                    {
                        VerboseError(s.Verbose, ret, string.Format(CultureInfo.InvariantCulture, "data block at {0}", offset));
                        return ret;
                    }

                    return (int)chunkHeader.ChunkSize;
                }
            case SparseFormat.CHUNK_TYPE_FILL:
                {
                    int ret = ProcessFillChunk(s, chunkDataSize, source, chunkHeader.ChunkSize, curBlock, ref crc);
                    if (ret < 0)
                    {
                        VerboseError(s.Verbose, ret, string.Format(CultureInfo.InvariantCulture, "fill block at {0}", offset));
                        return ret;
                    }

                    return (int)chunkHeader.ChunkSize;
                }
            case SparseFormat.CHUNK_TYPE_DONT_CARE:
                {
                    int ret = ProcessSkipChunk(s, chunkDataSize, source, chunkHeader.ChunkSize, curBlock, ref crc);
                    if (chunkDataSize != 0 && ret < 0)
                    {
                        VerboseError(s.Verbose, ret, string.Format(CultureInfo.InvariantCulture, "skip block at {0}", offset));
                        return ret;
                    }

                    return (int)chunkHeader.ChunkSize;
                }
            case SparseFormat.CHUNK_TYPE_CRC32:
                {
                    int ret = ProcessCrc32Chunk(source, chunkDataSize, ref crc);
                    if (ret < 0)
                    {
                        VerboseError(s.Verbose, Errno.Neg(Errno.EINVAL), string.Format(CultureInfo.InvariantCulture, "crc block at {0}", offset));
                        return ret;
                    }

                    return 0;
                }
            default:
                VerboseError(
                    s.Verbose,
                    Errno.Neg(Errno.EINVAL),
                    string.Format(CultureInfo.InvariantCulture, "unknown block {0:X4} at {1}", chunkHeader.ChunkType, offset));
                return 0;
        }
    }

    private static int ProcessRawChunk(
        SparseFile s,
        uint chunkSize,
        SparseFileSource source,
        uint blocks,
        uint block,
        ref uint? crc)
    {
        long len = (long)blocks * s.BlockSize;

        if (chunkSize % s.BlockSize != 0)
        {
            return Errno.Neg(Errno.EINVAL);
        }

        if (chunkSize / s.BlockSize != blocks)
        {
            return Errno.Neg(Errno.EINVAL);
        }

        int ret = source.AddToSparseFile(s, len, block);
        if (ret < 0)
        {
            return ret;
        }

        if (crc.HasValue)
        {
            uint crcValue = crc.Value;
            ret = source.GetCrc32(ref crcValue, len);
            if (ret < 0)
            {
                return ret;
            }

            crc = crcValue;
        }
        else
        {
            ret = source.Seek(len);
            if (ret < 0)
            {
                return ret;
            }
        }

        return 0;
    }

    private static int ProcessFillChunk(
        SparseFile s,
        uint chunkSize,
        SparseFileSource source,
        uint blocks,
        uint block,
        ref uint? crc)
    {
        if (chunkSize != sizeof(uint))
        {
            return Errno.Neg(Errno.EINVAL);
        }

        Span<byte> fillBytes = stackalloc byte[sizeof(uint)];
        int ret = source.ReadValue(fillBytes);
        if (ret < 0)
        {
            return ret;
        }

        uint fillVal = BinaryPrimitives.ReadUInt32LittleEndian(fillBytes);
        long len = (long)blocks * s.BlockSize;

        ret = s.AddFill(fillVal, (ulong)len, block);
        if (ret < 0)
        {
            return ret;
        }

        if (crc.HasValue)
        {
            Span<uint> fillBuf = MemoryMarshal.Cast<byte, uint>(s_copyBuf!.AsSpan());
            for (int i = 0; i < fillBuf.Length; i++)
            {
                fillBuf[i] = fillVal;
            }

            uint crcValue = crc.Value;
            long remain = len;
            while (remain > 0)
            {
                int chunk = (int)Math.Min(remain, CopyBufSize);
                crcValue = SparseCrc32.Compute(crcValue, s_copyBuf!.AsSpan(0, chunk));
                remain -= chunk;
            }

            crc = crcValue;
        }

        return 0;
    }

    private static int ProcessSkipChunk(
        SparseFile s,
        uint chunkSize,
        SparseFileSource source,
        uint blocks,
        uint block,
        ref uint? crc)
    {
        _ = source;
        _ = block;

        if (chunkSize != 0)
        {
            return Errno.Neg(Errno.EINVAL);
        }

        if (crc.HasValue)
        {
            Array.Clear(s_copyBuf!);
            uint crcValue = crc.Value;
            long len = (long)blocks * s.BlockSize;
            while (len > 0)
            {
                int chunk = (int)Math.Min(len, CopyBufSize);
                crcValue = SparseCrc32.Compute(crcValue, s_copyBuf!.AsSpan(0, chunk));
                len -= chunk;
            }

            crc = crcValue;
        }

        return 0;
    }

    private static int ProcessCrc32Chunk(SparseFileSource source, uint chunkSize, ref uint? crc)
    {
        if (chunkSize != sizeof(uint))
        {
            return Errno.Neg(Errno.EINVAL);
        }

        Span<byte> crcBytes = stackalloc byte[sizeof(uint)];
        int ret = source.ReadValue(crcBytes);
        if (ret < 0)
        {
            return ret;
        }

        uint fileCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcBytes);
        if (crc.HasValue && fileCrc != crc.Value)
        {
            return Errno.Neg(Errno.EINVAL);
        }

        return 0;
    }

    private static int SparseFileReadNormal(SparseFile s, Stream stream)
    {
        byte[] buf;
        try
        {
            buf = new byte[s.BlockSize];
        }
        catch
        {
            return Errno.Neg(Errno.ENOMEM);
        }

        return DoSparseFileReadNormal(s, stream, buf, 0, s.Len);
    }

    private static int SparseFileReadHole(SparseFile s, Stream stream)
    {
        _ = s;
        _ = stream;
        return Errno.Neg(Errno.ENOTSUP);
    }

    private static int DoSparseFileReadNormal(SparseFile s, Stream stream, byte[] buf, long offset, long remain)
    {
        if (buf.Length == 0)
        {
            return Errno.Neg(Errno.ENOMEM);
        }

        uint block = (uint)(offset / s.BlockSize);

        while (remain > 0)
        {
            int toRead = (int)Math.Min(remain, s.BlockSize);
            int ret = OutputFile.ReadAll(stream, buf.AsSpan(0, toRead));
            if (ret < 0)
            {
                return ret;
            }

            bool sparseBlock;
            if (toRead == s.BlockSize)
            {
                sparseBlock = true;
                uint firstWord = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, sizeof(uint)));
                for (int i = 1; i < s.BlockSize / sizeof(uint); i++)
                {
                    uint word = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * sizeof(uint), sizeof(uint)));
                    if (firstWord != word)
                    {
                        sparseBlock = false;
                        break;
                    }
                }
            }
            else
            {
                sparseBlock = false;
            }

            if (sparseBlock)
            {
                uint firstWord = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, sizeof(uint)));
                _ = s.AddFill(firstWord, (ulong)toRead, block);
            }
            else
            {
                _ = s.AddStream(stream, offset, (ulong)toRead, block);
            }

            remain -= toRead;
            offset += toRead;
            block++;
        }

        return 0;
    }

    private static void VerboseError(bool verbose, int err, string? location)
    {
        if (!verbose)
        {
            return;
        }

        StringBuilder message = new(ErrorString(err));
        if (!string.IsNullOrEmpty(location))
        {
            message.Append(" at ");
            message.Append(location);
        }

        message.AppendLine();
        SparseLog.SparsePrintVerbose(message.ToString());
    }

    private static string ErrorString(int err)
    {
        return err switch
        {
            -75 => "EOF while reading file",
            -22 => "Invalid sparse file format",
            -12 => "Failed allocation while reading file",
            _ => string.Format(CultureInfo.InvariantCulture, "Unknown error {0}", err),
        };
    }

    private abstract class SparseFileSource
    {
        public abstract int Seek(long offset);

        public abstract long GetOffset();

        public abstract int Rewind();

        public abstract int AddToSparseFile(SparseFile s, long len, uint block);

        public abstract int ReadValue(Span<byte> ptr);

        public abstract int ReadValue(byte[] ptr);

        public abstract int GetCrc32(ref uint crc32, long len);
    }

    private sealed class SparseFileStreamSource : SparseFileSource
    {
        private readonly Stream _stream;

        public SparseFileStreamSource(Stream stream)
        {
            _stream = stream;
        }

        public override int Seek(long offset)
        {
            if (!_stream.CanSeek)
            {
                return Errno.Neg(Errno.ESPIPE);
            }

            try
            {
                _stream.Seek(offset, SeekOrigin.Current);
                return 0;
            }
            catch
            {
                return Errno.Neg(Errno.EINVAL);
            }
        }

        public override long GetOffset()
        {
            if (!_stream.CanSeek)
            {
                return -1;
            }

            try
            {
                return _stream.Position;
            }
            catch
            {
                return -1;
            }
        }

        public override int Rewind()
        {
            if (!_stream.CanSeek)
            {
                return Errno.Neg(Errno.ESPIPE);
            }

            try
            {
                _stream.Seek(0, SeekOrigin.Begin);
                return 0;
            }
            catch
            {
                return Errno.Neg(Errno.EINVAL);
            }
        }

        public override int AddToSparseFile(SparseFile s, long len, uint block)
        {
            return s.AddStream(_stream, GetOffset(), (ulong)len, block);
        }

        public override int ReadValue(Span<byte> ptr)
        {
            return OutputFile.ReadAll(_stream, ptr);
        }

        public override int ReadValue(byte[] ptr)
        {
            return OutputFile.ReadAll(_stream, ptr.AsSpan());
        }

        public override int GetCrc32(ref uint crc32, long len)
        {
            while (len > 0)
            {
                int chunk = (int)Math.Min(len, CopyBufSize);
                int ret = OutputFile.ReadAll(_stream, s_copyBuf!.AsSpan(0, chunk));
                if (ret < 0)
                {
                    return ret;
                }

                crc32 = SparseCrc32.Compute(crc32, s_copyBuf.AsSpan(0, chunk));
                len -= chunk;
            }

            return 0;
        }
    }

    private sealed class SparseFileBufSource : SparseFileSource
    {
        private readonly byte[] _buf;
        private readonly int _bufEnd;
        private int _pos;
        private long _offset;

        public SparseFileBufSource(byte[] buf, int len)
        {
            _buf = buf;
            _bufEnd = len;
            _pos = 0;
            _offset = 0;
        }

        public override int Seek(long offset)
        {
            int ret = AccessOkay(offset);
            if (ret < 0)
            {
                return ret;
            }

            _pos += (int)offset;
            _offset += offset;
            return 0;
        }

        public override long GetOffset()
        {
            return _offset;
        }

        public override int Rewind()
        {
            _pos = 0;
            _offset = 0;
            return 0;
        }

        public override int AddToSparseFile(SparseFile s, long len, uint block)
        {
            int ret = AccessOkay(len);
            if (ret < 0)
            {
                return ret;
            }

            return s.AddData(_buf, _pos, (ulong)len, block);
        }

        public override int ReadValue(Span<byte> ptr)
        {
            int ret = AccessOkay(ptr.Length);
            if (ret < 0)
            {
                return ret;
            }

            _buf.AsSpan(_pos, ptr.Length).CopyTo(ptr);
            _pos += ptr.Length;
            _offset += ptr.Length;
            return 0;
        }

        public override int ReadValue(byte[] ptr)
        {
            return ReadValue(ptr.AsSpan());
        }

        public override int GetCrc32(ref uint crc32, long len)
        {
            int ret = AccessOkay(len);
            if (ret < 0)
            {
                return ret;
            }

            crc32 = SparseCrc32.Compute(crc32, _buf.AsSpan(_pos, (int)len));
            _pos += (int)len;
            _offset += len;
            return 0;
        }

        private int AccessOkay(long len)
        {
            if (len <= 0)
            {
                return Errno.Neg(Errno.EINVAL);
            }

            if (_pos < 0)
            {
                return Errno.Neg(Errno.EOVERFLOW);
            }

            if (_pos >= _bufEnd)
            {
                return Errno.Neg(Errno.EOVERFLOW);
            }

            if (len > _bufEnd - _pos)
            {
                return Errno.Neg(Errno.EOVERFLOW);
            }

            return 0;
        }
    }
}
