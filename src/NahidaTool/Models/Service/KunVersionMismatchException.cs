using System;
using NahidaTool.Models;

namespace NahidaTool.Models.Service;

public sealed class KunVersionMismatchException : InvalidOperationException
{
    public string ClientVersion { get; }
    public string ServerVersion { get; }

    public KunVersionMismatchException(string clientVersion, string serverVersion)
        : base(string.Format(Lang.HomePage_KunVersionMismatch, clientVersion, serverVersion))
    {
        ClientVersion = clientVersion;
        ServerVersion = serverVersion;
    }
}
