using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace AndroidSparseTools.LibSparse;

internal delegate int SparseWriteCallback(object? priv, byte[]? data, int len);

internal sealed class OutputFile
{
    private const int FillZeroBufSize = 2 * 1024 * 1024;
    private const int StreamCopyChunkSize = 1024 * 1024;

    private readonly IOutputTarget _target;
    private readonly bool _sparse;

    private long _curOutPtr;
    private uint _chunkCnt;
    private uint _crc32;
    private readonly bool _useCrc;
    private readonly uint _blockSize;
    private readonly long _len;
    private byte[] _zeroBuf;
    private readonly uint[] _fillBuf;

    private OutputFile(IOutputTarget target, uint blockSize, long len, bool sparse, bool crc)
    {
        _target = target;
        _blockSize = blockSize;
        _len = len;
        _sparse = sparse;
        _useCrc = crc;
        _zeroBuf = new byte[FillZeroBufSize];
        _fillBuf = new uint[FillZeroBufSize / sizeof(uint)];
    }

    public static OutputFile? OpenStream(
        Stream stream,
        uint blockSize,
        long len,
        bool gz,
        bool sparse,
        int chunks,
        bool crc)
    {
        if (gz)
        {
            return null;
        }

        OutputFile outFile = new(new StreamOutputTarget(stream), blockSize, len, sparse, crc);
        return outFile.Init(chunks) < 0 ? null : outFile;
    }

    public static OutputFile? OpenCallback(
        SparseWriteCallback callback,
        object? priv,
        uint blockSize,
        long len,
        bool gz,
        bool sparse,
        int chunks,
        bool crc)
    {
        if (gz)
        {
            return null;
        }

        OutputFile outFile = new(new CallbackOutputTarget(callback, priv), blockSize, len, sparse, crc);
        return outFile.Init(chunks) < 0 ? null : outFile;
    }

    public int Close()
    {
        int ret = WriteEndChunk();
        _zeroBuf = Array.Empty<byte>();
        _target.Close();
        return ret;
    }

    public int WriteDataChunk(ulong len, byte[] data, int dataOffset)
    {
        return _sparse
            ? WriteSparseDataChunk(len, data, dataOffset)
            : WriteNormalDataChunk(len, data, dataOffset);
    }

    public int WriteFillChunk(ulong len, uint fillVal)
    {
        return _sparse
            ? WriteSparseFillChunk(len, fillVal)
            : WriteNormalFillChunk(len, fillVal);
    }

    public int WriteStreamChunk(ulong len, Stream source, long offset)
    {
        return _sparse
            ? WriteSparseStreamChunk(len, source, offset)
            : WriteNormalStreamChunk(len, source, offset);
    }

    public int WriteFileChunk(ulong len, string file, long offset)
    {
        try
        {
            using FileStream fs = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            return WriteStreamChunk(len, fs, offset);
        }
        catch
        {
            return -1;
        }
    }

    public int WriteSkipChunk(ulong len)
    {
        return _sparse ? WriteSparseSkipChunk(len) : WriteNormalSkipChunk(len);
    }

    public static int ReadAll(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int ret;
            try
            {
                ret = stream.Read(buffer.Slice(total));
            }
            catch
            {
                return Errno.Neg(Errno.EINVAL);
            }

            if (ret < 0)
            {
                return Errno.Neg(Errno.EINVAL);
            }

            if (ret == 0)
            {
                return Errno.Neg(Errno.EINVAL);
            }

            total += ret;
        }

