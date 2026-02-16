# AndroidSparseTools

[English](README.en.md) | [简体中文](README.zh-CN.md)

AndroidSparseTools 是对 Android `libsparse` 核心主机工具的 C# 实现。

它提供一个可执行文件，内含多个子命令：

- `simg2img`
- `img2simg`（`img2img` 别名）
- `append2simg`

实现逻辑基于 AOSP `platform/system/core/libsparse` 的行为和数据结构。

## 项目目的

- 在 Windows 上直接使用 Android sparse 镜像工具，无需构建 AOSP 主机二进制。
- 尽量保持与 AOSP 工具一致的命令行为。
- 提供单文件 EXE 方便分发。

## 已实现范围

- Sparse 格式解析与写出（`RAW`、`FILL`、`DONT_CARE`、`CRC32`）。
- Raw 转 Sparse、Sparse 转 Raw。
- 向已有 sparse 输出追加 raw 镜像块。
- 实时进度输出（包含当前速度）。

## AOSP 源码对应

映射自：

- `platform/system/core/libsparse/simg2img.cpp`
- `platform/system/core/libsparse/img2simg.cpp`
- `platform/system/core/libsparse/append2simg.cpp`
- `platform/system/core/libsparse/sparse_read.cpp`
- `platform/system/core/libsparse/sparse.cpp`
- `platform/system/core/libsparse/backed_block.cpp`
- `platform/system/core/libsparse/output_file.cpp`
- `platform/system/core/libsparse/sparse_crc32.cpp`
- `platform/system/core/libsparse/sparse_format.h`

## 环境要求

- .NET SDK 10.0+
- 默认发布配置为 Windows x64

## 构建

```powershell
dotnet build AndroidSparseTools.sln
```

## 发布单文件 EXE

```powershell
dotnet publish AndroidSparseTools.csproj -c Release
```

发布产物：

- `bin/Release/net10.0/win-x64/publish/AndroidSparseTools.exe`

## 查看命令帮助

```powershell
AndroidSparseTools.exe --help
```

不带参数运行也会显示全部命令。

## 使用方法

### simg2img

```text
Usage: simg2img <sparse_image_files> <raw_image_file>
```

示例：

```powershell
AndroidSparseTools.exe simg2img system_a.sparse.img system_a.raw.img
```

### img2simg

```text
Usage: img2simg [-s] <raw_image_file> <sparse_image_file> [<block_size>]
```

示例：

```powershell
AndroidSparseTools.exe img2simg super.raw.img super.sparse.img
AndroidSparseTools.exe img2simg super.raw.img super.sparse.img 4096
```

`img2img` 是 `img2simg` 的别名。

### append2simg

```text
Usage: append2simg <output> <input>
```

示例：

```powershell
AndroidSparseTools.exe append2simg super.sparse.img vendor.raw.img
```

## 进度显示

写入操作会在 stderr 输出单行动态进度，包含：

- 百分比
- 已处理字节 / 总字节
- 当前速度

示例格式：

```text
img2simg:  75% (192MB/256MB) 850MB/s
```

## 平台说明

- `img2simg -s` 使用 hole-aware 读取模式。
- 该模式是否可用取决于平台和文件系统能力。
- 在不支持 hole API 的平台上，可能返回“不支持”。

## 项目结构

- `Program.cs`：命令分发和工具流程。
- `ProgressDisplay.cs`：进度渲染与速度计算。
- `LibSparse/`：sparse 格式、读写、CRC、块链表等核心逻辑。

## 兼容性目标

目标是在常见工作流上与 AOSP 主机工具行为尽量一致。  
本项目不追求对所有主机平台内部细节进行逐字节级别复刻。

## 许可证

本项目遵循其所基于的 Android `libsparse` 上游源码许可模型。  
详细信息请查看 AOSP 上游文件头和许可说明。

