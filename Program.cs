using AndroidSparseTools.LibSparse;
using System.Globalization;

namespace AndroidSparseTools;

internal static class Program
{
    private enum ToolMode
    {
        Simg2Img,
        Img2Simg,
        Append2Simg,
    }

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            UsageAll();
            return 1;
        }

        if (args[0] is "--help" or "-h" or "help")
        {
            UsageAll();
            return 0;
        }

        if (!TryParseMode(args[0], out ToolMode mode))
        {
            Console.Error.WriteLine($"Unknown command: {args[0]}");
            UsageAll();
            return 1;
        }

        return mode switch
        {
            ToolMode.Simg2Img => RunSimg2Img(args[1..]),
            ToolMode.Img2Simg => RunImg2Simg(args[1..]),
            ToolMode.Append2Simg => RunAppend2Simg(args[1..]),
            _ => 1,
        };
    }

    private static bool TryParseMode(string value, out ToolMode mode)
    {
        switch (value.ToLowerInvariant())
        {
            case "simg2img":
                mode = ToolMode.Simg2Img;
                return true;
            case "img2simg":
            case "img2img":
                mode = ToolMode.Img2Simg;
                return true;
            case "append2simg":
                mode = ToolMode.Append2Simg;
                return true;
            default:
                mode = ToolMode.Simg2Img;
                return false;
        }
    }

    private static void UsageSimg2Img()
    {
        Console.Error.WriteLine("Usage: simg2img <sparse_image_files> <raw_image_file>");
    }

    private static void UsageImg2Simg()
    {
        Console.Error.WriteLine("Usage: img2simg [-s] <raw_image_file> <sparse_image_file> [<block_size>]");
    }

    private static void UsageAppend2Simg()
    {
        Console.Error.WriteLine("Usage: append2simg <output> <input>");
    }

    private static void UsageAll()
    {
        Console.Error.WriteLine("AndroidSparseTools commands:");
        Console.Error.WriteLine("  simg2img <sparse_image_files> <raw_image_file>");
        Console.Error.WriteLine("  img2simg [-s] <raw_image_file> <sparse_image_file> [<block_size>]");
        Console.Error.WriteLine("  img2img  [-s] <raw_image_file> <sparse_image_file> [<block_size>]  (alias)");
        Console.Error.WriteLine("  append2simg <output> <input>");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Examples:");
        Console.Error.WriteLine("  AndroidSparseTools.exe simg2img system.sparse system.img");
        Console.Error.WriteLine("  AndroidSparseTools.exe img2simg system.img system.sparse");
        Console.Error.WriteLine("  AndroidSparseTools.exe append2simg super.sparse vendor.img");
    }

    private static int RunSimg2Img(string[] args)
    {
        if (args.Length < 2)
        {
            UsageSimg2Img();
            return 1;
        }

        string outputPath = args[^1];

        FileStream output;
        try
        {
            output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        }
        catch
        {
            Console.Error.WriteLine($"Cannot open output file {outputPath}");
            return 1;
        }

        using (output)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                string inputPath = args[i];
                Stream input;

                if (inputPath == "-")
                {
                    input = Console.OpenStandardInput();
                }
                else
                {
                    try
                    {
                        input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    }
                    catch
                    {
                        Console.Error.WriteLine($"Cannot open input file {inputPath}");
                        return 1;
                    }
                }

                using (input)
                {
                    SparseFile? sparse = SparseRead.SparseFileImport(input, true, false);
                    if (sparse is null)
                    {
                        Console.Error.WriteLine("Failed to read sparse file");
                        return 1;
                    }

                    try
                    {
                        output.Seek(0, SeekOrigin.Begin);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"lseek failed: {ex.Message}");
                        return 1;
                    }

                    using ProgressDisplay progress = new($"simg2img {i + 1}/{args.Length - 1}");
                    if (sparse.Write(output, false, false, false, progress.Report) < 0)
                    {
                        Console.Error.WriteLine("Cannot write output file");
                        return 1;
                    }
                }
            }
        }

        return 0;
    }

    private static int RunImg2Simg(string[] args)
    {
        SparseReadMode mode = SparseReadMode.Normal;
        int index = 0;

        while (index < args.Length && args[index].StartsWith("-", StringComparison.Ordinal))
        {
            if (args[index] == "-s")
            {
                mode = SparseReadMode.Hole;
                index++;
                continue;
            }

            UsageImg2Simg();
            return 1;
        }

        int extra = args.Length - index;
        if (extra < 2 || extra > 3)
        {
            UsageImg2Simg();
            return 1;
        }

        uint blockSize = 4096;
        if (extra == 3 &&
            !uint.TryParse(args[index + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out blockSize))
        {
            blockSize = 0;
        }

        if (blockSize < 1024 || blockSize % 4 != 0)
        {
            UsageImg2Simg();
            return 1;
        }

        string argIn = args[index];
        string argOut = args[index + 1];

        Stream input;
        if (argIn == "-")
        {
            input = Console.OpenStandardInput();
        }
        else
        {
            try
            {
                input = new FileStream(argIn, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch
            {
                Console.Error.WriteLine($"Cannot open input file {argIn}");
                return 1;
            }
        }

        Stream output;
        if (argOut == "-")
        {
            output = Console.OpenStandardOutput();
        }
        else
        {
            try
            {
                output = new FileStream(argOut, FileMode.Create, FileAccess.Write, FileShare.Read);
            }
            catch
            {
                input.Dispose();
                Console.Error.WriteLine($"Cannot open output file {argOut}");
                return 1;
            }
        }

        using (input)
        using (output)
        {
            if (!input.CanSeek)
            {
                Console.Error.WriteLine("Cannot seek input file");
                return 1;
            }

            long len;
            try
            {
                len = input.Seek(0, SeekOrigin.End);
                input.Seek(0, SeekOrigin.Begin);
            }
            catch
            {
                Console.Error.WriteLine("Cannot seek input file");
                return 1;
            }

            SparseFile? sparse = SparseFile.New(blockSize, len);
            if (sparse is null)
            {
                Console.Error.WriteLine("Failed to create sparse file");
                return 1;
            }

            sparse.Verbose = true;
            int ret = SparseRead.SparseFileRead(sparse, input, mode, false);
            if (ret != 0)
            {
                Console.Error.WriteLine("Failed to read file");
                return 1;
            }

            using ProgressDisplay progress = new("img2simg");
            ret = sparse.Write(output, false, true, false, progress.Report);
            if (ret != 0)
            {
                Console.Error.WriteLine("Failed to write sparse file");
                return 1;
            }
        }

        return 0;
    }

    private static int RunAppend2Simg(string[] args)
    {
        if (args.Length != 2)
        {
            UsageAppend2Simg();
            return 1;
        }

        string outputPath = args[0];
        string inputPath = args[1];
        string tmpPath = outputPath + ".append2simg";

        FileStream output;
        try
        {
            output = new FileStream(outputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Couldn't open output file ({ex.Message})");
            return 1;
        }

        using (output)
        {
            SparseFile? sparseOutput = SparseRead.SparseFileImportAuto(output, false, true);
            if (sparseOutput is null)
            {
                Console.Error.WriteLine("Couldn't import output file");
                return 1;
            }

            FileStream input;
            try
            {
                input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Couldn't open input file ({ex.Message})");
                return 1;
            }

            using (input)
            {
                long inputLen;
                try
                {
                    inputLen = input.Seek(0, SeekOrigin.End);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Couldn't get input file length ({ex.Message})");
                    return 1;
                }

                if (inputLen < 0)
                {
                    Console.Error.WriteLine("Couldn't get input file length");
                    return 1;
                }

                if (inputLen % sparseOutput.BlockSize != 0)
                {
                    Console.Error.WriteLine("Input file is not a multiple of the output file's block size");
                    return 1;
                }

                input.Seek(0, SeekOrigin.Begin);

                uint outputBlock = (uint)(sparseOutput.Len / sparseOutput.BlockSize);
                if (sparseOutput.AddStream(input, 0, (ulong)inputLen, outputBlock) < 0)
                {
                    Console.Error.WriteLine("Couldn't add input file");
                    return 1;
                }

                sparseOutput.Len += inputLen;

                FileStream tmp;
                try
                {
                    tmp = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Couldn't open temporary file ({ex.Message})");
                    return 1;
                }

                using (tmp)
                {
                    try
                    {
                        output.Seek(0, SeekOrigin.Begin);
                    }
                    catch
                    {
                        Console.Error.WriteLine("Couldn't rewind output file");
                        return 1;
                    }

                    using ProgressDisplay progress = new("append2simg");
                    if (sparseOutput.Write(tmp, false, true, false, progress.Report) < 0)
                    {
                        Console.Error.WriteLine("Failed to write sparse file");
                        return 1;
                    }
                }
            }
        }

        try
        {
            File.Move(tmpPath, outputPath, true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to rename temporary file ({ex.Message})");
            return 1;
        }

        return 0;
    }
}
