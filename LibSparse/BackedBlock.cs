using System.Diagnostics;

namespace AndroidSparseTools.LibSparse;

internal enum BackedBlockType
{
    Data,
    File,
    Stream,
    Fill,
}

internal sealed class BackedBlock
{
    public uint Block;
    public ulong Len;
    public BackedBlockType Type;

    public byte[]? Data;
    public int DataOffset;

    public string? Filename;
    public long FileOffset;

    public Stream? SourceStream;
    public long StreamOffset;

    public uint FillVal;

    public BackedBlock? Next;
}

internal sealed class BackedBlockList
{
    private BackedBlock? _dataBlocks;
    private BackedBlock? _lastUsed;
    private readonly uint _blockSize;

    public BackedBlockList(uint blockSize)
    {
        _blockSize = blockSize;
    }

    public BackedBlock? First => _dataBlocks;

    public IEnumerable<BackedBlock> Enumerate()
    {
        BackedBlock? bb = _dataBlocks;
        while (bb is not null)
        {
            yield return bb;
            bb = bb.Next;
        }
    }

    public void Move(BackedBlockList to, BackedBlock? start, BackedBlock? end)
    {
        if (start is null)
        {
            start = _dataBlocks;
        }

        if (end is null)
        {
            BackedBlock? scan = start;
            while (scan is not null && scan.Next is not null)
            {
                scan = scan.Next;
            }

            end = scan;
        }

        if (start is null || end is null)
        {
            return;
        }

        _lastUsed = null;
        to._lastUsed = null;

        if (_dataBlocks == start)
        {
            _dataBlocks = end.Next;
        }
        else
        {
            for (BackedBlock? bb = _dataBlocks; bb is not null; bb = bb.Next)
            {
                if (bb.Next == start)
                {
                    bb.Next = end.Next;
                    break;
                }
            }
        }

        if (to._dataBlocks is null)
        {
            to._dataBlocks = start;
            end.Next = null;
            return;
        }

        for (BackedBlock? bb = to._dataBlocks; bb is not null; bb = bb.Next)
        {
            if (bb.Next is null || bb.Next.Block > start.Block)
            {
                end.Next = bb.Next;
                bb.Next = start;
                break;
            }
        }
    }

    public int AddData(byte[] data, int dataOffset, ulong len, uint block)
    {
        BackedBlock bb = new()
        {
            Block = block,
            Len = len,
            Type = BackedBlockType.Data,
            Data = data,
            DataOffset = dataOffset,
        };

        return Queue(bb);
    }

    public int AddFill(uint fillVal, ulong len, uint block)
    {
        BackedBlock bb = new()
        {
            Block = block,
            Len = len,
            Type = BackedBlockType.Fill,
            FillVal = fillVal,
        };

        return Queue(bb);
    }

    public int AddFile(string filename, long offset, ulong len, uint block)
    {
        BackedBlock bb = new()
        {
            Block = block,
            Len = len,
            Type = BackedBlockType.File,
            Filename = filename,
            FileOffset = offset,
        };

        return Queue(bb);
    }

    public int AddStream(Stream source, long offset, ulong len, uint block)
    {
        BackedBlock bb = new()
        {
            Block = block,
            Len = len,
            Type = BackedBlockType.Stream,
            SourceStream = source,
            StreamOffset = offset,
        };

        return Queue(bb);
    }

    public int Split(BackedBlock bb, uint maxLen)
    {
        maxLen = SparseDefs.AlignDown(maxLen, _blockSize);

        if (bb.Len <= maxLen)
        {
            return 0;
        }

        BackedBlock newBb = new()
        {
            Block = bb.Block,
            Len = bb.Len,
            Type = bb.Type,
            Data = bb.Data,
            DataOffset = bb.DataOffset,
            Filename = bb.Filename,
            FileOffset = bb.FileOffset,
            SourceStream = bb.SourceStream,
            StreamOffset = bb.StreamOffset,
            FillVal = bb.FillVal,
            Next = bb.Next,
        };

        newBb.Len = bb.Len - maxLen;
        newBb.Block = bb.Block + maxLen / _blockSize;

        switch (bb.Type)
        {
            case BackedBlockType.Data:
                newBb.DataOffset = bb.DataOffset + (int)maxLen;
                break;
            case BackedBlockType.File:
                newBb.FileOffset += maxLen;
                break;
            case BackedBlockType.Stream:
                newBb.StreamOffset += maxLen;
                break;
            case BackedBlockType.Fill:
                break;
        }

        bb.Next = newBb;
        bb.Len = maxLen;
        return 0;
    }

    private int Queue(BackedBlock newBb)
    {
        if (_dataBlocks is null)
        {
            _dataBlocks = newBb;
            return 0;
        }

        if (_dataBlocks.Block > newBb.Block)
        {
            newBb.Next = _dataBlocks;
            _dataBlocks = newBb;
            return 0;
        }

        BackedBlock bb;
        if (_lastUsed is not null && newBb.Block > _lastUsed.Block)
        {
            bb = _lastUsed;
        }
        else
        {
            bb = _dataBlocks;
        }

        _lastUsed = newBb;

        while (bb.Next is not null && bb.Next.Block < newBb.Block)
        {
            bb = bb.Next;
        }

        if (bb.Next is null)
        {
            bb.Next = newBb;
        }
        else
        {
            newBb.Next = bb.Next;
            bb.Next = newBb;
        }

        _ = Merge(newBb, newBb.Next);
        if (Merge(bb, newBb) == 0)
        {
            _lastUsed = bb;
        }

        return 0;
    }

    private int Merge(BackedBlock? a, BackedBlock? b)
    {
        if (a is null || b is null)
        {
            return Errno.Neg(Errno.EINVAL);
        }

        Debug.Assert(a.Block < b.Block);
        if (a.Block >= b.Block)
        {
            return Errno.Neg(Errno.EINVAL);
        }

        if (a.Type != b.Type)
        {
            return Errno.Neg(Errno.EINVAL);
        }

        uint blockLen = (uint)(a.Len / _blockSize);
        if (a.Block + blockLen != b.Block)
        {
            return Errno.Neg(Errno.EINVAL);
        }

        switch (a.Type)
        {
            case BackedBlockType.Data:
                return Errno.Neg(Errno.EINVAL);
            case BackedBlockType.Fill:
                if (a.FillVal != b.FillVal)
                {
                    return Errno.Neg(Errno.EINVAL);
                }

                break;
            case BackedBlockType.File:
                if (!string.Equals(a.Filename, b.Filename, StringComparison.Ordinal) ||
                    a.FileOffset + (long)a.Len != b.FileOffset)
                {
                    return Errno.Neg(Errno.EINVAL);
                }

                break;
            case BackedBlockType.Stream:
                if (!ReferenceEquals(a.SourceStream, b.SourceStream) ||
                    a.StreamOffset + (long)a.Len != b.StreamOffset)
                {
                    return Errno.Neg(Errno.EINVAL);
                }

                break;
        }

        a.Len += b.Len;
        a.Next = b.Next;
        return 0;
    }
}