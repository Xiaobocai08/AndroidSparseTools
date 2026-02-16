namespace AndroidSparseTools.LibSparse;

internal static class SparseDefs
{
    public static uint DivRoundUp(uint x, uint y) => (x + y - 1U) / y;

    public static ulong DivRoundUp(ulong x, ulong y) => (x + y - 1UL) / y;

    public static long DivRoundUp(long x, long y) => (x + y - 1L) / y;

    public static ulong Align(ulong x, uint y) => (ulong)y * DivRoundUp(x, y);

    public static long Align(long x, uint y) => (long)y * DivRoundUp(x, y);

    public static long AlignDown(long x, uint y) => (long)y * (x / y);

    public static uint AlignDown(uint x, uint y) => y * (x / y);
}