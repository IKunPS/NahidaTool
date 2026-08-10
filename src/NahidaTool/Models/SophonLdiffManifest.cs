using System;
using System.Collections.Generic;
using Google.Protobuf;

namespace NahidaTool.Models;

public sealed class SophonLdiffManifest
{
    public List<LdiffAssetProperty> Assets { get; } = new();

    public static SophonLdiffManifest ParseFrom(byte[] data)
    {
        var result = new SophonLdiffManifest();
        using var input = new CodedInputStream(data);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (tag == 10)
                result.Assets.Add(LdiffAssetProperty.Parse(input.ReadBytes()));
            else
                input.SkipLastField();
        }
        return result;
    }
}

public sealed class LdiffAssetProperty
{
    public string AssetName { get; private set; } = string.Empty;
    public long AssetSize { get; private set; }
    public string AssetHashMd5 { get; private set; } = string.Empty;
    public List<LdiffAssetEntry> AssetLdiffs { get; } = new();

    internal static LdiffAssetProperty Parse(ByteString data)
    {
        var result = new LdiffAssetProperty();
        using var input = new CodedInputStream(data.ToByteArray());
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (tag)
            {
                case 10: result.AssetName = input.ReadString(); break;
                case 16: result.AssetSize = input.ReadInt64(); break;
                case 26: result.AssetHashMd5 = input.ReadString(); break;
                case 34: result.AssetLdiffs.Add(LdiffAssetEntry.Parse(input.ReadBytes())); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}

public sealed class LdiffAssetEntry
{
    public string LatestDiffVersion { get; private set; } = string.Empty;
    public LdiffAssetData? DiffData { get; private set; }

    internal static LdiffAssetEntry Parse(ByteString data)
    {
        var result = new LdiffAssetEntry();
        using var input = new CodedInputStream(data.ToByteArray());
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (tag)
            {
                case 10: result.LatestDiffVersion = input.ReadString(); break;
                case 18: result.DiffData = LdiffAssetData.Parse(input.ReadBytes()); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}

public sealed class LdiffAssetData
{
    public string ChunkName { get; private set; } = string.Empty;
    public string ChunkDiffVersion { get; private set; } = string.Empty;
    public string ChunkNode { get; private set; } = string.Empty;
    public long ChunkSize { get; private set; }
    public string ChunkHashMd5 { get; private set; } = string.Empty;
    public long HdiffInChunkOffset { get; private set; }
    public long HdiffSize { get; private set; }
    public string SourcePath { get; private set; } = string.Empty;
    public long SourceSize { get; private set; }
    public string SourceHashMd5 { get; private set; } = string.Empty;

    internal static LdiffAssetData Parse(ByteString data)
    {
        var result = new LdiffAssetData();
        using var input = new CodedInputStream(data.ToByteArray());
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (tag)
            {
                case 10: result.ChunkName = input.ReadString(); break;
                case 18: result.ChunkDiffVersion = input.ReadString(); break;
                case 26: result.ChunkNode = input.ReadString(); break;
                case 32: result.ChunkSize = input.ReadInt64(); break;
                case 42: result.ChunkHashMd5 = input.ReadString(); break;
                case 48: result.HdiffInChunkOffset = input.ReadInt64(); break;
                case 56: result.HdiffSize = input.ReadInt64(); break;
                case 66: result.SourcePath = input.ReadString(); break;
                case 72: result.SourceSize = input.ReadInt64(); break;
                case 82: result.SourceHashMd5 = input.ReadString(); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}
