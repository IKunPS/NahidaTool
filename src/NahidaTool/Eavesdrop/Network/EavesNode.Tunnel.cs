using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using NahidaTool.Models.Service;

namespace NahidaTool.Eavesdrop.Network;

public partial class EavesNode
{
    private Stream? _tunnelTransport;

    private static bool ShouldInterceptHost(string host) => ProxyHostPolicy.ShouldIntercept(
        host, Eavesdropper.InterceptHosts, Eavesdropper.PassthroughHosts);

    private async Task<Uri?> OpenTunnelAsync(Uri connectTarget)
    {
        Uri origin = connectTarget;
        if (IPAddress.TryParse(connectTarget.Host.Trim('[', ']'), out _))
        {
            // A VPN may resolve game domains to fake IPs before the client issues
            // CONNECT. Recover its TLS SNI, but replay the ClientHello unchanged so
            // unrelated traffic retains end-to-end TLS through the VPN.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var hello = await ClientHelloProbe.ReadAsync(_client.GetStream(), timeout.Token).ConfigureAwait(false);
            _tunnelTransport = new PrefixReplayStream(_client.GetStream(), hello.Prefix);
            if (!string.IsNullOrEmpty(hello.ServerName) &&
                Uri.CheckHostName(hello.ServerName) == UriHostNameType.Dns &&
                ShouldInterceptHost(hello.ServerName))
            {
                origin = new UriBuilder(Uri.UriSchemeHttps, hello.ServerName, connectTarget.Port).Uri;
                LogService.Info($"游戏代理通过 TLS 域名识别 IP 隧道: {hello.ServerName}");
            }
        }

        if (!ShouldInterceptHost(origin.Host))
        {
            await RelayTunnelAsync(connectTarget.Host, connectTarget.Port).ConfigureAwait(false);
            return null;
        }
        return await SecureTunnelAsync(origin.Host).ConfigureAwait(false) ? origin : null;
    }

    private async Task<bool> SecureTunnelAsync(string host)
    {
        try
        {
            var certificate = _certifier.GenerateCertificate(host)
                ?? throw new AuthenticationException("Game proxy certificate unavailable");
            _secureStream = new SslStream(GetStream());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _secureStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.None
            }, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Warn($"游戏代理 TLS 握手失败 ({host}): {ex.GetType().Name}");
            return false;
        }
    }

    private async Task RelayTunnelAsync(string remoteHost, int remotePort)
    {
        using var remoteClient = new TcpClient();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await remoteClient.ConnectAsync(remoteHost, remotePort, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogService.Warn($"TCP 隧道连接失败 ({remoteHost}:{remotePort}): {ex.GetType().Name}");
            return;
        }
        remoteClient.NoDelay = true;
        remoteClient.SendBufferSize = RelayBufferSize;
        remoteClient.ReceiveBufferSize = RelayBufferSize;
        Stream clientStream = _tunnelTransport ?? _client.GetStream();
        Stream remoteStream = remoteClient.GetStream();
        using var lifetime = new CancellationTokenSource();
        var outgoing = RelayAsync(clientStream, remoteStream, lifetime.Token);
        var incoming = RelayAsync(remoteStream, clientStream, lifetime.Token);
        await Task.WhenAny(outgoing, incoming).ConfigureAwait(false);
        lifetime.Cancel();
        await Task.WhenAll(outgoing, incoming).ConfigureAwait(false);
    }

    private static async Task RelayAsync(Stream source, Stream destination, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(RelayBufferSize);
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, RelayBufferSize), ct).ConfigureAwait(false);
                if (read == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                await destination.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
