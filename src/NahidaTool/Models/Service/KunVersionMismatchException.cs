using System;
using System.Collections.Generic;
using System.Linq;
using NahidaTool.Models;

namespace NahidaTool.Models.Service;

public sealed class KunVersionMismatchException : InvalidOperationException
{
    public string ClientVersion { get; }
    public IReadOnlyList<string> SupportedVersions { get; }

    public KunVersionMismatchException(string clientVersion, IReadOnlyList<string> supportedVersions)
        : base(string.Format(Lang.HomePage_KunVersionMismatch, clientVersion, string.Join(" / ", supportedVersions)))
    {
        ClientVersion = clientVersion;
        SupportedVersions = Array.AsReadOnly(supportedVersions.ToArray());
    }
}
