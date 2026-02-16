# AndroidSparseTools

[English](README.en.md) | [简体中文](README.zh-CN.md)

AndroidSparseTools is a C# implementation of core Android `libsparse` host tools.

It provides one executable with multiple subcommands:

- `simg2img`
- `img2simg` (`img2img` alias)
- `append2simg`

The implementation is based on AOSP `platform/system/core/libsparse` behavior and data structures.

## Why This Project

- Use Android sparse image tools on Windows without building AOSP host binaries.
- Keep command behavior close to AOSP tools.
- Provide one single-file EXE for easy distribution.

## Implemented Scope

- Sparse format parsing and writing (`RAW`, `FILL`, `DONT_CARE`, `CRC32`).
- Raw-to-sparse conversion and sparse-to-raw conversion.
- Appending raw image blocks to existing sparse image output.
- Real-time progress output with current transfer speed.

## AOSP Source Reference

Mapped from:

- `platform/system/core/libsparse/simg2img.cpp`
- `platform/system/core/libsparse/img2simg.cpp`
- `platform/system/core/libsparse/append2simg.cpp`
- `platform/system/core/libsparse/sparse_read.cpp`
- `platform/system/core/libsparse/sparse.cpp`
- `platform/system/core/libsparse/backed_block.cpp`
- `platform/system/core/libsparse/output_file.cpp`
- `platform/system/core/libsparse/sparse_crc32.cpp`
- `platform/system/core/libsparse/sparse_format.h`

## Requirements

- .NET SDK 10.0+
- Windows x64 for the default publish profile in this repo

## Build

```powershell
dotnet build AndroidSparseTools.sln
```

## Publish Single EXE

```powershell
dotnet publish AndroidSparseTools.csproj -c Release
```

Published file:

- `bin/Release/net10.0/win-x64/publish/AndroidSparseTools.exe`

## Command Help

```powershell
AndroidSparseTools.exe --help
```

Running with no arguments also prints all commands.

## Usage

### simg2img

```text
Usage: simg2img <sparse_image_files> <raw_image_file>
```

Example:

```powershell
AndroidSparseTools.exe simg2img system_a.sparse.img system_a.raw.img
```

### img2simg

```text
Usage: img2simg [-s] <raw_image_file> <sparse_image_file> [<block_size>]
```

Example:

```powershell
AndroidSparseTools.exe img2simg super.raw.img super.sparse.img
AndroidSparseTools.exe img2simg super.raw.img super.sparse.img 4096
```

`img2img` is supported as an alias of `img2simg`.

### append2simg

```text
Usage: append2simg <output> <input>
```

Example:

```powershell
AndroidSparseTools.exe append2simg super.sparse.img vendor.raw.img
```

## Progress Output

Write operations print a single live progress line to stderr, including:

- percentage
- processed bytes / total bytes
- current speed

Example format:

```text
img2simg:  75% (192MB/256MB) 850MB/s
```

## Platform Notes

- `img2simg -s` uses hole-aware read mode.
- Hole mode support depends on platform/filesystem capabilities.
- On platforms without hole APIs, this mode can return not supported.

## Project Layout

- `Program.cs`: CLI command dispatch and tool workflows.
- `ProgressDisplay.cs`: progress rendering and speed calculation.
- `LibSparse/`: sparse format, reader, writer, CRC, and block list logic.

## Compatibility Goal

The goal is behavior parity with AOSP host tools for normal workflows.
This is not a byte-for-byte reimplementation of every internal host-specific path.

## License

This project follows the upstream licensing model of Android `libsparse` sources it is based on.
See upstream AOSP headers and licensing files for details.