        return 0;
    }

    private int Init(int chunks)
    {
        if (!_sparse)
        {
            return 0;
        }

        SparseHeader sparseHeader = new(
            SparseFormat.SPARSE_HEADER_MAGIC,
            SparseFormat.SPARSE_HEADER_MAJOR_VER,
            SparseFormat.SPARSE_HEADER_MINOR_VER,
            SparseFormat.SPARSE_HEADER_LEN,
            SparseFormat.CHUNK_HEADER_LEN,
            _blockSize,
            (uint)SparseDefs.DivRoundUp((ulong)_len, _blockSize),
            (uint)chunks + (_useCrc ? 1U : 0U),
            0);

        byte[] header = new byte[SparseFormat.SPARSE_HEADER_LEN];
        sparseHeader.Write(header);
        return _target.Write(header, header.Length);
    }

    private int WriteSparseSkipChunk(ulong skipLen)
    {
        if (skipLen % _blockSize != 0)
        {
            return -1;
        }

        ChunkHeader chunkHeader = new(
            SparseFormat.CHUNK_TYPE_DONT_CARE,
            0,
            (uint)(skipLen / _blockSize),
            SparseFormat.CHUNK_HEADER_LEN);

        byte[] buf = new byte[SparseFormat.CHUNK_HEADER_LEN];
        chunkHeader.Write(buf);
        int ret = _target.Write(buf, buf.Length);
        if (ret < 0)
        {
            return -1;
        }

        _curOutPtr += (long)skipLen;
        _chunkCnt++;
        return 0;
    }

    private int WriteSparseFillChunk(ulong len, uint fillVal)
    {
        ulong roundedUpLen = SparseDefs.Align(len, _blockSize);
        ChunkHeader chunkHeader = new(
            SparseFormat.CHUNK_TYPE_FILL,
            0,
            (uint)(roundedUpLen / _blockSize),
            SparseFormat.CHUNK_HEADER_LEN + sizeof(uint));

        byte[] hdr = new byte[SparseFormat.CHUNK_HEADER_LEN];
        chunkHeader.Write(hdr);
        int ret = _target.Write(hdr, hdr.Length);
        if (ret < 0)
        {
            return -1;
        }

        Span<byte> fillBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(fillBytes, fillVal);
        ret = _target.Write(fillBytes, fillBytes.Length);
        if (ret < 0)
        {
            return -1;
        }

        if (_useCrc)
        {
            int count = (int)(_blockSize / sizeof(uint));
            while (count-- > 0)
            {
                _crc32 = SparseCrc32.Compute(_crc32, fillBytes);
            }
        }

        _curOutPtr += (long)roundedUpLen;
        _chunkCnt++;
        return 0;
    }

    private int WriteSparseDataChunk(ulong len, byte[] data, int dataOffset)
    {
        ulong roundedUpLen = SparseDefs.Align(len, _blockSize);
        ulong zeroLen = roundedUpLen - len;

        ChunkHeader chunkHeader = new(
            SparseFormat.CHUNK_TYPE_RAW,
            0,
            (uint)(roundedUpLen / _blockSize),
            (uint)(SparseFormat.CHUNK_HEADER_LEN + roundedUpLen));

        byte[] hdr = new byte[SparseFormat.CHUNK_HEADER_LEN];
        chunkHeader.Write(hdr);
        int ret = _target.Write(hdr, hdr.Length);
        if (ret < 0)
        {
            return -1;
        }

        ret = _target.Write(new ReadOnlySpan<byte>(data, dataOffset, (int)len), (int)len);
        if (ret < 0)
        {
            return -1;
        }

        if (zeroLen > 0)
        {
            ulong remain = zeroLen;
            while (remain > 0)
            {
                int writeLen = (int)Math.Min(remain, (ulong)FillZeroBufSize);
                ret = _target.Write(_zeroBuf.AsSpan(0, writeLen), writeLen);
                if (ret < 0)
                {
                    return ret;
                }

                remain -= (ulong)writeLen;
            }
        }

        if (_useCrc)
        {
            _crc32 = SparseCrc32.Compute(_crc32, new ReadOnlySpan<byte>(data, dataOffset, (int)len));
            if (zeroLen > 0)
            {
                ulong remain = zeroLen;
                while (remain > 0)
                {
                    int writeLen = (int)Math.Min(remain, (ulong)FillZeroBufSize);
                    _crc32 = SparseCrc32.Compute(_crc32, _zeroBuf.AsSpan(0, writeLen));
                    remain -= (ulong)writeLen;
                }
            }
        }

        _curOutPtr += (long)roundedUpLen;
        _chunkCnt++;
        return 0;
    }

    private int WriteSparseStreamChunk(ulong len, Stream source, long offset)
    {
        ulong roundedUpLen = SparseDefs.Align(len, _blockSize);
        ulong zeroLen = roundedUpLen - len;

        ChunkHeader chunkHeader = new(
            SparseFormat.CHUNK_TYPE_RAW,
            0,
            (uint)(roundedUpLen / _blockSize),
            (uint)(SparseFormat.CHUNK_HEADER_LEN + roundedUpLen));

        byte[] hdr = new byte[SparseFormat.CHUNK_HEADER_LEN];
        chunkHeader.Write(hdr);
        int ret = _target.Write(hdr, hdr.Length);
        if (ret < 0)
        {
            return -1;
        }

        ret = WriteStreamRange(source, offset, len, span =>
        {
            int r = _target.Write(span, span.Length);
            if (r < 0)
            {
                return false;
            }

            if (_useCrc)
            {
                _crc32 = SparseCrc32.Compute(_crc32, span);
            }

            return true;
        });
        if (ret < 0)
        {
            return -1;
        }

        if (zeroLen > 0)
        {
            ulong remain = zeroLen;
            while (remain > 0)
            {
                int writeLen = (int)Math.Min(remain, (ulong)FillZeroBufSize);
                ret = _target.Write(_zeroBuf.AsSpan(0, writeLen), writeLen);
                if (ret < 0)
                {
                    return ret;
                }

                remain -= (ulong)writeLen;
            }

            if (_useCrc)
            {
                remain = zeroLen;
                while (remain > 0)
                {
                    int writeLen = (int)Math.Min(remain, (ulong)FillZeroBufSize);
                    _crc32 = SparseCrc32.Compute(_crc32, _zeroBuf.AsSpan(0, writeLen));
                    remain -= (ulong)writeLen;
                }
            }
        }

        _curOutPtr += (long)roundedUpLen;
        _chunkCnt++;
        return 0;
    }

    private int WriteSparseEndChunk()
    {
        if (!_useCrc)
        {
            return 0;
        }

        ChunkHeader chunkHeader = new(
            SparseFormat.CHUNK_TYPE_CRC32,
            0,
            0,
            SparseFormat.CHUNK_HEADER_LEN + sizeof(uint));

        byte[] hdr = new byte[SparseFormat.CHUNK_HEADER_LEN];
        chunkHeader.Write(hdr);

        int ret = _target.Write(hdr, hdr.Length);
        if (ret < 0)
        {
            return ret;
        }

        Span<byte> crcBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(crcBytes, _crc32);
        ret = _target.Write(crcBytes, crcBytes.Length);
        if (ret < 0)
        {
            return ret;
        }

        _chunkCnt++;
        return 0;
    }

    private int WriteNormalDataChunk(ulong len, byte[] data, int dataOffset)
    {
        ulong roundedUpLen = SparseDefs.Align(len, _blockSize);
        int ret = _target.Write(new ReadOnlySpan<byte>(data, dataOffset, (int)len), (int)len);
        if (ret < 0)
        {
            return ret;
        }

        if (roundedUpLen > len)
        {
            ret = _target.Skip((long)(roundedUpLen - len));
        }

        return ret;
    }

    private int WriteNormalFillChunk(ulong len, uint fillVal)
    {
        for (int i = 0; i < _fillBuf.Length; i++)
        {
            _fillBuf[i] = fillVal;
        }

        ReadOnlySpan<byte> fillBytes = MemoryMarshal.AsBytes(_fillBuf.AsSpan());

        ulong remain = len;
        while (remain > 0)
        {
            int writeLen = (int)Math.Min(remain, (ulong)FillZeroBufSize);
            int ret = _target.Write(fillBytes.Slice(0, writeLen), writeLen);
            if (ret < 0)
            {
                return ret;
            }

            remain -= (ulong)writeLen;
        }

        return 0;
    }

    private int WriteNormalStreamChunk(ulong len, Stream source, long offset)
    {
        ulong roundedUpLen = SparseDefs.Align(len, _blockSize);

        int ret = WriteStreamRange(source, offset, len, span => _target.Write(span, span.Length) >= 0);
        if (ret < 0)
        {
            return ret;
        }

        if (roundedUpLen > len)
        {
            ret = _target.Skip((long)(roundedUpLen - len));
        }

        return ret;
    }

    private int WriteNormalSkipChunk(ulong len)
    {
        return _target.Skip((long)len);
    }

    private int WriteNormalEndChunk()
    {
        return _target.Pad(_len);
    }

    private int WriteEndChunk()
    {
        return _sparse ? WriteSparseEndChunk() : WriteNormalEndChunk();
    }

    private static int WriteStreamRange(Stream source, long offset, ulong len, Func<ReadOnlySpan<byte>, bool> callback)
    {
        if (!source.CanSeek)
        {
            return Errno.Neg(Errno.ESPIPE);
        }

        try
        {
            source.Seek(offset, SeekOrigin.Begin);
        }
        catch
        {
            return Errno.Neg(Errno.EINVAL);
        }

        byte[] buf = new byte[StreamCopyChunkSize];
        ulong written = 0;
        while (written < len)
        {
            int toRead = (int)Math.Min((ulong)buf.Length, len - written);
            int total = 0;
            while (total < toRead)
            {
                int ret;
                try
                {
                    ret = source.Read(buf, total, toRead - total);
                }
                catch
                {
                    return Errno.Neg(Errno.EINVAL);
                }

                if (ret <= 0)
                {
                    return Errno.Neg(Errno.EINVAL);
                }

                total += ret;
            }

            if (!callback(buf.AsSpan(0, total)))
            {
                return -1;
            }

            written += (ulong)total;
        }

        return 0;
    }

    private interface IOutputTarget
    {
        int Skip(long cnt);
        int Pad(long len);
        int Write(ReadOnlySpan<byte> data, int len);
        void Close();
    }

    private sealed class StreamOutputTarget : IOutputTarget
    {
        private readonly Stream _stream;

        public StreamOutputTarget(Stream stream)
        {
            _stream = stream;
        }

        public int Skip(long cnt)
        {
            if (!_stream.CanSeek)
            {
                return -1;
            }

            try
            {
                _stream.Seek(cnt, SeekOrigin.Current);
                return 0;
            }
            catch
            {
                return -1;
            }
        }

        public int Pad(long len)
        {
            if (!_stream.CanSeek)
            {
                return -1;
            }

            try
            {
                _stream.SetLength(len);
                return 0;
            }
            catch
            {
                return Errno.Neg(Errno.EINVAL);
            }
        }

        public int Write(ReadOnlySpan<byte> data, int len)
        {
            try
            {
                _stream.Write(data);
                return 0;
            }
            catch
            {
                return -1;
            }
        }

        public void Close()
        {
        }
    }

    private sealed class CallbackOutputTarget : IOutputTarget
    {
        private readonly SparseWriteCallback _write;
        private readonly object? _priv;

        public CallbackOutputTarget(SparseWriteCallback write, object? priv)
        {
            _write = write;
            _priv = priv;
        }

        public int Skip(long cnt)
        {
            while (cnt > 0)
            {
                int toWrite = (int)Math.Min(cnt, int.MaxValue);
                int ret = _write(_priv, null, toWrite);
                if (ret < 0)
                {
                    return ret;
                }

                cnt -= toWrite;
            }

            return 0;
        }

        public int Pad(long len)
        {
            return -1;
        }

        public int Write(ReadOnlySpan<byte> data, int len)
        {
            return _write(_priv, data.ToArray(), len);
        }

        public void Close()
        {
        }
    }
}
