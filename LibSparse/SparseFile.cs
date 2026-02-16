namespace AndroidSparseTools.LibSparse;

internal sealed class SparseFile
{
    private const uint MaxBackedBlockSize = 64U << 20;

    public uint BlockSize { get; }
    public long Len { get; set; }
    public bool Verbose { get; set; }

    private readonly BackedBlockList _backedBlockList;

    private SparseFile(uint blockSize, long len)
    {
        BlockSize = blockSize;
        Len = len;
        _backedBlockList = new BackedBlockList(blockSize);
    }

    public static SparseFile? New(uint blockSize, long len)
    {
        return new SparseFile(blockSize, len);
    }

    public int AddData(byte[] data, ulong len, uint block)
    {
        return _backedBlockList.AddData(data, 0, len, block);
    }

    public int AddData(byte[] data, int dataOffset, ulong len, uint block)
    {
        return _backedBlockList.AddData(data, dataOffset, len, block);
    }

    public int AddFill(uint fillVal, ulong len, uint block)
    {
        return _backedBlockList.AddFill(fillVal, len, block);
    }

    public int AddFile(string filename, long fileOffset, ulong len, uint block)
    {
        return _backedBlockList.AddFile(filename, fileOffset, len, block);
    }

    public int AddStream(Stream stream, long fileOffset, ulong len, uint block)
    {
        return _backedBlockList.AddStream(stream, fileOffset, len, block);
    }

    public int Write(
        Stream stream,
        bool gz,
        bool sparse,
        bool crc,
        Action<long, long>? progress = null)
    {
        for (BackedBlock? bb = _backedBlockList.First; bb is not null; bb = bb.Next)
        {
            int splitRet = _backedBlockList.Split(bb, MaxBackedBlockSize);
            if (splitRet != 0)
            {
                return splitRet;
            }
        }

        int chunks = CountChunks();
        OutputFile? outFile = OutputFile.OpenStream(stream, BlockSize, Len, gz, sparse, chunks, crc);
        if (outFile is null)
        {
            return Errno.Neg(Errno.ENOMEM);
        }

        int ret = WriteAllBlocks(outFile, progress);
        _ = outFile.Close();
        return ret;
    }

    public int Callback(bool sparse, bool crc, SparseWriteCallback write, object? priv)
    {
        int chunks = CountChunks();
        OutputFile? outFile = OutputFile.OpenCallback(write, priv, BlockSize, Len, false, sparse, chunks, crc);
        if (outFile is null)
        {
            return Errno.Neg(Errno.ENOMEM);
        }

        int ret = WriteAllBlocks(outFile, null);
        _ = outFile.Close();
        return ret;
    }

    public long GetOutputLength(bool sparse, bool crc)
    {
        Counter counter = new();
        int ret = Callback(sparse, crc, static (priv, _, len) =>
        {
            Counter counter = (Counter)priv!;
            counter.Value += len;
            return 0;
        }, counter);

        if (ret < 0)
        {
            return -1;
        }

        return counter.Value;
    }

    public IEnumerable<BackedBlock> EnumerateBlocks() => _backedBlockList.Enumerate();

    private int CountChunks()
    {
        uint lastBlock = 0;
        int chunks = 0;

        foreach (BackedBlock bb in _backedBlockList.Enumerate())
        {
            if (bb.Block > lastBlock)
            {
                chunks++;
            }

            chunks++;
            lastBlock = bb.Block + (uint)SparseDefs.DivRoundUp(bb.Len, BlockSize);
        }

        if (lastBlock < SparseDefs.DivRoundUp((ulong)Len, BlockSize))
        {
            chunks++;
        }

        return chunks;
    }

    private int WriteAllBlocks(OutputFile outFile, Action<long, long>? progress)
    {
        uint lastBlock = 0;
        long processed = 0;
        progress?.Invoke(0, Len);

        foreach (BackedBlock bb in _backedBlockList.Enumerate())
        {
            if (bb.Block > lastBlock)
            {
                uint blocks = bb.Block - lastBlock;
                int skipRet = outFile.WriteSkipChunk((ulong)blocks * BlockSize);
                if (skipRet < 0)
                {
                    return skipRet;
                }

                processed += (long)blocks * BlockSize;
                if (processed > Len)
                {
                    processed = Len;
                }

                progress?.Invoke(processed, Len);
            }

            int ret = WriteBlock(outFile, bb);
            if (ret != 0)
            {
                return ret;
            }

            lastBlock = bb.Block + (uint)SparseDefs.DivRoundUp(bb.Len, BlockSize);
            processed = (long)lastBlock * BlockSize;
            if (processed > Len)
            {
                processed = Len;
            }

            progress?.Invoke(processed, Len);
        }

        long pad = Len - (long)lastBlock * BlockSize;
        if (pad > 0)
        {
            int skipRet = outFile.WriteSkipChunk((ulong)pad);
            if (skipRet < 0)
            {
                return skipRet;
            }

            processed += pad;
            if (processed > Len)
            {
                processed = Len;
            }

            progress?.Invoke(processed, Len);
        }

        return 0;
    }

    private static int WriteBlock(OutputFile outFile, BackedBlock bb)
    {
        return bb.Type switch
        {
            BackedBlockType.Data => outFile.WriteDataChunk(bb.Len, bb.Data!, bb.DataOffset),
            BackedBlockType.File => outFile.WriteFileChunk(bb.Len, bb.Filename!, bb.FileOffset),
            BackedBlockType.Stream => outFile.WriteStreamChunk(bb.Len, bb.SourceStream!, bb.StreamOffset),
            BackedBlockType.Fill => outFile.WriteFillChunk(bb.Len, bb.FillVal),
            _ => Errno.Neg(Errno.EINVAL),
        };
    }

    private sealed class Counter
    {
        public long Value;
    }
}
