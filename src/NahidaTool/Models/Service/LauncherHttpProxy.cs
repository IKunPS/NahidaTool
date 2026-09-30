using System;
using System.Net;
using System.Net.Http;
using NahidaTool.Models.Config;

namespace NahidaTool.Models.Service;

/// <summary>
/// Launcher traffic must not enter our game's MITM proxy: its domain redirects
/// include the official metadata APIs. All other system proxy routes and their
/// credentials are retained, including changes made while the app is running.
/// </summary>
internal sealed class LauncherHttpProxy : IWebProxy
{
    internal static LauncherHttpProxy Shared { get; } = new(HttpClient.DefaultProxy);
    private readonly IWebProxy _systemProxy;

    internal LauncherHttpProxy(IWebProxy systemProxy)
    {
        _systemProxy = systemProxy ?? throw new ArgumentNullException(nameof(systemProxy));
    }

    public ICredentials? Credentials
    {
        get => _systemProxy.Credentials;
        set => _systemProxy.Credentials = value;
    }

    public bool IsBypassed(Uri destination) => _systemProxy.IsBypassed(destination) ||
        GameProxyEndpoint.Matches(_systemProxy.GetProxy(destination));

    public Uri? GetProxy(Uri destination)
    {
        Uri? proxy = _systemProxy.GetProxy(destination);
        return GameProxyEndpoint.Matches(proxy) ? destination : proxy;
    }
}
