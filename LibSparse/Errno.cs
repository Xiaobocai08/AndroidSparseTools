namespace AndroidSparseTools.LibSparse;

internal static class Errno
{
    public const int EINVAL = 22;
    public const int ENOMEM = 12;
    public const int EOVERFLOW = 75;
    public const int ENOTSUP = 95;
    public const int ESPIPE = 29;
    public const int ENXIO = 6;

    public static int Neg(int code) => -code;
}