using System.Buffers.Binary;

namespace AndroidSparseTools.LibSparse;

internal static class SparseFormat
{
    public const uint SPARSE_HEADER_MAGIC = 0xed26ff3a;

    public const ushort CHUNK_TYPE_RAW = 0xCAC1;
    public const ushort CHUNK_TYPE_FILL = 0xCAC2;
    public const ushort CHUNK_TYPE_DONT_CARE = 0xCAC3;
    public const ushort CHUNK_TYPE_CRC32 = 0xCAC4;

    public const ushort SPARSE_HEADER_MAJOR_VER = 1;
    public const ushort SPARSE_HEADER_MINOR_VER = 0;

    public const int SPARSE_HEADER_LEN = 28;
    public const int CHUNK_HEADER_LEN = 12;
}

internal readonly struct SparseHeader
{
    public readonly uint Magic;
    public readonly ushort MajorVersion;
    public readonly ushort MinorVersion;
    public readonly ushort FileHeaderSize;
    public readonly ushort ChunkHeaderSize;
    public readonly uint BlockSize;
    public readonly uint TotalBlocks;
    public readonly uint TotalChunks;
    public readonly uint ImageChecksum;

    public SparseHeader(
        uint magic,
        ushort majorVersion,
        ushort minorVersion,
        ushort fileHeaderSize,
        ushort chunkHeaderSize,
        uint blockSize,
        uint totalBlocks,
        uint totalChunks,
        uint imageChecksum)
    {
        Magic = magic;
        MajorVersion = majorVersion;
        MinorVersion = minorVersion;
        FileHeaderSize = fileHeaderSize;
        ChunkHeaderSize = chunkHeaderSize;
        BlockSize = blockSize;
        TotalBlocks = totalBlocks;
        TotalChunks = totalChunks;
        ImageChecksum = imageChecksum;
    }

    public static SparseHeader Read(ReadOnlySpan<byte> data)
    {
        return new SparseHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(data),
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4)),
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6)),
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8)),
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(10)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(20)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(24)));
    }

    public void Write(Span<byte> data)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(4), MajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(6), MinorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(8), FileHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(10), ChunkHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(12), BlockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(16), TotalBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(20), TotalChunks);
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(24), ImageChecksum);
    }
}

internal readonly struct ChunkHeader
{
    public readonly ushort ChunkType;
    public readonly ushort Reserved1;
    public readonly uint ChunkSize;
    public readonly uint TotalSize;

    public ChunkHeader(ushort chunkType, ushort reserved1, uint chunkSize, uint totalSize)
    {
        ChunkType = chunkType;
        Reserved1 = reserved1;
        ChunkSize = chunkSize;
        TotalSize = totalSize;
    }

    public static ChunkHeader Read(ReadOnlySpan<byte> data)
    {
        return new ChunkHeader(
            BinaryPrimitives.ReadUInt16LittleEndian(data),
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8)));
    }

    public void Write(Span<byte> data)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(data, ChunkType);
        BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(2), Reserved1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(4), ChunkSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(8), TotalSize);
    }
}