using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NahidaTool.Models.Service;

namespace NahidaTool.Models;

public class ManifestParser
{
    // 使用 ZstdSharp 进行实际解压
    public byte[] DecompressZstd(byte[] compressedData)
    {
        try
        {
            // Zstandard.Net 的用法
            using (var decompressor = new Zstandard.Net.ZstandardStream(
                       new MemoryStream(compressedData),
                       System.IO.Compression.CompressionMode.Decompress))
            {
                using (var output = new MemoryStream())
                {
                    decompressor.CopyTo(output);
                    // 使用 GetBuffer + SetLength 避免 ToArray 的额外复制
                    // 如果长度匹配，可以直接返回内部缓冲区的副本
                    var length = (int)output.Length;
                    var result = new byte[length];
                    Buffer.BlockCopy(output.GetBuffer(), 0, result, 0, length);
                    return result;
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"Zstd解压失败，将尝试返回原始数据: {ex.Message}", ex);
            return compressedData;
        }
    }

    /// <summary>
    /// 流式解压：直接从输入流解压到输出流，避免内存中保留完整数据
    /// </summary>
    public void DecompressZstdToStream(Stream inputStream, Stream outputStream, int bufferSize = 81920)
    {
        using (var decompressor = new Zstandard.Net.ZstandardStream(
                   inputStream,
                   System.IO.Compression.CompressionMode.Decompress,
                   leaveOpen: true))
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            try
            {
                int bytesRead;
                while ((bytesRead = decompressor.Read(buffer, 0, bufferSize)) > 0)
                {
                    outputStream.Write(buffer, 0, bytesRead);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    /// 异步流式解压
    /// </summary>
    public async Task DecompressZstdToStreamAsync(Stream inputStream, Stream outputStream, int bufferSize = 81920)
    {
        using (var decompressor = new Zstandard.Net.ZstandardStream(
                   inputStream,
                   System.IO.Compression.CompressionMode.Decompress,
                   leaveOpen: true))
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            try
            {
                int bytesRead;
                while ((bytesRead = await decompressor.ReadAsync(buffer, 0, bufferSize)) > 0)
                {
                    await outputStream.WriteAsync(buffer, 0, bytesRead);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    // 解析 Chunk 模式的 Manifest
    public List<FileChunkInfo> ParseChunkManifest(byte[] decompressedData)
    {
        try
        {
            var manifest = SophonChunkManifest.Parser.ParseFrom(decompressedData);
            var fileChunkInfos = new List<FileChunkInfo>();

            // 注意：proto中的字段名是 "chuncks"，但这是拼写错误，应该使用生成的属性名
            foreach (var chunkFile in manifest.Chuncks)
            {
                var fileChunkInfo = new FileChunkInfo
                {
                    FilePath = chunkFile.File,
                    Chunks = new List<ChunkInfo>(),
                    CheckSum = chunkFile.Md5 // 使用proto中的Md5字段作为合成文件的校验值
                };

                foreach (var chunk in chunkFile.Chunks)
                {
                    fileChunkInfo.Chunks.Add(new ChunkInfo
                    {
                        ChunkId = chunk.Id,
                        CompressedSize = chunk.CompressedSize,
                        UncompressedSize = chunk.UncompressedSize,
                        CheckSum = chunk.CompressedMd5, // 使用proto中的CompressedMd5字段作为压缩块的校验值
                        Url = null, // URL 在下载时构建
                        Offset = chunk.Offset, // chunk 在目标文件中的偏移量
                    });
                }

                fileChunkInfos.Add(fileChunkInfo);
            }

            return fileChunkInfos;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("解析Manifest失败", ex);
        }
    }
}