namespace AndroidSparseTools.LibSparse;

internal static class SparseLog
{
    public static Action<string> SparsePrintVerbose { get; set; } =
        message => Console.Error.Write(message);

    public static void Verbose(string format, params object[] args)
    {
        SparsePrintVerbose(string.Format(format, args));
    }
}