using System;
using NahidaTool.Models;
using NahidaTool.Models.Enum;

namespace NahidaTool.Models.Service;

public sealed class KunClientRegionException : InvalidOperationException
{
    public ServerRegionType? DetectedRegion { get; }

    public KunClientRegionException(ServerRegionType? detectedRegion)
        : base(Lang.HomePage_KunOsRequired)
    {
        DetectedRegion = detectedRegion;
    }
}
